// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using static VellumPdf.Layout.Tests.FontReportSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The reports for glyph 0 and for an unembedded font add diagnostics and one ToUnicode entry; they
/// do not change what the save writes otherwise (#592, #593). These pin the bytes of documents that
/// reach the changed drawing sites, hashed after the same normalisation the placement-loop pins
/// use. The hashes were taken before the change.
/// </summary>
public sealed class FontReportByteIdentityTests
{
    private static void Pin(string expected, byte[] pdf) =>
        Assert.True(expected == NormalisedSha256(pdf), $"computed {NormalisedSha256(pdf)}");

    private static Document Embedded(PdfConformance c, out TextStyle style, double size = 11)
    {
        var doc = NewDocument(c);
        style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes), FontSize = size };
        return doc;
    }

    // A claim, a default style, and a list and a table that do not use it.
    private static byte[] G3()
    {
        using var doc = Embedded(PdfConformance.PdfA2u, out var style);
        doc.SetDefaultFont(style);
        doc.Add("Hello");
        var list = new ListElement();
        list.Add("item");
        doc.Add(list);
        var table = new TableElement();
        table.AddRow().AddCell("cell");
        doc.Add(table);
        return Save(doc);
    }

    // The same, with an unstyled paragraph, heading and footer too.
    private static byte[] B593()
    {
        using var doc = Embedded(PdfConformance.PdfA2u, out var style);
        doc.SetDefaultFont(style);
        doc.Add("Hello");
        doc.Add(new Paragraph("para"));
        doc.Add(new Heading("head"));
        var list = new ListElement();
        list.Add("item");
        doc.Add(list);
        var table = new TableElement();
        table.AddRow().AddCell("cell");
        doc.Add(table);
        doc.SetFooter("foot");
        return Save(doc);
    }

    // An empty cell in the default font.
    private static byte[] B3()
    {
        using var doc = Embedded(PdfConformance.PdfA2u, out var style, 12);
        doc.Add(new Paragraph("x", style));
        var table = new TableElement();
        table.AddRow().AddCell("");
        doc.Add(table);
        return Save(doc);
    }

    // Two runs of one font at two sizes: the second is drawn at the size of the first.
    private static byte[] Sizes()
    {
        using var doc = NewDocument(PdfConformance.None);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var p = new Paragraph("ten ", new TextStyle { FontRef = font, FontSize = 10 });
        p.Add("twenty", new TextStyle { FontRef = font, FontSize = 20 });
        doc.Add(p);
        return Save(doc);
    }

    // No claim; Standard 14 and embedded text in every kind of element and in both bands.
    private static byte[] Mixed()
    {
        using var doc = NewDocument(PdfConformance.None);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var embedded = new TextStyle { FontRef = font, FontSize = 11 };
        var times = new TextStyle { Font = Standard14.TimesRoman, FontSize = 10 };
        var p = new Paragraph("Standard ", times);
        p.Add("embedded ", embedded);
        p.Add("standard again", times);
        doc.Add(p);
        doc.Add(new Heading("Heading", embedded));
        doc.Add(new Heading("Plain heading"));
        var list = new ListElement { DefaultStyle = embedded };
        list.Add("embedded item");
        doc.Add(list);
        var plainList = new ListElement();
        plainList.Add("plain item");
        doc.Add(plainList);
        var table = new TableElement { DefaultCellStyle = embedded };
        var row = table.AddRow();
        row.AddCell("embedded cell");
        row.AddCell(new Cell("styled cell") { Style = times });
        doc.Add(table);
        doc.SetHeader("header", embedded);
        doc.SetFooter("footer {page}", times);
        return Save(doc);
    }

    [Fact]
    public void G3_pinsBytes() => Pin("017D92FAF138AA27AA9017AA0E14A3FD74A6A492E81D17BF8B1F2EE4A6BB583E", G3());

    [Fact]
    public void B593_pinsBytes() => Pin("2734A24F2D1468EE6595743EF9DCC2AF8710E7E27A58254103572BC682D534E8", B593());

    [Fact]
    public void B3_pinsBytes() => Pin("DC5A7C047A27937AE8831209F13C63C3D199C30670AE38288BB05E7CFD7C661D", B3());

    [Fact]
    public void Sizes_pinsBytes_andWritesOneFontOperator()
    {
        var pdf = Sizes();

        Assert.Equal(["/TT1 10 Tf"], TfLines(pdf));
        Pin("1176E8A54DA85123D336594E0DFB5BD7BB4461DE3AF0F83FE24E64B9BF522E53", pdf);
    }

    [Fact]
    public void MixedNoClaimDocument_pinsBytes() => Pin("48AEBB1188593377629E948FCA1E73363BC6AE47B6A27493DC3E005B8A5C974F", Mixed());

    // The ToUnicode stream is the one place the change moves a byte for a document that draws a
    // missing character, so a projection without it is what must equal the file from before.
    private static readonly Regex ToUnicodeStream = new(
        @"(?<head>\d+ 0 obj\n<<(?:(?!>>).)*?/Length )\d+(?<mid>(?:(?!>>).)*>>\nstream\n)/CIDInit.*?(?<tail>\nendstream)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string ProjectionWithoutToUnicode(byte[] pdf)
    {
        var text = ToUnicodeStream.Replace(
            Normalise(pdf), m => m.Groups["head"].Value + "0" + m.Groups["mid"].Value + m.Groups["tail"].Value);
        Assert.DoesNotContain("begincmap", text, StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(text)));
    }

    [Fact]
    public void AMissingCharacterDocument_differsOnlyInItsToUnicodeStream()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        doc.Add(new Paragraph("A ⚹ B \U0001F31F C", new TextStyle { FontRef = font }));

        var pdf = Save(doc);

        Assert.Contains("<0000> <FFFD>", BfcharLines(pdf));
        Assert.True("E0FA95F4D77D310F8359CBF807FA884EA934D40132B2F00B1090D6078031E92C" == ProjectionWithoutToUnicode(pdf), $"computed {ProjectionWithoutToUnicode(pdf)}");
    }
}
