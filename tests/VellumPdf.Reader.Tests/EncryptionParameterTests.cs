// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Where the file encryption key's length comes from, and what happens when the permission bits are
/// edited after the fact. Every fixture in the corpus carries a redundant top-level <c>/Length</c>
/// that agrees with its crypt filter's, so none of them can tell a reader that reads the wrong one
/// from a reader that reads the right one — these documents remove the redundancy.
/// </summary>
public sealed class EncryptionParameterTests
{
    /// <summary>
    /// ISO 32000-1 Table 20 scopes the top-level <c>/Length</c> to "only if V is 2 or 3". For
    /// <c>/V 4</c> the length that applies is the crypt filter's own (Table 25), so a conformant
    /// producer may omit the top-level entry — and defaulting to 40 bits there derives a 5-byte key,
    /// fails the <c>/U</c> check, and rejects the CORRECT password on a file every other reader
    /// opens. The patch renames the top-level key rather than deleting it, so every byte offset in
    /// the cross-reference table stays valid.
    /// </summary>
    [Fact]
    public void V4_withNoTopLevelLength_takesTheKeyLengthFromTheCryptFilter()
    {
        var bytes = PatchOnce(Load("enc-aes-128.pdf"), "/Standard /Length 128", "/Standard /Zength 128");

        using var reader = PdfReader.Open(bytes, "u");

        Assert.Equal(128, reader.Encryption!.KeyLengthBits);
        Assert.Equal("GoldenStandardFont", GetInfoTitle(reader));
    }

