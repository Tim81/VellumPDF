// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Document and value generators for <see cref="PropertyTests"/>. Reading emitted coordinates
/// back is <see cref="ContentStreamReadback"/>'s job.
///
/// The layout engine had no property-based coverage at all: CsCheck was referenced by the Kernel,
/// Reader and Barcodes test projects and not by this one. Kernel's own
/// <c>PropertyTests.FiniteCoordinates_produceWellFormedPdf</c> restricts itself to finite
/// coordinates and says so in a comment, so the input that reaches <c>PdfCanvas</c>'s unguarded
/// number formatter is exactly what it does not generate.
///
/// The generators here are split deliberately. <see cref="ValidDoc"/> is the corner of the space
/// every current caller lives in, and the invariants over it hold today. The degenerate values sit
/// beside it so that widening a property to the corner where a defect lives is a change to the
/// property rather than a new fixture.
/// </summary>
internal static class LayoutGen
{
    // ── Scalars ──────────────────────────────────────────────────────────────

    /// <summary>A finite, non-negative length in points.</summary>
    internal static Gen<double> Points => Gen.Double[0.0, 1000.0];

    /// <summary>A finite length in points, either sign.</summary>
    internal static Gen<double> SignedPoints => Gen.Double[-1000.0, 1000.0];

    /// <summary>
    /// The three values .NET's numeric formatting does not render through a custom format string,
    /// so they reach a content stream as a bare symbol rather than as a PDF number.
    /// </summary>
    internal static Gen<double> NonFinite =>
        Gen.OneOfConst(double.NaN, double.PositiveInfinity, double.NegativeInfinity);

    /// <summary>A font size a caller would plausibly ask for.</summary>
    internal static Gen<double> FontSize => Gen.Double[6.0, 24.0];

    /// <summary>
    /// A raw image width or chart diameter, wide enough to exceed even the widest page this suite
    /// builds (900pt) before <see cref="Build"/> clamps it. The page-box property compares against
    /// the page, not the content box, so a width merely past the content box does not fail it:
    /// Left alignment only escapes the page once the width exceeds pageWidth - margin, and Centre
    /// only once it exceeds pageWidth itself. This has to reach past both.
    /// </summary>
    private static Gen<double> WideExtent => Gen.Double[1.0, 1300.0];

    /// <summary>
    /// An image display height small enough to fit inside every page this suite builds regardless
    /// of margin or band height, so the image case exercises the width clamp without also risking
    /// <c>ElementTooTall</c> — a concern <see cref="WideExtent"/> does not have to carry, since
    /// width and height are independent for an image the way they are not for a circle.
    /// </summary>
    private static Gen<double> SafeImageHeight => Gen.Double[1.0, 50.0];

    // ── Composites ───────────────────────────────────────────────────────────

    internal static Gen<EdgeInsets> Insets =>
        Gen.Select(Points, Points, Points, Points, (t, r, b, l) => new EdgeInsets(t, r, b, l));

    /// <summary>
    /// An extent that reaches the boundary <see cref="LayoutBox.IsEmpty"/> is defined at. Drawing
    /// it from <see cref="Points"/> alone would not: sampling CsCheck's <c>Gen.Double[0, 1000]</c>
    /// directly gave 42 exact zeros in 200,000 draws, so a 500-iteration run reaches a zero extent
    /// about one time in five and never reaches a negative one, which left the non-positive half of
    /// that predicate untested.
    /// </summary>
    private static Gen<double> Extent =>
        Gen.OneOf(Points, Gen.Const(0.0), Gen.Double[-1000.0, -0.001]);

    internal static Gen<LayoutBox> Box =>
        Gen.Select(SignedPoints, SignedPoints, Extent, Extent, (x, y, w, h) => new LayoutBox(x, y, w, h));

    internal static Gen<ColorRgb> Rgb =>
        Gen.Select(Gen.Double[0.0, 1.0], Gen.Double[0.0, 1.0], Gen.Double[0.0, 1.0],
            (r, g, b) => new ColorRgb(r, g, b));

