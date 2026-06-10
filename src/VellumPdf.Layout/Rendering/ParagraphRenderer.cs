// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Two-phase renderer for <see cref="Paragraph"/>.
/// Phase 1 (Layout): greedy word-wrap across all runs; produces a list of LineFragment arrays.
/// Phase 2 (Draw):   emits Tm/Tj/Tw PDF operators, switching font+colour only on change.
/// Supports Standard-14 fonts (Latin-1 ShowText) and embedded TrueType (hex glyph run).
/// Justification: Tw for Standard-14 lines; explicit per-word Tm for embedded-font lines.
/// </summary>
public sealed class ParagraphRenderer : IRenderer
{
    private readonly Paragraph _para;

    // Computed during Layout:
    private List<LineFragment[]>? _lines;
    private double _lineHeight;      // max EffectiveLeading across the paragraph
    private LayoutBox _occupied;

    // Pagination window: only lines[_startLine.._endLine) are in this renderer.
    private readonly int _startLine;
    private int _endLine;  // exclusive

    /// <summary>Creates a renderer for the paragraph, optionally starting at <paramref name="startLine"/> for pagination.</summary>
    public ParagraphRenderer(Paragraph para, int startLine = 0)
    {
        _para = para;
        _startLine = startLine;
    }

    // ── Phase 1: Layout ───────────────────────────────────────────────────────

