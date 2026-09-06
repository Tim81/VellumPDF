// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader.Content;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader;

/// <summary>
/// Positions and assembles one page's own text, the <c>ContentInterpreter</c> visitor analogue of
/// <see cref="ImageReachabilityWalker"/> (#98): real work happens in <see cref="OnOperator"/> for
/// the four text-showing operators (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c>); everything else is a
/// no-op, since Form XObject recursion, the composed CTM, and every other operator's own state are
/// already the interpreter's job.
/// </summary>
internal sealed class TextExtractionVisitor : IContentVisitor
{
    private static readonly PdfName FontKey = PdfName.Font;

    private readonly PdfDocumentReader _reader;
    private readonly ContentInterpreter _interpreter;
    private readonly TextCallBudget _budget;
    private readonly DiagnosticSink _diagnostics;
    private readonly int _pageIndex;
    private readonly TextAssembler _assembler;

    // Keyed on REFERENCE equality of both the Tf (or gs /Font) operand and the resources dictionary
    // in effect when it is looked up, not structural equality: PdfName compares by value, so two
    // unrelated "/F1" operands from two different /Resources subdictionaries would otherwise
    // collide on one cache entry and hand one font's glyphs the other's metrics.
    private readonly Dictionary<(PdfObject Font, PdfDictionary? Resources), PdfFontReader?> _fontLookup =
        new(FontLookupKeyComparer.Instance);

    internal TextExtractionVisitor(
        PdfDocumentReader reader, ContentInterpreter interpreter, TextCallBudget budget,
        DiagnosticSink diagnostics, int pageIndex)
    {
        _reader = reader;
        _interpreter = interpreter;
        _budget = budget;
        _diagnostics = diagnostics;
        _pageIndex = pageIndex;
        _assembler = new TextAssembler(pageIndex, budget);
    }

    /// <summary>Every run this page's walk produced, once <see cref="Finish"/> has closed whatever
    /// run was still open.</summary>
    internal IReadOnlyList<TextRun> Runs => _assembler.Runs;

    /// <summary>Closes the assembler's own last open run. Call once after <c>Run</c> or
    /// <c>RunFormXObject</c> returns for this page.</summary>
    internal void Finish() => _assembler.Finish();

    public void OnOperator(string operatorName, IReadOnlyList<PdfObject> operands, int offset)
    {
        if (_budget.IsPageExhausted)
            return;

        switch (operatorName)
        {
            case "Tj":
                if (TryGetShowableBytes(operands[0], out var tjBytes))
                    ShowString(tjBytes);
                else
                    ReportNonStringOperand();
                break;

            case "'":
                // The interpreter already performed '"s own T*-equivalent move (Table 107) before
                // this call; ValidateOperandTypes already guarantees operands[0] is a string.
                ShowString(GetBytes(operands[0]));
                break;

            case "\"":
                // The interpreter already set Tw/Tc and performed the T*-equivalent move (Table
                // 107) before this call; the string to show is operands[2], not operands[0].
                ShowString(GetBytes(operands[2]));
                break;

            case "TJ":
                // ContentInterpreter's own switch already confirmed operands[0] is a PdfArray.
                ShowTJArray((PdfArray)operands[0]);
                break;
        }
    }

    public void OnFormBegin(
        PdfDictionary formDictionary, Matrix formMatrix, PdfRectangle? boundingBox, int objectNumber,
        int offset)
    {
    }

    public void OnFormEnd(int objectNumber)
    {
    }

    public void OnInlineImage(PdfDictionary dictionary, ReadOnlyMemory<byte> data, int offset)
    {
    }

    public void OnImageXObject(ParsedStream stream, int offset)
    {
    }

    // ── Text showing (ISO 32000-2 §9.4.3) ───────────────────────────────────────────────────────

    private void ShowTJArray(PdfArray array)
    {
        var gs = _interpreter.GraphicsState;
        for (var i = 0; i < array.Count; i++)
        {
            if (_budget.IsPageExhausted)
                return;

            switch (array[i])
            {
                case PdfLiteralString or PdfHexString:
                    ShowString(GetBytes(array[i]));
                    break;

                case PdfInteger or PdfReal:
                    // Table 107's own prose form: a standalone translation, not folded into the
                    // next glyph's own displacement (see GlyphPositioner.ComputeNumericAdjustment's
                    // own remarks for why). Neither Tc nor Tw applies to it.
                    var tj = NumberValue(array[i]);
                    var tx = GlyphPositioner.ComputeNumericAdjustment(tj, gs.FontSize, gs.HorizontalScaling);
                    _interpreter.TextState.TextMatrix =
                        Matrix.Translation(tx, 0).Concat(_interpreter.TextState.TextMatrix);
                    break;

                default:
                    ReportNonStringOperand();
                    break;
            }
        }
    }

