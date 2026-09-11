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
    /// <c>ElementTooTall</c> (#471) — a concern <see cref="WideExtent"/> does not have to carry,
    /// since width and height are independent for an image the way they are not for a circle.
    /// </summary>
    private static Gen<double> SafeImageHeight => Gen.Double[1.0, 50.0];

    /// <summary>
    /// A table's own margin. Before this field existed the generator never set
    /// <see cref="TableElement.Margins"/> at all, so the property sweep this drives could not reach
    /// a table margin applied twice (#480): every sample had nothing there to double.
    ///
    /// Bounded to 5pt rather than to the row height, because the binding constraint here is column
    /// width, not row height: <see cref="CellWord"/> hard-breaks once its column shrinks enough,
    /// which would make a table margin fail this file's own placement-count invariant for a reason
    /// unrelated to what this field is for. Measured by sweeping this generator's most adverse
    /// corner — page width 400 (its minimum), page margin 60 (its maximum), font size 24 (its
    /// maximum, so the fewest columns share the least remaining width against the widest word) — in
    /// 0.5pt steps: "Wgggg" draws whole through a table margin of 7.5pt and hard-breaks from 8pt.
    /// 5pt keeps every generated sample clear of that boundary.
    /// </summary>
    private static Gen<double> TableMargin => Gen.Double[0.0, 5.0];

    /// <summary>
    /// Which of the generated table's two rows, if any, are headers: neither, the first only (the
    /// ordinary repeating-header shape), or the second only (a header after a data row, the #480
    /// shape this field exists to reach). Deliberately excludes both — a table with no data rows at
    /// all reports <see cref="LayoutResult.Outcome.Nothing"/> unconditionally from
    /// <c>TableRenderer.Layout</c> regardless of how much page is actually free, which
    /// <c>DocumentRenderer.CountPlaceRenderer</c> then reads as "this element can never render" and
    /// throws <c>ElementTooTall</c> even on an empty page with room to spare. Measured directly:
    /// widening this generator to include it broke both
    /// <c>PropertyTests.ValidDocument_rendersWithoutThrowing</c> and
    /// <c>ValidDocument_placesNothingOutsideThePage</c> on the very first run, on documents whose
    /// content area had hundreds of points to spare. That is a real defect, filed as #488, and
    /// outside this pull request's scope; excluding the shape that reaches it keeps the property sweep testing what
    /// #480 fixed rather than failing on something else it happens to have found.
    /// </summary>
    private static Gen<(bool Row0IsHeader, bool Row1IsHeader)> RowHeaderShape => Gen.OneOfConst(
        (false, false),
        (true, false),
        (false, true));

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
    /// meeting a known defect: the page is large enough for the content and the margins leave a
    /// positive content area. Band templates are an exception on their face —
    /// <see cref="ShortBandTemplate"/> deliberately carries a 500-character entry, far wider than
    /// any page this suite builds — and so is <see cref="ImageWidth"/>: <see cref="Build"/> gives
    /// the image a small, independent height so an oversized width cannot also trip
    /// <c>ElementTooTall</c>, but the width itself still reaches the renderer unclamped, which is
    /// the point of the case — the renderer's own width clamp is what this property exercises.
    /// <see cref="ChartDiameterRaw"/> is the one field <see cref="Build"/> does clamp before the
    /// renderer sees it, to what the rest of the same spec leaves room for vertically, since a
    /// chart's diameter drives its height as well as its width and an unclamped one would risk
    /// <c>ElementTooTall</c> in a way this property is not testing for.
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
        double ChartDiameterRaw,
        double TableMargin,
        bool Row0IsHeader,
        bool Row1IsHeader);

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
    private static Gen<(string? Header, string? Footer, double ImageWidth, double ImageHeight,
        double ChartDiameterRaw, double TableMargin, bool Row0IsHeader, bool Row1IsHeader)> BandsAndMedia =>
        Gen.Select(Bands, WideExtent, SafeImageHeight, WideExtent, TableMargin, RowHeaderShape,
            (bands, iw, ih, cd, tm, shape) =>
                (bands.Header, bands.Footer, iw, ih, cd, tm, shape.Row0IsHeader, shape.Row1IsHeader));

    /// <summary>
    /// The upper bound was <c>Gen.Int[0, 6]</c>, which never reached the roman-numeral-length
    /// boundary the known-answer test in <c>OffPagePlacementTests</c> pins at 10pt Helvetica — the
    /// first ordered-roman marker wider than the default 20pt indent there is item 27, "xxvii." at
    /// 22.22pt. It already reached the widening branch by a different route, though: FontSize runs
    /// to 24, and "iv." and "vi." are exactly 1.0 em, wide enough to exceed a 20pt indent from font
    /// size 20 up. Raised so an ordered list sometimes reaches the item-27 boundary at 10pt
    /// specifically. This is coverage rather than a defect this range closes: the overprint sits
    /// well inside the page, so it moves no existing property, and the marker literal is not
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
                    media.ImageWidth, media.ImageHeight, media.ChartDiameterRaw,
                    media.TableMargin, media.Row0IsHeader, media.Row1IsHeader));

    /// <summary>
    /// The word every generated table cell holds, and also — via <c>DocSpec.Word</c>, which this
    /// field fills directly in <see cref="ValidDoc"/> above rather than being drawn from its own
    /// generator — the word <see cref="ParagraphText"/> repeats three times. Every generated list
    /// item holds it too, and all three are coupled to this constant, at different lengths.
    /// Measured at forty items across all four <see cref="ListStyle"/> values, at the generator's
    /// narrowest content box of 280pt: a cell stops drawing its word whole at six characters, a
    /// list item at nineteen, and the paragraph's own line at twenty-one. The cell is the binding
    /// one, which is why the constant sits at five, and the earlier claim that a list item never
    /// breaks it was measured only over the lengths near that binding constraint.
    ///
    /// #468 is why it was ever a two-character constant rather than a generated string: before
    /// that fix, `TableGridResolver.AutoWidth` floored a column at its minimum content width and
    /// never capped the sum, so a long enough word pushed the table off the page. At this
    /// generator's narrowest content box — a 400pt page, 60pt margins, size 24, so 280pt — on the
    /// word family "W" followed by g's, the cell's content floor and its capped, final width
    /// coincide at 81.33pt (a 93.33pt equal share minus the default padding) and diverge above it,
    /// since the capped width stays at 93.33pt while the floor keeps growing. The paragraph's own
    /// three-repetition line wraps, rather than hard-breaking, past a different threshold, 88.89pt
    /// — first true in this word family at "Wggggg" (six characters, 89.376pt), which also happens
    /// to be the first length past the cell's 81.33pt line. The two thresholds differ; they cross
    /// in the same character step here only because this word family's words are 13.34pt apart at
    /// this size, not because the two elements share a geometry.
    ///
    /// Widening the constant past that shared step does not make the paragraph hard-break: it only
    /// wraps, and a single word first exceeds the 280pt box outright — the paragraph's own
    /// hard-break trigger — at twenty-one characters. What actually fails first is
    /// <c>PropertyTests.ValidDocument_placesNothingOutsideThePage</c>'s per-element placement
    /// counts, which pin the paragraph as one literal and each cell as the cell word once: the
    /// cell's hard-break at six characters already produces two literals where that property
    /// expects one, well before the paragraph's own count would move. "Wgggg" (five characters)
    /// stays under both the cell's 81.33pt line and the paragraph's 88.89pt one, so it widens the
    /// constant without crossing either; #468 itself is covered where a property has to be —
    /// failing before the fix and passing after — by
    /// <c>TableColumnAxisTests.AutoWidth_columnFloorsExceedingTheSum_capsToTheContentBox</c>, a
    /// fixed fixture built for exactly this corner rather than a shared, randomly-visited one.
    /// </summary>
    private const string CellWord = "Wgggg";

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

        var table = new TableElement { DefaultCellStyle = style, Margins = new EdgeInsets(spec.TableMargin) };
        var row0 = table.AddRow(isHeader: spec.Row0IsHeader);
        for (var c = 0; c < spec.ColumnCount; c++)
            row0.AddCell(spec.Word);
        var row1 = table.AddRow(isHeader: spec.Row1IsHeader);
        for (var c = 0; c < spec.ColumnCount; c++)
            row1.AddCell(spec.Word);
        doc.Add(table);

        // An explicit Height, never null: with the 2×2 fixture image null Height takes the
        // clamped width and makes the image square, and on a 900x400 page at margin 0 a Width of
        // 401 or more then throws ElementTooTall (#471) — so does Width null on that same page. That is
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

        // Every alignment, because the fix covers every alignment. Clamping the diameter alone
        // does not: it leaves the chart's own margins to push the circle out on whichever side the
        // alignment favours, which measured 4.8pt off a 454.4pt page under both Left and Right
        // while Centre stayed inside. Draw answers that by aligning within the content box rather
        // than within the margins once the circle no longer fits between them, so a generated
        // diameter past the box lands on the box edge under all three.
        //
        // StartAngle is left at its default, and that is load-bearing rather than incidental. The
        // Bezier arc approximation is exact at each segment's own endpoints and at its midpoint;
        // with four quarter-turn segments starting at the default 90-degree angle, the circle's
        // four extrema -- 0, 90, 180 and 270 degrees -- all land on a segment endpoint, so the
        // drawn curve matches the nominal circle there with no bulge, and a circle clamped to the
        // content box has an extent equal to its diameter with no allowance needed. That is not a
        // property of every start angle off the quadrant -- 45 degrees also lands exactly, on each
        // segment's own midpoint rather than its endpoint -- but most do bulge: measured sweeping 0
        // to 90 degrees at a quarter-degree step, up to 0.0409pt outside the nominal edge at
        // diameter 300, 40 times this suite's tolerance, which is why a start angle off this
        // default is left to the direct probes in OffPagePlacementTests rather than generated here.
        //
        // One drawable slice is equally load-bearing: PieChartRenderer.Draw takes the
        // seamless-circle branch only when exactly one slice carries a positive value, and that
        // branch keeps every arc boundary on a quadrant. The multi-slice wedge branch's own
        // MoveTo/LineTo pair ahead of each AppendArc would otherwise reach this generator's output.
        doc.Add(new PieChart
        {
            Diameter = diameter,
            Alignment = spec.Alignment,
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
