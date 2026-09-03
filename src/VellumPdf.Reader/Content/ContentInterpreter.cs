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
/// </remarks>
internal sealed class ContentInterpreter
{
    // ISO 32000-2 §7.8.2 gives an operator's own operands no declared bound; this reader's own
    // ceiling against a hostile or corrupted stream that never emits an operator at all.
    private const int MaxOperandsPerOperator = 32;

    // §9.4.3's own TJ array holds a mix of strings and numeric adjustments; this reader's own
    // ceiling on how many elements one such array may carry.
    private const int MaxTjElements = 8192;

    // §8.4.4's q/Q pair; this reader's own ceiling on how deep a legitimate document nests them.
    private const int MaxGraphicsStateDepth = 64;

    // §14.6.2's BMC/BDC/EMC nesting; this reader's own ceiling, mirroring MaxGraphicsStateDepth.
    private const int MaxMarkedContentDepth = 64;

    // This interpreter's own budget on total successful Form XObject recursions across one page
    // (§8.10), independent of PdfReaderOptions.MaxFormXObjectDepth, which bounds nesting DEPTH
    // rather than the total COUNT of forms a page may draw. A wide, shallow graph (one page
    // invoking the same shallow form thousands of times) is not caught by a depth cap at all.
    private const int MaxFormInvocationsPerPage = 4096;

    // This reader's own ceiling on the total decoded bytes one page's /Contents may contribute,
    // summed across every stream in the array (ISO 32000-2 §7.7.3.3 Table 31).
    private const long MaxContentBytes = 64L * 1024 * 1024;

    private static readonly PdfName XObjectSubtypeForm = new("Form");
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
    private readonly HashSet<int> _openForms = [];
    private int _formDepth;
    private int _formInvocations;
    private readonly HashSet<string> _reportedUnknownOperators = [];
    private ReadOnlyMemory<byte> _currentBuffer;

    /// <summary>The current graphics state, the top of the <c>q</c>/<c>Q</c> stack, readable from
    /// inside an <see cref="IContentVisitor"/> callback. Mutated in place; a callback that needs a
    /// value after the interpreter moves on must copy it.</summary>
    internal GraphicsState GraphicsState => _gs;

    /// <summary>The current text-positioning state, readable the same way as
    /// <see cref="GraphicsState"/>.</summary>
    internal TextState TextState => _textState;

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
        _openForms.Clear();
        _formDepth = 0;
        _formInvocations = 0;
        _reportedUnknownOperators.Clear();

        var diagnostics = _reader.CreateContentDiagnosticScope();
        var pageIndex = page.Index;

        var buffer = BuildPageContentBuffer(page, diagnostics, pageIndex, out var soleObjectNumber);
        if (buffer.IsEmpty)
            return;

        var ctx = new StreamContext(page.Resources, soleObjectNumber);
        InterpretStream(buffer, ctx, visitor, pageIndex, diagnostics);
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

