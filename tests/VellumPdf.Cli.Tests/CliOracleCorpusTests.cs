// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Tests.Oracle;
using Xunit;

namespace VellumPdf.Cli.Tests;

/// <summary>
/// Runs the entire <c>VellumPdf.Conformance</c> oracle corpus through the CLI, proving the
/// command-line front-end reaches the same conformant / non-conformant verdict as the library for
/// every profile and check. Each fixture is piped to the tool over stdin and validated against its
/// declared profile; the exit code must match the fixture's expected verdict.
/// </summary>
public sealed class CliOracleCorpusTests
{
    public static IEnumerable<object[]> Fixtures =>
        OracleCorpus.All.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Cli_Verdict_MatchesFixture(string name)
    {
        var fixture = OracleCorpus.ByName(name);

        using var stdin = new MemoryStream(fixture.Bytes);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // Read the PDF from stdin, validate against the fixture's profile, suppress the report.
        var exit = PreflightRunner.Run(["-", "-p", fixture.VeraFlavour, "-q"], stdout, stderr, stdin);

        // 0 = conformant, 1 = non-conformant; must match the fixture's expected verdict.
        var expected = fixture.ExpectedCompliant ? 0 : 1;
        Assert.Equal(expected, exit);
    }
}
