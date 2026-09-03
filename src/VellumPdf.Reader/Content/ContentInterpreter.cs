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

    // §9.4.3's own TJ array holds a mix of strings and numeric adjustments; this reader's own
    // ceiling on how many elements one such array may carry.
    private const int MaxTjElements = 8192;

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

    // The bounded resync probe (see LooksLikeResyncPoint) lexes at most this many bytes, and at
    // most ProbeTokens tokens, from each 'EI' candidate: bounding the work per candidate is what
    // keeps ScanForEi linear in the content length rather than quadratic (a probe that lexed to
    // the end of the buffer from every candidate cost O(N) per candidate, O(N^2) overall).
    private const int ProbeWindowBytes = 128;
    private const int ProbeTokens = 8;

    // The second, larger window LooksLikeResyncPoint re-probes with when a token runs off the
    // clipped ProbeWindowBytes window: a legitimate token following a false 'EI' candidate (a long
    // literal string, say) can run well past 128 bytes without being image data at all, so a
    // candidate is rejected only once a token also runs off THIS window (#402 round 2; the
    // previous single-window probe treated running off a clipped window as inconclusive and
    // accepted the candidate either way, which let a sufficiently long unterminated token past the
    // window mask a malformed stream).
    private const int ExtendedProbeWindowBytes = 4096;

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
    /// value after the interpreter moves on must copy it.</summary>
    internal GraphicsState GraphicsState => _gs;

    /// <summary>The current text-positioning state, readable the same way as
    /// <see cref="GraphicsState"/>.</summary>
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
        catch (InvalidDataException ex)
        {
            // The outermost guard for a malformed indirect-reference chain reached through
            // resource, XObject, or Form XObject resolution (a corrupt cross-reference offset,
            // say): PdfDocumentReader.Resolve and friends can throw here even though this
            // interpreter's own lexer and parser never do (InterpretStream's own catch already
            // handles those). Consistent with this type's own class doc promise that
            // InvalidDataException never escapes Run, and with the notify-and-continue policy
            // every other diagnostic in this channel follows.
            diagnostics.Report(
                PdfReaderDiagnosticCode.ContentStreamLexError,
                $"The page's content could not be fully resolved: {ex.Message}",
                pageIndex: pageIndex);
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
            if (budgetSpent)
                return;

            if (element is not PdfIndirectReference elementRef)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ContentStreamLexError,
                    "A /Contents element is not an indirect reference to a stream (ISO 32000-2 "
                    + "§7.7.3.3 Table 31); it was skipped.",
                    pageIndex: pageIndex);
                return;
            }

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

                        case TokenKind.ArrayBegin:
                            lexer.Seek(offset);
                            PushOperand(parser.ParseObject(), ctx, diagnostics, pageIndex);
                            break;

                        case TokenKind.DictBegin:
                            lexer.Seek(offset);
                            PushOperand(parser.ParseObject(), ctx, diagnostics, pageIndex);
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
                                    var name = System.Text.Encoding.Latin1.GetString(raw);
                                    HandleOperator(name, offset, ctx, visitor, diagnostics, pageIndex);
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

    // System.Buffers.Text.Utf8Parser backs this, but against a normalised copy of the token's
    // bytes, not the raw span, because PDF's own numeric grammar
    // (ISO 32000-2 §7.3.3) allows a bare leading or trailing decimal point ("-.5", "6.") that the
    // BCL's own double formats do not universally accept the same way across runtimes; padding a
    // missing digit on either side of '.' sidesteps that without reimplementing number parsing.
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

        Span<byte> padded = stackalloc byte[span.Length + 2];
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

    // ── Operator dispatch ────────────────────────────────────────────────────────────────────────

    private void HandleOperator(
        string name, int offset, StreamContext ctx, IContentVisitor visitor, DiagnosticSink diagnostics,
        int pageIndex)
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
            // whatever REAL operator followed rather than to "R" itself, but that leniency broke a
            // differently-shaped input just as easily: '10 20 Zork' ahead of '1 w' silently fed
            // the leftover 20 into 'w' as its own operand instead of 1).
            if (_bxDepth <= 0)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.UnknownOperator,
                    $"'{name}' is not one of the operators ISO 32000-2 Annex A Table A.1 defines; "
                    + "it was ignored.",
                    pageIndex: pageIndex);
            }
            ClearOperands();
            return;
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

        var expected = ContentOperators.ExpectedOperandCount(name);
        if (_operandOverflow)
        {
            ClearOperands();
            return; // Already reported when the overflow itself happened.
        }
        if (expected != ContentOperators.Variable && _operands.Count != expected)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
                $"'{name}' expects {expected} operand(s) but {_operands.Count} were on the stack; "
                + "it was dropped.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            ClearOperands();
            return;
        }

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

        switch (name)
        {
            case "TJ":
                if (_operands[0] is not PdfArray tjArray)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.OperandStackMalformed,
                        "TJ's operand is not an array; it was dropped.",
                        ctx.DiagObjectNumber, pageIndex: pageIndex);
                    ClearOperands();
                    return;
                }
                if (tjArray.Count > MaxTjElements)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ContentLimitExceeded,
                        $"TJ's array operand exceeds {MaxTjElements} elements; it was dropped.",
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
            $"'{op}' names '/{name.Value}', absent from the applicable /Resources /{category.Value} "
            + "dictionary.",
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
                $"'gs' names '/{gsName.Value}', absent from the applicable /Resources /ExtGState "
                + "dictionary.",
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
            // text-positioning, text-showing and marked-content categories of Table 50 inside a
            // text object; 'Do' sits in the XObjects category, so a producer that invokes it
            // there is wrong regardless of what the named XObject turns out to be. The
            // recursion below still runs (a text-object violation is not itself a reason to skip
            // an otherwise-resolvable Form), but the shared _textState instance a form's own
            // content may disturb (BT/Td/ET, unbracketed by any q/Q-style save) is saved and
            // restored around that recursion below regardless of this check, so the report here is
            // purely informational: nothing downstream depends on _inTextObject to avoid the leak
            // (#402 round 2).
            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
                "'Do' occurred inside a text object (ISO 32000-2 §8.2 Figure 9 admits no XObjects "
                + "category operator there).",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
        }

        if (xobjectNameOperand is not PdfName xobjectName)
            return;

        if (ctx.Resources is null || !TryGetResource(ctx.Resources, PdfName.XObject, xobjectName, out var entryRaw))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{xobjectName.Value}', absent from the applicable /Resources /XObject "
                + "dictionary.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        if (entryRaw is not PdfIndirectReference xobjectRef)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{xobjectName.Value}', present in the applicable /Resources "
                + "/XObject dictionary but not as an indirect reference to a stream.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        var stream = _reader.ResolveStream(xobjectRef);
        if (stream is null)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{xobjectName.Value}', but object {xobjectRef.ObjectNumber} does not "
                + "resolve to a stream.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }

        if (stream.Dictionary.Get(PdfName.Subtype) is not PdfName subtype)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{xobjectName.Value}', object {stream.ObjectNumber}, whose "
                + "/Subtype is missing or is not a name, so it cannot be used as an XObject.",
                stream.ObjectNumber, pageIndex: pageIndex);
            return;
        }

        if (subtype.Equals(XObjectSubtypeImage))
            return; // An Image XObject: no recursion; the caller already got Do.

        if (!subtype.Equals(XObjectSubtypeForm))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ResourceMissing,
                $"'Do' names '/{xobjectName.Value}', object {stream.ObjectNumber}, whose /Subtype "
                + $"'/{subtype.Value}' is neither /Form nor /Image, so it cannot be used as an "
                + "XObject.",
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
                    _bxFloor = savedBxDepth;
                    _ignoredGsPushes = 0;
                    _ignoredMcPushes = 0;
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
    /// dictionary or data could not be delimited at all: the caller stops interpreting this
    /// stream, since nothing past this point can be resynchronised reliably.</summary>
    private bool HandleInlineImage(
        PdfLexer lexer, PdfObjectParser parser, StreamContext ctx, IContentVisitor visitor,
        DiagnosticSink diagnostics, int pageIndex, int biOffset)
    {
        var dict = new PdfDictionary();

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

            var key = InlineImageAbbreviations.ExpandKey(PdfObjectParser.ParseName(keyTok));
            var isColorSpaceKey = key.Equals(PdfName.ColorSpace);
            var isFilterKey = key.Equals(PdfName.Filter);

            lexer.SkipWhitespaceAndComments();
            var valueStart = lexer.Position;
            var valueTok = lexer.NextToken();

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

        // §8.9.7: "Unless the image uses ASCIIHexDecode or ASCII85Decode as one of its filters, the
        // ID operator shall be followed by a single white-space character, and the next character
        // shall be interpreted as the first byte of image data." "As ONE OF its filters" (not
        // merely "as its final filter", the narrower phrasing NOTE 2 below happens to use for its
        // own skip-without-decoding shortcut) is what decides this: a filter array may name either
        // one anywhere, not only last. NOTE 2: "if the final or only filter is ASCIIHexDecode or
        // ASCII85Decode skip any further white-space [after the first]" before counting /L's own
        // bytes; applied here to "as one of its filters" (matching the normative sentence, not
        // NOTE 2's narrower "final or only") for the same reason this reader treats every position
        // in a /Filter array as eligible elsewhere (CollectFilterNames itself does not distinguish
        // position either). Before this fix, at most one whitespace byte was consumed for every
        // filter shape, so 'BI /F /A85 /L 48 ID  <48-byte payload> EI' (two spaces after ID) came
        // out 3 bytes short: the fixed-at-one skip left the payload's own second byte behind as
        // data, and the rest re-lexed as content instead. §7.2.3: "The combination of a CARRIAGE
        // RETURN followed immediately by a LINE FEED shall be treated as one EOL marker", so a CR
        // immediately followed by an LF is consumed as that ONE separator, not as the separator
        // plus a data byte, before any of the above.
        var skipsExtraWhitespace = filterNames.Any(f => f.Value is "ASCIIHexDecode" or "ASCII85Decode");
        var consumedCrLf = false;
        if (lexer.TryPeek() is var separatorByte && separatorByte >= 0
            && PdfLexer.IsWhitespaceByte((byte)separatorByte))
        {
            if (separatorByte == (byte)'\r' && lexer.Position + 1 < _currentBuffer.Length
                && _currentBuffer.Span[lexer.Position + 1] == (byte)'\n')
            {
                lexer.Seek(lexer.Position + 2);
                consumedCrLf = true;
            }
            else
            {
                lexer.Seek(lexer.Position + 1);
            }

            if (skipsExtraWhitespace)
            {
                while (lexer.TryPeek() is var extraByte && extraByte >= 0
                    && PdfLexer.IsWhitespaceByte((byte)extraByte))
                    lexer.Seek(lexer.Position + 1);
            }
        }

        var dataStart = lexer.Position;

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
            var scanEnd = ScanForEi(dataStart);
            if (scanEnd is null)
            {
                ReportInlineImageMalformed(
                    "no 'EI' operator delimiting the image data could be found", ctx, diagnostics,
                    pageIndex);
                return false;
            }
            length = scanEnd.Value - dataStart;
            lengthFromScan = true;
        }

        var dataEnd = dataStart + length.Value;
        if (dataEnd < dataStart || dataEnd > _currentBuffer.Length)
        {
            ReportInlineImageMalformed(
                "the computed image data length runs past the end of the content stream", ctx,
                diagnostics, pageIndex);
            return false;
        }

        var data = _currentBuffer.Slice(dataStart, length.Value);
        var resyncPos = SkipToEi(dataEnd);

        // A tier-a (/L) or tier-b (computed) length that does not land on 'EI' is symmetric with
        // the /L-past-the-end case above: both recover through the same EI scan (tier c) rather
        // than losing the rest of the content stream outright. Tier c itself is excluded here
        // (lengthFromScan) since it already IS that fallback.
        if (resyncPos is null && !lengthFromScan)
        {
            // §7.2.3 treats a CR immediately followed by an LF as one EOL marker, but for binary
            // image data that reading is ambiguous: a producer may have meant only the CR as the ID
            // separator, with the LF as the image's own first byte. Retry once with the data window
            // shifted one byte earlier, since a payload that happens to begin with LF right after a
            // CR separator is exactly the case the CR-LF-as-one-marker choice above would otherwise
            // misjudge. The malformed report just below is skipped when this retry alone is what
            // recovers the image: a conforming file whose payload happens to start with LF right
            // after a lone CR separator must not carry a warning it recovered from cleanly
            // (#402 round 2; reporting unconditionally before the retry even ran is what made a
            // correctly-recovered file carry one anyway). The EI-scan fallback below is a
            // DIFFERENT case: reaching it at all means the declared or computed length was wrong
            // outright, not merely ambiguous, so recovering through IT still reports.
            var recoveredViaCrRetry = false;
            if (consumedCrLf)
            {
                var retryStart = dataStart - 1;
                var retryEnd = retryStart + length.Value;
                if (retryStart >= 0 && retryEnd <= _currentBuffer.Length)
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
                var scanEnd = ScanForEi(dataStart);
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
            ReportInlineImageMalformed(
                "no 'EI' operator was found at the computed end of the image data", ctx, diagnostics,
                pageIndex);
            return false;
        }

        lexer.Seek(resyncPos.Value);

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
            return null; // Table 91: /L is optional; falls through to tier b or the EI scan.

        if (lengthRaw is not PdfInteger lengthObj)
        {
            // Present but the wrong type (a PdfReal, say): reported the same way an invalid /W,
            // /H, or /BPC is (#402 round 2), rather than silently falling through to tier b/c as
            // if /L had never been written at all.
            ReportInlineImageMalformed(
                "'/L' is missing, or carries an invalid, non-integer value", ctx, diagnostics,
                pageIndex);
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
                $"An inline image's /CS names '/{csName.Value}', absent from the applicable "
                + "/Resources /ColorSpace dictionary.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
        }
        return -1;
    }

    // Tier (c): scan for whitespace-EI-whitespace/EOF, accepted only when the bounded probe just
    // past the candidate (LooksLikeResyncPoint) does not reject it.
    private int? ScanForEi(int dataStart)
    {
        var span = _currentBuffer.Span;
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

            if (!LooksLikeResyncPoint(after))
                continue;

            var dataEnd = i > dataStart && PdfLexer.IsWhitespaceByte(span[i - 1]) ? i - 1 : i;
            return dataEnd;
        }
        return null;
    }

    // Confirms an 'EI' candidate at exactly a known offset (used once tier a/b already computed a
    // length) by requiring the bytes there literally spell "EI" preceded and followed the way §8.9.7
    // describes; unlike ScanForEi this does not search, it verifies one position.
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

    // Bounded lookahead: lexes at most ProbeTokens tokens from a candidate resync point, over at
    // most ProbeWindowBytes bytes (ExtendedProbeWindowBytes on the retry, see LooksLikeResyncPoint
    // below), and accepts unless one of those tokens is a keyword this probe cannot justify as
    // legitimate content-stream syntax. Bounding both the token count and the byte window is what
    // keeps ScanForEi linear in the content length: an earlier version of this probe lexed all the
    // way to the end of the buffer from every candidate, which cost O(N) work per candidate and
    // made a content stream built from many false 'EI' candidates (a filtered image followed by
    // literal " EI (" text repeated many times, say) quadratic overall.
    //
    // A non-keyword token (a number, name, string, array or dictionary delimiter, whatever its
    // bytes) is neutral: it neither accepts nor rejects the candidate on its own. A keyword is
    // accepted when it is a Table A.1 operator (or true/false/null, or the one-byte content-mode
    // keywords '{', '}', '>' this lexer's own content-stream mode produces), and otherwise accepted
    // only when every one of its bytes is printable ASCII (0x21 to 0x7E): an unknown-but-printable
    // keyword is the kind of thing §7.8.2 already tolerates outside a compatibility section (a
    // future operator this reader does not know yet, a stray "R"), while a byte run containing
    // anything else is binary noise a coincidental "EI" byte pair inside DCT- or JPX-compressed
    // data would otherwise be mistaken for legitimate syntax. A 'BI' keyword ends the probe with
    // acceptance immediately, without lexing further: the bytes after ITS OWN following 'ID' are
    // raw image data and must not be judged as tokens at all.
    private enum ProbeOutcome { Accept, Reject, RanOffClippedWindow }

    private ProbeOutcome ProbeOnce(int pos, int windowBytes)
    {
        // Whether this window is itself an artificial cap, i.e. more buffer exists beyond it that
        // this probe deliberately does not look at. Only THAT case makes "ran off the window"
        // inconclusive rather than an outright rejection: a short buffer where the window reaches
        // the buffer's own true end behaves exactly like the unbounded lexer this probe replaces
        // (a token that never closes anywhere is malformed, full stop), so it must still reject
        // there. Matters for a short fixture, or a content stream that stops mid-token, where the
        // window and "the rest of the buffer" happen to coincide.
        var remaining = _currentBuffer.Length - pos;
        var windowClipped = remaining > windowBytes;
        var windowLength = Math.Min(windowBytes, remaining);
        var window = _currentBuffer.Slice(pos, windowLength);
        var probe = new PdfLexer(window, contentStreamMode: true);

        for (var i = 0; i < ProbeTokens; i++)
        {
            if (probe.AtEnd)
                return ProbeOutcome.Accept;

            Token token;
            try
            {
                token = probe.NextToken();
            }
            catch (InvalidDataException)
            {
                // Ran off the end of the window mid-token (an unterminated literal or hex string
                // straddling the boundary): inconclusive, since PdfLexer's own string readers
                // advance Position as they go, so Position sitting at or past the window's own
                // length here means the failure was purely the window's own limit, not malformed
                // bytes the probe saw, PROVIDED the window was clipped in the first place (see
                // above). Anything else, including running off a window that already reached the
                // buffer's own true end, rejects instead.
                return windowClipped && probe.Position >= window.Length
                    ? ProbeOutcome.RanOffClippedWindow
                    : ProbeOutcome.Reject;
            }

            if (token.Kind == TokenKind.EndOfInput)
                return ProbeOutcome.Accept;
            if (token.Kind != TokenKind.Keyword)
                continue;

            var raw = token.Raw.Span;
            if (raw.SequenceEqual("BI"u8))
                return ProbeOutcome.Accept;
            if (raw.SequenceEqual("true"u8) || raw.SequenceEqual("false"u8) || raw.SequenceEqual("null"u8))
                continue;
            if (raw.Length == 1 && (raw[0] == (byte)'{' || raw[0] == (byte)'}' || raw[0] == (byte)'>'))
                continue;
            if (ContentOperators.IsKnown(raw))
                continue;

            foreach (var b in raw)
            {
                if (b is < (byte)'!' or > (byte)'~')
                    return ProbeOutcome.Reject;
            }
        }
        return ProbeOutcome.Accept;
    }

    private bool LooksLikeResyncPoint(int pos)
    {
        var first = ProbeOnce(pos, ProbeWindowBytes);
        if (first != ProbeOutcome.RanOffClippedWindow)
            return first == ProbeOutcome.Accept;

        // A token ran off the clipped ProbeWindowBytes window: re-probe once with the much larger
        // ExtendedProbeWindowBytes window before accepting OR rejecting, since a legitimate token
        // following a false 'EI' candidate (a long literal string, say) can easily run past 128
        // bytes without being image data at all. Accepting on the first window's own inconclusive
        // result alone let a sufficiently long unterminated token mask a malformed stream, since
        // "ran off a clipped window" and "the token never closes" are indistinguishable from a
        // 128-byte window alone (#402 round 2: a DCT image without /L whose data contained an
        // unclosed literal string straddling the 128-byte mark, followed much later by the real
        // 'EI', reported ContentStreamLexError instead of InlineImageMalformed once the straddle
        // point crossed the window, and lost the trailing 'Q' to the caller's visitor). If a token
        // also runs off this larger window, the candidate is rejected outright rather than accepted
        // a second time.
        return ProbeOnce(pos, ExtendedProbeWindowBytes) == ProbeOutcome.Accept;
    }
}
