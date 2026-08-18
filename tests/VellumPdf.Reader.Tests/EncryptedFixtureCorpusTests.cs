// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Guards the committed encrypted corpus (#99) itself, before anything tries to decrypt it.
/// qpdf refuses to write RC4 without <c>--allow-weak-crypto</c> and still leaves a zero-byte file
/// behind, so a corpus can look complete on disk while three of its seven entries are empty.
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
        Assert.True(ContainsKey(text, "/Encrypt"), "no /Encrypt key found");
        Assert.Contains($"/V {v}", text, StringComparison.Ordinal);
        Assert.Contains($"/R {r}", text, StringComparison.Ordinal);
        if (cfm is not null)
            Assert.Contains($"/CFM {cfm}", text, StringComparison.Ordinal);

        // Every fixture grants all permissions. Pinned because the permission bits feed
        // Algorithm 2's key derivation, so a regenerated fixture that quietly narrowed them
        // would change what the decrypt tests exercise while satisfying everything above.
        Assert.Contains("/P -4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextBaseline_isPresent_andNotEncrypted()
    {
        var bytes = Load(BaselineName);
        Assert.Equal("886057c285e1f65d0ef39f43bae4367b1122f56295dbb5436c05108b1d3035ad",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.False(ContainsKey(Encoding.Latin1.GetString(bytes), "/Encrypt"), "baseline must not be encrypted");
    }

    /// <summary>
    /// The csproj embeds <c>Fixtures/Encrypted/*.pdf</c> by glob, so a fixture added to the folder
    /// would otherwise ship untested — the theory above is driven by a hand-written list. Fail loudly
    /// instead, naming what to add.
    /// </summary>
    [Fact]
    public void EveryEmbeddedFixture_isCoveredByTheTheory()
    {
        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
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


    /// <summary>
    /// True when <paramref name="key"/> appears as a whole PDF name — followed by whitespace or a
    /// delimiter rather than by a name character. Matching on a literal trailing space instead would
    /// let an encrypted file read as plaintext: ISO 32000-2 §7.2.3 allows any of six whitespace bytes
    /// after the key, and a dictionary or name value needs none at all.
    /// </summary>
    private static bool ContainsKey(string text, string key)
    {
        for (var i = text.IndexOf(key, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(key, i + 1, StringComparison.Ordinal))
        {
            var after = i + key.Length;
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
