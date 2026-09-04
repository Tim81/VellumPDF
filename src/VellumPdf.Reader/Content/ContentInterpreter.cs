// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using VellumPdf.Core;
using VellumPdf.Document;

namespace VellumPdf.Reader.Content;

/// <summary>
/// An operand-stack content-stream interpreter (ISO 32000-2 §7.8.2): walks a page's <c>/Contents</c>
/// (or a Form XObject's own content, recursively), tracking the graphics and text state, and reports
/// every recognised operator, inline image, and Form XObject boundary to an <see cref="IContentVisitor"/>.
/// Shared machinery for the reader's later text and image extraction (#98): this type positions
/// nothing and resolves no font. It keeps state current and delimits structure.
/// </summary>
/// <remarks>
/// Not thread-safe, and not reentrant across concurrent <see cref="Run"/> calls on the same
/// instance, matching every other stateful type in this package. A malformed document degrades
/// (a diagnostic and best-effort recovery) rather than throwing; the sole exception is
/// <see cref="UnsupportedPdfFeatureException"/>, which is allowed to propagate: "cannot continue"
/// belongs to the exception channel the rest of this reader already uses it for.
/// <para>
/// Retains at most TWO diagnostics past <see cref="PdfReaderOptions.MaxDiagnostics"/> per
/// <see cref="Run"/> (see <see cref="DiagnosticSink.ReportRetained"/>), one of each code:
/// <see cref="PdfReaderDiagnosticCode.ContentStreamTooLarge"/> fires at most once per run, because
/// the first truncation it reports, whether against the page's own <c>/Contents</c> or a Form
/// XObject's, drives the run's own decoded-bytes budget to zero, and every later stream this run
/// touches then takes the budget-already-spent skip silently instead of truncating (and reporting)
/// again; <see cref="PdfReaderDiagnosticCode.FormXObjectBudgetExceeded"/> fires at most once
/// because its own report carries no object number, so the sink's own (code, object, page) dedupe
/// already collapses every recursion past the 4096-invocation ceiling to one entry on its own.
/// </para>
/// </remarks>
internal sealed class ContentInterpreter
{
    // ISO 32000-2 §7.8.2 gives an operator's own operands no declared bound; this reader's own
    // ceiling against a hostile or corrupted stream that never emits an operator at all. 64, not
    // 32: Annex C.2 Table C.1 (informative; Annex C.1 is only the annex's own general preamble)
    // records 32 as the DeviceN colourant-count limit an earlier PDF version recommended, but
    // §8.6.6.5 itself allows "an arbitrary number" of colourants, and Table 73's scn operator
    // takes one numeric component per colourant plus an optional trailing pattern name, so a
    // legal scn call against a DeviceN space with 32 or more colourants needs 33 or more operands;
    // 32 as this reader's own ceiling would reject a legal call, not just a hostile one.
    private const int MaxOperandsPerOperator = 64;

    // This reader's own ceiling on how many tokens a single array or dictionary operand may carry,
    // counted at every nesting depth (each element, key, value and nested opening delimiter is one
    // token), so the count bounds what PdfObjectParser would allocate for the composite as a whole.
    // Counting only the top level would let '[[1 1 1 ...]]' hide millions of elements behind one.
    // §9.4.3's own TJ array is what most directly exercises it, but CompositeOperandWithinCap
    // applies it to every composite operand this interpreter parses, not only TJ's, and enforces it
    // BEFORE PdfObjectParser ever materialises the composite (#402 round 3): a 20,000,000-element TJ
    // array (about 40 MiB of source text) used to allocate the whole PdfArray, and every boxed
    // PdfInteger in it, before this cap was consulted at all, measured at 1,784 MiB allocated and
    // 977 MiB committed for one dropped operator and one 309.
    private const int MaxCompositeOperandElements = 8192;

    // §8.9.7's Table 91 lists eleven inline image dictionary entries (BitsPerComponent, ColorSpace,
    // Decode, DecodeParms, Filter, Height, ImageMask, Intent, Interpolate, Length, Width; Table 92
    // layers abbreviations onto some of their VALUES, not further entries), and §8.9.7 itself says
    // "Entries other than those listed shall be ignored", so this reader's ceiling on how many
    // key/value pairs one inline image dictionary may carry, before HandleInlineImage gives up on
    // it, is generous by construction: a dictionary using only Table 91's keys, full or
    // abbreviated, has at most 21 pairs. It exists to bound how much a hostile BI...ID section can
    // make this reader allocate one PdfName key (and, per MaxCompositeOperandElements above, one
    // capped value) at a time: the check fires on the 65th pair whether or not an ID ever follows
    // it (#402 round 7).
    private const int MaxInlineImageDictionaryPairs = 64;

    // §8.4.4's q/Q pair; this reader's own ceiling on how deep a legitimate document nests them.
    private const int MaxGraphicsStateDepth = 64;

    // §14.6.1's BMC/BDC/EMC nesting; this reader's own ceiling, mirroring MaxGraphicsStateDepth.
    private const int MaxMarkedContentDepth = 64;

    // This interpreter's own budget on total successful Form XObject recursions across one page
    // (§8.10), independent of PdfReaderOptions.MaxFormXObjectDepth, which bounds nesting DEPTH
    // rather than the total COUNT of forms a page may draw. A wide, shallow graph (one page
    // invoking the same shallow form thousands of times) is not caught by a depth cap at all.
    private const int MaxFormInvocationsPerPage = 4096;

    // This reader's own ceiling on the total decoded content bytes one Run interprets: the page's
    // own /Contents (ISO 32000-2 §7.7.3.3 Table 31) and every Form XObject invocation on that page
    // (§8.10), combined. Tracked as a running per-Run budget (_contentBytesRemaining) rather than
    // checked once against /Contents alone, since a small file drawing one large form many times
    // can interpret far more total content than its own /Contents ever declares.
    private const long MaxContentBytes = 64L * 1024 * 1024;

    // The bounded resync probe (see ProbeOnce/ClassifyResyncPoint) spends at most this many bytes
    // of lexing across an ENTIRE Run, shared by every 'EI' candidate ScanForEi tries: bounding the
    // RUN's own total work, not each candidate's, is what keeps ScanForEi's amortised cost linear in
    // the content length rather than quadratic (#402 round 3; a per-candidate byte/token cap alone
    // still let a probe reject the terminating 'EI' whose own legitimate follow-on token happened to be
    // longer than the cap, and a two-window retry to fix THAT still paid, in the worst case, one
    // full window's own lexing cost per false candidate: an adversarial run of false candidates
    // each immediately followed by an unterminated string, "\" EI (\"" repeated, drove that cost to
    // 16.6 s per decoded MiB with no diagnostic to explain it). Once this budget is spent, every
    // later candidate in the Run is accepted unverified rather than probed at all (see
    // ProbeOutcome.Exhausted): the total probe lexing one Run can ever do is bounded to exactly
    // this many bytes, whatever the content size or candidate count (#402 round 4: ProbeOnce's own
    // window length is Math.Min(remaining, _probeBytesRemaining), so a window can never itself run
    // past what is left of the budget; measured at exactly 16,777,216 charged at the 1, 4, and
    // 16 MiB settings alike, not that plus a further window's own worth).
    private const long MaxProbeBytesPerRun = 16L * 1024 * 1024;

    private static readonly PdfName XObjectSubtypeForm = new("Form");
    private static readonly PdfName XObjectSubtypeImage = new("Image");
    private static readonly PdfName ImageMaskKey = new("ImageMask");
    private static readonly PdfName WidthKey = new("Width");
    private static readonly PdfName HeightKey = new("Height");
    private static readonly PdfName BitsPerComponentKey = new("BitsPerComponent");
    private static readonly PdfName MatrixKey = new("Matrix");
    private static readonly PdfName BBoxKey = new("BBox");
    private static readonly PdfName FontKey = new("Font");

    private readonly PdfDocumentReader _reader;
    private readonly ReaderLimits _limits;

    // ── Per-Run mutable state: reset at the top of Run, threaded through the recursive descent
    // into Form XObjects via StreamContext rather than saved/restored on instance fields. ──────────
    private GraphicsState _gs = new();
    private readonly Stack<GraphicsState> _gsStack = new();
    private readonly TextState _textState = new();
    private readonly List<PdfObject> _operands = [];
    private bool _operandOverflow;
    private int _bxDepth;
    private int _markedContentDepth;
    private bool _inTextObject;
    private readonly HashSet<int> _openForms = [];
    private int _formDepth;
    private int _formInvocations;
    private long _contentBytesRemaining;
    private ReadOnlyMemory<byte> _currentBuffer;

    // The resync probe's own remaining share of MaxProbeBytesPerRun, and whether it has already
    // been spent this Run. Once _probeBudgetExhausted is true, ClassifyResyncPoint accepts every
    // later 'EI' candidate without probing it at all (see ProbeOnce's Exhausted outcome), and
    // HandleInlineImage reports that against EVERY later inline image this Run delimits, not only
    // the one whose own scan spent the budget: the sink's own (code, object, page) dedupe key means
    // a later image inside a DIFFERENT content stream (a different Form XObject, or the page's own
    // content once a form already spent it) is not deduped against the first report at all, so the
    // message names the offset AND the object number of the FIRST occurrence explicitly
    // (_probeBudgetExhaustedAtObjectNumber), rather than letting a later report's own ctx attribute
    // an offset from a DIFFERENT buffer to itself (#402 round 4).
    private long _probeBytesRemaining;
    private bool _probeBudgetExhausted;
    private int _probeBudgetExhaustedAtOffset;
    private int? _probeBudgetExhaustedAtObjectNumber;

    // Pushes PushGraphicsState/PushMarkedContent dropped for being over MaxGraphicsStateDepth or
    // MaxMarkedContentDepth: a matching 'Q'/'EMC' consumes one of these before it may report an
    // unbalanced pop (see PopGraphicsState/PopMarkedContent), so a producer that legitimately
    // nests past this reader's own ceiling and then balances every one of those nests does not
    // ALSO get accused of an unbalanced pop purely because this reader declined to push the state
    // it was asked to (#402 round 2). Saved and restored across a Form XObject invocation
    // (HandleDo) the same way _gsFloor/_markedContentFloor are, so a form's own credit and the
    // invoker's own credit can never be consumed across that boundary.
    private int _ignoredGsPushes;
    private int _ignoredMcPushes;

    // The graphics-state stack depth, marked-content depth, and BX/EX depth a 'Q', 'EMC', or 'EX'
    // may not pop or decrement below. All three are 0 for the page's own top-level content and set
    // to the invoker's own depth for the duration of one Form XObject's content (see HandleDo):
    // ISO 32000-2 §8.10.1 brackets a form's content in an implicit q/Q pair the form's own content
    // must not be able to see past, in either direction.
    private int _gsFloor;
    private int _markedContentFloor;
    private int _bxFloor;

    /// <summary>The current graphics state, the top of the <c>q</c>/<c>Q</c> stack, readable from
    /// inside an <see cref="IContentVisitor"/> callback. Mutated in place; a callback that needs a
    /// value after the interpreter moves on must copy it. Reset to a fresh default the moment
    /// <see cref="Run"/> returns, so a value read after that point is not the last state the run
    /// left behind.</summary>
    internal GraphicsState GraphicsState => _gs;

    /// <summary>The current text-positioning state, readable the same way as
    /// <see cref="GraphicsState"/>, and reset on the same schedule: a fresh default once
    /// <see cref="Run"/> returns.</summary>
    internal TextState TextState => _textState;

    /// <summary>
    /// Test-only visibility into how many content streams (the page's own <c>/Contents</c>
    /// elements, and Form XObject invocations) this <see cref="Run"/> decoded, so a test
    /// can pin how early the per-Run content budget stops further decoding without asserting on
    /// wall-clock time or process memory (#402 round 2: peak heap scales with how many oversized
    /// streams get decoded before the budget is charged, not with the operator count those streams
    /// produce).
    /// </summary>
    internal int ContentStreamsDecoded { get; private set; }

    /// <summary>
    /// Test-only visibility into how many bytes the resync probe (<c>ProbeOnce</c>) has lexed this
    /// <see cref="Run"/>, so a test can pin how the probe's own <c>MaxProbeBytesPerRun</c> budget
    /// bounds its work directly, the same way <see cref="ContentStreamsDecoded"/> pins the content
    /// budget (#402 round 3).
    /// </summary>
    internal long ProbeBytesConsumed { get; private set; }

    /// <summary>Creates an interpreter that resolves resources and streams through
    /// <paramref name="reader"/>, under that reader's own <see cref="PdfDocumentReader.Limits"/>.</summary>
    internal ContentInterpreter(PdfDocumentReader reader)
    {
        _reader = reader;
        _limits = reader.Limits;
    }

