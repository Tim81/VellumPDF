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
    /// A band template short enough to fit any page this suite generates, so the running-band
    /// truncation defect stays out of scope here and the page-box invariant is about the content.
    /// </summary>
    internal static Gen<string> ShortBandTemplate => Gen.OneOfConst(
        "Page {page}",
        "Page {page} of {pages}",
        "{pages}",
        "Report",
        "");

    // ── The document under test ──────────────────────────────────────────────

    /// <summary>
    /// One generated document. Every field sits inside the range a caller can use today without
    /// meeting a known defect: the page is large enough for the content, the margins leave a
    /// positive content area, and the band templates fit across it.
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
        string Word);

    /// <summary>A band template, or no band at all.</summary>
    private static Gen<string?> OptionalBand =>
        Gen.OneOf(ShortBandTemplate.Select(t => (string?)t), Gen.Const((string?)null));

    // Grouped because Gen.Select takes at most eight generators.
    private static Gen<(string? Header, string? Footer)> Bands =>
        Gen.Select(OptionalBand, OptionalBand, (h, f) => (h, f));

    private static Gen<(ListStyle Style, int Count)> Items =>
        Gen.Select(Bullets, Gen.Int[0, 6], (s, n) => (s, n));

    internal static Gen<DocSpec> ValidDoc =>
        Gen.Select(
            Gen.Double[400.0, 900.0],
            Gen.Double[400.0, 900.0],
            Gen.Double[0.0, 60.0],
            FontSize,
            Alignment,
            Rgb,
            Bands,
            Items,
            (w, h, margin, size, align, colour, bands, items) =>
                new DocSpec(w, h, new EdgeInsets(margin), size, align, colour,
                    bands.Header, bands.Footer, items.Style, items.Count, 3, CellWord));

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
