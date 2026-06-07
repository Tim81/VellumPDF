// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Two-phase renderer for <see cref="Paragraph"/>.
/// Performs greedy word-wrap in Layout; emits Td/Tj PDF operators in Draw.
/// </summary>
public sealed class ParagraphRenderer : IRenderer
{
    private readonly Paragraph _para;

    // Computed during Layout:
    private List<string>? _lines;
    private double        _lineHeight;
    private LayoutBox     _occupied;

    // Split point: if Partial, only lines[0.._splitAt] are in this renderer.
    private readonly int _startLine;
    private          int _endLine;  // exclusive

    public ParagraphRenderer(Paragraph para, int startLine = 0)
    {
        _para      = para;
        _startLine = startLine;
    }

    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_para.Margins);
        if (area.Width <= 0)
            return LayoutResult.Nothing();

        _lineHeight = _para.Style.EffectiveLeading;
        _lines    ??= WordWrap(_para.Text, _para.Style, area.Width);

        var maxLines = (int)Math.Floor(area.Height / _lineHeight);
        if (maxLines <= 0)
            return LayoutResult.Nothing();

        var remaining = _lines.Count - _startLine;
        if (remaining <= 0)
            return LayoutResult.Nothing();

        if (remaining <= maxLines)
        {
            _endLine  = _lines.Count;
            _occupied = area.WithHeight(remaining * _lineHeight);
            return LayoutResult.Full(_occupied);
        }

        // Partial: first maxLines lines fit
        _endLine  = _startLine + maxLines;
        _occupied = area.WithHeight(maxLines * _lineHeight);
        var overflow = new ParagraphRenderer(_para, _endLine) { _lines = _lines };
        var split    = new ParagraphRenderer(_para) { _lines = _lines, _endLine = _endLine, _lineHeight = _lineHeight, _occupied = _occupied };
        return LayoutResult.Partial(_occupied, split, overflow);
    }

    public void Draw(DrawContext ctx)
    {
        if (_lines is null) return;

        var fontResource = DocumentFontRegistry.GetOrCreate(_para.Style.Font);
        var area         = _occupied;
        var canvas       = ctx.Canvas;

        var (_, pdfY, _, _) = ctx.ToPdfRect(area);

        canvas.BeginText();
        canvas.SetFont(fontResource, _para.Style.FontSize);
        canvas.SetFillColorRgb(_para.Style.Color.R, _para.Style.Color.G, _para.Style.Color.B);

        var leading = _lineHeight;
        // Start at top-left of the area: PDF Y = area top → pdfY + height
        var startPdfY = ctx.ToPdfY(area.Y);
        canvas.SetTextMatrix(1, 0, 0, 1, area.X, startPdfY - _para.Style.FontSize);

        for (var i = _startLine; i < _endLine; i++)
        {
            var line    = _lines![i];
            var lineWidth = Standard14Metrics.MeasureString(_para.Style.Font, line, _para.Style.FontSize);

            double xOffset = _para.Alignment switch
            {
                HorizontalAlignment.Center  => (area.Width - lineWidth) / 2,
                HorizontalAlignment.Right   => area.Width - lineWidth,
                _ => 0
            };

            if (i == _startLine)
            {
                if (xOffset != 0)
                    canvas.MoveTextPosition(xOffset, 0);
            }
            else
            {
                canvas.MoveTextPosition(xOffset, -leading);
            }

            canvas.ShowText(line);
        }

        canvas.EndText();
    }

    private static List<string> WordWrap(string text, TextStyle style, double maxWidth)
    {
        var lines  = new List<string>();
        var words  = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        var currentW = 0.0;

        foreach (var word in words)
        {
            var wordW = Standard14Metrics.MeasureString(style.Font, word, style.FontSize);
            var spaceW = Standard14Metrics.MeasureString(style.Font, " ", style.FontSize);

            if (current.Length == 0)
            {
                current.Append(word);
                currentW = wordW;
            }
            else if (currentW + spaceW + wordW <= maxWidth)
            {
                current.Append(' ');
                current.Append(word);
                currentW += spaceW + wordW;
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
                currentW = wordW;
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }
}