    internal static Gen<HorizontalAlignment> Alignment => Gen.OneOfConst(
        HorizontalAlignment.Left,
        HorizontalAlignment.Center,
        HorizontalAlignment.Right,
        HorizontalAlignment.Justify);

    internal static Gen<ListStyle> Bullets => Gen.OneOfConst(
        ListStyle.Unordered,
        ListStyle.OrderedDecimal,
        ListStyle.OrderedAlpha,
        ListStyle.OrderedRoman);

    /// <summary>
    /// A band template, including ones far wider than any page this suite generates.
    ///
    /// These were all short while the running-band truncation defect was open, because a long one
    /// put the page-box invariant permanently red. That defect is fixed, so the exclusion is gone
    /// and the long templates are here: the invariant now covers the case it was written for,
    /// across every generated alignment, page size and margin rather than the hand-picked
    /// geometries of the band suite.
    /// </summary>
    internal static Gen<string> ShortBandTemplate => Gen.OneOfConst(
        "Page {page}",
        "Page {page} of {pages}",
        "{pages}",
        "Report",
        "",
        new string('W', 500),
        new string('W', 200) + " {page} of {pages}",
        // Deliberately not the cell word: this template has an early space, so the word-boundary
        // cut draws exactly its first token, and if that token were the cell word it would collide
        // with the per-element placement counts in PropertyTests. Widening the generator is what
        // exposed that coupling.
        "Zz " + new string('M', 400));

    // ── The document under test ──────────────────────────────────────────────

    /// <summary>
    /// One generated document. Every field sits inside the range a caller can use today without
    /// meeting a known defect: the page is large enough for the content, the margins leave a
    /// positive content area, and the band templates fit across it. <see cref="ImageWidth"/> and
    /// <see cref="ChartDiameterRaw"/> are the exception on their face — both can reach well past
    /// any page this suite builds — but <see cref="Build"/> gives the image a small, independent
    /// height and clamps the chart's raw diameter to what the rest of the same spec leaves room
    /// for vertically, so what reaches the renderers still sits inside that same known-good range.
    /// </summary>
    internal sealed record DocSpec(
        double PageWidth,
        double PageHeight,
        EdgeInsets Margins,
        double FontSize,
        HorizontalAlignment Alignment,
        ColorRgb Colour,
        string? Header,
        string? Footer,
        ListStyle ListStyle,
        int ItemCount,
        int ColumnCount,
        string Word,
        double ImageWidth,
        double ImageHeight,
        double ChartDiameterRaw);

    /// <summary>A band template, or no band at all.</summary>
    private static Gen<string?> OptionalBand =>
        Gen.OneOf(ShortBandTemplate.Select(t => (string?)t), Gen.Const((string?)null));

    // Grouped because Gen.Select takes at most eight generators.
    private static Gen<(string? Header, string? Footer)> Bands =>
        Gen.Select(OptionalBand, OptionalBand, (h, f) => (h, f));

    /// <summary>
    /// Bands plus the image and chart dimensions, grouped into one generator for the same reason
    /// <see cref="Bands"/> is grouped: <see cref="ValidDoc"/> already uses eight generators, the
    /// most <c>Gen.Select</c> takes in one call.
    /// </summary>
    private static Gen<(string? Header, string? Footer, double ImageWidth, double ImageHeight, double ChartDiameterRaw)> BandsAndMedia =>
        Gen.Select(Bands, WideExtent, SafeImageHeight, WideExtent,
            (bands, iw, ih, cd) => (bands.Header, bands.Footer, iw, ih, cd));

