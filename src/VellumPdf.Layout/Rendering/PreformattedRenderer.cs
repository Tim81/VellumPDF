// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Canvas;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Renderer for <see cref="Preformatted"/>. It is internal, as <see cref="HeadingRenderer"/> is, so
/// it is only ever a top-level element added with <see cref="Document.Add(Preformatted)"/>, and the
/// checks the document runs before any layout see every instance at the width it will be laid out
/// at.
/// </summary>
/// <remarks>
/// Both passes lay out the same cached lines and read no pagination plan, so the measuring pass and
/// the drawing pass agree by construction.
/// </remarks>
internal sealed class PreformattedRenderer : IRenderer, IPageBreakSource, IPlacementSource
{
    // The continuation marker is a hook drawn with two open subpaths inside one column, 0.6 em wide
    // (the width the wrap reserves, PreformattedText.MarkerUnits), between the baseline and the cap
    // height. All factors are fractions of the font size. The stroke is 0.06 em, round-capped and
    // round-joined, so it reads at any size without a font glyph, which pdftotext would extract.
    private const double MarkerStrokeWidth = 0.06;
    private const double MarkerTop = 0.6;
    private const double MarkerFoot = 0.2;
    private const double MarkerRight = 0.48;
    private const double MarkerTip = 0.15;
    private const double MarkerBarbX = 0.27;
    private const double MarkerBarbUpper = 0.35;
    private const double MarkerBarbLower = 0.05;

    // A string shown with Tj longer than 32,767 bytes passes the implementation limit of ISO
    // 32000-1 Annex C and ISO 19005-2, 6.1.13. A line that fits the box only reaches that through
    // zero-advance glyphs, so the show is cut into runs of at most this many scalars, with no
    // positioning between them: 4,096 scalars are at most 8,192 bytes in either encoding.
    private const int MaxScalarsPerShow = 4096;

    private readonly Preformatted _element;

    private List<PreformattedLine>? _lines;
    private int _wrappedSourceLines;
    private double _wrapWidth;
    private double _lineHeight;
    private LayoutBox _occupied;

    private readonly int _startLine;
    private int _endLine;

    /// <summary>How many times this instance wrapped its text; the cache keeps it at one.</summary>
    internal int WrapCalls { get; private set; }

    /// <summary>The structure type of each drawn piece in a tagged document.</summary>
    internal string StructType { get; init; } = "P";

    /// <summary>The element this renderer draws.</summary>
    internal Preformatted Element => _element;

    internal PreformattedRenderer(Preformatted element, int startLine = 0)
    {
        _element = element;
        _startLine = startLine;
        _lineHeight = element.Style.EffectiveLeading;
    }

    PageBreakMode IPageBreakSource.BreakBefore => _element.BreakBefore;

