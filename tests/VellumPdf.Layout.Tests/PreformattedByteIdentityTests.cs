// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A pin for a document that holds none of the preformatted element: a paragraph, a heading, a
/// list, a table and both running bands. The value was recorded on the commit before the element
/// existed, so a difference means the output of a document without it moved (#591).
/// </summary>
public sealed class PreformattedByteIdentityTests
{
    private static readonly string Regular = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");

    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    private static Document Build(bool tagged)
    {
        var doc = new Document
        {
            Timestamp = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Tagged = tagged,
            Language = "nl-NL",
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(30),
            Header = new RunningBand("Header {page}"),
            Footer = new RunningBand("{page} of {pages}"),
        };
        doc.Info.Title = "Pin";
        var embedded = new TextStyle { FontRef = doc.LoadTrueTypeFont(Regular), FontSize = 11 };
        doc.Add(new Heading("Installatie"));
        doc.Add(new Paragraph("Een alinea  met   dubbele spaties\ten een tab, lang genoeg om af te breken op een smalle pagina."));
        doc.Add(new Paragraph("Embedded font paragraph with enough words to wrap across more than one line.", embedded));
        doc.Add(new ListElement(ListStyle.Unordered).Add("First").Add("Second").Add("Third"));
        var table = new TableElement();
        table.AddRow().AddCell("a").AddCell("b");
        table.AddRow().AddCell("c").AddCell("d");
        doc.Add(table);
        for (var i = 0; i < 30; i++)
            doc.Add(new Paragraph($"Filler {i} " + new string('x', 40), new TextStyle { Font = Standard14.TimesRoman }));
        return doc;
    }

    private static string Hash(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return NormalisedSha256(ms.ToArray(), foldToUnicodeLineEnds: true) + " pages=" + PageCount(ms.ToArray());
    }

    [Fact]
    public void UntaggedDocumentWithoutTheElement_pinsTheBytes() =>
        Pin("F726116FCB3F29EE794620EB2FD8C38092098B083C1390478736C4D4EB9E1807 pages=6", Hash(Build(tagged: false)));

    [Fact]
    public void TaggedDocumentWithoutTheElement_pinsTheBytes() =>
        Pin("B641183750D6D7E10BE0959968DAAD046415B1493ADEEFD0E59891D6CEBA253A pages=6", Hash(Build(tagged: true)));
}
