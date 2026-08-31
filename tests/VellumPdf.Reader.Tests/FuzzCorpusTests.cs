// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Guards <c>Fixtures/Fuzz</c> (#99) the same way <see cref="EncryptedFixtureCorpusTests"/> and
/// <see cref="ThirdPartyFixtureCorpusTests"/> guard their own corpora: every embedded fixture must
/// have a matching row here, SHA-256-pinned, so a regeneration cannot silently drop the property
/// the fixture was captured to pin. See <c>Fixtures/Fuzz/README.md</c> for the capture rule this
/// corpus exists to make enforceable — a finding is not "fixed" until its minimized input lands
/// here.
///
/// Empty today. That is not evidence <see cref="ParserFuzzTests"/> has found nothing on any given
/// run — only that no finding has yet been minimized, fixed, and captured. Add a row (and its
/// fixture) the day that changes.
/// </summary>
public sealed class FuzzCorpusTests
{
    // file, SHA-256, tokens that must be present, tokens that must be absent.
    private static readonly (string Name, string Sha256, string[] MustContain, string[] MustNotContain)[]
        Corpus = [];

    // A plain Fact looping the corpus, not a [Theory]/[MemberData] pair: xUnit v3 fails a theory
    // outright when its data source yields zero cases ("No data found"), which is exactly what an
    // empty Corpus produces before the first finding is captured. That failure mode exists to catch
    // a MemberData source that broke by accident — it would be actively wrong here, where empty is
    // the expected starting state.
    [Fact]
    public void Fixture_isExactlyTheFileItClaimsToBe()
    {
        foreach (var (name, sha256, mustContain, mustNotContain) in Corpus)
        {
            var bytes = Load(name);
            Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

            var text = Encoding.Latin1.GetString(bytes);
            foreach (var token in mustContain)
                Assert.Contains(token, text, StringComparison.Ordinal);
            foreach (var token in mustNotContain)
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The csproj embeds <c>Fixtures/Fuzz/*.pdf</c> under folder-qualified logical names, so a
    /// fixture dropped into the folder without a matching row here would ship untested — the exact
    /// gap the capture rule exists to close. Fails loudly and names what to add.
    /// </summary>
    [Fact]
    public void EveryEmbeddedFixture_isCoveredByTheTheory()
    {
        const string Prefix = "Fuzz/";
        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)
                        && n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[Prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        var covered = Corpus.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            covered.OrderBy(n => n, StringComparer.Ordinal),
            embedded.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static byte[] Load(string name)
    {
        const string Prefix = "Fuzz/";
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
