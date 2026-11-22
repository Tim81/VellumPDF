// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #306 pins for documents that use none of the keep members: the bytes after normalisation. Each
/// value was recorded on the commit before the keep rules existed, with the same test bodies, so a
/// difference means a document that asks for no keep came out differently. These bodies use no
/// keep member, which is why they also compile there.
/// </summary>
public sealed class KeepRulesByteIdentityTests
{
    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    private static Document NewDoc(bool footer = false)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 200),
            Margins = new EdgeInsets(20),
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        if (footer) doc.Footer = new RunningBand("{page} of {pages}");
        return doc;
    }

    private static Paragraph Lines(string tag, int count) =>
        new(string.Join('\n', Enumerable.Range(1, count).Select(i => tag + i.ToString(CultureInfo.InvariantCulture))), Body);

    private static string Hash(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        return $"{NormalisedSha256(bytes)} pages={PageCount(bytes)}";
    }

    [Fact]
    public void StrandedHeading_isStillTheLastLineOfPage1()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(new Heading("HeadA", Body));
        doc.Add(new Paragraph("BodyA", Body));

        Pin("E803DC0B37C2010DFEC8D715BA824482D0D5A09251BB358845559501E5F2BE25 pages=2", Hash(doc));
    }

    [Fact]
    public void Orphan_isStillOneLineOnPage1()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(Lines("o", 3));

        Pin("E1B19450F3D22619D8BE161C007F2857777540614C09C1645B7D3FE7F9B72968 pages=2", Hash(doc));
    }

    [Fact]
    public void ADocumentOfEveryElementKind_withBandsAndMirroredMargins_pinsBytes()
    {
        using var doc = NewDoc(footer: true);
        doc.MirrorMargins = true;
        doc.Tagged = true;
        doc.Add(Lines("f", 9));
        doc.Add(new Heading("A heading", Body) { Anchor = "h" });
        doc.Add(new Paragraph(ParagraphText(1), Body));
        doc.Add(new PageBreak { Recto = true });
        var table = new TableElement();
        for (var r = 0; r < 12; r++)
            table.AddRow().AddCell($"Row {r}").AddCell("cell");
        doc.Add(table);
        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < 8; i++)
            list.Add($"Item {i} of the list", Body);
        doc.Add(list);
        doc.Add(new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng())) { Width = 40, Height = 40 });
        doc.Add(new Paragraph(ParagraphText(2), Body) { BreakBefore = PageBreakMode.Page });
        doc.Add(new LineSeparator());

        Pin("7E926F36FB46E4BC70B8509EF0BC86165A6761EE5A5C505B15A1D7582B85B80B pages=7", Hash(doc));
    }
}
