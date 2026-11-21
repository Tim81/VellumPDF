// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #311 pins for documents that use no break: the bytes after normalisation. Each value was
/// recorded on the commit before the break step existed, with the same test bodies, so a
/// difference means a document that asks for no break came out differently. These bodies use no
/// page-break member.
/// </summary>
public sealed class PageBreakByteIdentityTests
{
    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    // The workaround the break elements replace: a renderer that answers Partial with a zero-height
    // split to force the next page. It must keep making two pages.
    private sealed class ForceNextPage : IRenderer
    {
        private sealed class Empty : IRenderer
        {
            public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));
            public void Draw(DrawContext context) { }
        }

        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Partial(context.Area.WithHeight(0), new Empty(), new Empty());

        public void Draw(DrawContext context) { }
    }

    private static string WorkaroundDocument(RunningBand? footer)
    {
        var renderer = new DocumentRenderer(NewPdf(), Page, Margins) { Footer = footer };
        renderer.Add(new ParagraphRenderer(new Paragraph("before", Body)));
        renderer.Add(new ForceNextPage());
        renderer.Add(new ParagraphRenderer(new Paragraph("after", Body)));
        var bytes = Render(renderer);
        return $"{NormalisedSha256(bytes)} pages={PageCount(bytes)}";
    }

    [Fact]
    public void ZeroHeightWorkaround_keepsMakingTwoPages_withoutABand() =>
        Pin("6CE508D0FF9F5DE3C54F0A91B536C7A7234F5939DB23A03026B9251EE8CFC1D5 pages=2", WorkaroundDocument(null));

    [Fact]
    public void ZeroHeightWorkaround_keepsMakingTwoPages_withAFooter() =>
        Pin("BBF5F4BEC575811DABA8EBCC481331BE0DEF855AC03D446F7798D0C4E8469BF7 pages=2", WorkaroundDocument(new RunningBand("{page}/{pages}")));

    // A form feed in text is drawn as a space; it is not a page break.
    [Fact]
    public void FormFeedInText_isStillASpace()
    {
        var renderer = new DocumentRenderer(NewPdf(), Page, Margins);
        renderer.Add(new ParagraphRenderer(new Paragraph("before\fafter", Body)));
        var bytes = Render(renderer);

        Pin("64F3BD064C427646C5F06867BCE641C08E8559AFFCBB526060C9C74D96207A01 pages=1", $"{NormalisedSha256(bytes)} pages={PageCount(bytes)}");
    }

    [Fact]
    public void ADocumentOfEveryElementKind_withBands_pinsBytes()
    {
        using var doc = new Document
        {
            PageSize = Page,
            Margins = Margins,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Tagged = true,
            Header = new RunningBand("Header"),
            Footer = new RunningBand("{page} of {pages}"),
        };
        for (var i = 0; i < 6; i++)
            doc.Add(new Paragraph(ParagraphText(i), Body));
        doc.Add(new Heading("A heading", Body));
        var table = new TableElement();
        for (var r = 0; r < 10; r++)
            table.AddRow().AddCell($"Row {r}").AddCell("cell");
        doc.Add(table);
        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < 6; i++)
            list.Add($"Item {i} of the list", Body);
        doc.Add(list);
        doc.Add(new LineSeparator());
        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Pin("56373109849B9BB7B1BD2FA2FDA09EF9FF99EF3B588C7107B60A00B19B7C640A pages=3", $"{NormalisedSha256(bytes)} pages={PageCount(bytes)}");
    }
}