    /// <summary>
    /// Interprets <paramref name="page"/>'s content (ISO 32000-2 §7.8.2), reporting every event to
    /// <paramref name="visitor"/> and every recoverable condition through this reader's diagnostics
    /// channel, scoped per call via <see cref="PdfDocumentReader.CreateContentDiagnosticScope"/>.
    /// Never throws for a malformed document; <see cref="UnsupportedPdfFeatureException"/> is the
    /// one exception allowed to propagate (see this type's own remarks).
    /// </summary>
    internal void Run(PdfReadPage page, IContentVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(visitor);

        _gs = new GraphicsState();
        _gsStack.Clear();
        _textState.BeginText();
        _operands.Clear();
        _operandOverflow = false;
        _bxDepth = 0;
        _markedContentDepth = 0;
        _inTextObject = false;
        _openForms.Clear();
        _formDepth = 0;
        _formInvocations = 0;
        _contentBytesRemaining = MaxContentBytes;
        _gsFloor = 0;
        _markedContentFloor = 0;
        _bxFloor = 0;
        _ignoredGsPushes = 0;
        _ignoredMcPushes = 0;
        ContentStreamsDecoded = 0;
        _probeBytesRemaining = MaxProbeBytesPerRun;
        _probeBudgetExhausted = false;
        _probeBudgetExhaustedAtOffset = 0;
        _probeBudgetExhaustedAtObjectNumber = null;
        ProbeBytesConsumed = 0;

        var diagnostics = _reader.CreateContentDiagnosticScope();
        var pageIndex = page.Index;

        try
        {
            var buffer = BuildPageContentBuffer(page, diagnostics, pageIndex, out var soleObjectNumber);
            if (buffer.IsEmpty)
                return;

            var ctx = new StreamContext(page.Resources, soleObjectNumber);
            InterpretStream(buffer, ctx, visitor, pageIndex, diagnostics);
        }
        catch (InvalidDataException)
        {
            // The outermost guard for a malformed indirect-reference chain reached through
            // resource, XObject, or Form XObject resolution (a corrupt cross-reference offset,
            // say): PdfDocumentReader.Resolve and friends can throw here even though this
            // interpreter's own lexer and parser never do (InterpretStream's own catch already
            // handles those). Consistent with this type's own class doc promise that
            // InvalidDataException never escapes Run, and with the notify-and-continue policy
            // every other diagnostic in this channel follows.
            //
            // The exception's own Message is not forwarded: PdfObjectParser quotes the offending
            // header keyword or numeric literal whole, with no bound of its own, and a diagnostic
            // is retained for the reader's own lifetime (DiagnosticSink), so an attacker- or
            // corruption-sized token would become a comparably sized permanent allocation once per
            // (code, object, page) the sink's own dedupe key admits (#402 round 7).
            diagnostics.Report(
                PdfReaderDiagnosticCode.ContentStreamLexError,
                "The page's content could not be fully resolved: an object it references could "
                + "not be parsed.",
                pageIndex: pageIndex);
        }
        finally
        {
            // Only _operands, _gsStack and _gs itself can still hold a content-derived reference
            // once Run returns: an attacker-sized operand pushed but never consumed (no closing
            // operator at all, or one that never overwrites the GraphicsState field holding it)
            // must not stay pinned on this interpreter for the rest of its own lifetime, since an
            // interpreter reused only after a long delay, or never reused, would otherwise keep the
            // LAST Run's content alive regardless of the entry resets above (#402 round 7).
            // _openForms holds object numbers only and _operandOverflow is a bool, and both are
            // reset on entry like every other value-typed field (_bxDepth, _formDepth, the probe
            // budget); they are cleared here as well so the exit state matches the entry state
            // rather than because either can pin content. _textState.BeginText() restores the two
            // matrices §9.4.1 scopes to a text object for the same symmetry. ProbeBytesConsumed is
            // left alone: a test reads it after Run returns as telemetry, not as content-derived
            // state.
            _operands.Clear();
            _operandOverflow = false;
            _gsStack.Clear();
            _gs = new GraphicsState();
            _openForms.Clear();
            _textState.BeginText();
        }
    }

    // ── /Contents resolution and concatenation (ISO 32000-2 §7.7.3.3 Table 31) ─────────────────────

    private ReadOnlyMemory<byte> BuildPageContentBuffer(
        PdfReadPage page, DiagnosticSink diagnostics, int pageIndex, out int? soleObjectNumber)
    {
        soleObjectNumber = null;
        var raw = page.Dictionary.Get(PdfName.Contents);
        if (raw is null or PdfNull)
            return ReadOnlyMemory<byte>.Empty; // Optional; a page with no content draws nothing.

        var chunks = new List<byte[]>();
        var contributingStreams = 0;
        int? soleObjectNumberLocal = null;

        // Tracks decoded bytes charged so far in THIS loop, separate from _contentBytesRemaining
        // itself (which Concatenate below still consumes its own share of, against the full
        // per-Run budget, once the loop finishes): stopping further decodes here is what keeps a
        // /Contents array naming the same oversized stream many times from decoding, and holding
        // in memory, every one of them before Concatenate ever gets a chance to truncate. Before
        // this fix, peak heap scaled with element count x MaxDecodedStreamBytes, since every
        // element decoded in full regardless of how far over budget earlier ones already were: one
        // 20 MiB-decoding stream referenced 128 times from a 103 KB file measured at 4662 MiB peak
        // managed heap while interpreting exactly the same operators as a four-element array would
        // (#402 round 2). A single element is still bounded only by MaxDecodedStreamBytes, not by
        // this budget: GetDecodedStreamData itself throws before this method ever sees a decode
        // larger than that limit.
        var decodedSoFar = 0L;
        var budgetSpent = false;

        void AddElement(PdfObject element)
        {
            // Checked ahead of the budgetSpent short-circuit below, unlike the resolve/decode work
            // further down: a type check against an object already in hand costs nothing, so a
            // /Contents element that is not even an indirect reference gets its own 300 report
            // whether or not the budget already ran out on an earlier element, while resolving and
            // decoding (costly work, gated on the budget) stay behind the short-circuit (#402
            // round 3; the 300 doc's own "resumes with the next stream" clause covers only the
            // resolve-or-decode failure this short-circuit still gates).
            if (element is not PdfIndirectReference elementRef)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ContentStreamLexError,
                    "A /Contents element is not an indirect reference to a stream (ISO 32000-2 "
                    + "§7.7.3.3 Table 31); it was skipped.",
                    pageIndex: pageIndex);
                return;
            }

            if (budgetSpent)
                return;