    private void ShowString(ReadOnlyMemory<byte> bytes)
    {
        var gs = _interpreter.GraphicsState;
        if (gs.Font is null)
        {
            // §9.3.1: font and size have no initial value and "shall be specified explicitly using
            // Tf before any text is shown." No characters, no advance: there is no font to decode
            // this string's codes with at all.
            _diagnostics.Report(
                PdfReaderDiagnosticCode.TextShownWithoutFont,
                "A text-showing operator ran before any 'Tf' set a font (ISO 32000-2 §9.3.1); it "
                + "produced no characters.",
                pageIndex: _pageIndex);
            return;
        }

        var fontReader = ResolveFont(gs.Font, _interpreter.CurrentResources);
        if (fontReader is null)
            return; // Already reported (400/405), or the /Font resource itself is missing (306).

        var span = bytes.Span;
        var offset = 0;
        while (offset < span.Length)
        {
            if (_budget.IsPageExhausted)
                return;

            if (!fontReader.TryDecodeNext(span, ref offset, out var glyph))
                break;

            if (!_budget.TryConsumeGlyph(_pageIndex))
                return;

            var tm = _interpreter.TextState.TextMatrix;
            var ctm = gs.Ctm;
            var trm = GlyphPositioner.ComputeTextRenderingMatrix(
                gs.FontSize, gs.HorizontalScaling, gs.Rise, tm, ctm);
            // The line-grouping key: the same Trm with rise forced to zero (see
            // PositionedGlyph.LineY's own remarks for why rise must not enter it).
            var lineY = GlyphPositioner.ComputeTextRenderingMatrix(
                gs.FontSize, gs.HorizontalScaling, 0, tm, ctm).F;
            var tx = GlyphPositioner.ComputeGlyphDisplacement(
                glyph, gs.FontSize, gs.CharSpacing, gs.WordSpacing, gs.HorizontalScaling);

            // §404 (UnmappedGlyphs) already covers the no-Unicode-route case from
            // PdfFontReader.TryDecodeNext itself; this reader adds no character for it but still
            // applies the advance below, so later glyphs on the line stay correctly positioned.
            var characters = glyph.Unicode ?? string.Empty;
            if (characters.Length > 0)
            {
                if (!_budget.TryConsumeCharacters(characters.Length, _pageIndex))
                    return;

                var positioned = new PositionedGlyph(
                    characters, glyph.Code, trm, lineY, tx, gs.FontSize, gs.RenderMode);
                if (!_assembler.Add(positioned))
                    return;
            }

            _interpreter.TextState.TextMatrix = Matrix.Translation(tx, 0).Concat(tm);
        }
    }

    private void ReportNonStringOperand() =>
        _diagnostics.Report(
            PdfReaderDiagnosticCode.OperandStackMalformed,
            "A text-showing operand was neither a string nor (inside a 'TJ' array) a number; it "
            + "was skipped.",
            pageIndex: _pageIndex);

    // ── Font resolution (ISO 32000-2 §9.3.1 Table 103, §8.4.5 Table 57) ────────────────────────

    private PdfFontReader? ResolveFont(PdfObject fontOperand, PdfDictionary? resources)
    {
        var key = (fontOperand, resources);
        if (_fontLookup.TryGetValue(key, out var cached))
            return cached;

        // A bare Tf operand is a /Resources /Font name; an ExtGState's own /Font array already
        // names the font dictionary directly (GraphicsState.Font's own doc explains why), so only
        // the PdfName case needs a resource-dictionary lookup at all.
        PdfObject? rawFontEntry = fontOperand;
        if (fontOperand is PdfName fontName)
        {
            rawFontEntry = null;
            if (resources is not null
                && resources.Get(FontKey) is { } fontDictRaw
                && _reader.ResolveValue(fontDictRaw) is PdfDictionary fontDict
                && fontDict.Get(fontName) is { } entry and not PdfNull)
            {
                rawFontEntry = entry;
            }
        }

        // rawFontEntry is null exactly when the /Resources /Font lookup itself failed: the
        // interpreter's own ValidateFontResource already reported ResourceMissing (306) for that at
        // 'Tf' time, so nothing further is reported here.
        var fontReader = rawFontEntry is null
            ? null
            : _reader.GetFontReader(rawFontEntry, _diagnostics, _pageIndex);

        _fontLookup[key] = fontReader;
        return fontReader;
    }

    // ── Operand helpers ──────────────────────────────────────────────────────────────────────────

    private static bool TryGetShowableBytes(PdfObject obj, out ReadOnlyMemory<byte> bytes)
    {
        switch (obj)
        {
            case PdfLiteralString s:
                bytes = s.Bytes;
                return true;
            case PdfHexString h:
                bytes = h.Bytes;
                return true;
            default:
                bytes = default;
                return false;
        }
    }

    private static ReadOnlyMemory<byte> GetBytes(PdfObject obj) => obj switch
    {
        PdfLiteralString s => s.Bytes,
        PdfHexString h => h.Bytes,
        _ => ReadOnlyMemory<byte>.Empty,
    };

    private static double NumberValue(PdfObject obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => 0,
    };

    private sealed class FontLookupKeyComparer : IEqualityComparer<(PdfObject Font, PdfDictionary? Resources)>
    {
        internal static readonly FontLookupKeyComparer Instance = new();

        public bool Equals(
            (PdfObject Font, PdfDictionary? Resources) x, (PdfObject Font, PdfDictionary? Resources) y) =>
            ReferenceEquals(x.Font, y.Font) && ReferenceEquals(x.Resources, y.Resources);

        public int GetHashCode((PdfObject Font, PdfDictionary? Resources) obj) => HashCode.Combine(
            RuntimeHelpers.GetHashCode(obj.Font),
            obj.Resources is null ? 0 : RuntimeHelpers.GetHashCode(obj.Resources));
    }
}
