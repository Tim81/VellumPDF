// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Guards the committed encrypted corpus (#99) itself, before anything tries to decrypt it.
/// qpdf refuses to write RC4 without <c>--allow-weak-crypto</c> and still leaves a zero-byte file
/// behind, so a corpus can look complete on disk while several of its entries are empty.
///
/// The digest is what actually pins each fixture: it subsumes non-emptiness, truncation, and — the
/// case a <c>/V</c>+<c>/R</c> check cannot see — two fixtures being swapped for each other.
/// <c>enc-aes-128</c> and <c>enc-rc4-128-v4</c> are both <c>/V 4 /R 4</c> and differ only in
/// <c>/CFM</c>, which is exactly the AES-versus-RC4 confusion the decrypt work most needs pinned.
/// </summary>
public sealed class EncryptedFixtureCorpusTests
{
    // file, /V, /R, /CFM (null where V < 4 predates crypt filters), SHA-256
    private static readonly (string Name, int V, int R, string? Cfm, string Sha256)[] Corpus =
    [
        ("enc-rc4-40.pdf", 1, 2, null, "c913f7ee3ee41200bc2b166f3f6e472c61825085b85ff081d5a00b2670669705"),
        ("enc-rc4-128.pdf", 2, 3, null, "e303cd643459fff8455a8812104c3f55ce71d922c0ab8e109f9eb703daba0386"),
        ("enc-rc4-128-v4.pdf", 4, 4, "/V2", "9be805936ef595bafb8396272a546acc5d183579449cef640adbde8932e5d413"),
        ("enc-aes-128.pdf", 4, 4, "/AESV2", "c525e277fdbfb1d332eda71df6f9894c80d1a11b34512967a44df1d81fd14f9a"),
        ("enc-aes-256-r5.pdf", 5, 5, "/AESV3", "ce74060cbc4056fd125a2a40efcdade2f684f38284e1aa6b5d694299d6c56df8"),
        ("enc-aes-256-r6.pdf", 5, 6, "/AESV3", "af3ed586e3246d51523f6b546d9c9fb3e896d5968e283c4305b1dba2b7f361d6"),
        ("enc-aes-128-cleartextmd.pdf", 4, 4, "/AESV2", "df43e52507998c60fde7631a1694b4731ac0adcaede69715a63da526a9ab5750"),
        ("enc-256-cleartextmd.pdf", 5, 6, "/AESV3", "4ed43c7731177823ce3dd6a6dc072f9a1029cbfd1126b0f2c474cfc7988f326f"),
        // The one fixture in this corpus with object streams AND a cross-reference stream — every
        // other row uses a classic xref table over uncompressed objects (see the fixture README's
        // "Known gaps"). Needed so #97's decrypt-side tests can pin two things nothing else here
        // can: that a cross-reference stream is never decrypted, and that an object stream's
        // container is decrypted exactly ONCE and its compressed members are not separately
        // re-decrypted (ISO 32000-2 §7.5.7) — RC4, not AES, because RC4 double-decryption is
        // SILENT (returns the original ciphertext, no exception), so only RC4 actually exercises
        // that guard; an AES fixture would throw either way and prove nothing about it.
        ("enc-rc4-objstm.pdf", 4, 4, "/V2", "c349678e875f0aeba5593c034a5ff8e4e2db4e1d464ee6ca0537cfb9cb30c9c9"),
        // Empty user password ("" not "u") — the shape most real-world encrypted PDFs actually use
        // (permissions restricted via the owner password only). PdfReader.Open(bytes) with no
        // password argument has to authenticate against this one; #97's whole point depends on it.
        ("enc-aes-128-emptyuser.pdf", 4, 4, "/AESV2", "43e958654cad7611373c241db3e257932e82979950ba0e09f38c2ef26f6a6b98"),
        // Not built from plaintext-baseline.pdf's own object graph — /P -4 still holds (see the
        // theory's own assertion below), but the byte-identity assertions the other rows get from
        // PlaintextBaseline_isPresent_andNotEncrypted's docs don't apply to this one; see
        // EncryptedReaderTests for what it actually pins (Algorithm 1 step (a): a string nested two
        // levels inside a dictionary decrypts under its CONTAINING indirect object's identity, not
        // the string's own position or a hardcoded generation).
        ("enc-aes-128-nestedstrings.pdf", 4, 4, "/AESV2", "5a90b0b7e06324dd80218426bfe3b766fae3372b291529d77f56ac19c386de5c"),
        // A 40-character user password. Algorithm 2 step (a) truncates to 32 bytes, and no other
        // fixture's password is long enough to notice: this one opens under its first 32 characters
        // and refuses the first 31, which is what fixes the truncation point.
        ("enc-aes-128-longpassword.pdf", 4, 4, "/AESV2", "42b78b95f492295d2eb4df64c5429890571933a9d5f1ef0c56c825d873db4ae0"),
        // One password serving as BOTH owner and user. Every other row has distinct ones, so
        // nothing else can pin the documented owner-first trial order — the whole argument for it
        // is what to report when a single password satisfies both checks.
        ("enc-aes-128-samepassword.pdf", 4, 4, "/AESV2", "d290ad661f592fff6f213377b92c518deefea28b027f6cae8867cf62f8e98e75"),
        // User password "pässwörd", whose /U qpdf derived from PDFDocEncoding bytes rather than
        // UTF-8. The only fixture whose password is not pure ASCII, and therefore the only one that
        // can tell the two encodings apart: it does not open on the UTF-8 attempt alone.
        ("enc-aes-128-pdfdocpassword.pdf", 4, 4, "/AESV2", "ca8277c5c924bc27c3973957bff1e354fdb5f5261aef84d1c9e580826f57463f"),
        // Two revisions: an empty-user-password document with an incremental update appended over
        // it. Every other row is single-revision, so this is the only one where /Prev chaining and
        // decryption meet — the shape any encrypted document acquires the moment it is annotated,
        // form-filled or signed.
        ("enc-aes-128-tworevisions.pdf", 4, 4, "/AESV2", "c3161391a66b4cd987e16325db368d8379ab2ba29e85f4888b9ed0c4df488c20"),
    ];