            var stream = _reader.ResolveStream(elementRef);
            if (stream is null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ContentStreamLexError,
                    $"/Contents object {elementRef.ObjectNumber} does not resolve to a stream; it "
                    + "was skipped.",
                    elementRef.ObjectNumber, pageIndex: pageIndex);
                return;
            }

            byte[]? decoded;
            try
            {
                decoded = _reader.GetDecodedStreamData(stream);
            }
            catch (InvalidDataException)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ContentStreamLexError,
                    $"Content stream object {stream.ObjectNumber} failed to decode; it was skipped.",
                    stream.ObjectNumber, pageIndex: pageIndex);
                return;
            }

            if (decoded is null)
            {
                // An image filter (DCTDecode, JPXDecode, ...) in the chain is never valid on a
                // content stream, which must decode to PDF operator syntax.
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ContentStreamLexError,
                    $"Content stream object {stream.ObjectNumber} carries an image filter and "
                    + "cannot be decoded as content; it was skipped.",
                    stream.ObjectNumber, pageIndex: pageIndex);
                return;
            }

            ContentStreamsDecoded++;
            chunks.Add(decoded);
            contributingStreams++;
            soleObjectNumberLocal = contributingStreams == 1 ? stream.ObjectNumber : null;

            decodedSoFar += decoded.Length + 1L; // the separator byte Concatenate also charges below
            if (decodedSoFar > _contentBytesRemaining)
            {
                budgetSpent = true;
                diagnostics.ReportRetained(
                    PdfReaderDiagnosticCode.ContentStreamTooLarge,
                    $"The page's /Contents exceeded the {MaxContentBytes / (1024 * 1024)} MiB "
                    + "decoded-size cap shared with every Form XObject it draws; interpretation "
                    + "stopped there.",
                    pageIndex: pageIndex);
            }
        }

        if (raw is PdfArray directArray)
        {
            foreach (var element in Enumerate(directArray))
                AddElement(element);
        }
        else
        {
            // A single reference: could name a stream directly, or (nonconformant but tolerated)
            // an array object.
            var resolved = _reader.ResolveValue(raw);
            if (resolved is PdfArray arr)
            {
                foreach (var element in Enumerate(arr))
                    AddElement(element);
            }
            else
            {
                AddElement(raw);
            }
        }

        soleObjectNumber = soleObjectNumberLocal;
        var buffer = Concatenate(chunks, diagnostics, pageIndex, _contentBytesRemaining, out var truncated);
        // A truncation here already spent the whole per-Run budget (see Concatenate's own remarks
        // on why it always reports at most once): forcing the remainder to exactly zero, rather
        // than the few leftover bytes the whitespace-boundary back-off may have left unused, is
        // what keeps a later Form XObject from triggering a SECOND ContentStreamTooLarge report
        // against a different object number once this run is already over budget.
        _contentBytesRemaining = truncated ? 0 : _contentBytesRemaining - buffer.Length;
        return buffer;
    }

    private static IEnumerable<PdfObject> Enumerate(PdfArray array)
    {
        for (var i = 0; i < array.Count; i++)
            yield return array[i];
    }

    // Joins each stream's decoded bytes with a single '\n' between them, per ISO 32000-2 §7.7.3.3
    // Table 31's own text: "the division between streams may occur only at the boundaries between
    // lexical tokens". So a token split across two streams (e.g. one ending "BT" and the next
    // starting immediately with "ET") is not glued into one token by naive concatenation. Enforces
    // budget (this Run's own remaining share of MaxContentBytes) across the total, truncating at
    // the last whitespace boundary within budget rather than mid-token, so what IS interpreted is a
    // clean prefix rather than one broken by an artificial cut. Reports and sets
    // truncated = true at most once: the caller (BuildPageContentBuffer, or HandleDo for a Form
    // XObject's own decoded content) is responsible for driving the run's remaining budget to zero
    // once this returns true, so a later stream never re-triggers the report.
    private static ReadOnlyMemory<byte> Concatenate(
        List<byte[]> chunks, DiagnosticSink diagnostics, int pageIndex, long budget, out bool truncated)
    {
        truncated = false;
        if (chunks.Count == 0)
            return ReadOnlyMemory<byte>.Empty;

        long total = 0;
        foreach (var chunk in chunks)
            total += chunk.Length + 1; // +1 for the separator this method inserts after each chunk

        if (total <= budget)
        {
            var buffer = new byte[total];
            var pos = 0;
            foreach (var chunk in chunks)
            {
                chunk.CopyTo(buffer, pos);
                pos += chunk.Length;
                buffer[pos++] = (byte)'\n';
            }
            return buffer;
        }

        // Over budget: copy whole chunks while they fit, then take as much of the chunk that would
        // overflow as fits, backing off to the nearest preceding whitespace byte so the cut falls on
        // a token boundary rather than through the middle of one.
        var cappedLength = (int)Math.Min(budget, int.MaxValue);
        var capped = new byte[cappedLength];
        var written = 0;
        foreach (var chunk in chunks)
        {
            var remaining = cappedLength - written;
            if (remaining <= 0)
                break;

            if (chunk.Length + 1 <= remaining)
            {
                chunk.CopyTo(capped, written);
                written += chunk.Length;
                capped[written++] = (byte)'\n';
                continue;
            }

            var take = TruncateAtWhitespaceBoundary(chunk, Math.Min(chunk.Length, remaining));
            Array.Copy(chunk, 0, capped, written, take);
            written += take;
            break;
        }

        truncated = true;
        diagnostics.ReportRetained(
            PdfReaderDiagnosticCode.ContentStreamTooLarge,
            $"The page's /Contents exceeded the {MaxContentBytes / (1024 * 1024)} MiB decoded-size "
            + "cap shared with every Form XObject it draws; interpretation stopped there.",
            pageIndex: pageIndex);

        return new ReadOnlyMemory<byte>(capped, 0, written);
    }

    // Backs a byte-budget cut off to the nearest preceding whitespace byte, so neither Concatenate
    // (across a /Contents array) nor HandleDo (a single Form XObject's own decoded content) ever
    // cuts a token in half when the per-Run content budget runs out mid-chunk.
    private static int TruncateAtWhitespaceBoundary(ReadOnlySpan<byte> chunk, int take)
    {
        while (take > 0 && !PdfLexer.IsWhitespaceByte(chunk[take - 1]))
            take--;
        return take;
    }

    // ── Main interpretation loop ─────────────────────────────────────────────────────────────────

    /// <summary>Resources and diagnostic-attribution identity for one content stream being
    /// interpreted: the page's own for the top-level call, a Form XObject's own (falling back to
    /// its invoker's per §8.10.2) for a recursive one.</summary>
    private readonly record struct StreamContext(PdfDictionary? Resources, int? DiagObjectNumber);

    private void InterpretStream(
        ReadOnlyMemory<byte> data, StreamContext ctx, IContentVisitor visitor, int pageIndex,
        DiagnosticSink diagnostics)
    {
        var outerBuffer = _currentBuffer;
        _currentBuffer = data;
        try
        {
            var lexer = new PdfLexer(data, contentStreamMode: true);
            var parser = new PdfObjectParser(lexer);

            try
            {
                while (true)
                {
                    lexer.SkipWhitespaceAndComments();
                    if (lexer.AtEnd)
                        break;

                    var offset = lexer.Position;
                    var token = lexer.NextToken();
                    if (token.Kind == TokenKind.EndOfInput)
                        break;

                    switch (token.Kind)
                    {
                        case TokenKind.Integer or TokenKind.Real:
                            HandleNumber(token, ctx, diagnostics, pageIndex);
                            break;

                        case TokenKind.LiteralString:
                            PushOperand(
                                PdfObjectParser.DecodeLiteralString(token.Raw), ctx, diagnostics,
                                pageIndex);
                            break;

                        case TokenKind.HexString:
                            PushOperand(
                                PdfObjectParser.DecodeHexString(token.Raw), ctx, diagnostics,
                                pageIndex);
                            break;

                        case TokenKind.Name:
                            PushOperand(PdfObjectParser.ParseName(token), ctx, diagnostics, pageIndex);
                            break;

                        case TokenKind.ArrayBegin or TokenKind.DictBegin:
                            // Pre-scanned with the lexer alone (no PdfObject allocation) before
                            // ParseObject ever materialises it: an adversarial TJ array can name
                            // millions of elements in a source text small enough to decode well
                            // within the content budget, so the token-count cap has to be
                            // consulted before the allocation it exists to bound, not after (#402
                            // round 3; see CompositeOperandWithinCap and MaxCompositeOperandElements).
                            if (CompositeOperandWithinCap(lexer, out var countPassLexerFailed))
                            {
                                lexer.Seek(offset);
                                PushOperand(parser.ParseObject(), ctx, diagnostics, pageIndex);
                            }
                            else
                            {
                                _operandOverflow = true;
                                var shape = token.Kind == TokenKind.ArrayBegin ? "An array" : "A dictionary";
                                diagnostics.Report(
                                    PdfReaderDiagnosticCode.ContentLimitExceeded,
                                    $"{shape} operand exceeds {MaxCompositeOperandElements} tokens; "
                                    + "the operator taking it was dropped.",
                                    ctx.DiagObjectNumber, pageIndex: pageIndex);
                                if (countPassLexerFailed)
                                {
                                    // The count pass itself hit a malformed byte inside the
                                    // composite (an unterminated string, say) before it ever
                                    // reached the cap-comparison this branch reports 309 for; that
                                    // failure did not merely bail the count pass out early on count
                                    // alone, so it gets its own 300 too (#402 round 4: an
                                    // over-cap composite whose count pass failed this way used to
                                    // report only the 309, silently ending interpretation of the
                                    // rest of the stream with nothing to explain why, where the
                                    // identical failure on an UNDER-cap composite already reported
                                    // 300 through ParseObject's own re-parse).
                                    diagnostics.Report(
                                        PdfReaderDiagnosticCode.ContentStreamLexError,
                                        "The content stream's syntax could not be interpreted past "
                                        + "this point; interpretation of it stopped here.",
                                        ctx.DiagObjectNumber, pageIndex: pageIndex);
                                }
                            }
                            break;

                        case TokenKind.Keyword:
                            {
                                var raw = token.Raw.Span;
                                if (raw.SequenceEqual("true"u8))
                                    PushOperand(PdfBoolean.True, ctx, diagnostics, pageIndex);
                                else if (raw.SequenceEqual("false"u8))
                                    PushOperand(PdfBoolean.False, ctx, diagnostics, pageIndex);
                                else if (raw.SequenceEqual("null"u8))
                                    PushOperand(PdfNull.Instance, ctx, diagnostics, pageIndex);
                                else if (raw.SequenceEqual("BI"u8))
                                {
                                    if (!HandleInlineImage(lexer, parser, ctx, visitor, diagnostics, pageIndex, offset))
                                        goto endOfStream;
                                }
                                else
                                {
                                    // Decoded only far enough to name the operator or, for an
                                    // unrecognised one, excerpt it in the 301 below (DiagnosticExcerpt.Quote
                                    // truncates past DiagnosticExcerpt.MaxChars anyway); ReadKeyword puts
                                    // no bound on a keyword's own length, so materialising the
                                    // whole thing here for an attacker-sized token would allocate
                                    // what the diagnostic then discards most of (#402 round 6).
                                    // HandleOperator's own dispatch below goes through
                                    // ContentOperators.IsKnown(string), a bare dictionary lookup
                                    // with no length guard of its own (only the ReadOnlySpan<byte>
                                    // overload the resync probe uses, in ContentOperators.cs, bails
                                    // out past 8 bytes); a keyword truncated to DiagnosticExcerpt.MaxChars
                                    // + 1 bytes still fails that lookup exactly the way the whole
                                    // one did, since no key in the table is longer than 3 characters,
                                    // and every recognised operator is decoded in full either way.
                                    var decodeLength = Math.Min(raw.Length, DiagnosticExcerpt.MaxChars + 1);
                                    var name = System.Text.Encoding.Latin1.GetString(raw[..decodeLength]);
                                    HandleOperator(
                                        name, raw.Length, offset, ctx, visitor, diagnostics, pageIndex);
                                }
                                break;
                            }

                        default:
                            throw new InvalidDataException(
                                $"Unexpected token {token.Kind} at content-stream offset {offset}.");
                    }
                }
            endOfStream:;
            }
            catch (InvalidDataException)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ContentStreamLexError,
                    "The content stream's syntax could not be interpreted past this point; "
                    + "interpretation of it stopped here.",
                    ctx.DiagObjectNumber, pageIndex: pageIndex);
            }
        }
        finally
        {
            _currentBuffer = outerBuffer;
        }
    }

    // ── Operand collection ───────────────────────────────────────────────────────────────────────

    private void HandleNumber(Token token, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (TryParseOperandNumber(token.Raw.Span, token.Kind == TokenKind.Real, out var value))
        {
            PushOperand(value!, ctx, diagnostics, pageIndex);
            return;
        }

        diagnostics.Report(
            PdfReaderDiagnosticCode.OperandStackMalformed,
            "A numeric operand did not parse, or was not finite; it was dropped.",
            ctx.DiagObjectNumber, pageIndex: pageIndex);
    }

    // System.Buffers.Text.Utf8Parser backs this, but against a normalised copy of the token's bytes,
    // not the raw span, because PDF's own numeric grammar (ISO 32000-2 §7.3.3) allows a bare leading
    // or trailing decimal point ("-.5", "6.") that the BCL's own double formats do not universally
    // accept the same way across runtimes; padding a missing digit on either side of '.' sidesteps
    // that without reimplementing number parsing.
    private static bool TryParseOperandNumber(ReadOnlySpan<byte> raw, bool isReal, out PdfObject? result)
    {
        result = null;
        if (raw.IsEmpty)
            return false;

        var negative = false;
        var span = raw;
        if (span[0] is (byte)'+' or (byte)'-')
        {
            negative = span[0] == (byte)'-';
            span = span[1..];
        }

        // §7.3.3 allows "an optional sign" (singular). A second sign character immediately after
        // the first ("--5", "-+5") is not this grammar's syntax; rejecting it here, rather than
        // letting the digit scan below fail on it less directly, keeps the failure attributable to
        // the actual malformation instead of a coincidentally-empty digit run.
        if (!span.IsEmpty && span[0] is (byte)'+' or (byte)'-')
            return false;

        if (!isReal)
        {
            if (span.IsEmpty || !Utf8Parser.TryParse(span, out long value, out var consumed) || consumed != span.Length)
                return false;
            result = new PdfInteger(negative ? -value : value);
            return true;
        }

        // The token length is attacker-controlled, so only stackalloc for short literals; an
        // operand of about 1.5 million digits or more would otherwise overflow the stack (an
        // uncatchable crash); one million digits alone still returns normally.
        var paddedLength = span.Length + 2;
        Span<byte> padded = paddedLength <= 1024 ? stackalloc byte[paddedLength] : new byte[paddedLength];
        var len = 0;
        if (span.IsEmpty || span[0] == (byte)'.')
            padded[len++] = (byte)'0';
        span.CopyTo(padded[len..]);
        len += span.Length;
        if (len == 0 || padded[len - 1] == (byte)'.')
            padded[len++] = (byte)'0';

        if (!Utf8Parser.TryParse(padded[..len], out double d, out var consumedReal) || consumedReal != len)
            return false;
        if (negative)
            d = -d;
        if (!double.IsFinite(d))
            return false;

        result = new PdfReal(d);
        return true;
    }

    private void PushOperand(PdfObject value, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_operandOverflow)
            return;

        if (_operands.Count >= MaxOperandsPerOperator)
        {
            _operandOverflow = true;
            diagnostics.Report(
                PdfReaderDiagnosticCode.ContentLimitExceeded,
                $"More than {MaxOperandsPerOperator} operands accumulated before an operator; the "
                + "next operator was dropped.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        _operands.Add(value);
    }

    private void ClearOperands()
    {
        _operands.Clear();
        _operandOverflow = false;
    }

    // Pre-scans an array or dictionary operand with the LEXER ALONE, starting right after its
    // already-consumed opening token, to decide whether it stays within
    // MaxCompositeOperandElements before PdfObjectParser ever allocates a PdfObject for it. Every
    // token inside the composite counts, at any depth, because every one of them becomes an
    // allocation once materialised; closing delimiters are the exception, since they allocate
    // nothing. When the composite terminates cleanly, this leaves the lexer positioned right after
    // the matching close, so the caller can either seek back to re-parse it (within cap) or move on
    // to the next token (over cap: nothing more from this composite is needed); an
    // unterminated composite (see lexerFailed below) leaves the lexer wherever the failed token
    // left it instead, which is NOT necessarily right after any close (#402 round 4: qualifying
    // this to the terminated case, since the unterminated one two paragraphs below already
    // contradicted an unqualified claim here). An unterminated composite is judged by the same
    // count: within the cap, the caller's ParseObject re-derives the failure and reports it
    // (ContentStreamLexError, 300) the way it always has; over the cap, lexerFailed tells the
    // caller to report that same 300 directly, since nothing will re-parse this composite to
    // derive it the way the within-cap path does (#402 round 4: over the cap used to report only
    // ContentLimitExceeded, silently dropping the fact that the composite was ALSO malformed and
    // not merely oversized).
    private static bool CompositeOperandWithinCap(PdfLexer lexer, out bool lexerFailed)
    {
        var depth = 1;
        var count = 0;
        lexerFailed = false;
        while (depth > 0)
        {
            Token token;
            try
            {
                token = lexer.NextToken();
            }
            catch (InvalidDataException)
            {
                lexerFailed = true;
                break;
            }

            if (token.Kind == TokenKind.EndOfInput)
                break;

            switch (token.Kind)
            {
                case TokenKind.ArrayBegin or TokenKind.DictBegin:
                    count++;
                    depth++;
                    break;

                case TokenKind.ArrayEnd or TokenKind.DictEnd:
                    depth--;
                    break;

                default:
                    count++;
                    break;
            }
        }
        return count <= MaxCompositeOperandElements;
    }

    // ── Operator dispatch ────────────────────────────────────────────────────────────────────────

    private void HandleOperator(
        string name, int keywordByteLength, int offset, StreamContext ctx, IContentVisitor visitor,
        DiagnosticSink diagnostics, int pageIndex)
    {
        if (!ContentOperators.IsKnown(name))
        {
            // Reported only outside a BX/EX compatibility section. Inside one, Table 33 is
            // explicit: "Unrecognised operators (along with their operands) shall be ignored
            // without error until the balancing EX operator is encountered." Outside one, §7.8.2
            // says "an error shall occur"; this reader instead notifies and continues, the same
            // notify-and-continue choice every other diagnostic in this channel makes. The sink's
            // dedupe key is (code, object, page), so only the first unknown name on a page is
            // recorded; a second distinct name on the same page is dropped by the sink.
            //
            // Either way, the operand stack is cleared: §7.8.2 "operands shall not be left over
            // when an operator finishes execution" applies to an unrecognised keyword's own
            // (no-op) execution just as much as to a recognised one (#402 round 2; an earlier
            // version kept the operands outside BX/EX on the theory that a stray "R" left over
            // from indirect-reference syntax §7.8.2 forbids in content streams usually belonged to
            // whatever OPERATOR followed rather than to "R" itself, but that leniency broke a
            // differently-shaped input just as easily: '10 20 Zork' ahead of '1 w' silently fed
            // the leftover 20 into 'w' as its own operand instead of 1).
            if (_bxDepth <= 0)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.UnknownOperator,
                    $"'{DiagnosticExcerpt.Quote(name, keywordByteLength)}' is not one of the operators ISO "
                    + "32000-2 Annex A Table A.1 defines; it was ignored.",
                    pageIndex: pageIndex);
            }
            ClearOperands();
            return;
        }

        // BX/EX's own arity (0) is checked the same way as every other operator's below, unlike the
        // dispatch this used to short-circuit through before any check ran at all: '1 2 3 BX 4 EX'
        // used to hand the visitor BX with 3 leftover operands and EX with 1, reporting nothing even
        // though _arity["BX"] is 0 (#402 round 3). A mismatch here still only drops the OPERANDS,
        // not the operator itself: Table 33 opens or closes a compatibility section regardless of
        // what garbage operands preceded BX/EX, so the section transition below still runs either
        // way, unlike an ordinary operator's mismatch, which drops the whole call.
        var expected = ContentOperators.ExpectedOperandCount(name);
        var arityOk = true;
        if (_operandOverflow)
        {
            arityOk = false;
            // A 'q', 'BMC', or 'BDC' dropped here for one of this reader's own ceilings (an
            // over-cap composite operand, or the 64-operand-per-operator cap in PushOperand) is
            // still credited toward its own matching pop, the same as an over-DEPTH push already
            // is in PushGraphicsState/PushMarkedContent: §14.6.2 puts no size bound on a property
            // list, so a producer whose BDC's own dictionary operand happens to exceed this
            // reader's own MaxCompositeOperandElements, and who then balances that BDC with an
            // EMC, must not ALSO be accused of an unbalanced EMC purely because this reader
            // declined to push the marked-content nesting it was asked to (#402 round 4). An
            // arity-mismatch drop (e.g. '1 q') is producer-side, not this reader's own ceiling, so
            // it keeps reporting through the branch below instead.
            switch (name)
            {
                case "q":
                    _ignoredGsPushes++;
                    break;
                case "BMC" or "BDC":
                    _ignoredMcPushes++;
                    break;
            }
            ClearOperands(); // Already reported when the overflow itself happened.
        }
        else if (expected != ContentOperators.Variable && _operands.Count != expected)
        {
            arityOk = false;
            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
                $"'{name}' expects {expected} operand(s) but {_operands.Count} were on the stack; "
                + "it was dropped.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            ClearOperands();
        }

        if (name is "BX")
        {
            _bxDepth++;
            EmitAndClear(name, offset, visitor);
            return;
        }
        if (name is "EX")
        {
            if (_bxDepth > _bxFloor)
                _bxDepth--;
            EmitAndClear(name, offset, visitor);
            return;
        }

        if (!arityOk)
            return;

        // ISO 32000-2 §7.8.2: "Dictionaries shall be permitted as operands only by certain specific
        // operators". BDC and DP (§14.6.2) are the only two in Annex A Table A.1 that take one.
        if (name is not ("BDC" or "DP"))
        {
            foreach (var operand in _operands)
            {
                if (operand is not PdfDictionary)
                    continue;
                diagnostics.Report(
                    PdfReaderDiagnosticCode.OperandStackMalformed,
                    $"'{name}' does not accept a dictionary operand (ISO 32000-2 §7.8.2); it was "
                    + "dropped.",
                    ctx.DiagObjectNumber, pageIndex: pageIndex);
                ClearOperands();
                return;
            }
        }

        // A numeric, name, or string operand this interpreter reads for its OWN state, or reads to
        // look up a resource on its own behalf (cm/Tf/Td/'/"/gs/cs/CS/sh/Do/etc. below), used to be
        // handed to NumberOperand or a bare pattern match unchecked: a wrongly typed operand at the
        // right arity silently substituted 0, silently no-oped a resource lookup, or (for Do)
        // silently dropped the invocation, with no diagnostic at all (#402 round 3; the resource-
        // lookup operators gs/cs/CS/sh joined this check in round 4, since ValidateNamedResource and
        // ValidateColorSpaceResource already read _operands[0] as a name and silently no-op on a
        // wrong type otherwise). An operator this interpreter only forwards to the visitor untouched
        // (w, J, the colour-setting operators, ...) is exempt: its own operand types are the
        // visitor's to type-check, not this interpreter's, since this interpreter never reads them
        // for its own state or its own resource lookups.
        if (!ValidateOperandTypes(name, ctx, diagnostics, pageIndex))
            return;

        switch (name)
        {
            case "TJ":
                if (_operands[0] is not PdfArray)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.OperandStackMalformed,
                        "TJ's operand is not an array; it was dropped.",
                        ctx.DiagObjectNumber, pageIndex: pageIndex);
                    ClearOperands();
                    return;
                }
                break;

            case "q":
                PushGraphicsState(ctx, diagnostics, pageIndex);
                break;

            case "Q":
                PopGraphicsState(ctx, diagnostics, pageIndex);
                break;

            case "cm":
                _gs.Ctm = new Matrix(
                    NumberOperand(0), NumberOperand(1), NumberOperand(2), NumberOperand(3),
                    NumberOperand(4), NumberOperand(5)).Concat(_gs.Ctm);
                break;

            case "BT":
                _textState.BeginText();
                _inTextObject = true;
                break;

            case "ET":
                _inTextObject = false;
                break;

            case "Tc":
                _gs.CharSpacing = NumberOperand(0);
                break;

            case "Tw":
                _gs.WordSpacing = NumberOperand(0);
                break;

            case "Tz":
                _gs.HorizontalScaling = NumberOperand(0);
                break;

            case "TL":
                _gs.Leading = NumberOperand(0);
                break;

            case "Tf":
                ValidateFontResource(ctx, diagnostics, pageIndex);
                _gs.Font = _operands[0];
                _gs.FontSize = NumberOperand(1);
                break;

            case "Tr":
                _gs.RenderMode = (int)NumberOperand(0);
                break;

            case "Ts":
                _gs.Rise = NumberOperand(0);
                break;

            case "Td":
                _textState.MoveTextPosition(NumberOperand(0), NumberOperand(1));
                break;

            case "TD":
                {
                    var ty = NumberOperand(1);
                    _gs.Leading = -ty;
                    _textState.MoveTextPosition(NumberOperand(0), ty);
                    break;
                }

            case "Tm":
                _textState.SetTextMatrix(new Matrix(
                    NumberOperand(0), NumberOperand(1), NumberOperand(2), NumberOperand(3),
                    NumberOperand(4), NumberOperand(5)));
                break;

            case "T*":
                _textState.MoveTextPosition(0, -_gs.Leading);
                break;

            case "'":
                // Table 107: "This operator shall have the same effect as the code T* string Tj".
                // T*'s own move (§9.4.3) runs here; the text-showing half stays the visitor's, the
                // same as Tj's own string operand, which this interpreter never reads (#402 round 4).
                _textState.MoveTextPosition(0, -_gs.Leading);
                break;

            case "\"":
                // Table 107: "This operator shall have the same effect as this code: aw Tw ac Tc
                // string '". aw and ac land in the text state before the T*-equivalent move that
                // "'" itself performs (#402 round 4).
                _gs.WordSpacing = NumberOperand(0);
                _gs.CharSpacing = NumberOperand(1);
                _textState.MoveTextPosition(0, -_gs.Leading);
                break;

            case "BDC" or "BMC":
                PushMarkedContent(ctx, diagnostics, pageIndex);
                break;

            case "EMC":
                PopMarkedContent(ctx, diagnostics, pageIndex);
                break;

            case "cs" or "CS":
                ValidateColorSpaceResource(name, ctx, diagnostics, pageIndex);
                break;

            case "sh":
                ValidateNamedResource(name, ShadingKey, ctx, diagnostics, pageIndex);
                break;

            case "gs":
                HandleExtGState(ctx, diagnostics, pageIndex);
                break;

            case "Do":
                HandleDo(offset, ctx, visitor, diagnostics, pageIndex);
                return; // HandleDo emits "Do" itself before recursing.

            default:
                break; // Recognised but state-inert: path, colour, clipping, rendering operators.
        }

        EmitAndClear(name, offset, visitor);
    }

    private void EmitAndClear(string name, int offset, IContentVisitor visitor)
    {
        visitor.OnOperator(name, _operands, offset);
        ClearOperands();
    }

    private double NumberOperand(int index) => _operands[index] switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => 0,
    };

    private static bool IsNumericOperand(PdfObject obj) => obj is PdfInteger or PdfReal;

    // Type-checks the operands of every operator this interpreter reads for its own graphics or
    // text state, or for its own resource lookup, ahead of the switch below (or, for gs/cs/CS/sh,
    // the resource-lookup helpers) that read them: by the time this runs, the arity check above
    // already guarantees _operands.Count matches each name's own Table A.1 arity, so only the TYPE
    // of each operand is in question here. A mismatch reports OperandStackMalformed (the same code
    // the 302 doc already covers "an operand of the wrong type where the arity is otherwise right"
    // under) and drops the operator entirely, the way the dictionary-operand check just above does,
    // rather than letting NumberOperand's own 0-default or a silent Do/gs/cs/CS/sh no-op mask the
    // malformation (#402 round 3; gs/cs/CS/sh joined this method in round 4).
    private bool ValidateOperandTypes(string name, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        bool ok;
        switch (name)
        {
            case "cm" or "Tm":
                ok = IsNumericOperand(_operands[0]) && IsNumericOperand(_operands[1])
                    && IsNumericOperand(_operands[2]) && IsNumericOperand(_operands[3])
                    && IsNumericOperand(_operands[4]) && IsNumericOperand(_operands[5]);
                break;

            case "Tc" or "Tw" or "Tz" or "TL" or "Tr" or "Ts":
                ok = IsNumericOperand(_operands[0]);
                break;

            case "Td" or "TD":
                ok = IsNumericOperand(_operands[0]) && IsNumericOperand(_operands[1]);
                break;

            case "Tf":
                ok = _operands[0] is PdfName && IsNumericOperand(_operands[1]);
                break;

            case "Do":
                ok = _operands[0] is PdfName;
                break;

            case "'":
                ok = _operands[0] is PdfLiteralString or PdfHexString;
                break;

            case "\"":
                ok = IsNumericOperand(_operands[0]) && IsNumericOperand(_operands[1])
                    && _operands[2] is PdfLiteralString or PdfHexString;
                break;

            // Table 73 (§8.6.8): CS/cs's own operand is "name". Table 56 (§8.4.4): gs's own operand
            // is "dictName". Table 76 (§8.7.4.2): sh's own operand is "name". All three are read for
            // a resource lookup below (ValidateColorSpaceResource/HandleExtGState/ValidateNamedResource),
            // the same reason Do's own name operand is checked here rather than left to the visitor.
            case "cs" or "CS" or "gs" or "sh":
                ok = _operands[0] is PdfName;
                break;

            default:
                return true; // Forwarded to the visitor untouched; not this interpreter's to check.
        }

        if (ok)
            return true;

        diagnostics.Report(
            PdfReaderDiagnosticCode.OperandStackMalformed,
            $"'{name}' operand is the wrong type for the arity ISO 32000-2 Annex A Table A.1 gives "
            + "it; it was dropped.",
            ctx.DiagObjectNumber, pageIndex: pageIndex);
        ClearOperands();
        return false;
    }

    // ── q/Q, BMC/BDC/EMC ─────────────────────────────────────────────────────────────────────────

    private void PushGraphicsState(StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_gsStack.Count >= MaxGraphicsStateDepth)
        {
            // The push itself is dropped, but the 'q' it belongs to is still credited toward a
            // LATER 'Q': nothing in §7.8.2 or §8.4.4 bounds how deep a legitimate document nests
            // 'q'/'Q', so a producer that nests past this reader's own ceiling and then balances
            // every one of those nests must not ALSO be accused of an unbalanced 'Q' purely
            // because this reader declined to push the state it was asked to (#402 round 2; without
            // this credit, 65 balanced 'q'...'Q' pairs reported ContentLimitExceeded AND
            // OperandStackMalformed together, and desynchronised GraphicsState.Ctm from the actual
            // nesting besides).
            _ignoredGsPushes++;
            diagnostics.Report(
                PdfReaderDiagnosticCode.ContentLimitExceeded,
                $"The graphics-state stack exceeded {MaxGraphicsStateDepth} nested 'q' saves; "
                + "further saves were ignored.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }
        _gsStack.Push(_gs);
        _gs = _gs.Clone();
    }

    private void PopGraphicsState(StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_gsStack.Count <= _gsFloor)
        {
            // A pop with nothing left on the stack to restore first spends a credit from an earlier
            // over-cap push (see PushGraphicsState) before it may report an unbalanced 'Q': only
            // once that credit is exhausted is this the "restore with nothing to restore" problem
            // the report below describes (#402 round 2).
            if (_ignoredGsPushes > 0)
            {
                _ignoredGsPushes--;
                return;
            }

            // An unbalanced 'q' still open at end of stream is fine (nothing downstream needs the
            // state restored past the last operator this interpreter saw); an unbalanced 'Q' is the
            // opposite problem (a restore with nothing to restore), so this one is reported. Inside
            // a Form XObject's own content, _gsFloor is that form's own entry depth (see HandleDo),
            // so a form's own 'Q' can pop no further than where the form started, and a 'Q' the
            // page itself already owns is never available for the form's content to pop.
            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
                "'Q' with no matching 'q' on the graphics-state stack; it was ignored.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }
        _gs = _gsStack.Pop();
    }

    private void PushMarkedContent(StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_markedContentDepth >= MaxMarkedContentDepth)
        {
            // See PushGraphicsState's own remarks: the same over-cap-push credit, tracked
            // separately here since marked-content nesting and the graphics-state stack are
            // independent depths.
            _ignoredMcPushes++;
            diagnostics.Report(
                PdfReaderDiagnosticCode.ContentLimitExceeded,
                $"Marked-content nesting exceeded {MaxMarkedContentDepth} levels; further "
                + "'BMC'/'BDC' operators were ignored.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }
        _markedContentDepth++;
    }

    private void PopMarkedContent(StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_markedContentDepth <= _markedContentFloor)
        {
            // See PopGraphicsState's own remarks: spend an over-cap push credit before reporting.
            if (_ignoredMcPushes > 0)
            {
                _ignoredMcPushes--;
                return;
            }

            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
                "'EMC' with no matching 'BMC'/'BDC'; it was ignored.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }
        _markedContentDepth--;
    }

    // ── Resource lookups (ISO 32000-2 §7.8.3) ───────────────────────────────────────────────────

    private static readonly PdfName ShadingKey = PdfName.Shading;

    private static readonly HashSet<string> _standaloneColorSpaceNames =
        new(StringComparer.Ordinal) { "DeviceGray", "DeviceRGB", "DeviceCMYK", "Pattern" };

    private void ValidateFontResource(StreamContext ctx, DiagnosticSink diagnostics, int pageIndex) =>
        ValidateNamedResource("Tf", PdfName.Font, ctx, diagnostics, pageIndex);

    private void ValidateColorSpaceResource(
        string op, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_operands[0] is not PdfName csName || _standaloneColorSpaceNames.Contains(csName.Value))
            return; // §8.6.3: the four device/pattern spaces are never resource-dictionary entries.
        ValidateNamedResource(op, PdfName.ColorSpace, ctx, diagnostics, pageIndex);
    }

    private void ValidateNamedResource(
        string op, PdfName category, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_operands.Count == 0 || _operands[0] is not PdfName name)
            return;

        if (ctx.Resources is not null && TryGetResource(ctx.Resources, category, name, out _))
            return;

        diagnostics.Report(
            PdfReaderDiagnosticCode.ResourceMissing,
            $"'{op}' names '/{DiagnosticExcerpt.Quote(name.Value)}', absent from the applicable /Resources "
            + $"/{category.Value} dictionary.",
            ctx.DiagObjectNumber, pageIndex: pageIndex);
    }

    private bool TryGetResource(PdfDictionary resources, PdfName category, PdfName name, out PdfObject value)
    {
        value = PdfNull.Instance;
        var categoryRaw = resources.Get(category);
        if (categoryRaw is null)
            return false;
        if (_reader.ResolveValue(categoryRaw) is not PdfDictionary categoryDict)
            return false;
        var raw = categoryDict.Get(name);
        if (raw is null or PdfNull)
            return false;
        value = raw;
        return true;
    }

    // ── gs (ISO 32000-2 §8.4.5 Table 57) ────────────────────────────────────────────────────────

    private void HandleExtGState(StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_operands[0] is not PdfName gsName)
            return;

        if (ctx.Resources is null || !TryGetResource(ctx.Resources, PdfName.ExtGState, gsName, out var raw))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'gs' names '/{DiagnosticExcerpt.Quote(gsName.Value)}', absent from the applicable /Resources "
                + "/ExtGState dictionary.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        if (_reader.ResolveValue(raw) is not PdfDictionary extGState)
            return;

        // Table 57: /Font is "an array of the form [font size] where font shall be an indirect
        // reference to a font dictionary". §7.3.10 lets any dictionary entry be given as an
        // indirect reference, not only the ones a table says must be one, so /Font's own value is
        // resolved before the shape check the same way a form XObject's /Matrix and /BBox are.
        // Every other ExtGState entry is out of scope for this interpreter: it neither positions
        // text nor renders colour or transparency.
        if (extGState.Get(FontKey) is { } fontRaw && _reader.ResolveValue(fontRaw) is PdfArray fontArray
            && fontArray.Count == 2)
        {
            _gs.Font = fontArray[0];
            _gs.FontSize = ReadNumber(fontArray[1]);
        }
    }

    private static double ReadNumber(PdfObject obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => 0,
    };

    // ── Do / Form XObjects (ISO 32000-2 §8.10) ──────────────────────────────────────────────────

    private void HandleDo(
        int offset, StreamContext ctx, IContentVisitor visitor, DiagnosticSink diagnostics, int pageIndex)
    {
        var xobjectNameOperand = _operands.Count == 1 ? _operands[0] : null;
        EmitAndClear("Do", offset, visitor);

        if (_inTextObject)
        {
            // §8.2 Figure 9 admits only the general graphics state, colour, text state,
            // text-positioning, text-showing, marked-content, and compatibility categories of
            // Table 50 inside a text object (seven, not six: #402 round 3); 'Do' sits in the
            // XObjects category, so a producer that invokes it there is wrong regardless of what
            // the named XObject turns out to be. The recursion below still runs (a text-object
            // violation is not itself a reason to skip an otherwise-resolvable Form), but the
            // shared _textState instance a form's own content may disturb (BT/Td/ET, unbracketed by
            // any q/Q-style save) is saved and restored around that recursion below regardless of
            // this check, so the report here is purely informational: nothing downstream depends on
            // _inTextObject to avoid the leak (#402 round 2).
            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
                "'Do' occurred inside a text object (ISO 32000-2 §8.2 Figure 9 admits no XObjects "
                + "category operator there).",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
        }

        // Defensive only: ValidateOperandTypes (~:839) already guarantees a one-element, PdfName
        // operand for 'Do' by the time HandleOperator dispatches here (#402 round 4).
        if (xobjectNameOperand is not PdfName xobjectName)
            return;

        if (ctx.Resources is null || !TryGetResource(ctx.Resources, PdfName.XObject, xobjectName, out var entryRaw))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{DiagnosticExcerpt.Quote(xobjectName.Value)}', absent from the applicable "
                + "/Resources /XObject dictionary.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        if (entryRaw is not PdfIndirectReference xobjectRef)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{DiagnosticExcerpt.Quote(xobjectName.Value)}', present in the applicable "
                + "/Resources /XObject dictionary but not as an indirect reference to a stream.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        var stream = _reader.ResolveStream(xobjectRef);
        if (stream is null)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{DiagnosticExcerpt.Quote(xobjectName.Value)}', but object "
                + $"{xobjectRef.ObjectNumber} does not resolve to a stream.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        if (stream.Dictionary.Get(PdfName.Subtype) is not PdfName subtype)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{DiagnosticExcerpt.Quote(xobjectName.Value)}', object {stream.ObjectNumber}, "
                + "whose /Subtype is missing or is not a name, so it cannot be used as an XObject.",
                stream.ObjectNumber, pageIndex: pageIndex);
            return;
        }

        if (subtype.Equals(XObjectSubtypeImage))
            return; // An Image XObject: no recursion; the caller already got Do.

        if (!subtype.Equals(XObjectSubtypeForm))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{DiagnosticExcerpt.Quote(xobjectName.Value)}', object {stream.ObjectNumber}, "
                + $"whose /Subtype '/{DiagnosticExcerpt.Quote(subtype.Value)}' is neither /Form nor /Image, "
                + "so it cannot be used as an XObject.",
                stream.ObjectNumber, pageIndex: pageIndex);
            return;
        }

        var objectNumber = stream.ObjectNumber;

        if (_formInvocations >= MaxFormInvocationsPerPage)
        {
            diagnostics.ReportRetained(
                PdfReaderDiagnosticCode.FormXObjectBudgetExceeded,
                $"The page invoked more than {MaxFormInvocationsPerPage} Form XObjects; further "
                + "'Do' recursions were skipped for the rest of the page.",
                pageIndex: pageIndex);
            return;
        }

        // The cycle check runs BEFORE the depth cap: with MaxFormXObjectDepth set low (1, say), a
        // self-referencing form would otherwise hit the depth cap first and report
        // FormXObjectDepthExceeded, which is technically true but strictly less informative than
        // FormXObjectCycle, the code that names the actual reason recursion cannot continue
        // (#402 round 2).
        if (_openForms.Contains(objectNumber))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.FormXObjectCycle,
                $"Form XObject {objectNumber} invokes itself, directly or through a chain of nested "
                + "'Do' operators; the recursive invocation was skipped.",
                objectNumber, pageIndex: pageIndex);
            return;
        }

        if (_formDepth >= _limits.MaxFormXObjectDepth)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.FormXObjectDepthExceeded,
                $"Form XObject recursion exceeded {_limits.MaxFormXObjectDepth} levels; this 'Do' "
                + "was not followed.",
                objectNumber, pageIndex: pageIndex);
            return;
        }

        _openForms.Add(objectNumber);
        // Counts every 'Do' that reaches this point, i.e. every invocation past the recursion
        // guards above, not only one whose content goes on to decode successfully (the 305 doc
        // says so; #402 round 3 fixed the doc to match, since this counter itself already counted
        // this way from the start).
        _formInvocations++;
        _formDepth++;
        try
        {
            var formDict = stream.Dictionary;
            var matrix = ReadFormMatrix(formDict);
            var bbox = ReadFormBBox(formDict);
            // §8.10.2: a form's /Resources is optional but strongly recommended; when absent, the
            // invoking content stream's own resources apply.
            var formResources = ResolveDictionaryEntry(formDict, PdfName.Resources) ?? ctx.Resources;

            visitor.OnFormBegin(formDict, matrix, bbox, objectNumber, offset);
            try
            {
                // Checked BEFORE decoding, not after: decoding this invocation's content only to
                // discard it once the budget already stood at zero still pays the full decode cost
                // (allocation, filter work) for nothing, every single invocation. Against an 8
                // MiB-decoding form drawn 256 times from a 43 KB file, checking after the decode
                // measured 6.9 GiB allocated once the budget was already spent on the first few
                // invocations; at the 4096-invocation cap with a 512 MiB form, roughly 2 TiB
                // (#402 round 2). Every invocation of a form still counts its bytes again against
                // this Run's own shared budget when it DOES decode: the cost being bounded is
                // interpretation WORK, and a form drawn many times is interpreted that many times,
                // not decoded-and-cached once.
                byte[]? decoded = null;
                if (_contentBytesRemaining > 0)
                {
                    try
                    {
                        decoded = _reader.GetDecodedStreamData(stream);
                    }
                    catch (InvalidDataException)
                    {
                        diagnostics.Report(
                            PdfReaderDiagnosticCode.ContentStreamLexError,
                            $"Form XObject {objectNumber}'s content stream failed to decode.",
                            objectNumber, pageIndex: pageIndex);
                        decoded = null;
                    }

                    if (decoded is not null)
                    {
                        ContentStreamsDecoded++;
                        if (decoded.Length > _contentBytesRemaining)
                        {
                            var take = TruncateAtWhitespaceBoundary(
                                decoded, (int)Math.Min(decoded.Length, _contentBytesRemaining));
                            decoded = decoded.AsSpan(0, take).ToArray();
                            diagnostics.ReportRetained(
                                PdfReaderDiagnosticCode.ContentStreamTooLarge,
                                $"Form XObject {objectNumber}'s content pushed this Run's combined "
                                + $"page-and-forms budget past {MaxContentBytes / (1024 * 1024)} MiB; "
                                + "interpretation of it stopped there.",
                                objectNumber, pageIndex: pageIndex);
                            // See BuildPageContentBuffer's own remark on why this is forced to
                            // exactly zero rather than left at whatever the whitespace back-off did
                            // not use.
                            _contentBytesRemaining = 0;
                        }
                        else
                        {
                            _contentBytesRemaining -= decoded.Length;
                        }
                    }
                }

                if (decoded is not null)
                {
                    var formCtx = new StreamContext(formResources, objectNumber);

                    // ISO 32000-2 §8.10.1: Do on a form XObject "a) Saves the current graphics
                    // state, as if by invoking the q operator" before interpreting the form's own
                    // content, and "e) Restores [it], as if by invoking the Q operator" once done.
                    // Implemented with floors rather than an actual _gsStack.Push/Pop so the
                    // implicit save does not itself consume the invoker's own
                    // MaxGraphicsStateDepth budget, and the form's own content cannot 'Q' past its
                    // own entry point back into the invoker's own saves. Marked-content nesting
                    // (§14.6.1) and BX/EX depth get the same bracketing: nothing a form does to
                    // either may leak into the invoker once Do returns. The over-cap push credits
                    // (_ignoredGsPushes/_ignoredMcPushes) are reset to 0 for the form's own scope
                    // and restored afterward too, so neither side of the boundary can consume a
                    // credit that belongs to the other's own over-cap pushes (#402 round 2).
                    //
                    // §9.4.1's text matrices (_textState) are ALSO saved and restored here, even
                    // though 'Do' is not itself a text-showing operator and §8.2 Figure 9 admits no
                    // XObjects-category operator inside a text object at all (see the
                    // _inTextObject check above): that only says the INVOKING content is wrong
                    // to call 'Do' from inside a text object, not that the form's OWN content
                    // cannot open its own, entirely independent text object (a 'BT' resets
                    // TextMatrix/TextLineMatrix unconditionally, with no check against whatever the
                    // invoker's own text state happened to be). Nothing else brackets _textState
                    // off from a form's content the way the floors above do for the graphics and
                    // marked-content stacks, so without this save/restore a form whose own content
                    // opens and moves a text object (even one that itself never leaves it open,
                    // e.g. 'BT 999 888 Td ET') silently overwrote the invoker's own TextMatrix once
                    // Do returned, with no diagnostic at all (#402 round 2).
                    var savedGs = _gs;
                    var savedGsStackCount = _gsStack.Count;
                    var savedMarkedContentDepth = _markedContentDepth;
                    var savedBxDepth = _bxDepth;
                    var savedGsFloor = _gsFloor;
                    var savedMarkedContentFloor = _markedContentFloor;
                    var savedBxFloor = _bxFloor;
                    var savedIgnoredGsPushes = _ignoredGsPushes;
                    var savedIgnoredMcPushes = _ignoredMcPushes;
                    var savedTextMatrix = _textState.TextMatrix;
                    var savedTextLineMatrix = _textState.TextLineMatrix;
                    var savedInTextObject = _inTextObject;

                    _gsFloor = savedGsStackCount;
                    _markedContentFloor = savedMarkedContentDepth;
                    _ignoredGsPushes = 0;
                    _ignoredMcPushes = 0;
                    // Unlike the graphics-state and marked-content floors just above, BX/EX depth
                    // does not carry the invoker's own state into the form's content: Table 33
                    // scopes one compatibility section to ONE content stream, and a form is its own
                    // content stream, so it has nothing of the invoker's BX/EX nesting to inherit or
                    // protect against. Both start at 0 here rather than at the invoker's current
                    // depth (#402 round 3; without this, a form invoked from inside 'BX ... Do ...
                    // EX' inherited _bxDepth > 0 from the invoker, which made the form's own unknown
                    // operators look like they were still inside the INVOKER's compatibility
                    // section and silently swallowed them, even though the same form invoked as a
                    // bare 'Do' correctly reported them).
                    _bxDepth = 0;
                    _bxFloor = 0;
                    // The form's own content starts outside any text object whatever the invoker
                    // was doing: its 'Do' is judged against its own BT/ET, and its ET must not
                    // close the invoker's text object once Do returns.
                    _inTextObject = false;
                    _gs = _gs.Clone();

                    // §8.10.1 b): "Concatenates the matrix from the form dictionary's Matrix entry
                    // with the current transformation matrix (CTM)". Applied to the CLONE
                    // above, not the invoker's own _gs, so a visitor reading GraphicsState.Ctm from
                    // inside the form's own first operator sees the composed matrix while the
                    // invoker's own CTM, restored in the finally below, is never touched by it
                    // (#402 round 2: before this, a visitor had no way to recover the composed CTM
                    // at all, since C0^-1 x M x C0 is undefined for a singular form /Matrix).
                    _gs.Ctm = matrix.Concat(_gs.Ctm);

                    // §7.8.2: operands never carry across an operator, and Do is the operator
                    // here, so neither an operand left over from before this Do nor one trailing
                    // the form's own content should reach the operator that follows on either side.
                    ClearOperands();
                    try
                    {
                        InterpretStream(decoded, formCtx, visitor, pageIndex, diagnostics);
                    }
                    finally
                    {
                        ClearOperands();
                        while (_gsStack.Count > savedGsStackCount)
                            _gsStack.Pop();
                        _gs = savedGs;
                        _markedContentDepth = savedMarkedContentDepth;
                        _bxDepth = savedBxDepth;
                        _gsFloor = savedGsFloor;
                        _markedContentFloor = savedMarkedContentFloor;
                        _bxFloor = savedBxFloor;
                        _ignoredGsPushes = savedIgnoredGsPushes;
                        _ignoredMcPushes = savedIgnoredMcPushes;
                        _textState.TextMatrix = savedTextMatrix;
                        _textState.TextLineMatrix = savedTextLineMatrix;
                        _inTextObject = savedInTextObject;
                    }
                }
            }
            finally
            {
                visitor.OnFormEnd(objectNumber);
            }
        }
        finally
        {
            _formDepth--;
            _openForms.Remove(objectNumber);
        }
    }

    private PdfDictionary? ResolveDictionaryEntry(PdfDictionary dict, PdfName key) =>
        dict.Get(key) is { } raw ? _reader.ResolveValue(raw) as PdfDictionary : null;

    private Matrix ReadFormMatrix(PdfDictionary formDict)
    {
        // §7.3.10 permits any dictionary entry to be an indirect reference; Table 93 gives /Matrix
        // no direct-only restriction, so the entry is resolved before the shape check below.
        if (formDict.Get(MatrixKey) is { } raw && _reader.ResolveValue(raw) is PdfArray arr
            && arr.Count == 6 && TryReadNumbers(arr, out var v))
            return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
        return Matrix.Identity; // §8.10.2 Table 93's own default.
    }

    private PdfRectangle? ReadFormBBox(PdfDictionary formDict)
    {
        // Same reasoning as ReadFormMatrix above: /BBox may be indirect too.
        if (formDict.Get(BBoxKey) is { } raw && _reader.ResolveValue(raw) is PdfArray arr
            && arr.Count == 4 && TryReadNumbers(arr, out var v))
        {
            return new PdfRectangle(
                Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
        }
        return null;
    }

    private bool TryReadNumbers(PdfArray array, out double[] values)
    {
        values = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            var resolved = _reader.ResolveValue(array[i]);
            if (resolved is not (PdfInteger or PdfReal))
                return false;
            values[i] = ReadNumber(resolved);
        }
        return true;
    }

    // ── Inline images (ISO 32000-2 §8.9.7) ──────────────────────────────────────────────────────

    /// <summary>Parses one <c>BI</c>…<c>ID</c>…<c>EI</c> inline image starting with <c>BI</c>
    /// already consumed by the caller. Returns <see langword="false"/> when the image's key/value
    /// dictionary or data could not be delimited at all, or when the dictionary hit one of this
    /// reader's ceilings (<see cref="MaxCompositeOperandElements"/> on a value,
    /// <see cref="MaxInlineImageDictionaryPairs"/> on the pair count): the caller stops interpreting
    /// this stream either way, since a ceiling drop happens before the image's data has been
    /// delimited, leaving nothing past that point to resynchronise on reliably.</summary>
    private bool HandleInlineImage(
        PdfLexer lexer, PdfObjectParser parser, StreamContext ctx, IContentVisitor visitor,
        DiagnosticSink diagnostics, int pageIndex, int biOffset)
    {
        var dict = new PdfDictionary();
        var entryCount = 0;

        while (true)
        {
            lexer.SkipWhitespaceAndComments();
            if (lexer.AtEnd)
            {
                ReportInlineImageMalformed(
                    "the 'ID' operator was never reached before the end of the content stream", ctx,
                    diagnostics, pageIndex);
                return false;
            }

            var keyTok = lexer.NextToken();
            if (keyTok.Kind == TokenKind.Keyword && keyTok.Raw.Span.SequenceEqual("ID"u8))
                break;

            if (keyTok.Kind != TokenKind.Name)
            {
                ReportInlineImageMalformed(
                    "a key name or 'ID' was expected in the inline image dictionary", ctx, diagnostics,
                    pageIndex);
                return false;
            }

            // Checked before this key is even decoded into a PdfName, not after: an over-cap
            // dictionary must not keep paying per-pair allocation cost for pairs this reader is
            // about to drop the whole image over anyway (#402 round 7; see
            // MaxInlineImageDictionaryPairs for the 21-pair bound a Table 91-only dictionary has).
            if (entryCount >= MaxInlineImageDictionaryPairs)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ContentLimitExceeded,
                    $"An inline image dictionary has more than {MaxInlineImageDictionaryPairs} "
                    + "key-value pairs; the image was dropped.",
                    ctx.DiagObjectNumber, pageIndex: pageIndex);
                return false;
            }
            entryCount++;

            var key = InlineImageAbbreviations.ExpandKey(PdfObjectParser.ParseName(keyTok));
            var isColorSpaceKey = key.Equals(PdfName.ColorSpace);
            var isFilterKey = key.Equals(PdfName.Filter);

            lexer.SkipWhitespaceAndComments();
            var valueStart = lexer.Position;
            var valueTok = lexer.NextToken();

            // Pre-scanned with the lexer alone, exactly the way the main operand loop's own
            // ArrayBegin/DictBegin case does (see CompositeOperandWithinCap's own remarks), before
            // either PdfObjectParser branch below ever materialises the value: an inline image
            // dictionary value has no operator-level arity check of its own to fall back on for
            // this, so without this pre-scan a value here bypassed MaxCompositeOperandElements
            // entirely, even though every other array or dictionary this interpreter parses from
            // content is bounded by it (#402 round 7).
            if (valueTok.Kind is TokenKind.ArrayBegin or TokenKind.DictBegin)
            {
                if (!CompositeOperandWithinCap(lexer, out var valueLexerFailed))
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ContentLimitExceeded,
                        $"An inline image dictionary value exceeds {MaxCompositeOperandElements} "
                        + "tokens; the image was dropped.",
                        ctx.DiagObjectNumber, pageIndex: pageIndex);
                    if (valueLexerFailed)
                    {
                        // Same reasoning as the identical branch in the main operand loop's own
                        // ArrayBegin/DictBegin case above: the count pass itself hit a malformed
                        // byte before it ever reached the cap comparison this 309 already covers,
                        // so that failure gets its own 300 too rather than silently ending
                        // interpretation with nothing to explain why.
                        diagnostics.Report(
                            PdfReaderDiagnosticCode.ContentStreamLexError,
                            "The content stream's syntax could not be interpreted past this "
                            + "point; interpretation of it stopped here.",
                            ctx.DiagObjectNumber, pageIndex: pageIndex);
                    }
                    return false;
                }
            }

            PdfObject value;
            if (valueTok.Kind == TokenKind.Name && (isColorSpaceKey || isFilterKey))
            {
                value = InlineImageAbbreviations.ExpandColorSpaceOrFilterName(
                    PdfObjectParser.ParseName(valueTok), isColorSpaceKey);
            }
            else if (valueTok.Kind == TokenKind.ArrayBegin && (isFilterKey || isColorSpaceKey))
            {
                lexer.Seek(valueStart);
                var arr = (PdfArray)parser.ParseObject();
                var items = new List<PdfObject>(arr.Count);
                for (var i = 0; i < arr.Count; i++)
                {
                    // §8.9.7 permits exactly one composite inline colour space, "a limited form of
                    // Indexed colour space" whose base is a device space, written as an array:
                    // [/I baseSpace hival lookup]. Only elements 0 and 1 are colour-space NAMES
                    // eligible for Table 92 expansion there (hival is a number, lookup a string or
                    // stream); a /Filter array has no such shape restriction, so every element of
                    // one is eligible.
                    var eligible = isFilterKey || i < 2;
                    items.Add(arr[i] is PdfName elName && eligible
                        ? InlineImageAbbreviations.ExpandColorSpaceOrFilterName(elName, isColorSpace: isColorSpaceKey)
                        : arr[i]);
                }
                value = new PdfArray(items);
            }
            else
            {
                lexer.Seek(valueStart);
                value = parser.ParseObject();

                // §7.8.2: "Indirect objects and object references shall not be permitted at all"
                // in a content stream. Table 91's entries each already have a documented default
                // or an existing missing-entry diagnostic, so the entry is treated as absent
                // rather than dropping the whole image over it: /F 5 0 R falls through to the
                // unfiltered-data-length computation from /W /H /BPC /CS, and /W 5 0 R becomes a
                // missing /W, which those existing paths already report.
                if (value is PdfIndirectReference)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.InlineImageMalformed,
                        "An inline image dictionary value is an indirect reference, which §7.8.2 "
                        + "does not permit in a content stream; the entry was ignored.",
                        ctx.DiagObjectNumber, pageIndex: pageIndex);
                    continue;
                }
            }

            dict.Set(key, value);
        }

        // Computed before the separator-skip logic below (not after, as an earlier version did),
        // since the whitespace rule itself depends on which filters are in play (#402 round 2): a
        // producer that names ASCIIHexDecode/ASCII85Decode anywhere in a /Filter array gets extra
        // whitespace skipped per NOTE 2 below, and deciding that requires already knowing the
        // (Table 91/92-expanded) filter names dict.Set left behind while the key/value loop above
        // ran.
        var filterNames = CollectFilterNames(dict);
        var hasDisallowedFilter = filterNames.Any(f =>
            f.Value is "JBIG2Decode" or "JPXDecode" or "Crypt");

        // §8.9.7's normative sentence excludes ASCIIHexDecode/ASCII85Decode "as one of its filters"
        // from the single-white-space rule; NOTE 2 gives the skip-without-decoding recipe, scoped
        // narrower, to "the final or only filter": "if the final or only filter is
        // ASCIIHexDecode or ASCII85Decode skip any further white-space [after the first]" before
        // counting /L's own bytes. NOTE 2's own skip only ever touches the raw bytes right after
        // ID, so its "final" filter can only mean final in ENCODING order: the filter applied LAST
        // when the data was written, and therefore the FIRST one a decoder strips. §7.4.1's own
        // EXAMPLE 2 fixes what "order" means for a /Filter array: data "encoded using LZW and ASCII
        // base-85 encoding (in that order)" (LZW applied first, A85 applied last) decodes through
        // "/Filter [/ASCII85Decode /LZWDecode]", A85 named FIRST, because the array is written in
        // DECODE order and A85 is what strips the literal bytes first. So NOTE 2's "final" filter is
        // always array position 0 (or the sole name), never any later position (#402 round 3: an
        // earlier version skipped whenever AHx/A85 appeared ANYWHERE in the array, which corrupts
        // data when a different filter is the one reading the raw bytes: '/F [/Fl /A85]' names A85
        // second, so FlateDecode owns the raw bytes, and skipping past the payload's own leading
        // byte as though A85 owned that position ate it). hasDisallowedFilter above stays
        // position-independent on purpose: the normative sentence's own "as one of its filters"
        // is a different question (any of JBIG2Decode/JPXDecode/Crypt at any position is forbidden)
        // from which filter reads the raw bytes first. Before the round-2 fix, at most one
        // whitespace byte was consumed for every filter shape, so 'BI /F /A85 /L 48 ID  <48-byte
        // payload> EI' (two spaces after ID) came out 3 bytes short: the fixed-at-one skip left the
        // payload's own second byte behind as data, and the rest re-lexed as content instead.
        // Decided from the RAW /Filter array's own element 0, not filterNames[0]: CollectFilterNames
        // drops a non-name element rather than keeping its place in the array, so '/F [null /A85]'
        // would otherwise promote /A85 into filterNames[0] and skip whitespace as though IT were
        // position 0, when the array's own position 0 is neither AHx nor A85 at all (#402 round 4).
        var skipsExtraWhitespace = FirstFilterIsAsciiHexOrAscii85(dict);

        // §8.9.7: "the ID operator shall be followed by a single white-space character, and the
        // next character shall be interpreted as the first byte of image data." Exactly ONE byte is
        // consumed as that mandated separator below, even when it is a CR immediately followed by
        // an LF, which §7.2.3's own EOL rule folds into one two-byte marker for LINE-ENDING
        // purposes only; §8.9.7 does not import that fold, and unlike §7.3.8.1 (which requires "an
        // end-of-line marker consisting of either a CARRIAGE RETURN and a LINE FEED or just a LINE
        // FEED" outright on the one construct, a stream's own keyword-to-data boundary, where a spec
        // author who wanted the fold applied there wrote it in), §8.9.7 says only "a single
        // white-space character". isCrLf records whether the mandated byte happened to be a CR
        // immediately followed by an LF, so tiers a/b below (each of which has a declared or
        // computed length to verify a reading against) can retry the two-byte reading once should
        // the one-byte reading fail to land on 'EI' (#402 round 4: reading the pair first fed a
        // payload's own leading LF byte to the ID separator instead of to the image data whenever a
        // producer wrote a lone CR before a payload that happened to start with LF).
        var isCrLf = false;
        var oneByteSeparatorPos = lexer.Position;
        if (lexer.TryPeek() is var separatorByte && separatorByte >= 0
            && PdfLexer.IsWhitespaceByte((byte)separatorByte))
        {
            isCrLf = separatorByte == (byte)'\r' && lexer.Position + 1 < _currentBuffer.Length
                && _currentBuffer.Span[lexer.Position + 1] == (byte)'\n';
            lexer.Seek(lexer.Position + 1);
            oneByteSeparatorPos = lexer.Position;

            if (skipsExtraWhitespace)
            {
                while (lexer.TryPeek() is var extraByte && extraByte >= 0
                    && PdfLexer.IsWhitespaceByte((byte)extraByte))
                    lexer.Seek(lexer.Position + 1);
                // NOTE 2's own "skip any further white-space [after the first]" already consumes a
                // following LF the same way whether the CR alone or the CR LF pair is read as the
                // mandated separator, so the two readings converge on the same position here: there
                // is nothing left for tiers a/b's own retry, below, to try a second time.
                oneByteSeparatorPos = lexer.Position;
                isCrLf = false;
            }
        }
        var foldedSeparatorPos = isCrLf ? oneByteSeparatorPos + 1 : oneByteSeparatorPos;

        var dataStart = oneByteSeparatorPos;

        var length = TryLengthFromDictionary(dict, dataStart, ctx, diagnostics, pageIndex, out var lengthPastEnd);
        var usedTierA = length is not null;
        if (lengthPastEnd)
        {
            ReportInlineImageMalformed(
                "/L names a length past the end of the content stream", ctx, diagnostics, pageIndex);
        }

        if (length is null && filterNames.Count == 0)
            length = TryComputeUnfilteredLength(dict, ctx, dataStart, diagnostics, pageIndex);

        var lengthFromScan = false;
        if (length is null)
        {
            // Tier c has no declared or computed length to verify either ID-separator reading
            // against, so unlike tiers a/b just above it cannot retry between the two; it keeps
            // §7.2.3's own CR-LF fold as its one reading instead (#402 round 4): the fold misjudges
            // only a lone-CR separator immediately followed by an LF payload byte, a narrower
            // failure mode than the one-byte reading's own would be here, which would prepend a
            // spurious LF onto every ordinary CR LF producer's data, the common case, whenever this
            // scan has no length to tell the two readings apart with.
            dataStart = foldedSeparatorPos;
            var scanEnd = ScanForEi(dataStart, ctx.DiagObjectNumber);
            if (scanEnd is null)
            {
                ReportProbeBudgetExhaustedIfNeeded(ctx, diagnostics, pageIndex);
                ReportInlineImageMalformed(
                    "no 'EI' operator delimiting the image data could be found", ctx, diagnostics,
                    pageIndex);
                return false;
            }
            length = scanEnd.Value - dataStart;
            lengthFromScan = true;
        }

        // No bounds check needed here: tier a (TryLengthFromDictionary) already rejects a length
        // running past the buffer as pastEnd, tier b (TryComputeUnfilteredLength) returns null on
        // the same overrun, and tier c (ScanForEi) can only ever return an offset it found by
        // scanning forward from dataStart within the buffer. Every path into `length` already
        // guarantees dataStart + length.Value falls inside [dataStart, _currentBuffer.Length]
        // (#402 round 3: the equivalent check here was dead code no path could reach).
        var dataEnd = dataStart + length.Value;
        var data = _currentBuffer.Slice(dataStart, length.Value);
        var resyncPos = SkipToEi(dataEnd);

        // A tier-a (/L) or tier-b (computed) length that does not land on 'EI' is symmetric with
        // the /L-past-the-end case above: both recover through the same EI scan (tier c) rather
        // than losing the rest of the content stream outright. Tier c itself is excluded here
        // (lengthFromScan) since it already IS that fallback.
        if (resyncPos is null && !lengthFromScan)
        {
            // dataStart is still the one-byte-separator reading here (lengthFromScan is false, so
            // tier c's own reassignment above never ran). §8.9.7 gives that one-byte reading no way
            // to tell a lone-CR separator immediately followed by an LF payload byte apart from a
            // two-byte CR-LF separator, so when it fails to land on 'EI' and the mandated byte was a
            // CR immediately followed by an LF, retry once with §7.2.3's own fold: both bytes
            // consumed as the separator instead, since a producer that wrote a two-byte CR-LF
            // separator is exactly the case the one-byte reading above would otherwise misjudge
            // (#402 round 4). This retry only runs when the one-byte reading's own resync above
            // already failed to land on 'EI'; it does not cover every CR-LF producer. The CR LF
            // pair at the mandated separator is what shifts the one-byte reading off by one in the
            // first place (it leaves the LF of the pair at the front of the data instead of
            // consuming it as part of the separator); when the payload's own last byte then happens
            // to be white space, that shifted reading still lands on 'EI' regardless, because
            // SkipToEi skips leading white space before checking for 'EI', so the displaced trailing
            // byte gets skipped the same way. The retry never runs in that case, and the visitor
            // receives the data shifted one byte, with no diagnostic. That is the reading §8.9.7
            // mandates ("a single white-space character"), not a defect this retry is meant to close.
            //
            // The malformed report just below is skipped when this retry alone is what recovers the
            // image: a conforming file recovered from cleanly must not carry a warning about it
            // (#402 round 2). The EI-scan fallback below is a DIFFERENT case: reaching it at all
            // means neither reading's declared or computed length landed on 'EI', not merely one of
            // the two, so recovering through IT still reports.
            var recoveredViaCrRetry = false;
            if (isCrLf)
            {
                var retryStart = foldedSeparatorPos;
                var retryEnd = retryStart + length.Value;
                if (retryEnd <= _currentBuffer.Length)
                {
                    var retryResync = SkipToEi(retryEnd);
                    if (retryResync is not null)
                    {
                        dataStart = retryStart;
                        data = _currentBuffer.Slice(dataStart, length.Value);
                        resyncPos = retryResync;
                        recoveredViaCrRetry = true;
                    }
                }
            }

            if (!recoveredViaCrRetry)
            {
                var tierName = usedTierA ? "/L" : "the unfiltered image's computed length";
                ReportInlineImageMalformed(
                    $"the image data length from {tierName} did not land on an 'EI' operator", ctx,
                    diagnostics, pageIndex);
            }

            if (resyncPos is null)
            {
                // Neither reading's length landed on 'EI', so from here on this is tier c's own
                // situation: a scan with no length to verify a reading against. It takes tier c's
                // reading too (the CR-LF fold; see the comment on the tier-c branch above) rather
                // than keeping the one-byte reading tiers a/b started from, so a CR LF producer
                // whose /L is wrong does not get a spurious LF prepended to its recovered data.
                dataStart = foldedSeparatorPos;
                var scanEnd = ScanForEi(dataStart, ctx.DiagObjectNumber);
                if (scanEnd is not null)
                {
                    length = scanEnd.Value - dataStart;
                    data = _currentBuffer.Slice(dataStart, length.Value);
                    resyncPos = SkipToEi(dataStart + length.Value);
                }
            }
        }

        if (resyncPos is null)
        {
            ReportProbeBudgetExhaustedIfNeeded(ctx, diagnostics, pageIndex);
            ReportInlineImageMalformed(
                "no 'EI' operator was found at the computed end of the image data", ctx, diagnostics,
                pageIndex);
            return false;
        }

        lexer.Seek(resyncPos.Value);
        ReportProbeBudgetExhaustedIfNeeded(ctx, diagnostics, pageIndex);

        if (hasDisallowedFilter)
        {
            ReportInlineImageMalformed(
                "the image uses a filter (JBIG2Decode, JPXDecode, or Crypt) never valid on an "
                + "inline image (ISO 32000-2 §8.9.7)", ctx, diagnostics, pageIndex);
            return true;
        }

        visitor.OnInlineImage(dict, data, biOffset);
        return true;
    }

    // Reports that the resync probe's own MaxProbeBytesPerRun budget ran out before a candidate
    // 'EI' could be confirmed, so it (and every later candidate this Run) was accepted unverified
    // instead (#402 round 3; see ProbeOnce's Exhausted outcome and ClassifyResyncPoint). Called for
    // every inline image this Run still delimits once the budget is spent, not only the one whose
    // own scan spent it: the sink's own (code, object, page) dedupe collapses every call against
    // the SAME object into one, but a later inline image inside a DIFFERENT content stream (a
    // different Form XObject, or the page's own content once a form already spent the budget) is
    // not deduped against that first report at all, so this names the offset AND the object number
    // (or "the page's own content" when that object number is null) of the FIRST occurrence
    // explicitly, rather than pairing the first offset with whatever object happens to be current
    // on a later, separately-reported call (#402 round 4).
    private void ReportProbeBudgetExhaustedIfNeeded(
        StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (!_probeBudgetExhausted)
            return;

        var firstSpentIn = _probeBudgetExhaustedAtObjectNumber is { } objectNumber
            ? $"object {objectNumber}"
            : "the page's own content";
        ReportInlineImageMalformed(
            $"the resync probe's {MaxProbeBytesPerRun / (1024 * 1024)} MiB per-run byte budget was "
            + $"first spent at offset {_probeBudgetExhaustedAtOffset} of {firstSpentIn}, before a "
            + "candidate 'EI' there could be confirmed; that candidate, and every later candidate "
            + "this run, was accepted without verification",
            ctx, diagnostics, pageIndex);
    }

    private void ReportInlineImageMalformed(
        string reason, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex) =>
        diagnostics.Report(
            PdfReaderDiagnosticCode.InlineImageMalformed, $"Inline image malformed: {reason}.",
            ctx.DiagObjectNumber, pageIndex: pageIndex);

    private List<PdfName> CollectFilterNames(PdfDictionary dict)
    {
        var names = new List<PdfName>();
        if (dict.Get(PdfName.Filter) is { } filterVal)
        {
            switch (filterVal)
            {
                case PdfName n: names.Add(n); break;
                case PdfArray arr:
                    for (var i = 0; i < arr.Count; i++)
                        if (arr[i] is PdfName elName)
                            names.Add(elName);
                    break;
            }
        }
        return names;
    }

    // NOTE 2's "final or only filter" is array position 0 (see the remarks above
    // skipsExtraWhitespace's own assignment); this reads that position directly off the RAW
    // /Filter value rather than off CollectFilterNames' own result, since a non-name element there
    // is dropped rather than kept in place, which would otherwise let a later name slide into
    // position 0 (#402 round 4).
    private static bool FirstFilterIsAsciiHexOrAscii85(PdfDictionary dict)
    {
        var first = dict.Get(PdfName.Filter) switch
        {
            PdfName n => n,
            PdfArray { Count: > 0 } arr => arr[0] as PdfName,
            _ => null,
        };
        return first?.Value is "ASCIIHexDecode" or "ASCII85Decode";
    }

    // Tier (a): /L, PDF 2.0's own Table 91 entry (§8.9.7). See InlineImageAbbreviations' own remarks
    // for why this cites Table 91 rather than Table 93, which ISO 32000-2 reserves for a Form
    // XObject dictionary's unrelated entries (§8.10.2).
    private int? TryLengthFromDictionary(
        PdfDictionary dict, int dataStart, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex,
        out bool pastEnd)
    {
        pastEnd = false;
        var lengthRaw = dict.Get(PdfName.Length);
        if (lengthRaw is null)
            // §8.9.7 NOTE 1: Length is new to PDF 2.0, so an older file will not carry the key at
            // all; that is tolerated here by falling through to tier b or the EI scan, not treated
            // as itself a malformation the way a PRESENT-but-wrong-typed /L is just below.
            return null;

        if (lengthRaw is not PdfInteger lengthObj)
        {
            // Present but the wrong type (a PdfReal, say): reported the same way an invalid /W,
            // /H, or /BPC is (#402 round 2), rather than silently falling through to tier b/c as
            // if /L had never been written at all. This branch is only reachable once /L IS
            // present (the null check above already handled "absent"), so the message names what
            // was found wrong with it rather than repeating "missing" (#402 round 3).
            ReportInlineImageMalformed(
                "'/L' is present but its value is not an integer", ctx, diagnostics, pageIndex);
            return null;
        }

        if (lengthObj.Value < 0)
        {
            ReportInlineImageMalformed("'/L' is negative; it was ignored", ctx, diagnostics, pageIndex);
            return null;
        }

        var length = lengthObj.Value;
        if (length > int.MaxValue || dataStart + length > _currentBuffer.Length)
        {
            pastEnd = true;
            return null;
        }

        return (int)length;
    }

    // Tier (b): unfiltered data, Height x rowBytes, where rowBytes = ceil(Width x BitsPerComponent
    // x components / 8) (§8.9.7).
    private int? TryComputeUnfilteredLength(
        PdfDictionary dict, StreamContext ctx, int dataStart, DiagnosticSink diagnostics, int pageIndex)
    {
        var isMask = dict.Get(ImageMaskKey) is PdfBoolean { Value: true };
        var width = ReadIntEntry(dict, WidthKey);
        var height = ReadIntEntry(dict, HeightKey);
        var bpc = isMask ? 1 : ReadIntEntry(dict, BitsPerComponentKey);

        // Table 87 types Width and Height as integer, with no stated sign restriction of its own;
        // "positive" is this reader's own requirement (a zero or negative sample count computes
        // nothing meaningful), not the table's own wording. BitsPerComponent is different: Table 87
        // restricts its VALUE outright to 1, 2, 4, 8, or (from PDF 1.5) 16, so a value outside that
        // set is invalid regardless of sign, not merely non-positive; ImageMask forces bpc to the
        // one legal value for a mask (1) above, bypassing this check entirely. ReadIntEntry already
        // turns a non-integer or an out-of-int-range /W or /H into "missing" (null).
        if (width is null || height is null || bpc is null || width <= 0 || height <= 0
            || (!isMask && bpc is not (1 or 2 or 4 or 8 or 16)))
        {
            ReportInlineImageMalformed(
                "an unfiltered image is missing, or carries an invalid, /W, /H or /BPC needed to "
                + "compute its data length", ctx, diagnostics, pageIndex);
            return null;
        }

        var components = isMask ? 1 : ResolveComponentCount(dict, ctx, diagnostics, pageIndex);
        if (components <= 0)
            return null; // Unknown colour space: fall back to the EI scan (tier c).

        var rowBytes = ((long)width.Value * bpc.Value * components + 7) / 8;
        // rowBytes can reach roughly 2^34 (width up to int.MaxValue, bpc up to 16) and height up to
        // int.MaxValue - 1 (~2^31), so this multiply can wrap a signed 64-bit long. The wrap is
        // harmless: the total < 0 check below and the bounds check that follows it both still run,
        // and a surviving wrapped value that passes both usually takes the did-not-land-on-'EI'
        // path instead (a 307 reports first, then the scan, tier c, recovers the image), UNLESS the
        // wrapped value happens to land exactly on an 'EI', in which case it is silently accepted
        // with no diagnostic at all, the same as any other length that happens to be right. Measured
        // with /W 1824726041 /H 1263665316 /BPC 16 /CS /CMYK (rowBytes * height wraps to 32): one
        // image delivered, the operators that followed it reached the visitor, and exactly one 307.
        // With 32 bytes of image data instead of a mismatched payload, the same wrapped total (32)
        // lands squarely on 'EI': one 32-byte image delivered, zero 307s.
        var total = rowBytes * height.Value;
        if (total < 0 || dataStart + total > _currentBuffer.Length)
            return null; // Fall back to the EI scan rather than trusting a runaway computed size.

        return (int)total;
    }

    // Table 87 types Width, Height, and BitsPerComponent as integers; a PdfReal there, or an
    // integer outside int's range, is invalid rather than merely absent, but both are treated as
    // "missing" here so TryComputeUnfilteredLength's own null check catches either uniformly.
    private static int? ReadIntEntry(PdfDictionary dict, PdfName key) => dict.Get(key) switch
    {
        PdfInteger i when i.Value >= int.MinValue && i.Value <= int.MaxValue => (int)i.Value,
        _ => null,
    };

    private int ResolveComponentCount(
        PdfDictionary dict, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        var csValue = dict.Get(PdfName.ColorSpace);

        // §8.9.7's one composite inline colour space: [/Indexed base hival lookup]. Indexed
        // samples are always single-component index values (§8.6.6.3) regardless of the base
        // space's own component count, so this is the one array shape this method needs to
        // recognise without resolving the base space at all.
        if (csValue is PdfArray csArray && csArray.Count > 0 && csArray[0] is PdfName firstName
            && firstName.Value == "Indexed")
            return 1;

        if (csValue is not PdfName csName)
            return -1;

        if (csName.Equals(PdfName.DeviceRGB)) return 3;
        if (csName.Value == "DeviceCMYK") return 4;
        if (csName.Value == "DeviceGray") return 1;

        // A bare "/Indexed" name, rather than the array form above, is not itself a legal colour
        // space (§8.6.6.3 requires the array shape); it is also not a /Resources /ColorSpace
        // entry name a producer would define, so looking it up there and reporting
        // ResourceMissing when it is (as expected) absent would be wrong twice over. Falling back
        // to the EI scan silently is the simplest correct outcome.
        if (csName.Value == "Indexed")
            return -1;

        // A named resource colour space (§8.9.7: "the value of the ColorSpace entry may also be the
        // name of a colour space in the ColorSpace subdictionary of the current resource
        // dictionary"). Resolving its component count in general needs full colour-space semantics
        // (ICCBased /N, DeviceN's component array, ...) this interpreter does not implement;
        // reporting ResourceMissing when the name is absent, and otherwise falling back to the EI
        // scan (tier c) either way, keeps this interpreter's own scope bounded to delimiting the
        // image rather than fully understanding its colour space.
        if (ctx.Resources is null || !TryGetResource(ctx.Resources, PdfName.ColorSpace, csName, out _))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"An inline image's /CS names '/{DiagnosticExcerpt.Quote(csName.Value)}', absent from the "
                + "applicable /Resources /ColorSpace dictionary.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
        }
        return -1;
    }

    // Tier (c): scan for whitespace-EI-whitespace/EOF, accepted only when the bounded probe just
    // past the candidate (ClassifyResyncPoint) does not reject it outright. A WeakReject verdict
    // (see ProbeOutcome's own remarks) is remembered as a fallback rather than rejected on the
    // spot: the FIRST such candidate is kept while the scan keeps looking for a stronger one, and
    // is returned only once the scan finds no Accept-or-Exhausted candidate at all (#402 round 4).
    // Residual gap this scan cannot close on its own (stated here and in the PR body, not fixed:
    // closing it needs decoding the image data itself, out of scope for a byte-level scan): any
    // whitespace-delimited 'EI' followed by bytes that lex as a Table A.1 operator, as 'BI', or as
    // the buffer's own end is indistinguishable, at the byte level, from a resync point the
    // terminating 'EI' itself sets, so a false 'EI' followed, after any run of neutral tokens, by
    // any of those three shapes is accepted with no diagnostic. A '%' comment is the easiest worked
    // example: one running from right after a false 'EI' to the end of its own line can swallow the
    // LATER, terminating 'EI' written on that same line, so the operator that follows the comment's
    // own line is what the probe sees, and it accepts through the ordinary Table A.1 rule with
    // nothing to tell the two apart. The same blind spot applies to a coincidental operator inside
    // compressed noise (" EI n ", " EI W ", " EI f " each truncate the image data with no 307 and
    // hand the visitor a spurious operator) and to " EI BI " (the BI accept rule below): the bytes
    // are well-formed content either way.
    private int? ScanForEi(int dataStart, int? diagObjectNumber)
    {
        var span = _currentBuffer.Span;
        int? weakCandidate = null;
        for (var i = dataStart; i + 1 < span.Length; i++)
        {
            if (span[i] != (byte)'E' || span[i + 1] != (byte)'I')
                continue;

            var precededByWhitespace = i == dataStart || PdfLexer.IsWhitespaceByte(span[i - 1]);
            if (!precededByWhitespace)
                continue;

            var after = i + 2;
            var followedOk = after >= span.Length
                || PdfLexer.IsWhitespaceByte(span[after]) || PdfLexer.IsDelimiterByte(span[after]);
            if (!followedOk)
                continue;

            var verdict = ClassifyResyncPoint(after, diagObjectNumber);
            if (verdict == ResyncVerdict.Reject)
                continue;

            var dataEnd = TrimEiDelimiter(span, dataStart, i);
            if (verdict == ResyncVerdict.Accept)
                return dataEnd;

            // WeakReject: this candidate's own probe ran off the buffer's TRUE end mid-token
            // (§8.9.7 gives this scan no way to tell "the file ends with a malformed token" apart
            // from "this false 'EI' sits inside image data whose next token happens to run to the
            // file's own end without closing"), which is weaker evidence than a malformed byte
            // found strictly inside the probe window (still a Reject, above). Only the FIRST one is
            // kept: a later, stronger candidate always wins over an earlier weak one.
            weakCandidate ??= dataEnd;
        }
        return weakCandidate;
    }

    // Strips the single white-space byte §8.9.7 excludes from the image data (the one delimiting
    // 'EI'). §7.2.3 makes a CARRIAGE RETURN immediately followed by a LINE FEED ONE EOL marker, not
    // two separate white-space bytes, so when that pair sits right before 'EI' both bytes are the
    // delimiter, not just the LF (#402 round 3: stripping only the LF left the CR behind as the
    // image's own trailing byte).
    private static int TrimEiDelimiter(ReadOnlySpan<byte> span, int dataStart, int eiOffset)
    {
        var dataEnd = eiOffset;
        if (eiOffset > dataStart && PdfLexer.IsWhitespaceByte(span[eiOffset - 1]))
        {
            dataEnd = eiOffset - 1;
            if (dataEnd > dataStart && span[dataEnd] == (byte)'\n' && span[dataEnd - 1] == (byte)'\r')
                dataEnd--;
        }
        return dataEnd;
    }

    // Confirms an 'EI' candidate at exactly a known offset (used once tier a/b already computed a
    // length): skips zero or more §7.2.3 Table 1 white-space bytes, then requires the bytes right
    // after to literally spell "EI"; nothing before 'EI' is REQUIRED to be white space, only
    // tolerated when present ('/L 4 ID ABCDEIQ ' delimits with no white-space byte immediately
    // before 'EI' and no 307). Unlike ScanForEi (tier c), this does not search (it verifies one
    // position only) and checks nothing about what follows 'EI': ScanForEi's own followedOk check
    // (whitespace or a delimiter right after 'EI') has no counterpart here, so a tier a/b image is
    // delivered even when the byte immediately after 'EI' is neither.
    private int? SkipToEi(int dataEnd)
    {
        var pos = dataEnd;
        var span = _currentBuffer.Span;
        while (pos < span.Length && PdfLexer.IsWhitespaceByte(span[pos]))
            pos++;
        if (pos + 1 >= span.Length || span[pos] != (byte)'E' || span[pos + 1] != (byte)'I')
            return null;
        return pos + 2;
    }

    // Lexes forward from a candidate resync point until it finds a positive reason to accept or
    // reject, or exhausts this Run's own MaxProbeBytesPerRun budget (#402 round 3 redesign; replaces
    // the round-2 two-window, token-capped probe, which let every false candidate in a long run of
    // them each pay a full window's own lexing cost, driving the " EI (" repeated-candidate shape to
    // 16.6 s per decoded MiB, and separately let an unterminated token that merely ran off the
    // second, larger window mask the terminating 'EI' whose own legitimate follow-on token
    // happened to be longer still). A candidate needs a POSITIVE reason to accept now, not merely
    // the absence of a rejecting keyword: a number, name, string, array/dictionary delimiter,
    // true/false/null, or an unknown-but-printable keyword is neutral and keeps the probe lexing
    // rather than accepting by default, closing the round-2 gap where a straddling but well-formed
    // array or dictionary (never itself a keyword) produced the identical wrong "accept" outcome an
    // unterminated string used to.
    private enum ProbeOutcome
    {
        /// <summary>A Table A.1 operator other than 'EI'/'ID', 'BI', or the buffer's own true end
        /// (not merely this probe's budget) was reached.</summary>
        Accept,

        /// <summary>An 'EI' or 'ID' keyword (still inside image data that continues to a LATER
        /// 'EI'), a keyword containing a non-printable byte, or a lex failure found strictly inside
        /// the window, clipped or not (a malformed byte neither the window's own clip nor the
        /// buffer's true end had anything to do with).</summary>
        Reject,

        /// <summary>A lex failure that ran off the buffer's own TRUE end (not this probe's own
        /// window clip) trying to close a token: an unterminated literal or hex string with no more
        /// buffer left to find its closing delimiter in, say. Weaker evidence than <see cref="Reject"/>:
        /// this scan cannot tell "the file ends mid-token" apart from "this false 'EI' sits
        /// inside image data whose next token happens to run to the file's own end without closing"
        /// (#402 round 4; see ScanForEi's own remarks on how this outcome is used as a fallback
        /// rather than rejected outright).</summary>
        WeakReject,

        /// <summary>The probe's own share of MaxProbeBytesPerRun ran out before it reached an
        /// Accept, Reject, or WeakReject outcome. Treated as an accept by ClassifyResyncPoint (see
        /// its own remarks), but distinctly, so HandleInlineImage can report that this candidate was
        /// accepted unverified rather than confirmed.</summary>
        Exhausted,
    }

    private ProbeOutcome ProbeOnce(int pos)
    {
        var remaining = _currentBuffer.Length - pos;
        var windowLength = (int)Math.Min(remaining, _probeBytesRemaining);
        // Whether this window is itself an artificial cap, i.e. more buffer exists beyond it that
        // the probe's own remaining budget deliberately does not look at. Only THAT case makes
        // reaching the window's own end inconclusive (Exhausted) rather than an outright Accept or
        // Reject: a window that reaches the buffer's own true end behaves exactly like the
        // unbounded lexer this probe conceptually stands in for (a token that never closes anywhere
        // is malformed, full stop; the buffer's own end with nothing pending IS a legitimate resync
        // point), so those still resolve outright rather than staying inconclusive.
        var windowClipped = windowLength < remaining;
        var window = _currentBuffer.Slice(pos, windowLength);
        var probe = new PdfLexer(window, contentStreamMode: true);
        ProbeOutcome outcome;

        while (true)
        {
            if (probe.AtEnd)
            {
                outcome = windowClipped ? ProbeOutcome.Exhausted : ProbeOutcome.Accept;
                break;
            }

            Token token;
            try
            {
                token = probe.NextToken();
            }
            catch (InvalidDataException)
            {
                // Ran off the end of the window mid-token (an unterminated literal or hex string,
                // say). Three cases share this one catch block, distinguished by WHICH end the
                // token ran off: the budget's own artificial clip (Exhausted, windowClipped and the
                // lexer's own Position landed at or past the window's own length), the buffer's own
                // TRUE end with nothing left to close the token (WeakReject, the same Position
                // check but an unclipped window: #402 round 4), or a malformed byte found strictly
                // inside the window, short of either end, clipped or not (Reject outright).
                outcome = (windowClipped, probe.Position >= window.Length) switch
                {
                    (true, true) => ProbeOutcome.Exhausted,
                    (false, true) => ProbeOutcome.WeakReject,
                    _ => ProbeOutcome.Reject,
                };
                break;
            }

            if (token.Kind == TokenKind.EndOfInput)
            {
                outcome = windowClipped ? ProbeOutcome.Exhausted : ProbeOutcome.Accept;
                break;
            }

            if (token.Kind != TokenKind.Keyword)
                continue; // Neutral: a number, name, string, or array/dictionary delimiter.

            var raw = token.Raw.Span;
            if (raw.SequenceEqual("EI"u8) || raw.SequenceEqual("ID"u8))
            {
                // Still inside image data that continues to a LATER 'EI': this candidate is a false
                // one, not a resync point.
                outcome = ProbeOutcome.Reject;
                break;
            }
            if (raw.SequenceEqual("BI"u8))
            {
                // The bytes after ITS OWN following 'ID' are raw image data and must not be judged
                // as tokens at all, so the probe stops here rather than lexing into them.
                outcome = ProbeOutcome.Accept;
                break;
            }
            if (raw.SequenceEqual("true"u8) || raw.SequenceEqual("false"u8) || raw.SequenceEqual("null"u8))
                continue; // Neutral.
            if (raw.Length == 1 && raw[0] is (byte)'{' or (byte)'}' or (byte)'>')
                continue; // Neutral: this lexer's own one-byte content-mode keywords.
            if (ContentOperators.IsKnown(raw))
            {
                outcome = ProbeOutcome.Accept;
                break;
            }

            var hasNonPrintableByte = false;
            foreach (var b in raw)
            {
                if (b is < (byte)'!' or > (byte)'~')
                {
                    hasNonPrintableByte = true;
                    break;
                }
            }
            if (hasNonPrintableByte)
            {
                // Binary noise a coincidental "EI" byte pair inside DCT- or JPX-compressed data
                // would otherwise be mistaken for legitimate syntax.
                outcome = ProbeOutcome.Reject;
                break;
            }
            // Neutral: an unknown-but-printable keyword, the kind of thing §7.8.2 already tolerates
            // outside a compatibility section (a future operator this reader does not know yet, a
            // stray "R").
        }

        // Charges what this probe spent: the window's own length when it ran out of budget before
        // resolving (Exhausted), or however far the lexer got otherwise. A window of length 0
        // (the budget already fully spent when this call started) charges nothing more and
        // resolves to Exhausted immediately, which is what makes every candidate after the budget
        // runs out cost nothing to probe.
        var charged = outcome == ProbeOutcome.Exhausted ? windowLength : Math.Min(probe.Position, windowLength);
        _probeBytesRemaining -= charged;
        if (_probeBytesRemaining < 0)
            _probeBytesRemaining = 0;
        ProbeBytesConsumed += charged;
        return outcome;
    }

    // ScanForEi's own verdict on one candidate: Accept ends the scan immediately, Reject moves on
    // to the next candidate with nothing kept, and WeakReject moves on too but leaves the candidate
    // behind as a fallback ScanForEi returns if nothing stronger ever turns up (#402 round 4).
    private enum ResyncVerdict
    {
        Accept,
        Reject,
        WeakReject,
    }

    private ResyncVerdict ClassifyResyncPoint(int pos, int? diagObjectNumber)
    {
        var outcome = ProbeOnce(pos);
        if (outcome == ProbeOutcome.Exhausted)
        {
            // Accepted unverified, once and for the rest of this Run: HandleInlineImage reports
            // this the first time it happens, naming the offset AND object number this candidate
            // was accepted at without verification (#402 round 4: recording diagObjectNumber here,
            // alongside the offset, is what lets a LATER report against a different content
            // stream's own object number still name where the budget ran out, rather than
            // pairing this offset with whatever object happens to be current when it is reported),
            // so a caller can tell "the interpreter confirmed this resync point" apart from "the
            // interpreter ran out of budget and took its best guess" (#402 round 3).
            if (!_probeBudgetExhausted)
            {
                _probeBudgetExhausted = true;
                _probeBudgetExhaustedAtOffset = pos;
                _probeBudgetExhaustedAtObjectNumber = diagObjectNumber;
            }
            return ResyncVerdict.Accept;
        }
        return outcome switch
        {
            ProbeOutcome.Accept => ResyncVerdict.Accept,
            ProbeOutcome.WeakReject => ResyncVerdict.WeakReject,
            _ => ResyncVerdict.Reject,
        };
    }
}
