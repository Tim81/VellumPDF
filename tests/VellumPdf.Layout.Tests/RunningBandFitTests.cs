// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A running band was drawn as one unwrapped, unclipped line positioned by an alignment formula
/// that never checked whether the text fitted, so a template wider than the content box went off
/// the page with every glyph still emitted, and the whole template was measured and emitted again
/// on every page.
///
/// The fixture is chosen so every expected coordinate is exact rather than approximate. Helvetica's
/// advance for <c>Æ</c> is exactly 1000 thousandths of an em, so at 10pt each glyph is exactly
/// 10.0pt; the character maps straight onto WinAnsi 0xC6, which <c>PdfCanvas.WritePdfString</c> does
/// not escape, so it also survives the read-back unchanged. On a 300pt page with 50pt margins the
/// content box is exactly 200pt, which is exactly twenty glyphs.
/// </summary>
public sealed class RunningBandFitTests
{
    private const char Wide = 'Æ';
    private const double Size = 10.0;
    private const double PageWidth = 300.0;
    private const double PageHeight = 200.0;
    private const double Margin = 50.0;
    private const double ContentWidth = PageWidth - (2 * Margin);
    private const int GlyphsThatFit = 20;

    private static TextStyle Style => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = Size,
    };

    private static Document NewDoc() => new()
    {
        PageSize = new PdfRectangle(0, 0, PageWidth, PageHeight),
        Margins = new EdgeInsets(Margin),
    };

    /// <summary>Renders a document carrying one footer band and returns its decompressed stream.</summary>
    private static string RenderWithFooter(string template, HorizontalAlignment alignment)
    {
        using var doc = NewDoc();
        doc.Footer = new RunningBand(template, Style, alignment);
        doc.Add(new Paragraph("body", Style));

        var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    /// <summary>
    /// The band's own placement. Identified by its text starting with the fixture glyph, which no
    /// other element in these documents uses.
    /// </summary>
    private static ContentStreamReadback.TextPlacement BandPlacement(string stream) =>
        ContentStreamReadback.TextPlacements(stream).Single(p => p.Text.StartsWith(Wide));

    // ── (a) Alignment at a width the content box cannot hold ─────────────────

    [Theory]
    [InlineData(HorizontalAlignment.Left)]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    [InlineData(HorizontalAlignment.Justify)]
    public void Band_widerThanTheContentBox_staysInsideThePage(HorizontalAlignment alignment)
    {
        var stream = RenderWithFooter(new string(Wide, 500), alignment);
        var band = BandPlacement(stream);
        var right = band.X + Standard14Metrics.MeasureString(Standard14.Helvetica, band.Text, Size);

        // Left alignment already put its origin at the left margin before the fix, so an origin
        // assertion alone would pass either way and discriminate nothing. The right edge is what
        // fails for Left, and the origin is what fails for Center and Right.
        Assert.True(band.X >= Margin - 0.001, $"{alignment}: origin {band.X} is left of the margin");
        Assert.True(right <= PageWidth - Margin + 0.001,
            $"{alignment}: text ends at {right}, right of the {PageWidth - Margin} content edge");
    }

    // ── (b) The truncation boundary, as exact known answers ──────────────────

    [Fact]
    public void Band_exactlyFillingTheContentBox_isNotTruncated()
    {
        var template = new string(Wide, GlyphsThatFit);
        var band = BandPlacement(RenderWithFooter(template, HorizontalAlignment.Left));

        Assert.Equal(template, band.Text);
        Assert.Equal(ContentWidth,
            Standard14Metrics.MeasureString(Standard14.Helvetica, band.Text, Size), 0.001);
    }

    [Fact]
    public void Band_oneGlyphTooWide_dropsExactlyThatGlyph()
    {
        var band = BandPlacement(
            RenderWithFooter(new string(Wide, GlyphsThatFit + 1), HorizontalAlignment.Left));

        Assert.Equal(new string(Wide, GlyphsThatFit), band.Text);
    }

    [Fact]
    public void Band_cutMidWord_fallsBackToTheLastSpace()
    {
        // Nineteen glyphs, a space, then more: the space is at a legal width, so the cut takes it
        // rather than splitting the run that follows.
        var template = new string(Wide, 19) + " " + new string(Wide, 10);
        var band = BandPlacement(RenderWithFooter(template, HorizontalAlignment.Left));

        Assert.Equal(new string(Wide, 19), band.Text);
    }

    // ── (c) Nothing fits at all ──────────────────────────────────────────────

    [Fact]
    public void Band_narrowerThanOneGlyph_drawsNoTextObjectAtAll()
    {
        using var doc = new Document
        {
            // A content box 5pt wide against a 10pt glyph. The page has to be tall enough that the
            // vertical margins do not meet its height, which ValidateGeometry refuses.
            PageSize = new PdfRectangle(0, 0, 205, 400),
            Margins = new EdgeInsets(100),
        };
        doc.Footer = new RunningBand(new string(Wide, 4), Style);
        doc.Add(new Paragraph("b", Style));

        var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        Assert.DoesNotContain(Wide, stream);
        Assert.DoesNotContain(ContentStreamReadback.TextPlacements(stream),
            p => p.Text.StartsWith(Wide));
    }

    // ── (d) A band that fits is emitted exactly as before ────────────────────

    [Theory]
    [InlineData(HorizontalAlignment.Left, Margin)]
    [InlineData(HorizontalAlignment.Center, Margin + ((ContentWidth - 100.0) / 2))]
    [InlineData(HorizontalAlignment.Right, PageWidth - Margin - 100.0)]
    public void Band_thatFits_keepsItsExactOriginAndText(HorizontalAlignment alignment, double expectedX)
    {
        // Ten glyphs is exactly 100pt, comfortably inside the 200pt content box.
        var template = new string(Wide, 10);
        var band = BandPlacement(RenderWithFooter(template, alignment));

        Assert.Equal(template, band.Text);
        Assert.Equal(expectedX, band.X, 0.001);
    }

    // ── (e) The width a cut returns, which positions the band ───────────────

    /// <summary>
    /// The width returned for a word-boundary cut has to be the width of what is actually drawn,
    /// because the alignment formula divides by it. Pinned at centre alignment: at Left the origin
    /// is the margin whatever the width, so a Left assertion cannot see this at all.
    ///
    /// Mutating the returned width to include the space left every test green while moving the
    /// band 2.78pt, the space's own advance at 10pt. This is the case that closes that.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Center, 55.0)]
    [InlineData(HorizontalAlignment.Right, 60.0)]
    public void Band_cutAtASpace_isPositionedByTheDrawnWidthOnly(
        HorizontalAlignment alignment, double expectedX)
    {
        // Nineteen glyphs is 190pt, so the cut takes the space at index 19 and draws 190pt of text
        // in a 200pt box: centred that is 50 + (200 - 190) / 2 = 55, right-aligned 250 - 190 = 60.
        var template = new string(Wide, 19) + " " + new string(Wide, 10);
        var band = BandPlacement(RenderWithFooter(template, alignment));

        Assert.Equal(new string(Wide, 19), band.Text);
        Assert.Equal(expectedX, band.X, 0.001);
    }

    /// <summary>
    /// Only the first space of a run is a cut point. Recording every space would land the cut on
    /// the last of a consecutive run and keep the ones before it, so the drawn text would end in
    /// whitespace and the returned width would charge the alignment for ink that is not there.
    /// </summary>
    [Fact]
    public void Band_cutAtConsecutiveSpaces_keepsNoTrailingSpace()
    {
        var template = new string(Wide, 5) + "  " + new string(Wide, 30);
        var band = BandPlacement(RenderWithFooter(template, HorizontalAlignment.Center));

        Assert.Equal(new string(Wide, 5), band.Text);
        Assert.Equal(50.0 + ((ContentWidth - 50.0) / 2), band.X, 0.001);
    }

    // ── (f) A token that widens the string across pages ──────────────────────

    [Fact]
    public void Band_pageTokenWidening_truncatesOnlyThePagesThatOverflow()
    {
        // Sized so "<19 glyphs> 9" fits and "<19 glyphs> 10" does not: the digits are 5.56pt each
        // at 10pt and the space is 2.78pt, so page 9 needs 198.34pt and page 10 needs 203.9pt.
        using var doc = NewDoc();
        doc.Footer = new RunningBand(new string(Wide, 19) + " {page}", Style);
        for (var i = 0; i < 100; i++) doc.Add(new Paragraph("line " + i, Style));

        var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        var bands = ContentStreamReadback.TextPlacements(stream)
            .Where(p => p.Text.StartsWith(Wide))
            .ToList();

        Assert.True(bands.Count >= 10, $"expected at least ten pages, got {bands.Count}");
        foreach (var b in bands)
        {
            var right = b.X + Standard14Metrics.MeasureString(Standard14.Helvetica, b.Text, Size);
            Assert.True(right <= PageWidth - Margin + 0.001,
                $"page band \"{b.Text}\" ends at {right}");
        }

        // The single-digit pages keep their page number; a two-digit one loses it to the cut, which
        // is what the caller is told about through the warning channel rather than left to notice.
        Assert.Contains(bands, b => b.Text.EndsWith(" 9"));
        Assert.DoesNotContain(bands, b => b.Text.EndsWith(" 10"));
    }
    // ── (g) The report a caller can act on ───────────────────────────────────

    [Fact]
    public void Band_truncated_isReportedOnceWithExactCounts()
    {
        using var doc = NewDoc();
        doc.Footer = new RunningBand(new string(Wide, 25), Style);
        doc.Add(new Paragraph("body", Style));

        var ms = new MemoryStream();
        doc.Save(ms);

        var report = Assert.Single(doc.BandTruncations);
        Assert.Equal(
            new BandTruncationWarning(RunningBandKind.Footer, 1, GlyphsThatFit, 25),
            report);
        Assert.Equal(5, report.DroppedCharacters);
    }

    [Fact]
    public void Band_thatFits_reportsNothing()
    {
        using var doc = NewDoc();
        doc.Footer = new RunningBand(new string(Wide, GlyphsThatFit), Style);
        doc.Add(new Paragraph("body", Style));

        var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Empty(doc.BandTruncations);
    }

    [Fact]
    public void Bands_bothTruncated_areReportedSeparately()
    {
        using var doc = NewDoc();
        doc.Header = new RunningBand(new string(Wide, 23), Style);
        doc.Footer = new RunningBand(new string(Wide, 30), Style);
        doc.Add(new Paragraph("body", Style));

        var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Equal(2, doc.BandTruncations.Count);
        Assert.Contains(new BandTruncationWarning(RunningBandKind.Header, 1, GlyphsThatFit, 23),
            doc.BandTruncations);
        Assert.Contains(new BandTruncationWarning(RunningBandKind.Footer, 1, GlyphsThatFit, 30),
            doc.BandTruncations);
    }

    /// <summary>
    /// One report per band however many pages were cut, and it names the page that lost the most
    /// rather than the first. With a page token the resolved text lengthens as the number gains
    /// digits, so the worst page is the last one, and its dropped count is what tells a caller how
    /// much shorter the template has to be.
    /// </summary>
    [Fact]
    public void Band_truncatedOnManyPages_reportsOnceForTheWorstPage()
    {
        using var doc = NewDoc();
        doc.Footer = new RunningBand(new string(Wide, 25) + " {page}", Style);
        for (var i = 0; i < 100; i++) doc.Add(new Paragraph("line " + i, Style));

        var ms = new MemoryStream();
        doc.Save(ms);

        var report = Assert.Single(doc.BandTruncations);
        Assert.Equal(RunningBandKind.Footer, report.Band);
        Assert.Equal(GlyphsThatFit, report.DrawnCharacters);

        // Two-digit pages resolve one character longer than single-digit ones, so the worst page is
        // a two-digit one and the count reflects that rather than page one's.
        Assert.True(report.PageNumber >= 10, $"worst page was {report.PageNumber}");
        Assert.Equal(28, report.ResolvedCharacters);
        Assert.Equal(8, report.DroppedCharacters);
    }

    /// <summary>
    /// A second save cannot double the reports, because it never reaches the collector.
    ///
    /// Measured rather than assumed: the second call throws
    /// <c>InvalidOperationException</c> from the underlying <c>PdfDocument</c>, which refuses a
    /// second write, and every save path now collects only after its own write has succeeded, so
    /// the throw comes first and the previous report survives untouched.
    ///
    /// Two paths did not. The asynchronous and signing paths collected between the layout and the
    /// write, so a second call ran a whole second layout, appending pages to the same document,
    /// collected from it, and only then threw — leaving a report naming a page present in no
    /// output. Reordering them is what makes this test's claim true for all three rather than only
    /// for this one.
    /// </summary>
    [Fact]
    public void Band_secondSave_throwsAndLeavesTheFirstReportIntact()
    {
        using var doc = NewDoc();
        doc.Footer = new RunningBand(new string(Wide, 25), Style);
        doc.Add(new Paragraph("body", Style));

        doc.Save(new MemoryStream());
        var afterFirst = doc.BandTruncations.Single();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Equal(
            "This document has already been written; create a new PdfDocument to write again.",
            ex.Message);

        Assert.Equal(afterFirst, doc.BandTruncations.Single());
    }


    // ── (h) The paths the first review round found untested ──────────────────

    /// <summary>
    /// The embedded-font branch sets the band's colour too. Deleting that one line left every
    /// test green, because the only colour test built a style with no font reference and so took
    /// the Standard 14 branch exclusively.
    /// </summary>
    [Fact]
    public void Band_embeddedFont_setsItsOwnColour()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null)
        {
            OracleGate.Unavailable("platform TrueType font");
            return;
        }

        using var doc = NewDoc();
        var handle = doc.LoadTrueTypeFont(fontPath);
        var style = new TextStyle
        {
            FontRef = new FontReference(handle),
            FontSize = Size,
            Color = new ColorRgb(0, 0, 1),
        };
        doc.Footer = new RunningBand("FOOTER", style);
        doc.Add(new Paragraph("red", new TextStyle { FontSize = Size, Color = new ColorRgb(1, 0, 0) }));

        var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        Assert.Equal(1, PdfTestUtil.CountOccurrences(stream, "0 0 1 rg"));
        Assert.True(
            stream.LastIndexOf("0 0 1 rg", StringComparison.Ordinal)
            > stream.LastIndexOf("1 0 0 rg", StringComparison.Ordinal),
            "the band's colour should follow the content's");
    }

    /// <summary>
    /// The report is readable from the renderer as well as the document. That member exists
    /// because the renderer is the only thing that knows the content box a band was measured
    /// against, and its own documentation claimed a test drove it directly when none did.
    /// </summary>
    [Fact]
    public void Band_truncation_isReadableFromTheRendererItself()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(
            pdf, new PdfRectangle(0, 0, PageWidth, PageHeight), new EdgeInsets(Margin))
        {
            Footer = new RunningBand(new string(Wide, 25), Style),
        };
        renderer.Add(new ParagraphRenderer(new Paragraph("body", Style)));

        var ms = new MemoryStream();
        renderer.Render(ms);

        Assert.Equal(
            new BandTruncationWarning(RunningBandKind.Footer, 1, GlyphsThatFit, 25),
            Assert.Single(renderer.BandTruncations));
    }
}
