// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #608 pin for a document that does not use <c>MirrorMargins</c>: the bytes after normalisation.
/// The value was recorded on the commit before the property existed, with this body, so a
/// difference means a document that leaves the property alone came out differently. The margins
/// are not symmetric, so a left and a right margin that were swapped or mixed up would show.
/// </summary>
public sealed class MirrorMarginsByteIdentityTests
{
    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    [Fact]
    public void ADocumentWithAsymmetricMargins_bandsAndRectoBreaks_pinsBytes()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 320, 300),
            Margins = new EdgeInsets(20, 40, 20, 80),
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Header = new RunningBand("Header {page}", Body, HorizontalAlignment.Left),
            Footer = new RunningBand("{page} of {pages}", Body, HorizontalAlignment.Right),
        };
        doc.Add(new Paragraph(ParagraphText(1), Body) { Alignment = HorizontalAlignment.Right });
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Heading("A heading", Body));
        var table = new TableElement();
        for (var r = 0; r < 3; r++)
        {
            var row = table.AddRow(isHeader: r == 0);
            row.AddCell($"cell {r}a");
            row.AddCell($"cell {r}b with several words to wrap in the column");
        }

        doc.Add(table);
        doc.Add(new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng())) { Width = 50, Height = 50 });
        for (var i = 2; i < 12; i++)
            doc.Add(new Paragraph(ParagraphText(i), Body));
        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Pin("038108F903125ACD38911E07FFBFB417E54D596CB96E2D26048D77EC157E0530 pages=5", $"{NormalisedSha256(bytes)} pages={PageCount(bytes)}");
    }
}
