// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader.Tests.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Known-answer tests, through <see cref="PdfReader.Open(byte[])"/> on an unencrypted file, that an
/// unescaped end-of-line marker in a literal string reads as one 0Ah (ISO 32000-2, 7.3.4.2, #511)
/// at every place the reader decodes a string it knows is plaintext: an object parsed from the
/// file body of an unencrypted file, a member of an object stream, and a content-stream operand.
/// Each test fails when the normalisation is switched off at the site it names.
/// </summary>
public sealed class PdfReaderEndOfLineNormalisationTests
{
    private static readonly PdfName Lang = new("Lang");

    private static byte[] LangOf(PdfDocumentReader reader) =>
        Assert.IsType<PdfLiteralString>(reader.Catalog.Get(Lang)).Bytes.ToArray();

    [Theory]
    [InlineData("(en\r\n)", new byte[] { 0x65, 0x6E, 0x0A })]
    [InlineData("(en\r)", new byte[] { 0x65, 0x6E, 0x0A })]
    [InlineData("(en\n)", new byte[] { 0x65, 0x6E, 0x0A })]
    public void CatalogLangInTheFileBody_readsOneLineFeedForAnEndOfLineMarker(string literal, byte[] expected)
    {
        var pdf = TextTestSupport.BuildPdf(
            1,
            new TextTestSupport.Obj(1, $"<< /Type /Catalog /Pages 2 0 R /Lang {literal} >>"),
            new TextTestSupport.Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));

        using var reader = PdfReader.Open(pdf);

        Assert.Equal(expected, LangOf(reader));
    }

    [Fact]
    public void CatalogLangInTheFileBody_keepsTheEscapesAsWritten()
    {
        var pdf = TextTestSupport.BuildPdf(
            1,
            new TextTestSupport.Obj(1, @"<< /Type /Catalog /Pages 2 0 R /Lang (en\r\n) >>"),
            new TextTestSupport.Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));

        using var reader = PdfReader.Open(pdf);

        Assert.Equal(new byte[] { 0x65, 0x6E, 0x0D, 0x0A }, LangOf(reader));
    }

    /// <summary>
    /// The catalog is member 0 of an uncompressed-xref object stream, so resolving it goes through
    /// the object-stream member parse and not through the file-level parse.
    /// </summary>
    [Theory]
    [InlineData("(a\r\nb)", new byte[] { 0x61, 0x0A, 0x62 })]
    [InlineData("(a\rb)", new byte[] { 0x61, 0x0A, 0x62 })]
    public void ObjectStreamMemberString_readsOneLineFeedForAnEndOfLineMarker(string literal, byte[] expected)
    {
        var catalog = $"<< /Type /Catalog /Pages 3 0 R /Lang {literal} >>";
        var pages = "<< /Type /Pages /Kids [] /Count 0 >>";
        var header = $"2 0 3 {catalog.Length} ";
        var body = header + catalog + pages;

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.5\n");
        var o1 = (int)ms.Position;
        W($"1 0 obj\n<< /Type /ObjStm /N 2 /First {header.Length} /Length {body.Length} >>\nstream\n{body}\nendstream\nendobj\n");
        var xrefOffset = (int)ms.Position;
        var rows = new List<byte>();
        void Row(byte type, int f2, byte f3) => rows.AddRange([type, (byte)(f2 >> 8), (byte)f2, f3]);
        Row(0, 0, 255);
        Row(1, o1, 0);
        Row(2, 1, 0);
        Row(2, 1, 1);
        Row(1, xrefOffset, 0);
        W($"4 0 obj\n<< /Type /XRef /Size 5 /W [1 2 1] /Root 2 0 R /Length {rows.Count} >>\nstream\n");
        ms.Write(rows.ToArray());
        W("\nendstream\nendobj\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray());

        Assert.Equal(expected, LangOf(reader));
    }

    /// <summary>
    /// A font whose <c>/Differences</c> name code 0Ah "L" and code 0Dh "C", so the extracted text says
    /// which byte the content-stream decoder produced for the raw end-of-line marker.
    /// </summary>
    private static string ExtractFrom(string content)
    {
        var font = TextTestSupport.SimpleFontDict(0, 255).Replace(
            "/Encoding /WinAnsiEncoding",
            "/Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [10 /L 13 /C] >>");
        var pdf = TextTestSupport.BuildPageDoc(
            content, "<< /Font << /F1 5 0 R >> >>", new TextTestSupport.Obj(5, font));
        using var reader = PdfReader.Open(pdf);
        return reader.GetPage(0).ExtractText().Text;
    }

    [Theory]
    [InlineData("BT /F1 12 Tf 100 700 Td (a\rb) Tj ET", "aLb")]
    [InlineData("BT /F1 12 Tf 100 700 Td (a\r\nb) Tj ET", "aLb")]
    [InlineData("BT /F1 12 Tf 100 700 Td (a\\rb) Tj ET", "aCb")]
    public void ContentStreamTjOperand_readsOneLineFeedForAnEndOfLineMarker(string content, string expected)
    {
        Assert.Equal(expected, ExtractFrom(content));
    }

    /// <summary>An array operand is parsed by <c>PdfObjectParser</c>, not by the operand switch, so it is a separate site.</summary>
    [Theory]
    [InlineData("BT /F1 12 Tf 100 700 Td [(a\rb)] TJ ET", "aLb")]
    [InlineData("BT /F1 12 Tf 100 700 Td [(a\r\nb)] TJ ET", "aLb")]
    [InlineData("BT /F1 12 Tf 100 700 Td [(a\\rb)] TJ ET", "aCb")]
    public void ContentStreamTjArrayOperand_readsOneLineFeedForAnEndOfLineMarker(string content, string expected)
    {
        Assert.Equal(expected, ExtractFrom(content));
    }
}
