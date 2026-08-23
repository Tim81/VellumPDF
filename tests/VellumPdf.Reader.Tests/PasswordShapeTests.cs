// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Password shapes the rest of the corpus cannot express. Every other fixture's password is the one
/// ASCII character <c>u</c>, which makes several rules in Algorithm 2 unobservable: truncation never
/// bites, the two candidate encodings agree byte for byte, and no password ever satisfies the owner
/// and user checks at once. Each fixture here removes one of those coincidences.
/// </summary>
public sealed class PasswordShapeTests
{
    private const string LongPassword = "0123456789abcdefghijklmnopqrstuvwxyzABCD";

    // ── Algorithm 2 step (a): pad or truncate to exactly 32 bytes ───────────────────────────────

    /// <summary>
    /// The truncation point is a value, not a range, and only a password longer than 32 bytes can
    /// fix it: the first 32 characters have to open the document and the first 31 have to fail. A
    /// reader truncating at 31 — or not truncating at all — satisfies exactly one of those.
    /// </summary>
    [Theory]
    [InlineData(40, true)]   // the whole password
    [InlineData(32, true)]   // everything Algorithm 2 keeps
    [InlineData(31, false)]  // one character short of it
    public void PasswordLongerThan32Bytes_isTruncatedToExactly32(int prefixLength, bool expectedToOpen)
    {
        var bytes = Load("enc-aes-128-longpassword.pdf");
        var password = LongPassword[..prefixLength];

        if (expectedToOpen)
        {
            using var reader = PdfReader.Open(bytes, password);
            Assert.Equal("GoldenStandardFont", GetInfoTitle(reader));
        }
        else
        {
            Assert.Throws<PdfPasswordException>(() => PdfReader.Open(bytes, password));
        }
    }

    // ── Owner-first trial order ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>EncryptionSetup.TryAuthenticate</c> tries the supplied password as the OWNER password
    /// first, and its stated reason is this document: when one password satisfies both checks, the
    /// higher-privilege answer is the more informative one. With distinct passwords — every other
    /// fixture — the order is unobservable, so nothing enforced the choice.
    /// </summary>
    [Fact]
    public void PasswordThatIsBothOwnerAndUser_isReportedAsOwnerAccess()
    {
        using var reader = PdfReader.Open(Load("enc-aes-128-samepassword.pdf"), "same");

        Assert.True(reader.Encryption!.IsOwnerAccess);
        Assert.Equal("GoldenStandardFont", GetInfoTitle(reader));
    }

    // ── PDFDocEncoding retry (R≤4 only) ─────────────────────────────────────────────────────────

    /// <summary>
    /// qpdf derives <c>/U</c> for an R≤4 document from PDFDocEncoding bytes, so a document with a
    /// non-ASCII password does not open on the UTF-8 attempt: it needs the second candidate
    /// encoding. Nothing else in the corpus can show that, because for pure ASCII the two encodings
    /// produce identical bytes — the gap
    /// <c>EncryptionSetup.CandidatePasswordEncodings</c> names in its own comment.
    /// </summary>
    [Fact]
    public void NonAsciiPassword_authenticatesThroughThePdfDocEncodingRetry()
    {
        const string Password = "pässwörd";

        // The premise, stated as an assertion rather than assumed: the two candidate encodings
        // really do differ for this password, so opening the document means the retry ran.
        Assert.True(PdfDocEncoding.TryEncode(Password, out var docEncoded));
        Assert.NotEqual(Encoding.UTF8.GetBytes(Password), docEncoded);

        using var reader = PdfReader.Open(Load("enc-aes-128-pdfdocpassword.pdf"), Password);

        Assert.Equal(4, reader.Encryption!.R);
        Assert.Equal("GoldenStandardFont", GetInfoTitle(reader));
    }

    // ── Incremental updates over an encrypted document ──────────────────────────────────────────

    /// <summary>
    /// The shape any encrypted document acquires as soon as it is annotated, form-filled or signed,
    /// and the only fixture where revision chaining and decryption meet. The update's own trailer
    /// repeats <c>/Encrypt</c>, both revisions' objects have to decrypt under the same file key, and
    /// the object the update added has to be reachable.
    /// </summary>
    [Fact]
    public void EncryptedDocumentWithAnIncrementalUpdate_decryptsBothRevisions()
    {
        using var reader = PdfReader.Open(Load("enc-aes-128-tworevisions.pdf"), "");

        Assert.Equal(2, reader.Revisions.Count);
        Assert.NotNull(reader.Encryption);

        // From the base revision: the page content decrypts to the baseline's bytes.
        Assert.Equal(BaselinePageContent(), GetPageContent(reader));

        // From the update: the embedded file poppler appended, whose stream is encrypted under the
        // same file key as everything in the revision below it.
        var names = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(new PdfName("Names"))!));
        var embeddedFiles = Assert.IsType<PdfDictionary>(reader.ResolveValue(names.Get(new PdfName("EmbeddedFiles"))!));
        Assert.NotNull(embeddedFiles.Get(new PdfName("Names")));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] BaselinePageContent()
    {
        using var reader = PdfReader.Open(Load("plaintext-baseline.pdf"));
        return GetPageContent(reader);
    }

    private static byte[] GetPageContent(PdfDocumentReader reader)
    {
        var pages = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(new PdfName("Pages"))!));
        var kids = Assert.IsType<PdfArray>(reader.ResolveValue(pages.Get(new PdfName("Kids"))!));
        var page = Assert.IsType<PdfDictionary>(reader.ResolveValue(kids[0]));
        var contents = Assert.IsType<PdfIndirectReference>(page.Get(PdfName.Contents));
        return reader.GetDecodedStreamData(reader.ResolveStream(contents)!)!;
    }

    private static string GetInfoTitle(PdfDocumentReader reader)
    {
        var info = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Trailer.Get(new PdfName("Info"))!));
        var bytes = info.Get(new PdfName("Title")) switch
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
