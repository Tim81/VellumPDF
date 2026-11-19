// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance.Rules;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// <c>ContentStreamUsage</c> decodes the strings of a page's content stream with the reader's
/// literal-string decode, so an unescaped end-of-line marker reads as one 0Ah (ISO 32000-2, 7.3.4.2,
/// #511). Those bytes feed the PDF/UA font rules through <c>ContentUsage.TextShows</c>, and the
/// decoded text of an inline marked-content property dictionary feeds the language and alternate-text
/// rules.
/// </summary>
public sealed class ContentStreamUsageStringDecodeTests
{
    private static ContentUsage Analyze(string content)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new List<int>();
        void Obj(string body)
        {
            offsets.Add((int)ms.Position);
            W($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        W("%PDF-1.7\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var xref = (int)ms.Position;
        W($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray());
        var context = new PreflightContext(reader, PdfConformance.PdfUA1, []);
        return ContentStreamUsage.Analyze(context, context.EnumeratePages().First());
    }

    [Theory]
    [InlineData("BT /F1 12 Tf (a\rb) Tj ET", new byte[] { 0x61, 0x0A, 0x62 })]
    [InlineData("BT /F1 12 Tf (a\r\nb) Tj ET", new byte[] { 0x61, 0x0A, 0x62 })]
    [InlineData("BT /F1 12 Tf (a\nb) Tj ET", new byte[] { 0x61, 0x0A, 0x62 })]
    [InlineData("BT /F1 12 Tf [(a\rb)] TJ ET", new byte[] { 0x61, 0x0A, 0x62 })]
    public void TextShowBytes_readAnEndOfLineMarkerAsOneLineFeed(string content, byte[] expected)
    {
        var show = Assert.Single(Analyze(content).TextShows);

        Assert.Equal(expected, show.Bytes);
    }

    [Fact]
    public void TextShowBytes_keepTheEscapesAsWritten()
    {
        var show = Assert.Single(Analyze("BT /F1 12 Tf (a\\rb) Tj ET").TextShows);

        Assert.Equal(new byte[] { 0x61, 0x0D, 0x62 }, show.Bytes);
    }

    [Fact]
    public void InlineMarkedContentLang_readsAnEndOfLineMarkerAsOneLineFeed()
    {
        var usage = Analyze("/Span << /Lang (en\r) >> BDC BT /F1 12 Tf (a) Tj ET EMC");

        var sequence = Assert.Single(usage.MarkedContentSequences);
        Assert.Equal("en\n", sequence.Lang);
    }
}
