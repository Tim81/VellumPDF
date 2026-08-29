// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.TestSupport;

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>
/// A direct test of <see cref="ExternalTool.CheckIdentity"/>: proves it resolves each oracle CLI
/// to the tool it claims, not merely to *some* executable answering that name. That distinction
/// is not hypothetical: on this repository's own dev machine, a bare "pdftotext" is poppler
/// 25.07.0 launched from a PowerShell session but Xpdf 4.00 (<c>/mingw64/bin/pdftotext</c>, no
/// <c>-tsv</c> flag) launched from a Git Bash one, a silent swap a hand-check in the "wrong"
/// shell would never surface (#198).
///
/// Before #198's second review round this test *was* the probe: it ran <c>ExternalTool.TryRun</c>
/// with a version flag and checked the banner itself, which meant every consumer of
/// <c>ExternalTool.TryRun</c> could still resolve to the wrong tool and use its output anyway,
/// and this test caught the mismatch, but only for itself. The probe now lives in
/// <c>ExternalTool.CheckIdentity</c>, where <c>TryRun</c> consults it before returning anything a
/// caller might assert on, and this test exercises that shared probe directly instead of
/// re-deriving its own copy of the banner and flag checks.
///
/// A resolution that turns out not to be usable (absent, or answering as something else) goes
/// through <see cref="OracleGate.Unavailable(string, string)"/>: on a developer machine that is a
/// visible, actionable skip, not a hard failure, unless CI or REQUIRE_ORACLES demands the oracle
/// run. A probe that merely ran out of time is different: it goes through <see
/// cref="OracleGate.Transient(string, string)"/> instead, which always skips regardless of what
/// the environment demands. A single slow sample must not turn a clean checkout red, unless it
/// is the Nth *consecutive* timeout for that tool, in which case <see
/// cref="ExternalTool.CheckIdentity"/> itself has already turned it into the same definitive,
/// escalating verdict a confirmed-absent tool gets (see <c>ExternalTool.ConsecutiveTimeoutEscalationBound</c>).
/// This test calls <see cref="ExternalTool.EnsureUsable(ExternalTool.IdentityResult)"/> rather
/// than re-deriving that routing itself. A prior version of this test read only <see
/// cref="ExternalTool.IdentityStatus"/> and
/// sent a merely-timed-out probe to <see cref="OracleGate.Unavailable(string, string)"/>
/// unconditionally, which fails the build under CI/REQUIRE_ORACLES off one slow sample, exactly
/// the defect round 4's fix to <c>ExternalTool.TryRun</c> exists to prevent: three consumers each
/// re-deriving the same branch is what let one of them get it wrong (#198 review, round 5). Xpdf
/// shadowing poppler in Git Bash is a pre-existing environment problem this test surfaces, not
/// something this run can fix on its own, so it must not turn a clean checkout red. That is what
/// CI (or REQUIRE_ORACLES) is for.
/// </summary>
public sealed class ExternalToolResolutionTests
{
    public static IEnumerable<object[]> Tools =>
    [
        ["qpdf"],
        ["pdftotext"],
        ["pdftoppm"],
        ["pdfsig"],
        ["verapdf"],
    ];

    [Theory]
    [MemberData(nameof(Tools))]
    public void Resolves_ToTheClaimedTool(string tool)
    {
        // Probes once and gates on that same verdict via the IdentityResult overload of
        // EnsureUsable, rather than calling the string overload, which would probe "tool" again.
        // A prior version of this test did exactly that: CheckIdentity here to read the verdict,
        // then EnsureUsable(tool) to gate on a fresh one, and against veraPDF's slow JVM cold
        // start the two could disagree, a timed-out first probe against an Ok second one. When
        // they did, the gating branch's own return ran before the assertion below ever did, so
        // xUnit recorded a passed test that had run no assertion, the exact #198 failure mode in
        // the test whose job is to catch tool misresolution (#198 review, round 6).
        var identity = ExternalTool.CheckIdentity(tool);
        ExternalTool.EnsureUsable(identity);

        // Reachable on every case now, not only when identity.Status is already Ok: a bad verdict
        // fails or skips inside EnsureUsable above, so this line only runs once that call has
        // already returned normally, which means identity.Status is Ok. That makes this assertion
        // tautological given the control flow above it, and it cannot by itself catch a
        // CheckIdentity that regressed to always reporting Ok; that regression is
        // ExternalToolTests.CheckIdentity_ForVerapdf_ReportsUnusableNotWrong_WhenTheProbeTimesOut's
        // job. What this line buys is narrower: every case of this theory runs an assertion,
        // never a bare return ahead of one (#198 review, round 6).
        Assert.Equal(ExternalTool.IdentityStatus.Ok, identity.Status);
    }
}
