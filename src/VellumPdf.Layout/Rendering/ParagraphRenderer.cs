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

        var fontResource = ctx.GetFont(_para.Style.Font);
        var area         = _occupied;
        var canvas       = ctx.Canvas;

        canvas.BeginText();
        canvas.SetFont(fontResource, _para.Style.FontSize);
        canvas.SetFillColorRgb(_para.Style.Color.R, _para.Style.Color.G, _para.Style.Color.B);

        var leading   = _lineHeight;
        var startPdfY = ctx.ToPdfY(area.Y) - _para.Style.FontSize;

        for (var i = _startLine; i < _endLine; i++)
        {
            var line      = _lines![i];
            var lineWidth = Standard14Metrics.MeasureString(_para.Style.Font, line, _para.Style.FontSize);

            // Use SetTextMatrix for every line to avoid horizontal drift when
            // Center/Right alignment produces non-zero xOffset that accumulates
            // via successive Td (relative) calls.
            double xOffset = _para.Alignment switch
            {
                HorizontalAlignment.Center => (area.Width - lineWidth) / 2,
                HorizontalAlignment.Right  => area.Width - lineWidth,
                _ => 0
            };

            var linePdfY = startPdfY - (i - _startLine) * leading;
            canvas.SetTextMatrix(1, 0, 0, 1, area.X + xOffset, linePdfY);

            canvas.ShowText(line);
        }

        canvas.EndText();
    }

    private static List<string> WordWrap(string text, TextStyle style, double maxWidth)
    {
        var lines   = new List<string>();
        var words   = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        var currentW = 0.0;

        foreach (var word in words)
        {
            var wordW  = Standard14Metrics.MeasureString(style.Font, word, style.FontSize);
            var spaceW = Standard14Metrics.MeasureString(style.Font, " ", style.FontSize);

            if (current.Length == 0)
            {
                // If the word alone is wider than the line, hard-break it at char granularity.
                if (wordW > maxWidth)
                {
                    HardBreak(word, style, maxWidth, lines);
                    // currentW stays 0; current stays empty (HardBreak always ends with a full flush)
                    continue;
                }
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
                if (wordW > maxWidth)
                {
                    HardBreak(word, style, maxWidth, lines);
                    currentW = 0.0;
                }
                else
                {
                    current.Append(word);
                    currentW = wordW;
                }
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    /// <summary>
    /// Splits a single word that is wider than the available line width at character
    /// granularity and appends each fragment to <paramref name="lines"/>.
    /// </summary>
    private static void HardBreak(string word, TextStyle style, double maxWidth, List<string> lines)
    {
        var fragment = new System.Text.StringBuilder();
        var fragmentW = 0.0;

        foreach (var ch in word)
        {
            var charW = Standard14Metrics.MeasureString(style.Font, ch.ToString(), style.FontSize);
            if (fragment.Length > 0 && fragmentW + charW > maxWidth)
            {
                lines.Add(fragment.ToString());
                fragment.Clear();
                fragmentW = 0.0;
            }
            fragment.Append(ch);
            fragmentW += charW;
        }
        if (fragment.Length > 0) lines.Add(fragment.ToString());
    }
}