        void AddElement(PdfObject element)
        {
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

            chunks.Add(decoded);
            contributingStreams++;
            soleObjectNumberLocal = contributingStreams == 1 ? stream.ObjectNumber : null;
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
        return Concatenate(chunks, diagnostics, pageIndex);
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
    // MaxContentBytes across the total, truncating at the last whitespace boundary within budget
    // rather than mid-token, so what IS interpreted is a clean prefix rather than one broken by an
    // artificial cut.
    private static ReadOnlyMemory<byte> Concatenate(
        List<byte[]> chunks, DiagnosticSink diagnostics, int pageIndex)
    {
        if (chunks.Count == 0)
            return ReadOnlyMemory<byte>.Empty;

        long total = 0;
        foreach (var chunk in chunks)
            total += chunk.Length + 1; // +1 for the separator this method inserts after each chunk

        if (total <= MaxContentBytes)
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
        var capped = new byte[MaxContentBytes];
        var written = 0;
        foreach (var chunk in chunks)
        {
            var remaining = (int)Math.Min(MaxContentBytes - written, int.MaxValue);
            if (remaining <= 0)
                break;

            if (chunk.Length + 1 <= remaining)
            {
                chunk.CopyTo(capped, written);
                written += chunk.Length;
                capped[written++] = (byte)'\n';
                continue;
            }

            var take = Math.Min(chunk.Length, remaining);
            while (take > 0 && !PdfLexer.IsWhitespaceByte(chunk[take - 1]))
                take--;
            Array.Copy(chunk, capped, take);
            written += take;
            break;
        }

        diagnostics.Report(
            PdfReaderDiagnosticCode.ContentStreamTooLarge,
            $"The page's /Contents exceeded the {MaxContentBytes / (1024 * 1024)} MiB decoded-size "
            + "cap; interpretation stopped there.",
            pageIndex: pageIndex);

        return new ReadOnlyMemory<byte>(capped, 0, written);
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
                PdfReaderDiagnosticCode.OperandStackMalformed,
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
            // ISO 32000-2 §7.8.2: "an error shall occur" outside a compatibility section; this
            // reader instead notifies and continues. Deliberately does NOT clear the operand stack
            // (this reader's own leniency, distinct from Table 33's "ignored ... along with
            // operands" for a genuine future operator inside BX/EX): the most common way an
            // unrecognised keyword appears in an otherwise-conforming stream is a stray "R" left
            // over from indirect-reference syntax that §7.8.2 forbids in content streams at all
            // ("Indirect objects and object references shall not be permitted"), and the operands
            // that precede it usually belong to whatever REAL operator follows, not to "R" itself.
            if (_bxDepth == 0 && _reportedUnknownOperators.Add(name))
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.UnknownOperator,
                    $"'{name}' is not one of the operators ISO 32000-2 Annex A Table A.1 defines; "
                    + "it was ignored.",
                    pageIndex: pageIndex);
            }
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
            if (_bxDepth > 0)
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
                if (_operands[0] is not PdfArray tjArray || tjArray.Count > MaxTjElements)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.OperandStackMalformed,
                        $"TJ's array operand is missing or exceeds {MaxTjElements} elements; it was "
                        + "dropped.",
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
            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
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
        if (_gsStack.Count == 0)
        {
            // An unbalanced 'q' still open at end of stream is fine (nothing downstream needs the
            // state restored past the last operator this interpreter saw); an unbalanced 'Q' is the
            // opposite problem (a restore with nothing to restore), so this one is reported.
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
            diagnostics.Report(
                PdfReaderDiagnosticCode.OperandStackMalformed,
                $"Marked-content nesting exceeded {MaxMarkedContentDepth} levels; further "
                + "'BMC'/'BDC' operators were ignored.",
                ctx.DiagObjectNumber, pageIndex: pageIndex);
            return;
        }
        _markedContentDepth++;
    }

    private void PopMarkedContent(StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (_markedContentDepth == 0)
        {
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
        // reference to a font dictionary". Every other ExtGState entry is out of scope for this
        // interpreter: it neither positions text nor renders colour or transparency.
        if (extGState.Get(FontKey) is PdfArray fontArray && fontArray.Count == 2)
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
            return;

        var stream = _reader.ResolveStream(xobjectRef);
        if (stream is null)
            return;

        if (stream.Dictionary.Get(PdfName.Subtype) is not PdfName subtype || !subtype.Equals(XObjectSubtypeForm))
            return; // An Image XObject, or anything else: no recursion; the caller already got Do.

        var objectNumber = stream.ObjectNumber;

        if (_formInvocations >= MaxFormInvocationsPerPage)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.FormXObjectBudgetExceeded,
                $"The page invoked more than {MaxFormInvocationsPerPage} Form XObjects; further "
                + "'Do' recursions were skipped for the rest of the page.",
                pageIndex: pageIndex);
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

        if (!_openForms.Add(objectNumber))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.FormXObjectCycle,
                $"Form XObject {objectNumber} invokes itself, directly or through a chain of nested "
                + "'Do' operators; the recursive invocation was skipped.",
                objectNumber, pageIndex: pageIndex);
            return;
        }

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
                byte[]? decoded;
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
                    var formCtx = new StreamContext(formResources, objectNumber);
                    InterpretStream(decoded, formCtx, visitor, pageIndex, diagnostics);
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
        if (formDict.Get(MatrixKey) is PdfArray arr && arr.Count == 6 && TryReadNumbers(arr, out var v))
            return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
        return Matrix.Identity; // §8.10.2 Table 93's own default.
    }

    private PdfRectangle? ReadFormBBox(PdfDictionary formDict)
    {
        if (formDict.Get(BBoxKey) is PdfArray arr && arr.Count == 4 && TryReadNumbers(arr, out var v))
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
            else if (valueTok.Kind == TokenKind.ArrayBegin && isFilterKey)
            {
                lexer.Seek(valueStart);
                var arr = (PdfArray)parser.ParseObject();
                var items = new List<PdfObject>(arr.Count);
                for (var i = 0; i < arr.Count; i++)
                {
                    items.Add(arr[i] is PdfName elName
                        ? InlineImageAbbreviations.ExpandColorSpaceOrFilterName(elName, isColorSpace: false)
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

        // §8.9.7: "Unless the image uses ASCIIHexDecode or ASCII85Decode ..., the ID operator shall
        // be followed by a single white-space character, and the next character shall be
        // interpreted as the first byte of image data." Consuming at most one whitespace byte here
        // is correct for every filter, including AHx/A85, since a producer is free to include that
        // one separating byte regardless (the spec exempts them from being REQUIRED to, not from
        // being ALLOWED to).
        if (lexer.TryPeek() is var b && b >= 0 && PdfLexer.IsWhitespaceByte((byte)b))
            lexer.Seek(lexer.Position + 1);

        var dataStart = lexer.Position;
        var filterNames = CollectFilterNames(dict);
        var hasDisallowedFilter = filterNames.Any(f =>
            f.Value is "JBIG2Decode" or "JPXDecode" or "Crypt");

        var length = TryLengthFromDictionary(dict, dataStart, out var lengthPastEnd);
        if (lengthPastEnd)
        {
            ReportInlineImageMalformed(
                "/L names a length past the end of the content stream", ctx, diagnostics, pageIndex);
        }

        if (length is null && filterNames.Count == 0)
            length = TryComputeUnfilteredLength(dict, ctx, dataStart, diagnostics, pageIndex);

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
    private int? TryLengthFromDictionary(PdfDictionary dict, int dataStart, out bool pastEnd)
    {
        pastEnd = false;
        if (dict.Get(PdfName.Length) is not PdfInteger lengthObj || lengthObj.Value < 0)
            return null;

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

        if (width is null || height is null || bpc is null || width < 0 || height < 0 || bpc <= 0)
        {
            ReportInlineImageMalformed(
                "an unfiltered image is missing /W, /H, or /BPC needed to compute its data length",
                ctx, diagnostics, pageIndex);
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

    private static int? ReadIntEntry(PdfDictionary dict, PdfName key) => dict.Get(key) switch
    {
        PdfInteger i => (int)i.Value,
        PdfReal r => (int)r.Value,
        _ => null,
    };

    private int ResolveComponentCount(
        PdfDictionary dict, StreamContext ctx, DiagnosticSink diagnostics, int pageIndex)
    {
        if (dict.Get(PdfName.ColorSpace) is not PdfName csName)
            return -1;

        if (csName.Equals(PdfName.DeviceRGB)) return 3;
        if (csName.Value == "DeviceCMYK") return 4;
        if (csName.Value == "DeviceGray") return 1;
        if (csName.Value == "Indexed") return 1;

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

    // Tier (c): scan for whitespace-EI-whitespace/EOF, accepted only when what follows lexes as
    // operators or EOF in content mode (the false-EI-inside-DCT-data problem).
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

    // Bounded lookahead: lexes up to a handful of tokens in content mode from a candidate resync
    // point and accepts it if none of them throws before either running out of tokens to try or
    // reaching end of input. This is what rejects a coincidental "EI" byte pair sitting inside
    // DCT-compressed binary data, which is followed by more binary noise the lexer chokes on almost
    // immediately (an unterminated literal string or hex string is the most common trip).
    private bool LooksLikeResyncPoint(int pos)
    {
        var probe = new PdfLexer(_currentBuffer, contentStreamMode: true);
        probe.Seek(pos);
        try
        {
            for (var i = 0; i < 8; i++)
            {
                if (probe.AtEnd)
                    return true;
                if (probe.NextToken().Kind == TokenKind.EndOfInput)
                    return true;
            }
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
