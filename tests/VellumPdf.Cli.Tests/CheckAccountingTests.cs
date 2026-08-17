// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using VellumPdf.Cli;
using VellumPdf.Conformance;
using VellumPdf.Conformance.Coverage;
using VellumPdf.Conformance.Tests.Oracle;

namespace VellumPdf.Cli.Tests;

/// <summary>
/// The report must account for every catalogued check exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The report withdrew a passing claim from every catalogued check sharing a failing assertion's
/// clause number, and then listed those checks nowhere: not failed, not passed, not un-evaluated.
/// Its <c>total</c> was <c>failed + passed + notEvaluated</c>, which added a count of assertions to
/// a count of checks and so happened to look plausible while being two different things added
/// together. Across the oracle corpus the reported total came out as much as 40 short of the
/// profile's catalog.
/// </para>
/// <para>
/// Attribution is now per-check where the rule id allows it. Some rules carry a veraPDF-style test
/// id (<c>6.1.13-10</c>) that names one catalogued check; most carry a descriptive id
/// (<c>6.2.5-extgstate</c>) that pins only the clause. The second kind cannot be attributed, so the
/// checks in its clause go to a named <c>inconclusive</c> bucket instead of vanishing.
/// </para>
/// </remarks>
public sealed class CheckAccountingTests
{
    public static TheoryData<string> CorpusFixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var f in OracleCorpus.All)
            data.Add(f.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFixtureNames))]
    public void EveryCatalogedCheck_isAccountedForExactlyOnce(string fixtureName)
    {
        var fixture = OracleCorpus.ByName(fixtureName);
        var report = RunJson(fixture.Bytes, ProfileFlag(fixture.Level));

        var buckets = new[] { "passed", "failedChecks", "inconclusive", "notEvaluated" };
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            foreach (var check in report.GetProperty(bucket).EnumerateArray())
            {
                var testId = check.GetProperty("testId").GetString()!;
                Assert.False(
                    seen.TryGetValue(testId, out var firstBucket),
                    $"{fixtureName}: check {testId} is in both '{firstBucket}' and '{bucket}'.");
                seen[testId] = bucket;
            }
        }

        var catalogIds = ConformanceCatalog.For(fixture.Level).Select(c => c.TestId).ToHashSet(StringComparer.Ordinal);
        var missing = catalogIds.Except(seen.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"{fixtureName}: {missing.Count} catalogued checks appear in no bucket: {string.Join(", ", missing.Take(10))}");

        var extra = seen.Keys.Except(catalogIds).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(extra.Count == 0, $"{fixtureName}: reported checks not in the catalog: {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(CorpusFixtureNames))]
    public void ReportedTotal_equalsTheProfileCatalogSize(string fixtureName)
    {
        var fixture = OracleCorpus.ByName(fixtureName);
        var report = RunJson(fixture.Bytes, ProfileFlag(fixture.Level));
        var summary = report.GetProperty("summary");

        // The old total mixed the assertion count into the check count. Pinning it to the catalog
        // size is what makes that impossible to reintroduce: an assertion count cannot be made to
        // equal a catalog size by accident.
        Assert.Equal(
            ConformanceCatalog.Coverage(fixture.Level).Total,
            summary.GetProperty("total").GetInt32());

        Assert.Equal(
            summary.GetProperty("total").GetInt32(),
            summary.GetProperty("passed").GetInt32()
                + summary.GetProperty("failedChecks").GetInt32()
                + summary.GetProperty("inconclusive").GetInt32()
                + report.GetProperty("notEvaluated").GetArrayLength());
    }

    [Fact]
    public void AttributableFailure_blamesOnlyTheCheckItNames()
    {
        // ISO19005-2:6.1.13-10 is a rule id in veraPDF test-id form, so it names exactly one
        // catalogued check. Its clause-mates (6.1.13-1, 6.1.13-2, …) must be untouched — blaming
        // them was the bug.
        var fixture = OracleCorpus.ByName("pdfa2b-hex-invalid-digit");
        var report = RunJson(fixture.Bytes, ProfileFlag(fixture.Level));

        var failedChecks = Ids(report, "failedChecks");
        Assert.Contains("6.1.13-10", failedChecks);

        var clauseMates = ConformanceCatalog.For(fixture.Level)
            .Where(c => c.Clause == "6.1.13" && c.TestId != "6.1.13-10")
            .Select(c => c.TestId)
            .ToList();
        Assert.NotEmpty(clauseMates);

        // No clause-mate may be blamed. They can legitimately end up inconclusive — this document
        // also trips a descriptive rule in the same clause — but a blame that was never asserted is
        // exactly what the clause-wide withdrawal used to manufacture.
        var blamed = failedChecks.ToHashSet(StringComparer.Ordinal);
        foreach (var mate in clauseMates)
            Assert.DoesNotContain(mate, blamed);
    }

    [Fact]
    public void UnattributableFailure_putsItsClauseMatesInInconclusive_notNowhere()
    {
        // ISO19005-2:6.1.2-binary-marker is one of this library's own descriptive rule ids: it says
        // the clause failed but not which numbered check within it. Those checks are the reason the
        // inconclusive bucket exists — before it they were dropped from the report silently.
        var fixture = OracleCorpus.ByName("pdfa2b-bad-binary-marker");
        var report = RunJson(fixture.Bytes, ProfileFlag(fixture.Level));

        var failedRuleIds = report.GetProperty("failed").EnumerateArray()
            .Select(f => f.GetProperty("ruleId").GetString()!)
            .ToList();
        Assert.Contains(failedRuleIds, id => id.EndsWith("6.1.2-binary-marker", StringComparison.Ordinal));

        var inconclusive = Ids(report, "inconclusive");
        Assert.NotEmpty(inconclusive);
        Assert.All(inconclusive, id => Assert.StartsWith("6.1.2", id, StringComparison.Ordinal));
    }

    [Fact]
    public void CleanDocument_hasNothingInconclusive()
    {
        // The bucket must stay empty when nothing failed — otherwise it would be a place for checks
        // to hide rather than an honest account of what the run could not decide.
        var fixture = OracleCorpus.ByName("pdfa2b-compliant");
        var report = RunJson(fixture.Bytes, ProfileFlag(fixture.Level));

        Assert.Empty(Ids(report, "inconclusive"));
        Assert.Empty(Ids(report, "failedChecks"));
    }

    private static List<string> Ids(JsonElement report, string bucket) =>
        report.GetProperty(bucket).EnumerateArray()
            .Select(c => c.GetProperty("testId").GetString()!)
            .ToList();

    private static string ProfileFlag(PdfConformance level) => level switch
    {
        PdfConformance.PdfA2B => "2b",
        PdfConformance.PdfA2U => "2u",
        PdfConformance.PdfA2A => "2a",
        PdfConformance.PdfUA1 => "ua1",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private static JsonElement RunJson(byte[] pdfBytes, string profile)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");
        try
        {
            File.WriteAllBytes(tmp, pdfBytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", profile, "-f", "json"], stdout, stderr, null);
            return JsonDocument.Parse(stdout.ToString()).RootElement.Clone();
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
