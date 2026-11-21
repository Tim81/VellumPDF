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
/// use. The hashes are those of a build without the reports, so they move when a change to the
/// library alters what it writes for these documents.
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
    private static byte[] DefaultFontWithUnstyledListAndTable()
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
    private static byte[] UnstyledElementsAndFooterUnderAClaim()
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
    private static byte[] EmptyUnstyledCell()
    {
        using var doc = Embedded(PdfConformance.PdfA2u, out var style, 12);
        doc.Add(new Paragraph("x", style));
        var table = new TableElement();
        table.AddRow().AddCell("");
        doc.Add(table);
        return Save(doc);
    }

    // Two runs of one font at two sizes: the second is drawn at the size of the first.
    private static byte[] TwoSizesOfOneFont()
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
    public void DefaultFontWithUnstyledListAndTable_pinsBytes() => Pin("AA73683C89C619860C1C6F8186CBC71627EA9DF9536F8842BDF5C61A17062624", DefaultFontWithUnstyledListAndTable());

    [Fact]
    public void UnstyledElementsAndFooterUnderAClaim_pinsBytes() => Pin("75D28BEF643ED0314FBFA1BD2BAF650323D5C1C49991149DEE87DA9D03B25EC1", UnstyledElementsAndFooterUnderAClaim());

    [Fact]
    public void EmptyUnstyledCell_pinsBytes() => Pin("4E8A3E5ED673875A32D725DFA2580A780458527DA36CE7C10D2EED34873F19F3", EmptyUnstyledCell());

    [Fact]
    public void TwoSizesOfOneFont_pinsBytes_andWritesOneFontOperator()
    {
        var pdf = TwoSizesOfOneFont();

        Assert.Equal(["/TT1 10 Tf"], TfLines(pdf));
        Pin("F43A4C9947E3A4630FC0A7F3AF69141EFEC37C1F6295FE2727B2AB37E57588D0", pdf);
    }

    [Fact]
    public void MixedNoClaimDocument_pinsBytes() => Pin("55AE9646D345D4F18987546EEF5A862346A8E1C800B826286F6266C4464E9681", Mixed());

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
        Assert.True("418EEEA4A3F947CB0DDAB22D49AE6AE9993B24796C377E384E953CC14A8FBF71" == ProjectionWithoutToUnicode(pdf), $"computed {ProjectionWithoutToUnicode(pdf)}");
    }
}