    /// <summary>
    /// The upper bound was <c>Gen.Int[0, 6]</c>, which a roman marker never reaches past item 26
    /// and so never widens: the first ordered-roman marker wider than the default 20pt indent at
    /// 10pt Helvetica is item 27, "xxvii." at 22.22pt. Raised so an ordered list sometimes reaches
    /// it. This is coverage rather than a defect this range closes: the overprint sits well inside
    /// the page, so it moves no existing property, and the marker literal is not
    /// <see cref="CellWord"/>, so the per-element placement counts do not move either — the
    /// known-answer test in <c>OffPagePlacementTests</c> is what actually discriminates the fix.
    /// </summary>
    private static Gen<(ListStyle Style, int Count)> Items =>
        Gen.Select(Bullets, Gen.Int[0, 40], (s, n) => (s, n));

    internal static Gen<DocSpec> ValidDoc =>
        Gen.Select(
            Gen.Double[400.0, 900.0],
            Gen.Double[400.0, 900.0],
            Gen.Double[0.0, 60.0],
            FontSize,
            Alignment,
            Rgb,
            BandsAndMedia,
            Items,
            (w, h, margin, size, align, colour, media, items) =>
                new DocSpec(w, h, new EdgeInsets(margin), size, align, colour,
                    media.Header, media.Footer, items.Style, items.Count, 3, CellWord,
                    media.ImageWidth, media.ImageHeight, media.ChartDiameterRaw));

    /// <summary>
    /// The word every generated table cell holds, and the reason it is a constant rather than a
    /// generated string: #468. `TableGridResolver.AutoWidth` floors a column at its minimum content
    /// width and never caps the sum, so a long enough word pushes the table off the page. Measured
    /// on this generator's own worst corner — a 400pt page, 60pt margins, three auto-width columns
    /// at 24pt, so a content box ending at x = 340 — and on the word family "W" followed by g's,
    /// the rightmost cell rectangle sits at 340.00 at two and five characters, 364.13 at six, and
    /// 404.16 at seven, where the page itself is breached. The boundary belongs to that family:
    /// alternating W and g breaches the content box at five characters and the page at seven.
    ///
    /// Two characters is below that boundary, so the page-box invariant holds. Generating the word
    /// would turn #468 into a failing property, which is where it belongs: in the pull request that
    /// fixes it, failing before and passing after.
    /// </summary>
    private const string CellWord = "Wg";

