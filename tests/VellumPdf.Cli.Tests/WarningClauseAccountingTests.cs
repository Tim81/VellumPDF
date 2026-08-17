// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using VellumPdf.Cli;
using VellumPdf.Conformance;
using VellumPdf.Conformance.Coverage;
using VellumPdf.Conformance.Tests.Oracle;

namespace VellumPdf.Cli.Tests;

/// <summary>
/// A Warning-severity assertion must not withdraw a catalogued check's passing claim.
/// </summary>
/// <remarks>
/// <para>
/// The report used to build its failing-clause set from assertions of <em>any</em> severity, so a
/// warning made every catalogued check sharing that clause number stop being claimed as passed.
/// The check then appeared in no section of the report: not failed (a warning is below the default
/// display threshold), not passed, and not un-evaluated — and the totals stopped adding up.
/// </para>
/// <para>
/// <c>A2aContentItemTaggingRule</c> is the rule that exposed it, reporting a Warning at clause
/// 6.7.3.3 and thereby unclaiming 6.7.3.3-1, the <c>/StructTreeRoot</c> presence check that
/// <c>LogicalStructureRule</c> had just passed on the same document. These tests assert the
/// arithmetic rather than the specific clause, so they keep holding for any future
/// warning-severity rule.
/// </para>
/// </remarks>
public sealed class WarningClauseAccountingTests
{
    /// <summary>A tagged PDF/A-2a document with real text that was never tagged.</summary>
    private static byte[] UntaggedRealContent() => OracleCorpus.ByName("pdfa2a-untagged-real-content").Bytes;

    /// <summary>The same document, properly tagged.</summary>
    private static byte[] FullyTagged() => OracleCorpus.ByName("pdfa2a-tagged").Bytes;

    [Fact]
    public void WarningOnlyDocument_stillClaimsEveryCatalogedCheck()
    {
        var json = RunJson(UntaggedRealContent(), "2a");
        using var doc = JsonDocument.Parse(json);
        var report = doc.RootElement;

        var passed = report.GetProperty("passed").GetArrayLength();
        var notEvaluated = report.GetProperty("notEvaluated").GetArrayLength();
        var failed = report.GetProperty("failed").GetArrayLength();

        // Every catalogued check must be accounted for in exactly one bucket. This is the invariant
        // that silently broke: the total came out one short, and the missing check was in no bucket.
        var total = ConformanceCatalog.Coverage(PdfConformance.PdfA2A).Total;
        Assert.Equal(total, passed + notEvaluated + failed);
    }

    [Fact]
    public void WarningOnlyDocument_isStillReportedConformant()
    {
        var json = RunJson(UntaggedRealContent(), "2a");
        using var doc = JsonDocument.Parse(json);
        var report = doc.RootElement;

        // A warning does not affect conformance at the default --fail-on error.
        Assert.True(report.GetProperty("conformant").GetBoolean());
    }

    [Fact]
    public void WarningOnlyDocument_claimsTheSameChecksAsAFullyTaggedOne()
    {
        // The sharpest form of the assertion: the only difference between these two documents is
        // untagged content, which produces a warning and nothing else. So the set of catalogued
        // checks claimed as passed must be identical — a warning withdraws nothing.
        var withWarning = PassedClauses(RunJson(UntaggedRealContent(), "2a"));
        var fullyTagged = PassedClauses(RunJson(FullyTagged(), "2a"));

        Assert.Equal(fullyTagged, withWarning);
    }

    private static SortedSet<string> PassedClauses(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var report = doc.RootElement;
        var clauses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var check in report.GetProperty("passed").EnumerateArray())
            clauses.Add(check.GetProperty("clause").GetString()!);
        return clauses;
    }

    private static string RunJson(byte[] pdfBytes, string profile)
    {
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, pdfBytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", profile, "-f", "json"], stdout, stderr, null);
            return stdout.ToString();
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
