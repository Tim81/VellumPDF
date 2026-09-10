// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

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

    // ── (f) The report a caller can act on ───────────────────────────────────

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
    /// second write, and it throws inside the render before <c>CollectDiagnostics</c> runs. So the
    /// first save's single report survives untouched and the collector's Clear is unreachable
    /// through this path — defensive rather than exercised. Worth pinning anyway: the exception
    /// names a type a layout caller never used, which is its own defect, and if that is ever fixed
    /// so a second save proceeds, the Clear becomes load-bearing and this test starts covering it.
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

    // ── (e) A token that widens the string across pages ──────────────────────

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
}