    private const string BaselineName = "plaintext-baseline.pdf";

    public static TheoryData<string, int, int, string?, string> Fixtures
    {
        get
        {
            var data = new TheoryData<string, int, int, string?, string>();
            foreach (var (name, v, r, cfm, sha) in Corpus) data.Add(name, v, r, cfm, sha);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_isExactlyTheFileItClaimsToBe(string name, int v, int r, string? cfm, string sha256)
    {
        var bytes = Load(name);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var text = Encoding.Latin1.GetString(bytes);
        Assert.True(ContainsToken(text, "/Encrypt"), "no /Encrypt key found");
        Assert.True(ContainsToken(text, $"/V {v}"), $"expected /V {v}");
        Assert.True(ContainsToken(text, $"/R {r}"), $"expected /R {r}");
        if (cfm is not null)
            Assert.True(ContainsToken(text, $"/CFM {cfm}"), $"expected /CFM {cfm}");

        // Every fixture grants all permissions. Pinned because the permission bits feed
        // Algorithm 2's key derivation, so a regenerated fixture that quietly narrowed them
        // would change what the decrypt tests exercise while satisfying everything above.
        // Anchored for the same reason /Encrypt is: unanchored, "/P -4" is a prefix of the
        // "/P -44" that qpdf --modify=form emits, so the narrowing would pass unnoticed.
        Assert.True(ContainsToken(text, "/P -4"), "expected /P -4 (all permissions granted)");
    }

    [Fact]
    public void PlaintextBaseline_isPresent_andNotEncrypted()
    {
        var bytes = Load(BaselineName);
        Assert.Equal("886057c285e1f65d0ef39f43bae4367b1122f56295dbb5436c05108b1d3035ad",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.False(ContainsToken(Encoding.Latin1.GetString(bytes), "/Encrypt"), "baseline must not be encrypted");
    }

    /// <summary>
    /// The csproj embeds <c>Fixtures/Encrypted/*.pdf</c> by glob, so a fixture added to the folder
    /// would otherwise ship untested — the theory above is driven by a hand-written list. Fail loudly
    /// instead, naming what to add.
    /// </summary>
    [Fact]
    public void EveryEmbeddedFixture_isCoveredByTheTheory()
    {
        // Excludes only the "ThirdParty/" names (#196), covered by ThirdPartyFixtureCorpusTests's
        // own theory instead. A general slash test would exclude that corpus too, but it would just
        // as happily exclude a future one: a third corpus folder with its own folder-qualified
        // LogicalName -- say "Fixtures/Extra/*.pdf" with <LogicalName>Extra/%(Filename)%(Extension)
        // -- would satisfy "contains a slash" and slip past this guard fully uncovered. Naming the
        // one known prefix means a new prefix isn't recognized, fails this check loudly, and has to
        // earn its own guard rather than being swallowed by resembling a pattern already excluded.
        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.StartsWith("ThirdParty/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var covered = Corpus.Select(f => f.Name)
            .Append(BaselineName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(covered.OrderBy(n => n, StringComparer.Ordinal),
            embedded.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>--cleartext-metadata</c> must leave the XMP packet readable in the raw bytes; the R6 fixture
    /// built without it must not. The read-side mirror of the writer defect in #182.
    /// </summary>
    [Fact]
    public void CleartextMetadataFixture_exposesXmp_whereTheR6FixtureDoesNot()
    {
        Assert.Contains("xpacket", Encoding.Latin1.GetString(Load("enc-256-cleartextmd.pdf")), StringComparison.Ordinal);
        Assert.DoesNotContain("xpacket", Encoding.Latin1.GetString(Load("enc-aes-256-r6.pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public void CleartextMetadataFixture_atR4_declaresTheFlag_andExposesXmp()
    {
        // The R4 pair is the one that exercises ISO 32000-1 Algorithm 2 step (f): with
        // /EncryptMetadata false, four bytes of 0xFFFFFFFF are passed to the MD5 hash on top of the
        // step (a) padding, which is unchanged. That shifts the file encryption key, so /U differs.
        // /O is identical for a separate reason: Algorithm 3 derives it from the passwords, /R and
        // the key length alone and never sees the file encryption key. The R6 pair cannot show any
        // of this — R6 derives its key differently, and both its /O and /U differ.
        var cleartext = Encoding.Latin1.GetString(Load("enc-aes-128-cleartextmd.pdf"));
        var plainR4 = Encoding.Latin1.GetString(Load("enc-aes-128.pdf"));
        Assert.True(ContainsToken(cleartext, "/EncryptMetadata false"), "expected /EncryptMetadata false");
        // Pin the shared /O, so the property the comment rests on is guarded rather than asserted in prose.
        Assert.Equal(OwnerKey(plainR4), OwnerKey(cleartext));
        Assert.NotEqual(UserKey(plainR4), UserKey(cleartext));
        Assert.Contains("xpacket", cleartext, StringComparison.Ordinal);
        Assert.DoesNotContain("xpacket", Encoding.Latin1.GetString(Load("enc-aes-128.pdf")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The hex string of <c>/O</c> or <c>/U</c>, read from the standard security handler's own
    /// dictionary rather than from anywhere in the file: the surrounding bytes are ciphertext, and
    /// a bare scan for "/O &lt;" could land in one. /Filter /Standard bounds the search below and the
    /// dictionary's own &gt;&gt; bounds it above, so a fixture that omitted the entry fails loudly here
    /// rather than matching something later in the file. The upper bound relies on qpdf writing keys
    /// sorted, which puts /CF and its nested &gt;&gt; ahead of /Filter; verified for every fixture. Not a parser — both entries are
    /// fixed-width hex in every committed fixture.
    /// </summary>
    private static string HexEntry(string text, string key)
    {
        var dict = text.IndexOf("/Filter /Standard", StringComparison.Ordinal);
        Assert.True(dict >= 0, "no /Filter /Standard encryption dictionary found");
        var dictEnd = text.IndexOf(">>", dict, StringComparison.Ordinal);
        Assert.True(dictEnd > dict, "unterminated encryption dictionary");
        var i = text.IndexOf(key + " <", dict, StringComparison.Ordinal);
        Assert.True(i >= 0 && i < dictEnd, $"no {key} entry in the encryption dictionary");
        var start = i + key.Length + 2;
        var end = text.IndexOf('>', start);
        Assert.True(end > start, $"unterminated {key} entry");
        return text[start..end];
    }

    private static string OwnerKey(string text) => HexEntry(text, "/O");

    private static string UserKey(string text) => HexEntry(text, "/U");

    /// <summary>
    /// True when <paramref name="token"/> is followed by whitespace or a delimiter rather than by a
    /// regular character. That anchors the token's tail only: the caller supplies any interior
    /// separator literally, so "/P -4" matches just the single space qpdf writes, not the arbitrary
    /// whitespace or comment ISO 32000-2 §7.2.3 also permits there. Every committed fixture uses the
    /// single-space form, and the failure direction is a loud assertion, never a silent pass.
    /// Matching on a literal trailing space instead would let an encrypted file read as plaintext:
    /// ISO 32000-2 §7.2.3 allows any of six whitespace bytes after the key, and a dictionary or name
    /// value needs none at all. Leaving it unanchored would let "/P -44" satisfy "/P -4".
    /// </summary>
    private static bool ContainsToken(string text, string token)
    {
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + 1, StringComparison.Ordinal))
        {
            var after = i + token.Length;
            if (after >= text.Length) return true;
            var c = text[after];
            // Whitespace (ISO 32000-2 Table 1) or a delimiter (Table 2) ends the name. Numeric
            // codes rather than escapes: NUL, TAB, LF, FF, CR, SPACE.
            if (c is (char)0 or (char)9 or (char)10 or (char)12 or (char)13 or (char)32
                or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%')
                return true;
        }
        return false;
    }

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