    /// <summary>
    /// ISO 32000-1 §7.6.3.3, Algorithm 2, step (i): "n shall always be 5 for security handlers of
    /// revision 2". The revision overrides <c>/Length</c>, so this document — R2 claiming a 128-bit
    /// key, with an <c>/O</c> and <c>/U</c> that were computed at n=5 — must still authenticate.
    /// Reading /Length instead derives a 16-byte key and rejects the right password.
    /// </summary>
    [Fact]
    public void R2_ignoresLength_andAlwaysUsesAFiveByteKey()
    {
        // /O, /U and /P come from enc-rc4-40.pdf (R2, and therefore n=5), as does the trailer /ID
        // this document repeats — all four are inputs to Algorithm 2.
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 2 /R 2 /Length 128 "
            + "/O <853fee3f6550fc3bc212797eaed99cc9be53347583a738e25fdfb1242bf93366> "
            + "/U <72ed1b62959818a597a7a72491caf6084542a05eaf1d681d65b617e608e3ef4d> /P -4 >>");

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(40, reader.Encryption!.KeyLengthBits);
    }

    /// <summary>
    /// ISO 32000-2 §7.6.4.4.12, Algorithm 13. At R≤4 <c>/P</c> is an input to Algorithm 2, so
    /// editing it breaks authentication by itself. At R≥5 the file key is random and <c>/P</c> is
    /// protected only by <c>/Perms</c>, which holds a copy of it encrypted under that key — so
    /// without the Algorithm 13 check a two-byte edit to <c>/P</c> silently grants every permission,
    /// and the library reports the attacker's bits as the document's.
    /// </summary>
    [Fact]
    public void R6_permissionBitsEditedAfterEncryption_areRejected()
    {
        var tampered = PatchOnce(Load("enc-aes-256-r6.pdf"), "/P -4 ", "/P -8 ");

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(tampered, "u"));

        Assert.Contains("/Perms", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control for the test above: untouched, the same fixture verifies and opens.</summary>
    [Fact]
    public void R6_untamperedPermissions_verify()
    {
        using var reader = PdfReader.Open(Load("enc-aes-256-r6.pdf"), "u");

        Assert.Equal(-4 & (int)PdfPermissions.All, (int)reader.Encryption!.Permissions);
    }

    /// <summary>
    /// An empty string is legal PDF, and a producer has nothing to encrypt for one — not even an
    /// IV, so its encrypted form is empty too. Demanding an IV rejects the document, and rejects it
    /// hard: the exception comes out of every object that contains such a string.
    /// </summary>
    [Fact]
    public void EmptyString_underAes_decryptsToEmpty_ratherThanThrowing()
    {
        var doc = BuildWithEncryptDict(
            "<< /CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF "
            + "/Filter /Standard /Length 128 /V 4 /R 4 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            "<< /Empty <> /AlsoEmpty () >>");

        using var reader = PdfReader.Open(doc, "u");
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));

        Assert.Empty(((PdfHexString)dict.Get(new PdfName("Empty"))!).Bytes.ToArray());
        Assert.Empty(((PdfLiteralString)dict.Get(new PdfName("AlsoEmpty"))!).Bytes.ToArray());
    }

    /// <summary>
    /// ISO 32000-1 §7.6.1 requires only the STRINGS in the encryption dictionary to be direct
    /// objects. Everything else — <c>/P</c> here, <c>/CF</c> in the next test — may legally be an
    /// indirect reference, and a file that uses one is not malformed.
    /// </summary>
    [Fact]
    public void IndirectPValue_isResolved_ratherThanRejected()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 2 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P 5 0 R >>",
            extraObjects: ["-4"]);

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(-4 & (int)PdfPermissions.All, (int)reader.Encryption!.Permissions);
    }

    /// <summary>
    /// The <c>/CF</c> case is the nastier one: authentication never touches the crypt filter table,
    /// so an unresolved <c>/CF</c> lets the document open normally and then throws on the first
    /// stream — "names a /CFM this handler does not implement" — on a file that is perfectly valid.
    /// </summary>
    [Fact]
    public void IndirectCryptFilterDictionary_isResolved()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 /CF 5 0 R /StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            extraObjects: ["<< /StdCF << /CFM /AESV2 /Length 16 >> >>"]);

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(PdfCipherAlgorithm.Aes128, reader.Encryption!.Cipher);
    }

    /// <summary>
    /// An incremental update over an encrypted document has to repeat <c>/Encrypt</c> in its own
    /// trailer. One that drops it is malformed, and the dangerous reading is the quiet one: only the
    /// newest trailer is consulted, so the document would open as plaintext and every stream would
    /// decode to ciphertext with no error at all — a caller would take the noise for the content.
    /// </summary>
    [Fact]
    public void EncryptDeclaredOnlyOnAnEarlierRevision_isRejected_ratherThanReadAsPlaintext()
    {
        var baseRevision = BuildWithEncryptDict(
            "<< /Filter /Standard /V 2 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        // Sanity: the base revision on its own is a document this reader opens.
        using (var ok = PdfReader.Open(baseRevision, "u"))
            Assert.NotNull(ok.Encryption);

        var baseStartxref = int.Parse(
            Encoding.Latin1.GetString(baseRevision)
                .Split("startxref\n")[^1]
                .Split('\n')[0],
            System.Globalization.CultureInfo.InvariantCulture);

        var ms = new MemoryStream();
        ms.Write(baseRevision);
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));

        var updateXref = (int)ms.Position;
        W("xref\n0 1\n0000000000 65535 f \n");
        W($"trailer\n<< /Size 5 /Root 1 0 R /Prev {baseStartxref} "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{updateXref}\n%%EOF\n");

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(ms.ToArray(), "u"));

        Assert.Contains("/Encrypt", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A /StrF that resolves to nothing means every string in the document is undecryptable, and
    /// before this it was entirely silent: the document opened, each string came back as ciphertext,
    /// and no API reported the condition at all. An unresolvable /StmF is deliberately NOT treated
    /// this way (see StmFNamingUndefinedCfEntry_opensButThrowsOnDecode) — a document whose streams
    /// cannot be decrypted still has readable strings, while the converse has nothing to offer.
    /// </summary>
    [Fact]
    public void StrF_namingAnUndefinedCryptFilter_failsAtOpen()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /AESV2 /Length 16 >> >> "
            + "/StmF /StdCF /StrF /Ghost "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(doc, "u"));

        Assert.Contains("/StrF", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ISO 32000-2 Table 20 lets a document use different crypt filters for strings and streams, so
    /// one reported cipher cannot describe both.
    /// </summary>
    [Fact]
    public void StreamAndStringCiphers_areReportedSeparately()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StdCF << /CFM /AESV2 /Length 16 >> /Legacy << /CFM /V2 /Length 16 >> >> "
            + "/StmF /StdCF /StrF /Legacy "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(PdfCipherAlgorithm.Aes128, reader.Encryption!.Cipher);
        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption.StringCipher);
    }

    /// <summary>
    /// ISO 32000-1 §7.6.1 lets a document encrypt only its attachments: <c>/StmF</c> and
    /// <c>/StrF</c> Identity, with <c>/EFF</c> naming a real crypt filter for embedded file streams.
    /// This handler has no per-stream <c>/EFF</c> selection, so it would decode those streams under
    /// <c>/StmF</c> and hand back ciphertext as though it were the attachment.
    /// </summary>
    [Fact]
    public void AttachmentOnlyEncryption_isRefused_ratherThanSilentlyMishandled()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /Identity /StrF /Identity /EFF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        var ex = Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(doc, "u"));

        Assert.Contains("/EFF", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A malformed top-level <c>/Length</c> is a malformed-file condition, not something to round
    /// off: ISO 32000-1 Table 20 requires a multiple of 8 between 40 and 128.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(0)]
    [InlineData(2147483647)]
    public void TopLevelLength_outsideTheLegalRange_isRejected(int bits)
    {
        var doc = BuildWithEncryptDict(
            $"<< /Filter /Standard /V 2 /R 3 /Length {bits} "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(doc, "u"));

        Assert.Contains("/Length", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/V</c> and <c>/R</c> are required (ISO 32000-1 Table 20). Defaulting a missing one would
    /// pick an algorithm the document never asked for and then fail authentication for a reason
    /// that has nothing to do with the password.
    /// </summary>
    [Theory]
    [InlineData("/V 2")]
    [InlineData("/R 3")]
    public void MissingRequiredEncryptEntry_isRejected(string omitted)
    {
        var full = "<< /Filter /Standard /V 2 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>";

        var doc = BuildWithEncryptDict(full.Replace(omitted + " ", "", StringComparison.Ordinal));

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(doc, "u"));

        Assert.Contains(omitted.Split(' ')[0], ex.Message, StringComparison.Ordinal);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static readonly byte[] Id0 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    // Same byte count in and out, so every offset in the cross-reference table stays correct and the
    // document under test differs from the fixture in exactly the one way the test is about.
    private static byte[] PatchOnce(byte[] bytes, string find, string replace)
    {
        Assert.Equal(find.Length, replace.Length);

        var text = Encoding.Latin1.GetString(bytes);
        var at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{find}' not found in the fixture.");
        Assert.Equal(-1, text.IndexOf(find, at + 1, StringComparison.Ordinal));

        var patched = text[..at] + replace + text[(at + find.Length)..];
        return Encoding.Latin1.GetBytes(patched);
    }

    private static byte[] BuildWithEncryptDict(
        string encryptDict,
        string thirdObject = "<< /Probe 1 >>",
        params string[] extraObjects)
    {
        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            thirdObject,
            encryptDict,
        };
        bodies.AddRange(extraObjects);

        var offsets = new List<int>();
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets.Add((int)ms.Position);
            W($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var size = bodies.Count + 1;
        var xref = (int)ms.Position;
        W($"xref\n0 {size}\n{0:D10} 65535 f \n");
        foreach (var offset in offsets)
            W($"{offset:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R /Encrypt 4 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string GetInfoTitle(PdfDocumentReader reader)
    {
        var info = (PdfDictionary)reader.Resolve((PdfIndirectReference)reader.Trailer.Get(new PdfName("Info"))!)!;
        var title = info.Get(new PdfName("Title"))!;
        var bytes = title switch
        {
            PdfLiteralString l => l.Bytes.ToArray(),
            PdfHexString h => h.Bytes.ToArray(),
            _ => throw new InvalidOperationException("/Title is not a string."),
        };

        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.ASCII.GetString(bytes);
    }
}