    /// <summary>
    /// The fixture image every generated document places, the same 2×2 opaque PNG
    /// <c>PdfTestUtil.CreateMinimalRgbPng</c> builds for the direct renderer tests. Built once:
    /// the XObject is immutable and each document registers its own copy of the underlying stream,
    /// so sharing one instance across the hundreds of documents <see cref="ValidDoc"/> generates
    /// per run is the same reuse the byte-identity corpus harness in the scratchpad already relies
    /// on for its own fixture image.
    /// </summary>
    private static readonly VellumPdf.Images.PdfImageXObject FixtureImage =
        VellumPdf.Images.PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng());

    /// <summary>Builds the document a <see cref="DocSpec"/> describes. The caller owns disposal.</summary>
    internal static Document Build(DocSpec spec)
    {
        var style = new TextStyle
        {
            FontRef = new FontReference(VellumPdf.Fonts.Standard14.Helvetica),
            FontSize = spec.FontSize,
            Color = spec.Colour,
        };

        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, spec.PageWidth, spec.PageHeight),
            Margins = spec.Margins,
        };

        if (spec.Header is not null) doc.SetHeader(spec.Header, style);
        if (spec.Footer is not null) doc.SetFooter(spec.Footer, style);

        doc.Add(new Paragraph(ParagraphText(spec), style) { Alignment = spec.Alignment });

        if (spec.ItemCount > 0)
        {
            var list = new ListElement(spec.ListStyle) { DefaultStyle = style };
            for (var i = 0; i < spec.ItemCount; i++)
                list.Add(new ListItem(spec.Word, style));
            doc.Add(list);
        }

        var table = new TableElement { DefaultCellStyle = style };
        var row = table.AddRow();
        for (var c = 0; c < spec.ColumnCount; c++)
            row.AddCell(spec.Word);
        doc.Add(table);

        // An explicit Height, never null: with the 2×2 fixture image null Height takes the
        // clamped width and makes the image square, and on a 900x400 page at margin 0 a Width of
        // 401 or more then throws ElementTooTall — so does Width null on that same page. That is
        // the same "only one axis is checked" family this pull request is about, but on the height
        // axis rather than the width one, and fixing it would turn an exception into output, a
        // public behaviour change outside this fix's mandate. SafeImageHeight sidesteps that
        // corner entirely rather than covering it.
        doc.Add(new LayoutImage(FixtureImage)
        {
            Width = spec.ImageWidth,
            Height = spec.ImageHeight,
            Alignment = spec.Alignment,
        });

        // A chart's Diameter drives both its width and its height, so unlike the image above it
        // cannot be widened for horizontal coverage without also risking ElementTooTall: an
        // oversized diameter that does not fit any page vertically is refused by
        // PieChartRenderer.Layout today, correctly, by design (PaginationDepthTests pins exactly
        // that). Derive the vertical budget from the rest of this same spec — page height,
        // margins, and the running-band heights DocumentRenderer reserves — and clamp the raw
        // generated diameter to it, so ChartDiameterRaw's reach past the page only shows up when
        // this document's own geometry has the room to draw it without also being too tall.
        var bandHeight = (spec.FontSize * 1.2) + 4;
        var bandsHeight = (spec.Header is not null ? bandHeight : 0) + (spec.Footer is not null ? bandHeight : 0);
        var fullPageContentHeight = spec.PageHeight - spec.Margins.Vertical - bandsHeight;
        // 12pt is PieChart.Margins' default vertical inset (EdgeInsets(6), top and bottom); the
        // chart below leaves that default in place rather than overriding it to zero, since the
        // default absorbs AppendArc's own small overshoot past the nominal circle (see
        // PieChartRenderer's clamp). The extra 1pt is slack under that boundary, not pinned to it.
        var maxDiameter = Math.Max(1.0, fullPageContentHeight - 12 - 1);
        var diameter = Math.Min(spec.ChartDiameterRaw, maxDiameter);

        // Centre only, not spec.Alignment. PieChartRenderer's clamp bounds the placement diameter
        // against ctx.Area.Width (undeflated), then Draw applies it inside the area deflated by
        // the chart's own margins — deliberately, per the acceptance table, since Centre's offset
        // formula halves the difference and lands symmetrically inside the box even when the
        // clamped diameter exceeds the deflated width. Left and Right have no such cushion: their
        // offset is pinned at 0 (Left) or area.Width - diameter (Right) regardless of margins, so
        // once the clamp lands at exactly ctx.Area.Width — any raw diameter past the content box
        // does that — the drawn circle still overshoots the page by up to the chart's own margin.
        // Measured: a 454.4pt-wide page, 1.2pt document margin, default 6pt chart margins and a
        // diameter clamped to ctx.Area.Width = 451.9 puts a Left-aligned circle's right edge at
        // 459.1, 4.8pt past the page. The acceptance table verifies Centre only, so this is the
        // range this fix actually closes; a wide diameter under Left or Right is a corner this
        // pull request leaves open, not one the property below should be widened into.
        doc.Add(new PieChart
        {
            Diameter = diameter,
            Alignment = HorizontalAlignment.Center,
            Slices = [new PieSlice(1, spec.Colour)],
        });

        return doc;
    }

    /// <summary>
    /// The single literal the generated paragraph shows. Distinct from <see cref="CellWord"/> on
    /// purpose: it is what lets a property tell the paragraph's own output apart from the list
    /// items' and the table cells', which are all the cell word.
    /// </summary>
    internal static string ParagraphText(DocSpec spec) =>
        spec.Word + " " + spec.Word + " " + spec.Word;

    /// <summary>Renders a spec to PDF bytes.</summary>
    internal static byte[] Render(DocSpec spec)
    {
        using var doc = Build(spec);
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