    // Explicit, as in the other renderers. The anchor lands where the first line does, so only the
    // piece that starts at line 0 reports it; the overflow of a split block starts later and
    // reports nothing, which keeps the destination on the page the first line is on. The box is
    // the occupied area, whose top is the top of the first line.
    void IPlacementSource.ReportPlacements(PlacementSink sink)
    {
        if (_startLine == 0 && _element.Anchor is { } anchor)
            sink.ReportAnchor(_element, anchor, _occupied);
    }

    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_element.Margins);
        if (area.Width <= 0)
            return LayoutResult.Nothing();

        if (_lines is null)
        {
            _lines = PreformattedText.Layout(_element, area.Width, out _wrappedSourceLines);
            _wrapWidth = area.Width;
            WrapCalls++;
        }

        var maxLines = (int)Math.Floor(area.Height / _lineHeight);
        if (maxLines <= 0)
            return LayoutResult.Nothing();

        var remaining = _lines.Count - _startLine;
        if (remaining <= 0)
            return LayoutResult.Nothing();

        if (remaining <= maxLines)
        {
            _endLine = _lines.Count;
            _occupied = area.WithHeight(remaining * _lineHeight);
            return LayoutResult.Full(_occupied);
        }

        _endLine = _startLine + maxLines;
        _occupied = area.WithHeight(maxLines * _lineHeight);

        var overflow = new PreformattedRenderer(_element, _endLine)
        {
            _lines = _lines,
            _wrappedSourceLines = _wrappedSourceLines,
            _wrapWidth = _wrapWidth,
            StructType = StructType,
        };
        var split = new PreformattedRenderer(_element, _startLine)
        {
            _lines = _lines,
            _wrappedSourceLines = _wrappedSourceLines,
            _wrapWidth = _wrapWidth,
            _endLine = _endLine,
            _occupied = _occupied,
            StructType = StructType,
        };
        return LayoutResult.Partial(_occupied, split, overflow);
    }

    public void Draw(DrawContext ctx)
    {
        if (_lines is null)
            return;

        // Reported from the drawing pass, once per element: a page-counting pass lays the same
        // renderer out again, and every piece of a split shares the wrap but only the first starts
        // at line 0.
        if (_startLine == 0 && _wrappedSourceLines > 0)
        {
            ctx.Diagnostics?.Report(
                LayoutDiagnosticCode.PreformattedLineWrapped,
                0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_wrappedSourceLines} line(s) wider than the content width of {_wrapWidth:0.##} pt were wrapped and marked as continued."),
                _wrappedSourceLines,
                perElement: true);
        }

        var style = _element.Style;
        var area = _occupied;
        var canvas = ctx.Canvas;
        var size = style.FontSize;

        // Tagged PDF: BDC must open before BeginText so the BDC...EMC encloses BT...ET.
        var mcid = -1;
        if (ctx.Tagged)
            mcid = canvas.BeginMarkedContent(StructType);

        canvas.BeginText();

        var fontSelected = false;
        for (var k = _startLine; k < _endLine; k++)
        {
            var text = _lines[k].Text;
            if (text.Length == 0)
                continue;

            if (!fontSelected)
            {
                TextEmitter.SelectedFont? current = null;
                TextEmitter.SelectFont(ctx, canvas, style, ref current);
                canvas.SetFillColorRgb(style.Color.R, style.Color.G, style.Color.B);
                fontSelected = true;
            }

            canvas.SetTextMatrix(1, 0, 0, 1, area.X, Baseline(ctx, area, size, k));
            Show(canvas, style, text);
        }

        canvas.EndText();

        if (ctx.Tagged && mcid >= 0)
        {
            canvas.EndMarkedContent();
            ctx.RegisterStructElem(new PdfStructElem(StructType) { Mcid = mcid });
        }

        for (var k = _startLine; k < _endLine; k++)
        {
            if (_lines[k].Continued)
                DrawMarker(ctx, canvas, style, area.X + style.FontRef.MeasureString(_lines[k].Text, size), Baseline(ctx, area, size, k));
        }
    }

    private double Baseline(DrawContext ctx, LayoutBox area, double size, int line) =>
        ctx.ToPdfY(area.Y) - size - (line - _startLine) * _lineHeight;

    // The runs follow one another on the text line by the advance of the glyphs shown, so no Tm
    // sits between them.
    private static void Show(PdfCanvas canvas, TextStyle style, string text)
    {
        var from = 0;
        while (from < text.Length)
        {
            var to = from;
            var scalars = 0;
            while (to < text.Length && scalars < MaxScalarsPerShow)
            {
                to += char.IsHighSurrogate(text[to]) && to + 1 < text.Length ? 2 : 1;
                scalars++;
            }

            // Through TextEmitter so a glyph 0 is listed with its character, as a paragraph's is.
            TextEmitter.Show(canvas, style, from == 0 && to == text.Length ? text : text[from..to]);
            from = to;
        }
    }

    // An artifact: a mechanical layout mark, so it is kept out of the structure tree (ISO 14289-1,
    // 7.1) and out of extracted text. The graphics state it sets is inside q ... Q so a custom
    // renderer's leftover dash, cap or join cannot change it and it cannot reach later content.
    private void DrawMarker(DrawContext ctx, PdfCanvas canvas, TextStyle style, double x0, double y)
    {
        var s = style.FontSize;
        if (ctx.Tagged)
            canvas.BeginArtifactMarkedContent();
        canvas.SaveState();
        canvas.SetStrokeColorRgb(style.Color.R, style.Color.G, style.Color.B);
        canvas.SetLineCap(1);
        canvas.SetLineJoin(1);
        canvas.SetSolidLine();
        canvas.SetLineWidth(MarkerStrokeWidth * s);
        canvas.MoveTo(x0 + MarkerRight * s, y + MarkerTop * s);
        canvas.LineTo(x0 + MarkerRight * s, y + MarkerFoot * s);
        canvas.LineTo(x0 + MarkerTip * s, y + MarkerFoot * s);
        canvas.Stroke();
        canvas.MoveTo(x0 + MarkerBarbX * s, y + MarkerBarbUpper * s);
        canvas.LineTo(x0 + MarkerTip * s, y + MarkerFoot * s);
        canvas.LineTo(x0 + MarkerBarbX * s, y + MarkerBarbLower * s);
        canvas.Stroke();
        canvas.RestoreState();
        if (ctx.Tagged)
            canvas.EndMarkedContent();
    }
}