    /// <summary>Word-wraps the paragraph and fits as many lines as the area allows, splitting at line boundaries on overflow.</summary>
    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_para.Margins);
        if (area.Width <= 0)
            return LayoutResult.Nothing();

        _lines ??= WordWrap(_para.Runs, area.Width);

        // Line height = max EffectiveLeading across every fragment in every line.
        _lineHeight = 0;
        foreach (var line in _lines)
            foreach (var frag in line)
                _lineHeight = Math.Max(_lineHeight, frag.Style.EffectiveLeading);
        if (_lineHeight <= 0)
            _lineHeight = TextStyle.Default.EffectiveLeading;

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

        // Partial: first maxLines lines fit
        _endLine = _startLine + maxLines;
        _occupied = area.WithHeight(maxLines * _lineHeight);

        var overflow = new ParagraphRenderer(_para, _endLine) { _lines = _lines, StructType = StructType, ParentStructElem = ParentStructElem, ElementLanguage = ElementLanguage };
        var split = new ParagraphRenderer(_para, _startLine)
        {
            _lines = _lines,
            _endLine = _endLine,
            _lineHeight = _lineHeight,
            _occupied = _occupied,
            StructType = StructType,
            ParentStructElem = ParentStructElem,
            ElementLanguage = ElementLanguage,
        };
        return LayoutResult.Partial(_occupied, split, overflow);
    }

    // ── Phase 2: Draw ─────────────────────────────────────────────────────────

    /// <summary>
    /// Structure type used when tagging is enabled. "P" for normal paragraphs.
    /// Set to "H1"…"H6" by <see cref="HeadingRenderer"/> before calling Draw.
    /// </summary>
    internal string StructType { get; set; } = "P";

    /// <summary>
    /// When non-null, the struct elem produced by this renderer is added as a child
    /// of <see cref="ParentStructElem"/> instead of being registered at the document
    /// structure tree root. Used by table/list renderers to build nested hierarchies.
    /// </summary>
    internal PdfStructElem? ParentStructElem { get; set; }

    /// <summary>
    /// Per-element language override forwarded from <see cref="Paragraph.Language"/>
    /// (or <see cref="Heading.Language"/> via <see cref="HeadingRenderer"/>).
    /// Written as <c>/Lang</c> on the struct element when non-null.
    /// </summary>
    internal string? ElementLanguage { get; set; }

    /// <summary>Emits the wrapped lines as PDF text operators, applying alignment, justification, links and tagging.</summary>
    public void Draw(DrawContext ctx)
    {
        if (_lines is null) return;

        var area = _occupied;
        var canvas = ctx.Canvas;
        var leading = _lineHeight;
        var isLastLine = false;

        // Tagged PDF: BDC must open BEFORE BeginText so the BDC…EMC actually encloses BT…ET.
        int mcid = -1;
        if (ctx.Tagged)
            mcid = canvas.BeginMarkedContent(StructType);

        canvas.BeginText();

        // Track current font/colour to suppress redundant operators.
        FontReference? currentFont = null;
        ColorRgb? currentColor = null;

        for (var lineIdx = _startLine; lineIdx < _endLine; lineIdx++)
        {
            var fragments = _lines![lineIdx];
            if (fragments.Length == 0) continue;

            isLastLine = lineIdx == _lines.Count - 1;

            // Per-line metrics
            var lineWidth = fragments.Sum(f => f.Width);
            var maxFontSize = fragments.Max(f => f.Style.FontSize);

            // Justify only non-last lines
            var isJustified = _para.Alignment == HorizontalAlignment.Justify && !isLastLine;
            var slack = area.Width - lineWidth;

            // Count inter-word gaps in this line (gaps between fragments that end/start with space-adjacent text)
            // We count total word-tokens in the line to compute spacing.
            var wordGapCount = CountWordGaps(fragments);
            var wordSpacing = isJustified && wordGapCount > 0 ? slack / wordGapCount : 0.0;

            // Baseline X for left, center, right — for Justify treat as Left (we use Tw or Tm).
            double xOffset = _para.Alignment switch
            {
                HorizontalAlignment.Center => (area.Width - lineWidth) / 2,
                HorizontalAlignment.Right => area.Width - lineWidth,
                _ => 0,  // Left and Justify start at 0
            };

            var linePdfY = ctx.ToPdfY(area.Y) - maxFontSize - (lineIdx - _startLine) * leading;
            var curX = area.X + xOffset;

            // Layout-space top of this text line for link annotation rects.
            var lineLayoutTop = area.Y + (lineIdx - _startLine) * leading;

            // Check if ANY fragment on this line uses an embedded font.
            var hasEmbedded = fragments.Any(f => f.Style.FontRef.IsEmbedded);

            if (isJustified && hasEmbedded)
            {
                // Embedded fonts: Tw does not work with 2-byte CID encoding.
                // Position each word-token explicitly with Tm.
                DrawJustifiedEmbeddedLine(canvas, ctx, fragments, curX, linePdfY, area.Width, slack, wordGapCount, ref currentFont, ref currentColor);
                RegisterLineLinks(ctx, fragments, curX, lineLayoutTop, leading);
            }
            else
            {
                // Standard-14 path (or non-justified): use Tw for word spacing.
                if (isJustified && wordGapCount > 0)
                    canvas.SetWordSpacing(wordSpacing);

                var linkStartX = curX;
                foreach (var frag in fragments)
                {
                    SwitchFont(canvas, ctx, frag.Style, ref currentFont);
                    SwitchColor(canvas, frag.Style, ref currentColor);

                    canvas.SetTextMatrix(1, 0, 0, 1, curX, linePdfY);
                    ShowText(canvas, frag.Style, frag.Text);

                    // Register link annotation for this fragment if it has a URI.
                    if (frag.Style.LinkUri is not null)
                    {
                        var fragBox = new LayoutBox(curX, lineLayoutTop, frag.Width, leading);
                        ctx.AddUriLinkAnnotation(fragBox, frag.Style.LinkUri);
                    }

                    curX += frag.Width;
                    // Word spacing is handled by Tw operator, so advance by frag.Width only.
                }

                if (isJustified && wordGapCount > 0)
                    canvas.SetWordSpacing(0);
            }
        }

        canvas.EndText();

        // Tagged PDF: close the BDC sequence after ET and register the /StructElem.
        if (ctx.Tagged && mcid >= 0)
        {
            canvas.EndMarkedContent();
            var elem = new PdfStructElem(StructType) { Mcid = mcid };
            if (ElementLanguage is not null)
                elem.Language = ElementLanguage;
            if (ParentStructElem is not null)
            {
                // Nested mode: add as child of the provided parent (table/list use case).
                ctx.StampStructElemPage(elem);
                ParentStructElem.AddChild(elem);
            }
            else
            {
                // Top-level mode: register at the document structure tree root.
                ctx.RegisterStructElem(elem);
            }
        }
    }

    // ── Link annotation registration ──────────────────────────────────────────

    /// <summary>
    /// For each fragment in an embedded-font justified line that carries a
    /// <see cref="TextStyle.LinkUri"/>, registers a link annotation at its
    /// approximate position. Because exact token positions are computed inside
    /// <see cref="DrawJustifiedEmbeddedLine"/>, we use the fragment's full width
    /// as a reasonable approximation.
    /// </summary>
    private static void RegisterLineLinks(DrawContext ctx, LineFragment[] fragments, double startX, double lineTop, double lineHeight)
    {
        var curX = startX;
        foreach (var frag in fragments)
        {
            if (frag.Style.LinkUri is not null)
            {
                var box = new LayoutBox(curX, lineTop, frag.Width, lineHeight);
                ctx.AddUriLinkAnnotation(box, frag.Style.LinkUri);
            }
            curX += frag.Width;
        }
    }

    // ── Justified embedded draw ───────────────────────────────────────────────

    private static void DrawJustifiedEmbeddedLine(
        PdfCanvas canvas,
        DrawContext ctx,
        LineFragment[] fragments,
        double startX,
        double linePdfY,
        double lineAreaWidth,
        double slack,
        int wordGapCount,
        ref FontReference? currentFont,
        ref ColorRgb? currentColor)
    {
        // Tokenise fragments into word-tokens; inter-token gaps get extra spacing.
        // A "word gap" is any transition between tokens where the left token ends without
        // trailing space and the right starts without leading space — we add slack/gaps there.
        var tokens = SplitToWordTokens(fragments);
        var extraPerGap = wordGapCount > 0 ? slack / wordGapCount : 0.0;
        var curX = startX;

        for (var t = 0; t < tokens.Count; t++)
        {
            var (text, style, width) = tokens[t];
            SwitchFont(canvas, ctx, style, ref currentFont);
            SwitchColor(canvas, style, ref currentColor);
            canvas.SetTextMatrix(1, 0, 0, 1, curX, linePdfY);
            ShowText(canvas, style, text);
            curX += width;

            // Add extra spacing after this token if there's a gap before the next
            if (t < tokens.Count - 1 && IsWordGap(tokens[t], tokens[t + 1]))
                curX += extraPerGap;
        }
    }

    // ── Font / colour switching helpers ──────────────────────────────────────

    private static void SwitchFont(PdfCanvas canvas, DrawContext ctx, TextStyle style, ref FontReference? current)
    {
        if (current.HasValue && FontRefEqual(current.Value, style.FontRef))
            return;

        if (style.FontRef.IsEmbedded)
        {
            var resourceName = ctx.UseEmbeddedFont(style.FontRef.Embedded);
            canvas.SetFontByName(resourceName, style.FontSize);
        }
        else
        {
            var fontResource = ctx.GetFont(style.Font);
            canvas.SetFont(fontResource, style.FontSize);
        }
        current = style.FontRef;
    }

    private static void SwitchColor(PdfCanvas canvas, TextStyle style, ref ColorRgb? current)
    {
        if (current.HasValue && current.Value == style.Color)
            return;
        canvas.SetFillColorRgb(style.Color.R, style.Color.G, style.Color.B);
        current = style.Color;
    }

    private static bool FontRefEqual(FontReference a, FontReference b)
    {
        if (a.IsEmbedded != b.IsEmbedded) return false;
        return a.IsEmbedded
            ? ReferenceEquals(a.Embedded, b.Embedded)
            : a.Standard14 == b.Standard14;
    }

    // ── Text emission ─────────────────────────────────────────────────────────

    private static void ShowText(PdfCanvas canvas, TextStyle style, string text)
    {
        if (style.FontRef.IsEmbedded)
            ShowEmbeddedText(canvas, style.FontRef.Embedded, text);
        else
            canvas.ShowText(text);
    }

    private static void ShowEmbeddedText(PdfCanvas canvas, EmbeddedFontHandle handle, string text)
    {
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
    }

    // ── Word-gap counting ─────────────────────────────────────────────────────

    /// <summary>
    /// Counts the number of inter-word gaps on a line (spaces between consecutive words).
    /// We define a gap as: the number of space characters within each fragment's text
    /// (since WordWrap preserves spaces between words within a fragment) plus cross-fragment
    /// boundaries that represent a space join.
    /// </summary>
    private static int CountWordGaps(LineFragment[] fragments)
    {
        var count = 0;
        for (var i = 0; i < fragments.Length; i++)
        {
            var text = fragments[i].Text;
            // Count internal spaces
            foreach (var ch in text)
                if (ch == ' ') count++;

            // Count cross-fragment boundary gaps:
            // if this fragment ends without space AND the next starts without space,
            // there is an implicit gap between them (they were split at a run boundary
            // in the middle of what was originally word spacing).
            // We do NOT add a gap here because WordWrap joins them without an extra space.
        }
        return count;
    }

    // ── Word-token splitting for embedded justified lines ─────────────────────

    private static List<(string Text, TextStyle Style, double Width)> SplitToWordTokens(LineFragment[] fragments)
    {
        var tokens = new List<(string, TextStyle, double)>();
        foreach (var frag in fragments)
        {
            var parts = frag.Text.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length == 0)
                {
                    // Empty between two spaces — counts as a gap but no token
                    continue;
                }
                var w = frag.Style.FontRef.MeasureString(part, frag.Style.FontSize);
                tokens.Add((part, frag.Style, w));

                // If there's a space after this part (not the last), insert a space token
                if (i < parts.Length - 1)
                {
                    var sw = frag.Style.FontRef.MeasureString(" ", frag.Style.FontSize);
                    tokens.Add((" ", frag.Style, sw));
                }
            }
        }
        return tokens;
    }

    private static bool IsWordGap(
        (string Text, TextStyle Style, double Width) left,
        (string Text, TextStyle Style, double Width) right) =>
        left.Text == " " || right.Text == " ";

    // ── Word-wrap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Greedy word-wrap across all runs. Words inherit the style of the run they belong to.
    /// A word that spans a run boundary keeps the style of the run that contains most of it
    /// (in practice, words never straddle boundaries because we split on spaces — each space
    /// terminates a word token which is always within one run's text).
    /// </summary>
    private static List<LineFragment[]> WordWrap(IReadOnlyList<TextRun> runs, double maxWidth)
    {
        var lines = new List<LineFragment[]>();

        // Flatten all runs into a sequence of (word, style) tokens
        var tokens = new List<(string Word, TextStyle Style)>();
        foreach (var run in runs)
        {
            var words = run.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var w in words)
                tokens.Add((w, run.Style));
        }

        if (tokens.Count == 0)
        {
            lines.Add([]);
            return lines;
        }

        // Greedy wrap
        var lineFrags = new List<(string Word, TextStyle Style, double Width)>();
        var lineWidth = 0.0;

        for (var ti = 0; ti < tokens.Count; ti++)
        {
            var (word, style) = tokens[ti];
            var spaceW = style.FontRef.MeasureString(" ", style.FontSize);
            var wordW = style.FontRef.MeasureString(word, style.FontSize);

            if (lineFrags.Count == 0)
            {
                if (wordW > maxWidth)
                {
                    // Hard-break oversized word
                    HardBreakWord(word, style, maxWidth, lines);
                    lineWidth = 0.0;
                    continue;
                }
                lineFrags.Add((word, style, wordW));
                lineWidth = wordW;
            }
            else
            {
                // Check if adding a space + this word still fits
                // Space belongs to the style of the PRECEDING word for measurement purposes
                var prevStyle = lineFrags[^1].Style;
                var prevSpaceW = prevStyle.FontRef.MeasureString(" ", prevStyle.FontSize);

                if (lineWidth + prevSpaceW + wordW <= maxWidth)
                {
                    // Fits — append to current line
                    // Merge into the same fragment if styles match; otherwise new fragment
                    if (lineFrags[^1].Style == style)
                    {
                        var last = lineFrags[^1];
                        lineFrags[^1] = (last.Word + " " + word, style, last.Width + prevSpaceW + wordW);
                        lineWidth += prevSpaceW + wordW;
                    }
                    else
                    {
                        // Different style: append a space to the preceding fragment and start a new one
                        var last = lineFrags[^1];
                        lineFrags[^1] = (last.Word + " ", last.Style, last.Width + prevSpaceW);
                        lineFrags.Add((word, style, wordW));
                        lineWidth += prevSpaceW + wordW;
                    }
                }
                else
                {
                    // Doesn't fit — commit current line and start new one
                    lines.Add(BuildFragments(lineFrags));
                    lineFrags.Clear();

                    if (wordW > maxWidth)
                    {
                        HardBreakWord(word, style, maxWidth, lines);
                        lineWidth = 0.0;
                    }
                    else
                    {
                        lineFrags.Add((word, style, wordW));
                        lineWidth = wordW;
                    }
                }
            }
        }

        if (lineFrags.Count > 0)
            lines.Add(BuildFragments(lineFrags));

        return lines;
    }

    private static LineFragment[] BuildFragments(List<(string Word, TextStyle Style, double Width)> frags)
    {
        var result = new LineFragment[frags.Count];
        for (var i = 0; i < frags.Count; i++)
            result[i] = new LineFragment(frags[i].Word, frags[i].Style, frags[i].Width);
        return result;
    }

    /// <summary>Hard-breaks a single word wider than maxWidth at character granularity.</summary>
    private static void HardBreakWord(string word, TextStyle style, double maxWidth, List<LineFragment[]> lines)
    {
        var fragment = new System.Text.StringBuilder();
        var fragmentW = 0.0;

        foreach (var rune in word.EnumerateRunes())
        {
            var ch = rune.ToString();
            var charW = style.FontRef.MeasureString(ch, style.FontSize);
            if (fragment.Length > 0 && fragmentW + charW > maxWidth)
            {
                var text = fragment.ToString();
                lines.Add([new LineFragment(text, style, fragmentW)]);
                fragment.Clear();
                fragmentW = 0.0;
            }
            fragment.Append(ch);
            fragmentW += charW;
        }
        if (fragment.Length > 0)
            lines.Add([new LineFragment(fragment.ToString(), style, fragmentW)]);
    }
}
