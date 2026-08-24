// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;
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
    ///
    /// <para>The sibling below patches that entry to 40 bits rather than hiding it. Removing it
    /// alone does not prove much: with no top-level <c>/Length</c> the fallback answers 128 bits at
    /// <c>/V</c> 4 anyway, which is the same answer the crypt filter gives, so the document opens
    /// either way. 40 bits derives a five-byte key that fails the <c>/U</c> check.</para>
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
    /// The same rule with the top-level entry present and wrong — the shape a producer leaves behind
    /// when it upgrades a document to <c>/V</c> 4 and does not clear the entry Table 20 has stopped
    /// scoping to it. Only the crypt filter's length opens this file; the top-level 40 bits derives a
    /// five-byte key. Written as <c>040</c> so the patch is byte-for-byte the same length as the
    /// <c>128</c> it replaces and every cross-reference offset stays valid.
    /// </summary>
    [Fact]
    public void V4_topLevelLengthContradictingTheCryptFilter_isIgnored()
    {
        var bytes = PatchOnce(Load("enc-aes-128.pdf"), "/Standard /Length 128", "/Standard /Length 040");

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
    /// The <c>/V</c> 1 rule, end to end. <c>enc-rc4-40.pdf</c> cannot show it: that fixture declares
    /// <c>/Length 40</c>, which is what the rule produces anyway, so a reader with no <c>/V</c> 1 rule
    /// at all opens it. Table 20 makes the entry optional and scopes it to "V is 2 or 3" regardless,
    /// so a <c>/V</c> 1 file may legitimately carry none — and the fallback's own default is 40 bits
    /// there, which agrees again. Declaring 128 is the one shape that disagrees, and no tool in reach
    /// writes it. Renaming the key to <c>/Zength</c> keeps every cross-reference offset valid; the
    /// document is then <c>/V</c> 1 with nothing said about its length at all.
    /// </summary>
    [Fact]
    public void V1_withNoLengthAtAll_isStillFortyBit()
    {
        var bytes = PatchOnce(Load("enc-rc4-40.pdf"), "/Standard /Length 40", "/Standard /Zength 40");

        using var reader = PdfReader.Open(bytes, "u");

        Assert.Equal(40, reader.Encryption!.KeyLengthBits);
        Assert.Equal("GoldenStandardFont", GetInfoTitle(reader));
    }

    /// <summary>
    /// ISO 32000-2 §7.6.4.4.12, Algorithm 13. At R≤4 <c>/P</c> is an input to Algorithm 2, so editing
    /// it breaks authentication by itself. At R≥5 the file key is random and the dictionary's
    /// <c>/P</c> is unprotected — <c>/Perms</c> carries the copy sealed under the file key when the
    /// document was written, and where the two disagree that copy is the document's real permission
    /// set. Reported rather than refused: qpdf, poppler and pdfium all read such a file, so throwing
    /// would make this the only library that cannot open it, while taking the dictionary's word
    /// would hand the caller permissions someone else chose.
    /// </summary>
    [Fact]
    public void R6_permissionBitsEditedAfterEncryption_reportTheSealedValue()
    {
        // /P -4 grants everything; /P -8 clears the print bit. Same length, so every cross-reference
        // offset survives, and /Perms is untouched either way.
        var edited = PatchOnce(Load("enc-aes-256-r6.pdf"), "/P -4 ", "/P -8 ");

        using var reader = PdfReader.Open(edited, "u");

        Assert.Equal(-4 & (int)PdfPermissions.All, (int)reader.Encryption!.Permissions);
        Assert.True(reader.Encryption.Permissions.HasFlag(PdfPermissions.Print));
    }

    /// <summary>The control for the test above: untouched, the same fixture verifies and opens.</summary>
    [Fact]
    public void R6_untamperedPermissions_verify()
    {
        using var reader = PdfReader.Open(Load("enc-aes-256-r6.pdf"), "u");

        Assert.Equal(-4 & (int)PdfPermissions.All, (int)reader.Encryption!.Permissions);
    }

    /// <summary>
    /// The same rule at R5. Algorithm 13 and the <c>/Perms</c> entry arrived with R5, not R6 — ISO
    /// 32000-2 Table 21 lists <c>/Perms</c> for "R is 5 or 6" alike — and the recovery is gated on a
    /// revision comparison, which a whole suite written at R6 cannot tell from one revision higher.
    /// </summary>
    [Fact]
    public void R5_permissionBitsEditedAfterEncryption_reportTheSealedValue()
    {
        var edited = PatchOnce(Load("enc-aes-256-r5.pdf"), "/P -4 ", "/P -8 ");

        using var reader = PdfReader.Open(edited, "u");

        Assert.Equal(-4 & (int)PdfPermissions.All, (int)reader.Encryption!.Permissions);
        Assert.True(reader.Encryption.Permissions.HasFlag(PdfPermissions.Print));
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
            $"<< /Empty <> /AlsoEmpty () /IvOnly <{new string('0', 32)}> >>");

        using var reader = PdfReader.Open(doc, "u");
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));

        Assert.Empty(((PdfHexString)dict.Get(new PdfName("Empty"))!).Bytes.ToArray());
        Assert.Empty(((PdfLiteralString)dict.Get(new PdfName("AlsoEmpty"))!).Bytes.ToArray());

        // Sixteen bytes is an IV and nothing after it — what a producer writes when it emits the IV
        // unconditionally and has nothing to encrypt. It reaches the real CBC decryption with an
        // empty ciphertext, which returns empty; the length guard below it, which demands whole
        // blocks after the IV, is what this row shows does not reject 16.
        Assert.Empty(((PdfHexString)dict.Get(new PdfName("IvOnly"))!).Bytes.ToArray());
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
    [Theory]
    // The whole /CF dictionary indirect.
    [InlineData("/CF 5 0 R", "<< /StdCF << /CFM /AESV2 /Length 16 >> >>")]
    // One entry inside /CF indirect, which needs a second level of dereferencing.
    [InlineData("/CF << /StdCF 5 0 R >>", "<< /CFM /AESV2 /Length 16 >>")]
    // A third level: the /CFM VALUE indirect. DereferenceValues copies /CF and its per-filter
    // dictionaries and stops there, so this one is resolved by BuildCfTable's own callback and
    // nothing else in the chain would catch it. Without it the filter reads as having no /CFM at
    // all, which is Unsupported, which throws on the first stream of a valid file.
    [InlineData("/CF << /StdCF << /CFM 5 0 R /Length 16 >> >>", "/AESV2")]
    public void IndirectCryptFilterDictionary_isResolved(string cfEntry, string referencedObject)
    {
        var doc = BuildWithEncryptDict(
            $"<< /Filter /Standard /V 4 /R 4 /Length 128 {cfEntry} /StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            extraObjects: [referencedObject]);

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(PdfCipherAlgorithm.Aes128, reader.Encryption!.StreamCipher);
    }

    /// <summary>
    /// The PDFDocEncoding retry is gated on R≤4, because at R≥5 the password is UTF-8 either way
    /// (ISO 32000-2 §7.6.4.3.3). No fixture can pin the gate being CLOSED — qpdf writes UTF-8 at R6
    /// whatever the password, so a document cannot tell the two apart — which leaves the private
    /// candidate list itself as the only honest subject, the way the key-length rules are tested.
    /// </summary>
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    public void PasswordEncodingCandidates_offerThePdfDocEncodingRetryOnlyBelowRevision5(int r, int expectedCandidates)
    {
        var method = typeof(EncryptionSetup).GetMethod(
            "CandidatePasswordEncodings", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CandidatePasswordEncodings not found by reflection.");

        // Non-ASCII, so the two encodings genuinely differ — for an ASCII password they agree and
        // the count would say nothing about which encodings were offered.
        var candidates = ((IEnumerable<byte[]>)method.Invoke(null, ["pässwörd", r])!).ToList();

        Assert.Equal(expectedCandidates, candidates.Count);
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

        Assert.Equal(PdfCipherAlgorithm.Aes128, reader.Encryption!.StreamCipher);
        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption.StringCipher);
    }

    /// <summary>
    /// ISO 32000-1 §7.6.1 lets a document encrypt only its attachments: <c>/StmF</c> and
    /// <c>/StrF</c> Identity, with <c>/EFF</c> naming the crypt filter for embedded file streams.
    /// Acrobat writes exactly this for "encrypt only file attachments". Everything outside the
    /// attachments is in the clear, and the attachment itself is encrypted under <c>/EFF</c>'s
    /// filter — read with <c>/StmF</c>'s instead, its ciphertext comes back as the file.
    /// </summary>
    [Fact]
    public void AttachmentOnlyEncryption_readsThePlaintextOutsideAndDecryptsTheAttachment()
    {
        var probe = "NOT-ENCRYPTED-AT-ALL"u8.ToArray();
        var attachment = EncryptRc4(5, 0, "attachment payload"u8.ToArray());

        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /Identity /StrF /Identity /EFF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            $"<< /Probe <{Convert.ToHexStringLower(probe)}> >>",
            extraObjects:
            [
                $"<< /Type /EmbeddedFile /Length {attachment.Length} >>\n"
                + $"stream\n{Encoding.Latin1.GetString(attachment)}\nendstream",
            ]);

        using var reader = PdfReader.Open(doc, "u");

        // Outside the attachment: Identity, so the bytes are already plaintext in the file.
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
        Assert.Equal(PdfCipherAlgorithm.Identity, reader.Encryption!.StreamCipher);
        Assert.Equal(
            "NOT-ENCRYPTED-AT-ALL",
            Encoding.ASCII.GetString(((PdfHexString)dict.Get(new PdfName("Probe"))!).Bytes.Span));

        // The attachment: RC4 under /EFF's filter.
        var embedded = reader.ResolveStream(5)!;
        Assert.Equal("attachment payload", Encoding.ASCII.GetString(reader.GetDecodedStreamData(embedded)!));
    }

    /// <summary>
    /// Table 20 gives <c>/EFF</c> as the filter for embedded file streams "that do not have their
    /// own crypt filter specifier", and tells a writer to respect it "except for embedded file
    /// streams that have their own". Leaving one attachment in the clear inside an encrypted
    /// document is written exactly that way, so the stream's own <c>/Crypt</c> has to win: taking
    /// <c>/EFF</c> over it decrypts plaintext into noise under RC4, and throws under AES.
    /// </summary>
    [Fact]
    public void EmbeddedFileWithItsOwnCryptSpecifier_beatsEff()
    {
        var clear = "LEFT-IN-THE-CLEAR"u8.ToArray();

        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF /EFF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            "<< /Probe 1 >>",
            extraObjects:
            [
                $"<< /Type /EmbeddedFile /Length {clear.Length} /Filter [/Crypt] "
                + $"/DecodeParms [<< /Name /Identity >>] >>\n"
                + $"stream\n{Encoding.Latin1.GetString(clear)}\nendstream",
            ]);

        using var reader = PdfReader.Open(doc, "u");
        var embedded = reader.ResolveStream(5)!;

        Assert.Equal("LEFT-IN-THE-CLEAR", Encoding.ASCII.GetString(reader.GetDecodedStreamData(embedded)!));
    }

    /// <summary>
    /// <c>/EFF</c> is meaningful only at <c>/V</c> 4 and above (Table 20). A <c>/V</c> 2 document has
    /// no crypt filters at all, so an embedded file stream there is an ordinary RC4 stream.
    ///
    /// <para>
    /// This pins that outcome, not the gate that produces it: at <c>/V</c> 2 the embedded-file filter
    /// and the stream filter are both RC4, so the two paths cannot be told apart by any document.
    /// The gate is worth keeping for what it says rather than what it changes — a non-null filter on
    /// a document that never mentioned <c>/EFF</c> is a claim the encryption dictionary did not make.
    /// </para>
    /// </summary>
    [Fact]
    public void EmbeddedFileStream_inAV2Document_takesTheOrdinaryStreamFilter()
    {
        var attachment = EncryptRc4(5, 0, "V2-ATTACHMENT"u8.ToArray());

        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 2 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            "<< /Probe 1 >>",
            extraObjects:
            [
                $"<< /Type /EmbeddedFile /Length {attachment.Length} >>\n"
                + $"stream\n{Encoding.Latin1.GetString(attachment)}\nendstream",
            ]);

        using var reader = PdfReader.Open(doc, "u");
        var embedded = reader.ResolveStream(5)!;

        Assert.Equal("V2-ATTACHMENT", Encoding.ASCII.GetString(reader.GetDecodedStreamData(embedded)!));
    }

    /// <summary>
    /// The other direction: with no <c>/EFF</c>, an embedded file stream is an ordinary stream and
    /// takes <c>/StmF</c> like everything else. Reaching for a filter that was never named would
    /// decrypt it twice.
    /// </summary>
    [Fact]
    public void EmbeddedFileStream_withNoEff_takesTheDocumentWideStreamFilter()
    {
        var attachment = EncryptRc4(5, 0, "ordinary stream rules"u8.ToArray());

        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            "<< /Probe 1 >>",
            extraObjects:
            [
                $"<< /Type /EmbeddedFile /Length {attachment.Length} >>\n"
                + $"stream\n{Encoding.Latin1.GetString(attachment)}\nendstream",
            ]);

        using var reader = PdfReader.Open(doc, "u");
        var embedded = reader.ResolveStream(5)!;

        Assert.Equal("ordinary stream rules", Encoding.ASCII.GetString(reader.GetDecodedStreamData(embedded)!));
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

    /// <summary>
    /// Both <c>/Length</c> entries are optional — Table 20's and Table 25's alike — so a conformant
    /// <c>/V 4</c> document may carry neither, and its key length is whatever the cipher implies.
    /// Falling back to Table 20's 40-bit default rejects the correct password on a file every other
    /// reader opens, which is what reading only the top-level entry did.
    /// </summary>
    [Fact]
    public void V4_withNoLengthAnywhere_takesTheLengthTheCipherImplies()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /CF << /StdCF << /CFM /V2 >> >> /StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(128, reader.Encryption!.KeyLengthBits);
    }

    /// <summary>
    /// A document that encrypts its strings but not its streams names <c>/Identity</c> for
    /// <c>/StmF</c> — which is not a <c>/CF</c> entry to look up, so the length has to come from
    /// <c>/StrF</c>'s. Two entries with different lengths, because with one the same answer arrives
    /// through the single-entry fallback and through <c>/V</c> 4's own default, and the test would
    /// pass with the <c>/StrF</c> fallback removed. The top-level <c>/Length 40</c> is there for the
    /// same reason: without it the 128-bit answer also arrives through <c>/V</c> 4's default, and
    /// nothing here would depend on <c>/StrF</c> being consulted at all.
    /// </summary>
    [Fact]
    public void StmFIdentity_takesTheKeyLengthFromStrF()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 40 "
            + "/CF << /Unused << /CFM /V2 /Length 5 >> /StrCF << /CFM /V2 /Length 16 >> >> "
            + "/StmF /Identity /StrF /StrCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(128, reader.Encryption!.KeyLengthBits);
        Assert.Equal(PdfCipherAlgorithm.Identity, reader.Encryption.StreamCipher);
        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption.StringCipher);
    }

    /// <summary>
    /// ISO 32000-1 Table 20 forbids <c>/V</c> 0 and 3 alike, and they are not the same failure:
    /// 3 names an unpublished algorithm a clean-room implementation can never provide, while 0 names
    /// no algorithm at all. A caller holding the first has a file some other tool can read.
    /// </summary>
    [Fact]
    public void V3_isUnsupportedRatherThanMalformed()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 3 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        var ex = Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(doc, "u"));

        Assert.Contains("/V 3", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The other half: <c>/V</c> 0 has no algorithm to apply, so the file is malformed.</summary>
    [Fact]
    public void V0_isMalformed()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 0 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(doc, "u"));

        // The distinguishing phrase, not just "/V 0": the decryptor's own constructor rejects 0 too,
        // with a message that also names it, so a looser assertion passes with this guard deleted.
        Assert.Contains("shall not be used", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/StrF</c> naming a crypt filter the document DOES define, whose <c>/CFM</c> this library
    /// does not implement, is a valid document beyond our reach rather than a malformed one — the
    /// distinction <c>/V 3</c> already draws. A <c>/StrF</c> naming an entry that does not exist at
    /// all stays an <see cref="InvalidDataException"/>; its own test above covers that.
    /// </summary>
    [Fact]
    public void StrF_namingADefinedFilterWithAnUnimplementedMethod_isUnsupportedRatherThanMalformed()
    {
        var doc = BuildWithEncryptDict(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StdCF << /CFM /SomeFutureAlgorithm /Length 16 >> >> /StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        var ex = Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(doc, "u"));

        Assert.Contains("StdCF", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Algorithm 13 verifies the permission bits. Byte 8 of the decrypted <c>/Perms</c> block carries
    /// the <c>/EncryptMetadata</c> flag instead, and a producer that writes it inconsistently with the
    /// dictionary's own entry has a bookkeeping bug rather than tampered permissions — denying the
    /// whole document over it is a refusal qpdf does not make either, and this fixture is otherwise
    /// intact.
    /// </summary>
    [Fact]
    public void PermsWithAnInconsistentMetadataFlag_stillOpens()
    {
        var patched = WithPermsMetadataFlagFlipped(Load("enc-aes-256-r6.pdf"));

        using var reader = PdfReader.Open(patched, "u");

        Assert.Equal(-4 & (int)PdfPermissions.All, (int)reader.Encryption!.Permissions);
    }

    /// <summary>
    /// AES-128 has exactly one legal key size, so a crypt filter declaring anything else is the
    /// document contradicting itself — and the cipher wins, because it is what will actually be
    /// applied. Rejecting the correct password over a stray <c>/Length</c> would refuse a file every
    /// other reader opens. Only RC4 has a range to declare (Table 20: 40 to 128 bits).
    /// </summary>
    [Theory]
    [InlineData("/AESV2", 5)]
    [InlineData("/AESV2", 10)]
    [InlineData("/AESV2", 32)]
    public void CryptFilterLengthTheCipherCannotUse_isIgnoredInFavourOfTheCipher(string cfm, int declaredLength)
    {
        // /Length 40 at the top level so the clamp is the only thing that can produce a 16-byte key:
        // with no top-level entry the /V 4 fallback answers 128 bits too, and the document opens
        // whether or not the cipher ever overrode the declaration.
        var doc = BuildWithEncryptDict(
            $"<< /Filter /Standard /V 4 /R 4 /Length 40 /CF << /StdCF << /CFM {cfm} /Length {declaredLength} >> >> "
            + "/StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>");

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(128, reader.Encryption!.KeyLengthBits);
    }

    /// <summary>
    /// Zeroing the file encryption key on <c>Dispose</c> makes a disposed reader dangerous rather
    /// than merely useless: RC4 against an all-zero key returns garbage and reports nothing, and a
    /// Flate stream surfaces as an inflate failure blamed on the file. The caller gets the disposal
    /// error the situation actually is.
    /// </summary>
    [Fact]
    public void ReaderUsedAfterDispose_throwsObjectDisposed_ratherThanReturningGarbage()
    {
        var reader = PdfReader.Open(Load("enc-rc4-128.pdf"), "u");
        var infoRef = (PdfIndirectReference)reader.Trailer.Get(new PdfName("Info"))!;

        // Sound before disposal, so the assertion below is about disposal and not about the fixture.
        Assert.NotNull(reader.Resolve(infoRef));

        // A stream resolved before disposal, so the decode path below is reached with a live
        // ParsedStream rather than turned away by the resolver's own guard.
        var contentRef = (PdfIndirectReference)((PdfDictionary)reader.Resolve(
            (PdfIndirectReference)((PdfArray)((PdfDictionary)reader.Resolve(
                (PdfIndirectReference)reader.Catalog.Get(new PdfName("Pages"))!)!)
                    .Get(new PdfName("Kids"))!)[0])!).Get(PdfName.Contents)!;
        var cached = reader.ResolveStream(contentRef)!;

        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Resolve(infoRef));
        Assert.Throws<ObjectDisposedException>(() => reader.ResolveStream(infoRef));

        // The decode path too: it holds the key just as the resolvers do, and a ParsedStream the
        // caller already has in hand reaches it without passing either of them.
        Assert.Throws<ObjectDisposedException>(() => reader.GetDecodedStreamData(cached));

        // And the key itself is gone, which is the point of disposing at all.
        var fileKey = (byte[])typeof(PdfDocumentReader)
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        Assert.All(fileKey, b => Assert.Equal(0, b));

        // Disposing twice is not an error.
        reader.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    // RC4 is symmetric, so the reader's own decrypt path doubles as the encryptor these documents
    // need. Safe here because these tests are about which FILTER is chosen, not about the key
    // derivation: a derivation error would cancel on both sides, but a filter error cannot, since
    // the encryptor always uses RC4 while the reader picks its filter from the dictionary.
    private static byte[] EncryptRc4(int objectNumber, int generation, byte[] plaintext)
    {
        using var reader = PdfReader.Open(Load("enc-rc4-128.pdf"), "u");
        var type = typeof(PdfDocumentReader);
        var decryptor = (StandardSecurityDecryptor)type
            .GetField("_decryptor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        var fileKey = (byte[])type
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return decryptor.DecryptString(fileKey, objectNumber, generation, plaintext);
    }

    // Decrypts /Perms with the document's own file key, flips byte 8 (the /EncryptMetadata flag),
    // re-encrypts and patches it back — same length, so every cross-reference offset survives.
    private static byte[] WithPermsMetadataFlagFlipped(byte[] bytes)
    {
        byte[] fileKey;
        using (var reader = PdfReader.Open(bytes, "u"))
        {
            // Copied, not aliased: Dispose zeroes the key on the way out of this block.
            fileKey = [.. (byte[])typeof(PdfDocumentReader)
                .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!];
        }

        var text = Encoding.Latin1.GetString(bytes);
        var at = text.IndexOf("/Perms <", StringComparison.Ordinal) + "/Perms <".Length;
        var end = text.IndexOf('>', at);
        var perms = Convert.FromHexString(text[at..end]);

        using var aes = Aes.Create();
        aes.Key = fileKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var block = aes.CreateDecryptor().TransformFinalBlock(perms, 0, perms.Length);
        block[8] = block[8] == (byte)'T' ? (byte)'F' : (byte)'T';
        var reencrypted = aes.CreateEncryptor().TransformFinalBlock(block, 0, block.Length);

        var result = text[..at] + Convert.ToHexString(reencrypted).ToLowerInvariant() + text[end..];
        Assert.Equal(bytes.Length, result.Length);
        return Encoding.Latin1.GetBytes(result);
    }

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
