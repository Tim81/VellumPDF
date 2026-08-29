// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace VellumPdf.TestSupport;

/// <summary>
/// Gates an oracle test on a problem with the external dependency it needs — missing entirely, or
/// resolved to something that cannot be used, such as the wrong tool (see
/// <see cref="Unavailable(string, string)"/>) or a probe that merely ran out of time rather than
/// confirming either (see <see cref="Transient"/>). On CI, or when a caller explicitly demands the
/// oracle run, a confirmed problem is a build failure: these tests exist to catch a regression the
/// in-process implementation alone would miss, so a CI image quietly missing the tool, or silently
/// running the wrong one, must not go unnoticed. Otherwise — including a transient probe result,
/// regardless of what the environment demands — the problem is made visible without failing the
/// build.
///
/// Before #198 the missing-tool case was <c>{ GateOnCi(tool); return; }</c>, five near-identical
/// copies of it across three test projects (not byte-identical — the messages and the exact
/// environment checks each grew independently, which is exactly why <see
/// cref="IsBarcodeDecodeDependency"/> below still has to name a distinct scope of its own): the
/// bare <c>return</c> let the calling method finish and report normally, so xUnit recorded a PASS
/// for a test that never ran its assertion. 73 call sites (counted by counting every
/// <c>GateOnCi(...)</c> invocation across the five files on <c>main</c>) is not the same number as
/// the tests that phantom-passed — a <c>[Theory]</c> reaches one call site once per case, not
/// once per method — so it is not restated as a test count here: with every tool the suite depends
/// on made unresolvable, the population that phantom-passed measured 193 test cases (135 in
/// Barcodes, 47 in Layout, 11 in Kernel) across 122 distinct methods. <see cref="Assert.Skip"/> is
/// the fix: it aborts the test with a visible, distinctly-reported outcome instead of falling
/// through to a normal return.
/// </summary>
public static class OracleGate
{
    /// <summary>
    /// Fails the current test if the environment demands the oracle run; otherwise skips it.
    /// <paramref name="dependency"/> names what's missing — a CLI tool, or a resource like
    /// "platform TrueType font" for the call sites that gate on something other than a process —
    /// and appears in the failure or skip message, and decides whether REQUIRE_VERAPDF or
    /// REQUIRE_BARCODE_ORACLE apply (see <see cref="IsRequired"/>).
    /// </summary>
    [DoesNotReturn]
    public static void Unavailable(string dependency)
        => Gate(dependency, $"oracle '{dependency}' unavailable");

    /// <summary>
    /// Same as <see cref="Unavailable(string)"/>, but for a dependency that resolved to something
    /// concrete rather than being absent outright — <paramref name="detail"/> names what was found
    /// and, where there is one, how to fix it, so the message is something a developer can act on
    /// rather than a bare "unavailable".
    /// </summary>
    [DoesNotReturn]
    public static void Unavailable(string dependency, string detail)
        => Gate(dependency, $"oracle '{dependency}' unavailable: {detail}");

    /// <summary>
    /// This call itself always skips. It never escalates to a build failure, even when the
    /// environment would otherwise demand the oracle run (see <see cref="IsRequired"/>), because a
    /// probe outcome that only shows this one attempt did not finish in time is not proof that
    /// <paramref name="dependency"/> is absent or resolved to the wrong tool: escalating a single
    /// timeout the way <see cref="Unavailable(string, string)"/> does would fail every test sharing
    /// that dependency's identity check off one slow sample instead of the one that happened to hit
    /// it (#198 review, round 4). That does not mean a persistently timing-out probe can never fail
    /// the build: <c>ExternalTool.CheckIdentity</c> only routes here while consecutive timeouts for
    /// the same tool stay under its own bound. Past that bound it hands back a definitive verdict
    /// that <c>ExternalTool.EnsureUsable</c> sends to <see cref="Unavailable(string, string)"/>
    /// instead, so this method is never reached for that Nth attempt (#198 review, round 5).
    /// </summary>
    [DoesNotReturn]
    public static void Transient(string dependency, string detail)
        => Assert.Skip($"oracle '{dependency}' unavailable: {detail}");

    [DoesNotReturn]
    private static void Gate(string dependency, string message)
    {
        if (IsRequired(dependency))
            Assert.Fail($"{message}. See CONTRIBUTING.md's Prerequisites section for how to satisfy it.");

        Assert.Skip(message);
    }

    // CI, GITHUB_ACTIONS and REQUIRE_ORACLES are global — any missing or wrong oracle fails the
    // build under any of them. REQUIRE_VERAPDF and REQUIRE_BARCODE_ORACLE predate this shared
    // gate (#198) and stay scoped to the oracle each is named after, not promoted to a second
    // global switch: on main, REQUIRE_VERAPDF was read only by the conformance suite's own
    // veraPDF checks, and REQUIRE_BARCODE_ORACLE only by the barcode decode oracle's
    // pdftoppm/python/zxing-cpp gate. Widening either to every tool would make the documented
    // "set REQUIRE_VERAPDF=1 to reproduce CI" recipe fail the qpdf and poppler gates too, on any
    // machine (this one included) where those aren't on PATH. REQUIRE_BARCODE_ORACLE reaching
    // pdftoppm (see IsBarcodeDecodeDependency) is deliberate even though pdftoppm is also probed
    // outside the barcode suite, by ExternalToolResolutionTests' generic identity theory: it is
    // the barcode decode oracle's own rasterizer dependency, named for what it is required by,
    // not for every consumer that happens to share the same binary.
    private static bool IsRequired(string dependency)
        => IsTrueOrOne("CI")
        || IsTrueOrOne("GITHUB_ACTIONS")
        || IsTrueOrOne("REQUIRE_ORACLES")
        || (dependency == "verapdf" && IsTrueOrOne("REQUIRE_VERAPDF"))
        || (IsBarcodeDecodeDependency(dependency) && IsTrueOrOne("REQUIRE_BARCODE_ORACLE"));

    private static bool IsBarcodeDecodeDependency(string dependency)
        => dependency is "pdftoppm" or "python" or "zxing-cpp";

    // All five switches accept "1" or "true" (case-insensitively). ci.yml sets REQUIRE_VERAPDF and
    // REQUIRE_BARCODE_ORACLE to the literal "1" (ci.yml:80-81); CI and GITHUB_ACTIONS are set to
    // "true" by the GitHub Actions runner itself, not by this repository's workflow file. A CI
    // system that instead exports CI=1 (common outside GitHub Actions) needs the same acceptance,
    // and a developer who reaches for "true" on REQUIRE_ORACLES, REQUIRE_VERAPDF or
    // REQUIRE_BARCODE_ORACLE should get the same behaviour CI and GITHUB_ACTIONS already give — a
    // "1"-only check on those three would be a silent footgun for exactly that developer.
    private static bool IsTrueOrOne(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
