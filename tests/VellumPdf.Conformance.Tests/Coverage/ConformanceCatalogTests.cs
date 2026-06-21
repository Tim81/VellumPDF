// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text.RegularExpressions;
using VellumPdf.Conformance.Coverage;
using Xunit;

namespace VellumPdf.Conformance.Tests.Coverage;

public sealed class ConformanceCatalogTests
{
    private static readonly PdfConformance[] Profiles =
        [PdfConformance.PdfA2B, PdfConformance.PdfA2U, PdfConformance.PdfA2A, PdfConformance.PdfUA1];

    [Fact]
    public void Catalog_IsWellFormedAndComplete()
    {
        var all = ConformanceCatalog.All;

        // Every check has a distinct, well-formed test id (clause-testNumber) and at least one profile.
        Assert.Equal(all.Count, all.Select(c => c.TestId).Distinct().Count());
        foreach (var c in all)
        {
            Assert.Matches(@"^[0-9][0-9.]*-[0-9]+$", c.TestId);
            Assert.Equal(c.TestId[..c.TestId.LastIndexOf('-')], c.Clause);
            Assert.NotEmpty(c.Profiles);
            // A non-implemented check should record why (its gap or the subsystem it needs).
            if (c.Status != CoverageStatus.Implemented)
                Assert.False(string.IsNullOrWhiteSpace(c.Note), $"{c.TestId} ({c.Status}) has no note.");
        }

        // The catalogued totals match each profile's known rule count, and the status buckets partition it.
        Assert.Equal(144, ConformanceCatalog.Coverage(PdfConformance.PdfA2B).Total);
        Assert.Equal(146, ConformanceCatalog.Coverage(PdfConformance.PdfA2U).Total);
        Assert.Equal(153, ConformanceCatalog.Coverage(PdfConformance.PdfA2A).Total);
        Assert.Equal(106, ConformanceCatalog.Coverage(PdfConformance.PdfUA1).Total);
        foreach (var p in Profiles)
        {
            var s = ConformanceCatalog.Coverage(p);
            Assert.Equal(s.Total, s.Implemented + s.Partial + s.Deferred);
        }
    }

    /// <summary>
    /// Cross-checks the catalogued id inventory against the authoritative veraPDF profile (read from
    /// the bundled CLI jar) so that a new or retired veraPDF rule cannot drift out of the catalog
    /// unnoticed. Gated like the other oracle tests: skipped when veraPDF is not installed, unless
    /// REQUIRE_VERAPDF=1 forces it.
    /// </summary>
    [Theory]
    [InlineData("PDFA-2B.xml", PdfConformance.PdfA2B)]
    [InlineData("PDFA-2U.xml", PdfConformance.PdfA2U)]
    [InlineData("PDFA-2A.xml", PdfConformance.PdfA2A)]
    [InlineData("PDFUA-1.xml", PdfConformance.PdfUA1)]
    public void Catalog_MatchesVeraPdfProfile(string profileFile, PdfConformance profile)
    {
        // This diff reads the profile straight from the veraPDF CLI jar, so it needs the jar on the
        // host filesystem. CI backs veraPDF with a Docker-image shim (the jar lives inside the
        // container, not on the host), so the diff is skipped there — REQUIRE_VERAPDF does NOT force
        // it, because that variable gates the CLI-based oracle, which still runs in CI. The always-on
        // Catalog_IsWellFormedAndComplete test guards the catalog totals everywhere; this diff adds the
        // version-drift check wherever the jar is directly readable (local dev).
        var jar = FindCliJar();
        if (jar is null)
        {
            Assert.Skip("veraPDF CLI jar not directly accessible (e.g. Docker-backed CI); catalog diff skipped.");
            return;
        }

        var profileIds = ReadProfileIds(jar, profileFile);
        var catalogIds = ConformanceCatalog.For(profile).Select(c => c.TestId).ToHashSet();

        var missing = profileIds.Except(catalogIds).Order().ToList();
        var phantom = catalogIds.Except(profileIds).Order().ToList();

        Assert.True(missing.Count == 0, $"{profile}: catalog is missing veraPDF rules: {string.Join(", ", missing)}");
        Assert.True(phantom.Count == 0, $"{profile}: catalog has ids absent from veraPDF: {string.Join(", ", phantom)}");
    }

    private static HashSet<string> ReadProfileIds(string jarPath, string profileFile)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        var entry = zip.Entries.First(e => e.FullName.EndsWith("/validation/" + profileFile, StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xml, @"clause=""([^""]+)"" testNumber=""([0-9]+)"""))
            ids.Add($"{m.Groups[1].Value}-{m.Groups[2].Value}");
        return ids;
    }

    // Locates the veraPDF CLI jar (which carries the validation profiles) from VERAPDF_HOME, the
    // `verapdf` launcher on PATH, or ~/verapdf — the same install the CLI oracle tests use.
    private static string? FindCliJar()
    {
        foreach (var home in CandidateHomes())
        {
            var bin = Path.Combine(home, "bin");
            if (!Directory.Exists(bin))
                continue;
            var jar = Directory.EnumerateFiles(bin, "*.jar")
                .FirstOrDefault(f => Path.GetFileName(f).StartsWith("cli", StringComparison.OrdinalIgnoreCase));
            if (jar is not null)
                return jar;
        }
        return null;
    }

    private static IEnumerable<string> CandidateHomes()
    {
        if (Environment.GetEnvironmentVariable("VERAPDF_HOME") is { Length: > 0 } env)
            yield return env;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "verapdf")))
                yield return dir; // the launcher sits at <home>/verapdf, so <dir> is the home

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userHome.Length > 0)
            yield return Path.Combine(userHome, "verapdf");
    }
}
