// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The Layout capability table is published twice, deliberately: once in
/// <c>src/VellumPdf.Layout/README.md</c>, which is what the NuGet listing shows, and once in
/// <c>docs/layout-guide.md</c>, alongside the narrative walkthrough it summarizes. Duplication only
/// stays honest if something keeps the two from drifting apart, so both copies are wrapped in
/// matching <c>&lt;!-- capability-table:layout:start/end --&gt;</c> markers, and this test asserts
/// the marked span is byte-identical between them — an edit to one copy without the other fails
/// here instead of shipping a README and a guide that disagree. The marked span covers the table
/// itself and the "flagged for reviewer verification" note beneath it, since an edit to a row's
/// status should carry its accompanying explanation along in lockstep.
/// </summary>
public sealed class LayoutDocsConsistencyTests
{
    [Fact]
    public void CapabilityTable_readmeAndGuide_areByteIdentical()
    {
        var root = FindRepoRoot();
        var readmeTable = ExtractMarkedBlock(
            Path.Combine(root, "src", "VellumPdf.Layout", "README.md"), "layout");
        var guideTable = ExtractMarkedBlock(
            Path.Combine(root, "docs", "layout-guide.md"), "layout");

        Assert.Equal(readmeTable, guideTable);
    }

    private static string ExtractMarkedBlock(string path, string key)
    {
        var text = File.ReadAllText(path);
        var startMarker = $"<!-- capability-table:{key}:start -->";
        var endMarker = $"<!-- capability-table:{key}:end -->";

        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startMarker} not found in {path}");
        Assert.True(end > start, $"{endMarker} not found after the start marker in {path}");

        return text[(start + startMarker.Length)..end];
    }

    /// <summary>Locates the repository root by walking up from the test assembly's directory to
    /// find <c>VellumPdf.slnx</c>, matching <c>ZxingDecodeOracleTests.FindRepoRoot</c>.</summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VellumPdf.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate VellumPdf.slnx by walking up from AppContext.BaseDirectory.");
    }
}
