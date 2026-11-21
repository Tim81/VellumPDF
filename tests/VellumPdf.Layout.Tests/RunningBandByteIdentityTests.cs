// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #518 pins for running bands: the bytes after normalisation and the truncation report. Each
/// value was recorded on the commit before the band template was compiled once per band, with the
/// same document bodies, so a difference means a banded document came out differently. A pin is
/// a document class (tokens, truncation, zero-width glyphs, tagging, blank pages), not a count.
/// </summary>
public sealed class RunningBandByteIdentityTests
{
    private static readonly TextStyle Plain = new() { Font = Standard14.Helvetica, FontSize = 12 };

    private static string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"w{i}"));

    private static Document NewDocument(bool tagged = false) => new()
    {
        PageSize = Page,
        Margins = Margins,
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        Tagged = tagged,
    };

    // One short paragraph per page, so the page count is exactly `pages`.
    private static void FillPages(Document doc, int pages)
    {
        for (var i = 0; i < pages; i++)
        {
            if (i > 0) doc.Add(new PageBreak());
            doc.Add(new Paragraph($"Body of page {i + 1}", Plain));
        }
    }

    private static string Run(Document doc)
    {
        using (doc)
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            var bytes = ms.ToArray();
            var cuts = string.Join(
                ';',
                doc.BandTruncations.Select(t => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{t.Band}@{t.PageNumber}:{t.DrawnCharacters}/{t.ResolvedCharacters}")));
            return $"{NormalisedSha256(bytes)} pages={PageCount(bytes)} cuts=[{cuts}]";
        }
    }

    private static Document Banded(string? header, string? footer, int pages, HorizontalAlignment alignment = HorizontalAlignment.Center, bool tagged = false, TextStyle? style = null)
    {
        var doc = NewDocument(tagged);
        if (header is not null) doc.Header = new RunningBand(header, style ?? Plain, alignment);
        if (footer is not null) doc.Footer = new RunningBand(footer, style ?? Plain, alignment);
        FillPages(doc, pages);
        return doc;
    }

    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    [Fact]
    public void Page_only() =>
        Pin("87D4498B07C8DCD0FDDFCAC22161E5162601DC64D992208063EC4C9798577C52 pages=12 cuts=[]", Run(Banded(null, "Page {page}", 12)));

    [Fact]
    public void Pages_only() =>
        Pin("3C50EFDD99B68FB4C9FFDFAFACD4AF0B508C347018C733B3431FB0E044ECE76E pages=12 cuts=[]", Run(Banded("of {pages}", null, 12)));

    [Fact]
    public void Both_tokens_header_and_footer() =>
        Pin("5DAD22E63FA09641F2A22C862F8CA4B27AA214B0C4AC34D64B75FB2AF410C077 pages=12 cuts=[]", Run(Banded("{page}/{pages} header", "Page {page} of {pages}", 12)));

    [Fact]
    public void No_token() =>
        Pin("A4F06C691CBA55548AC42DC3EC0FCFB395BBFDEBBA1B831CFA5CD576B94B0C7E pages=12 cuts=[]", Run(Banded("Static header", "Static footer", 12)));

    [Fact]
    public void Tagged_and_untagged_documents()
    {
        Pin("354B51FD95AD42FC973523B3B05D97B72F9CA7D10C4F0FF02FDD0FB1119702D3 pages=3 cuts=[]", Run(Banded("{page}/{pages}", "{page}", 3, tagged: true)));
        Pin("5F2CD1F1D28D2F8BA692182F18FB638110B2E98C2B58996D6FFCD080C3827453 pages=3 cuts=[]", Run(Banded("{page}/{pages}", "{page}", 3, tagged: false)));
    }

    [Fact]
    public void Empty_template() =>
        Pin("ABEA4CA651D878E88412461C6F9DF5291ADCDD63D239D56C1110CA22CC281AA9 pages=3 cuts=[]", Run(Banded("", "", 3)));

    [Fact]
    public void Partial_and_malformed_tokens_are_left_as_given() =>
        Pin("9BDA6FEFFF253C7D48DE29CF5B0825C87720C3750181468565D3F6E2054B7026 pages=4 cuts=[]", Run(Banded("{page {{page}} {pages}} {Page} { page }", "{pages{page}}", 4)));

    [Fact]
    public void Long_template_with_spaces_is_cut_at_a_word_and_reported()
    {
        Pin("1BDE036154A6A90FB6A7CB0B8C4D44AEEDC03355E315E868D6D39B57B113CC6D pages=12 cuts=[Footer@1:38/1894]", Run(Banded(null, "{page}/{pages} " + Words(400), 12)));
        Pin("C0695102CA6B372134D51DA84F364275BCC3349592EB65EE7AD046DD43521E25 pages=12 cuts=[Header@1:40/1892]", Run(Banded("{pages} " + Words(400), null, 12, HorizontalAlignment.Right)));
    }

    [Fact]
    public void Long_template_without_spaces_is_cut_mid_token()
    {
        Pin("0B0643197D674FC51A1861FD221102BC368E6D7BF951416B5BD16D74592BA260 pages=12 cuts=[Footer@10:43/5005]", Run(Banded(null, "{page}/{pages}" + new string('x', 5000), 12, HorizontalAlignment.Left)));
        Pin("400ECADDD66E8153E9F4ED00475854073B1307F7C8E40D5305E2181A0FC042DF pages=12 cuts=[Footer@10:43/5002]", Run(Banded(null, new string('x', 5000) + "{page}", 12)));
    }

    [Fact]
    public void Cut_that_changes_with_the_digit_count_reports_the_worst_page()
    {
        // 105 pages: the page number gains a digit at 10 and at 100, and so does the total, so the
        // resolved text is longer on later pages and the cut moves with it.
        Pin("65BCA11DEB5690A8D189A505B70B00EAFBC096B132B30A7E6CD70082DDB0833D pages=105 cuts=[Footer@1:39/235]", Run(Banded(null, "{page} {pages} " + Words(60), 105, HorizontalAlignment.Right)));
        Pin("49D80B4E6AD54B753DF5BD60424431F14EAF7D74FA42A6646BDEA0D89600DBB9 pages=105 cuts=[Header@100:25/46]", Run(Banded("{pages}{page}" + new string('W', 40), null, 105)));
    }

    [Fact]
    public void Template_that_fits_exactly_on_some_pages_and_not_others()
    {
        // About one content width of text, so the one-digit pages fit and later pages do not.
        Pin("14871AC2ECA5ADD13BBA201C9B5AB0564D91D263392E981B2FD7230DAE77B329 pages=12 cuts=[Footer@10:94/104]", Run(Banded(null, "{page}" + new string('i', 100) + "{pages}", 12)));
    }

    [Theory]
    [InlineData(Standard14.Symbol)]
    [InlineData(Standard14.ZapfDingbats)]
    public void Symbol_and_ZapfDingbats_bands_measure_zero_and_are_drawn_in_full(Standard14 font)
    {
        var style = new TextStyle { Font = font, FontSize = 12 };
        var expected = font == Standard14.Symbol
            ? "033A512911A8D38DAD75519FD84752B303570F513330CE056D2BF4172C50BE37 pages=12 cuts=[]"
            : "99B44DD9EFA462CD04C7965C8BA99320FCA8A2564676960267BAF9B5100714BE pages=12 cuts=[]";
        Pin(expected, Run(Banded(null, "{page}/{pages} " + new string('a', 3000), 12, style: style)));
    }

    [Fact]
    public void Surrogate_pair_and_lone_surrogate_in_the_template_on_a_standard_14_font()
    {
        Pin("BE0F4B3E8B93C6A2E6696D36ADDF2605818A6FF6AB1C7D4611FCAD629E5CFA24 pages=3 cuts=[]", Run(Banded("\U0001F600 {page}", "\uD800{page}\uDC00 x", 3)));
    }

    [Fact]
    public void Blank_recto_page_gets_no_band_and_is_counted()
    {
        var doc = NewDocument();
        doc.Header = new RunningBand("{page} of {pages}", Plain);
        doc.Footer = new RunningBand("{page} of {pages} " + Words(100), Plain);
        doc.Add(new Paragraph("one", Plain));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("three", Plain));
        Pin("DEBC7759A1A3AB017266B7E07BA943A21231B542A46399841044EFAA5DF0B3D6 pages=3 cuts=[Footer@1:40/396]", Run(doc));
    }
}
