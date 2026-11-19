// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// The signature dictionary's /Name, /Reason, /Location and /ContactInfo, and the output intent's
/// identifier, are PDF text strings (ISO 32000-2, Tables 255 and 401). Up to 2.3.3 they went
/// through <c>Encoding.Latin1.GetBytes</c>, whose fallback rewrites a character it cannot encode
/// into a look-alike (U+0141 to <c>L</c>, U+2014 to <c>-</c>) and reports nothing (#524).
///
/// Every expectation below is the exact byte sequence of the string as it sits in the file, found
/// by decoding the literal's own escapes, so a test cannot pass by finding a similar word nearby.
/// </summary>
public sealed class SignatureTextStringTests
{
    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf SignatureTextString Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    private static string S(params int[] codeUnits) => new(codeUnits.Select(c => (char)c).ToArray());

    private static byte[] Utf16(string s) => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(s)];

    private static byte[] Sign(PdfSignatureSettings settings)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        new PdfCanvas(page).Finish();
        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        return ms.ToArray();
    }

    /// <summary>The decoded bytes of the literal string that follows <paramref name="key"/> in the file.</summary>
    private static byte[] LiteralAfter(byte[] pdf, string key)
    {
        var needle = Encoding.ASCII.GetBytes(key + " (");
        var at = pdf.AsSpan().IndexOf(needle);
        Assert.True(at >= 0, $"{key} not found in the output");

        var result = new List<byte>();
        var depth = 1;
        for (var i = at + needle.Length; i < pdf.Length; i++)
        {
            var b = pdf[i];
            if (b == (byte)'\\')
            {
                var e = pdf[++i];
                result.Add(e switch { (byte)'n' => (byte)0x0A, (byte)'r' => (byte)0x0D, _ => e });
            }
            else if (b == (byte)'(')
            {
                depth++;
                result.Add(b);
            }
            else if (b == (byte)')' && --depth == 0)
            {
                return [.. result];
            }
            else
            {
                result.Add(b);
            }
        }

        throw new InvalidOperationException("unterminated literal string");
    }

    private static PdfSignatureSettings With(
        X509Certificate2 cert,
        string? name = null, string? reason = null, string? location = null, string? contact = null) =>
        new()
        {
            Certificate = cert,
            SignerName = name,
            Reason = reason,
            Location = location,
            ContactInfo = contact,
            SigningTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public void SignerName_withLStrokeAndEmDash_isWrittenAsUtf16_notTransliterated()
    {
        using var cert = CreateTestCertificate();
        // U+0141 u k a s z, space, M, u-diaeresis, l l e r, space, em dash, space, L t d
        var name = S(0x141) + "ukasz M" + S(0xFC) + "ller " + S(0x2014) + " Ltd";

        var pdf = Sign(With(cert, name: name));

        var written = LiteralAfter(pdf, "/Name");
        Assert.Equal(Utf16(name), written);
        // The 2.3.3 bytes, for contrast: "Lukasz M" 0xFC "ller - Ltd".
        Assert.NotEqual(Encoding.Latin1.GetBytes(name), written);
    }

    [Fact]
    public void SignerName_knownAnswer_exactBytes()
    {
        using var cert = CreateTestCertificate();

        var pdf = Sign(With(cert, name: S(0x141, 0x75)));

        // FE FF mark, U+0141, U+0075.
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x01, 0x41, 0x00, 0x75 }, LiteralAfter(pdf, "/Name"));
    }

    [Fact]
    public void Reason_Location_ContactInfo_eachKeepTheirNonLatin1Characters()
    {
        using var cert = CreateTestCertificate();
        var reason = "Review " + S(0x2014) + " round 3";
        var location = "Amsterdam " + S(0x2014) + " NL";
        var contact = "Tel " + S(0x20AC) + " 0" + S(0x65E5);

        var pdf = Sign(With(cert, reason: reason, location: location, contact: contact));

        Assert.Equal(Utf16(reason), LiteralAfter(pdf, "/Reason"));
        Assert.Equal(Utf16(location), LiteralAfter(pdf, "/Location"));
        Assert.Equal(Utf16(contact), LiteralAfter(pdf, "/ContactInfo"));
    }

    /// <summary>A name 2.3.3 wrote correctly must come out as the same bytes (rule 1).</summary>
    [Theory]
    [InlineData("Alice Tester")]
    [InlineData("Jean-Luc O'Brien (Ops)")]
    public void Ascii_isWrittenAsOneBytePerCharacter_withNoMark(string name)
    {
        using var cert = CreateTestCertificate();

        var pdf = Sign(With(cert, name: name, reason: name, location: name, contact: name));

        var expected = Encoding.ASCII.GetBytes(name);
        Assert.Equal(expected, LiteralAfter(pdf, "/Name"));
        Assert.Equal(expected, LiteralAfter(pdf, "/Reason"));
        Assert.Equal(expected, LiteralAfter(pdf, "/Location"));
        Assert.Equal(expected, LiteralAfter(pdf, "/ContactInfo"));
    }

    [Fact]
    public void Latin1Text_whichPdfDocEncodingShares_keepsItsSingleBytes()
    {
        using var cert = CreateTestCertificate();
        // M u-diaeresis ller, e-acute, n-tilde, y-diaeresis (0xFF), inverted exclamation (0xA1)
        var name = "M" + S(0xFC) + "ller " + S(0xE9, 0xF1, 0xFF, 0xA1);

        var pdf = Sign(With(cert, name: name));

        Assert.Equal(Encoding.Latin1.GetBytes(name), LiteralAfter(pdf, "/Name"));
    }

    /// <summary>
    /// The characters where Latin-1 and PDFDocEncoding give different bytes (Annex D.2, Table
    /// D.3): a reader would show the single byte as another character, so the UTF-16BE form is
    /// the one that says what the caller wrote.
    /// </summary>
    [Theory]
    [InlineData(0x18)] // PDFDocEncoding: BREVE
    [InlineData(0x1F)] // PDFDocEncoding: SMALL TILDE
    [InlineData(0x7F)] // undefined
    [InlineData(0x80)] // PDFDocEncoding: BULLET
    [InlineData(0x9E)] // PDFDocEncoding: z-caron
    [InlineData(0x9F)] // undefined
    [InlineData(0xA0)] // PDFDocEncoding: EURO SIGN, Latin-1: no-break space
    [InlineData(0xAD)] // undefined
    public void CharacterWhereTheTwoEncodingsDiffer_takesTheUtf16Form(int codeUnit)
    {
        using var cert = CreateTestCertificate();
        var name = "a" + S(codeUnit) + "b";

        var pdf = Sign(With(cert, name: name));

        Assert.Equal(Utf16(name), LiteralAfter(pdf, "/Name"));
    }

    /// <summary>
    /// 7.9.2.2.1 NOTE 3 and 4: a PDFDocEncoding string cannot begin with the bytes of a byte order
    /// mark. A name that does is a different string to a reader unless it takes the UTF-16BE form.
    /// </summary>
    [Theory]
    [InlineData(0xFE, 0xFF, 0x61)]
    [InlineData(0xEF, 0xBB, 0xBF)]
    public void NameBeginningWithTheBytesOfAByteOrderMark_takesTheUtf16Form(int a, int b, int c)
    {
        using var cert = CreateTestCertificate();
        var name = S(a, b, c) + "x";

        var pdf = Sign(With(cert, name: name));

        Assert.Equal(Utf16(name), LiteralAfter(pdf, "/Name"));
    }

    /// <summary>
    /// U+0000 to U+0017 other than the three the table defines are undefined in PDFDocEncoding
    /// (Table D.3) and stay one byte, as 2.3.3 wrote them; U+0009, U+000A and U+000D are defined.
    /// </summary>
    [Theory]
    [InlineData(0x01)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x17)]
    public void ControlBelowU0018_staysOneByte(int codeUnit)
    {
        using var cert = CreateTestCertificate();

        var pdf = Sign(With(cert, name: "a" + S(codeUnit) + "b"));

        Assert.Equal(new byte[] { 0x61, (byte)codeUnit, 0x62 }, LiteralAfter(pdf, "/Name"));
    }

    /// <summary>
    /// U+001B is not special-cased: it takes the UTF-16BE form like any code in U+0018 to U+001F
    /// and is written as it stands, which a reader takes for a language escape (7.9.2.2.2). A
    /// test records the behaviour the documentation describes.
    /// </summary>
    [Fact]
    public void Escape_isWrittenAsItStands_inTheUtf16Form()
    {
        using var cert = CreateTestCertificate();

        var pdf = Sign(With(cert, name: "a" + S(0x1B) + "b"));

        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x00, 0x61, 0x00, 0x1B, 0x00, 0x62 }, LiteralAfter(pdf, "/Name"));
    }

    [Fact]
    public void SupplementaryCharacter_isWrittenAsASurrogatePair()
    {
        using var cert = CreateTestCertificate();
        var name = "x" + char.ConvertFromUtf32(0x1F600);

        var pdf = Sign(With(cert, name: name));

        // FE FF, U+0078, D83D DE00.
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x00, 0x78, 0xD8, 0x3D, 0xDE, 0x00 }, LiteralAfter(pdf, "/Name"));
    }

    [Fact]
    public void LoneSurrogate_stillSigns_andIsWrittenAsTheReplacementCharacter()
    {
        using var cert = CreateTestCertificate();

        var pdf = Sign(With(cert, name: "a" + S(0xD800) + "b"));

        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x00, 0x61, 0xFF, 0xFD, 0x00, 0x62 }, LiteralAfter(pdf, "/Name"));
    }

    // ── Output intent identifier (Table 401) ────────────────────────────────────

    private static byte[] SavePdfA(Action<PdfDocument> configure)
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "t";
        configure(doc);
        new PdfCanvas(doc.AddPage()).Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void OutputIntentIdentifier_withEmDashAndLStroke_isNotTransliterated()
    {
        var id = "Generic CMYK " + S(0x2014) + " " + S(0x141) + " probe";

        var pdf = SavePdfA(d => d.UseCmykOutputIntent(id));

        Assert.Equal(Utf16(id), LiteralAfter(pdf, "/OutputConditionIdentifier"));
        Assert.Equal(Utf16(id), LiteralAfter(pdf, "/Info"));
    }

    [Fact]
    public void OutputIntentIdentifier_ascii_isUnchanged()
    {
        var pdf = SavePdfA(d => d.UseCmykOutputIntent("FOGRA-test"));

        Assert.Equal(Encoding.ASCII.GetBytes("FOGRA-test"), LiteralAfter(pdf, "/OutputConditionIdentifier"));
    }
}
