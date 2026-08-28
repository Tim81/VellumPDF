// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.TestSupport;
using Xunit.Sdk;

namespace VellumPdf.TestSupport.Tests;

/// <summary>
/// Every environment-variable-driven test here shares a disabled-parallelisation collection: the
/// variables it reads are process-global, so two of these tests racing each other would corrupt
/// each other's expectations the same way two oracle tests sharing a working tree do (CLAUDE.md).
/// </summary>
[CollectionDefinition("OracleGate environment", DisableParallelization = true)]
public sealed class OracleGateEnvironmentCollection;

/// <summary>
/// Direct coverage of <see cref="OracleGate"/> (#198 review): before this project existed, nothing
/// exercised the gate itself, only the 73 call sites built on top of it. A regression back to
/// #198's original defect, a bare <c>return</c> in place of <see cref="Assert.Skip"/>, would have
/// shown up only as every one of those call sites reporting a phantom pass, which is exactly the
/// failure mode #198 was filed to fix.
///
/// <see cref="SkipException"/> propagates through both <see
/// cref="Assert.Throws{T}(Func{object})"/> and <see cref="Record.Exception(Action)"/> by design.
/// xUnit v3 rethrows it from inside each of those helpers specifically so a test cannot use them
/// to silently absorb a dynamic skip (verified directly: swapping <c>try</c>/<c>catch</c> below
/// for <c>Record.Exception</c> still reports the test as <c>Skipped</c>, not <c>Passed</c>, even
/// though the type check afterward would have passed). That special case is not limited to a test
/// expecting a skip: a test asserting <see cref="FailException"/> is just as exposed, since a
/// regressed <see cref="OracleGate.IsRequired"/> that stops escalating would make
/// <c>OracleGate.Unavailable</c> call <see cref="Assert.Skip"/> instead of <see cref="Assert.Fail"/>,
/// and <c>Assert.Throws&lt;FailException&gt;</c> would let the resulting <see cref="SkipException"/>
/// straight through, reporting the whole test <c>Skipped</c> rather than failing it on the type
/// mismatch a plain exception would produce (#198 review, round 6: neutering the <c>CI</c> disjunct
/// in <c>IsRequired</c> reproduces exactly this against the escalation tests below). A plain,
/// unassisted <c>try</c>/<c>catch</c> has no such special case, since it is the CLR catching the
/// exception, not an xUnit helper, so every test below that asserts an exception type, whether
/// <see cref="SkipException"/> or <see cref="FailException"/>, uses that instead: the message
/// assertion after it actually runs, and the test reports <c>Passed</c> only when both the caught
/// type and its message match.
/// </summary>
[Collection("OracleGate environment")]
public sealed class OracleGateTests : IDisposable
{
    private static readonly string[] EnvVars =
        ["CI", "GITHUB_ACTIONS", "REQUIRE_ORACLES", "REQUIRE_VERAPDF", "REQUIRE_BARCODE_ORACLE"];

    private readonly Dictionary<string, string?> _saved;

    public OracleGateTests()
    {
        _saved = EnvVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (var v in EnvVars)
            Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        foreach (var v in EnvVars)
            Environment.SetEnvironmentVariable(v, _saved[v]);
    }

    [Fact]
    public void Unavailable_WithNoEnvironmentDemand_Skips()
    {
        SkipException? skip = null;
        try { OracleGate.Unavailable("widget"); }
        catch (SkipException ex) { skip = ex; }
        Assert.NotNull(skip);
        Assert.Contains("widget", skip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Transient_WithNoEnvironmentDemand_Skips()
    {
        SkipException? skip = null;
        try { OracleGate.Transient("widget", "did not finish within 30000 ms"); }
        catch (SkipException ex) { skip = ex; }
        Assert.NotNull(skip);
        Assert.Contains("widget", skip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_WithCiTrue_FailsNamingTheDependency()
    {
        Environment.SetEnvironmentVariable("CI", "true");

        Exception? caught = null;
        try { OracleGate.Unavailable("widget"); }
        catch (Exception ex) { caught = ex; }
        Assert.IsType<FailException>(caught);

        // "oracle 'widget'", not just "widget": the bare substring would equally accept a message
        // naming a different dependency that merely happens to mention "widget" in its detail text
        // (#198 review, round 7, finding 8).
        Assert.Contains("oracle 'widget'", caught!.Message, StringComparison.Ordinal);
    }

    // The defining difference from Unavailable: a probe that merely timed out must not fail the
    // build under any of the five escalation switches, only skip — escalating it would turn one
    // slow sample into a build failure for every test sharing that dependency's identity check
    // (#198 review, round 4).
    [Theory]
    [InlineData("CI")]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("REQUIRE_ORACLES")]
    [InlineData("REQUIRE_VERAPDF")]
    public void Transient_StillSkips_UnderEveryEscalationSwitch(string variable)
    {
        Environment.SetEnvironmentVariable(variable, variable == "REQUIRE_VERAPDF" ? "1" : "true");

        SkipException? skip = null;
        try { OracleGate.Transient("verapdf", "did not finish within 30000 ms"); }
        catch (SkipException ex) { skip = ex; }
        Assert.NotNull(skip);
        Assert.Contains("verapdf", skip.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    public void Unavailable_WithRequireOracles_Fails(string value)
    {
        Environment.SetEnvironmentVariable("REQUIRE_ORACLES", value);

        Exception? caught = null;
        try { OracleGate.Unavailable("widget"); }
        catch (Exception ex) { caught = ex; }
        Assert.IsType<FailException>(caught);
        Assert.Contains("oracle 'widget'", caught!.Message, StringComparison.Ordinal);
    }

    // REQUIRE_VERAPDF is scoped to the "verapdf" dependency, not promoted to a second global
    // switch (OracleGate.cs); nothing else in the suite enforced that scoping directly before
    // this test.
    [Fact]
    public void RequireVerapdf_FailsForVerapdf_ButSkipsForQpdf()
    {
        Environment.SetEnvironmentVariable("REQUIRE_VERAPDF", "1");

        Exception? caught = null;
        try { OracleGate.Unavailable("verapdf"); }
        catch (Exception ex) { caught = ex; }
        Assert.IsType<FailException>(caught);
        Assert.Contains("oracle 'verapdf'", caught!.Message, StringComparison.Ordinal);

        var skipped = false;
        try { OracleGate.Unavailable("qpdf"); }
        catch (SkipException) { skipped = true; }
        Assert.True(skipped, "expected OracleGate.Unavailable(\"qpdf\") to skip, not fail or pass through.");
    }

    [Fact]
    public void RequireBarcodeOracle_FailsForPdftoppm_ButSkipsForQpdf()
    {
        Environment.SetEnvironmentVariable("REQUIRE_BARCODE_ORACLE", "1");

        Exception? caught = null;
        try { OracleGate.Unavailable("pdftoppm"); }
        catch (Exception ex) { caught = ex; }
        Assert.IsType<FailException>(caught);
        Assert.Contains("oracle 'pdftoppm'", caught!.Message, StringComparison.Ordinal);

        var skipped = false;
        try { OracleGate.Unavailable("qpdf"); }
        catch (SkipException) { skipped = true; }
        Assert.True(skipped, "expected OracleGate.Unavailable(\"qpdf\") to skip, not fail or pass through.");
    }
}

/// <summary>
/// Direct coverage of <see cref="ExternalTool"/>. Shares the collection above even though none of
/// these tests read the environment-variable switches <see cref="OracleGate"/> does: several of
/// them mutate <c>VERAPDF_HOME</c> and <c>PATH</c> process-globally for tens of seconds and reset
/// process-global caches (<see cref="ExternalTool.ResetIdentityCacheForTests"/>,
/// <see cref="ExternalTool.VeraPdfProbeTimeoutMsOverrideForTests"/>), which is exactly the kind of
/// state <c>OracleGateTests</c> above already needed serialised against every other collection in
/// the assembly. A scratch xUnit v3 3.2.2 project mirroring this exact shape confirmed a
/// <c>DisableParallelization = true</c> collection is serialised against every *other* collection
/// too, not just against itself, so this class was already safe purely because
/// <c>OracleGateTests</c> happened to opt in, which this class did not declare for itself (#198
/// review, round 5).
/// </summary>
[Collection("OracleGate environment")]
public sealed class ExternalToolTests : IDisposable
{
    // Runs after every test in this class, not just the ones below that touch
    // VeraPdfProbeTimeoutMsOverrideForTests, so a leak is caught at its source regardless of
    // which test caused it or what order the class ran in — declaration order is not execution
    // order, and an earlier version of this class asserted the same default only inside one test,
    // which happened to run before the two that set the override but would not have caught a leak
    // from either of them (#198 review, round 7, finding 5).
    public void Dispose() => Assert.Equal(-1, ExternalTool.VeraPdfProbeTimeoutMsOverrideForTests);

    [Fact]
    public void TryRun_ReturnsFalse_ForANameThatCannotResolveToAnExecutable()
    {
        var found = ExternalTool.TryRun(
            "vellumpdf-definitely-not-a-real-binary-198",
            [],
            out _, out _, out _, out _,
            timeoutMs: 2_000);

        Assert.False(found);
    }

    // Regression for the one defect the pre-#198 copies got right and this port could have lost:
    // draining stdout/stderr concurrently BEFORE WaitForExit, not after. A report larger than the
    // OS pipe buffer (64KB on Windows) blocks the child on write once that buffer fills; if the
    // drain started only after WaitForExit, the child would block forever writing while this test
    // blocked forever waiting for it to exit. This is the only test in this project that would
    // fail if that ordering regressed.
    [Fact]
    public void TryRun_CapturesOutputLargerThanTheOsPipeBuffer()
    {
        var (tool, args) = BigOutputCommand();

        var ok = ExternalTool.TryRun(tool, args, out var exit, out var stdout, out _, out var timedOut, timeoutMs: 30_000);

        Assert.True(ok, $"'{tool}' could not be started.");
        Assert.False(timedOut, "the big-output command timed out, or its output could not be fully captured.");
        Assert.Equal(0, exit);
        Assert.True(
            stdout.Length > 256 * 1024,
            $"Expected over 256KB of stdout to prove the pipe-buffer boundary was crossed; got {stdout.Length} bytes.");
    }

    private static (string Tool, string[] Args) BigOutputCommand()
    {
        // 20,000 lines of 32 'X's (plus newline) is comfortably over 256KB and over the 64KB pipe
        // buffer both, on either OS.
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe",
                ["/c", "for /L %i in (1,1,20000) do @echo XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"]);
        }

        return ("/bin/sh",
            ["-c", "for i in $(seq 1 20000); do echo XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX; done"]);
    }

    // #198 review, round 3, P0: a version probe that ran past its budget left exitCode at
    // RunProcess's -1 sentinel, and the pre-fix ProbeIdentity read that -1 as "resolved to
    // something that exited -1" — a false diagnosis of the WRONG tool, cached in IdentityCache
    // for the rest of the process and escalated to Assert.Fail under CI=1 (reproduced against a
    // verapdf.bat answering after 14 seconds against the old, shared 10-second probe budget).
    //
    // Runs on both platforms, not Windows-only (#198 review, round 4): the only job that runs
    // `dotnet test` is ubuntu-latest (ci.yml), so a Windows-only regression test for a CI-breaking
    // bug has zero CI coverage. ExternalTool.ResolveLauncher resolves "verapdf" two different
    // ways depending on OS (see its own doc), so the fixture and the environment variable this
    // test manipulates differ by platform: a `.bat` behind VERAPDF_HOME on Windows, a bare-name
    // `PATH` lookup elsewhere — the same shape ci.yml's own shim uses.
    [Fact]
    public void CheckIdentity_ForVerapdf_ReportsUnusableNotWrong_WhenTheProbeTimesOut()
    {
        var savedHome = Environment.GetEnvironmentVariable("VERAPDF_HOME");
        var savedPath = Environment.GetEnvironmentVariable("PATH");

        // This test asserts the literal 30000 ms below, so it depends on EffectiveVeraPdfProbeTimeoutMs
        // reading VeraPdfProbeTimeoutMs rather than a leaked VeraPdfProbeTimeoutMsOverrideForTests,
        // a seam this test neither sets nor otherwise reads. Pinning that precondition here means a
        // failed literal-30000ms assertion below names the actual cause instead of silently
        // laundering the wrong budget through the message this test exists to pin. Catching a leak
        // at its source, wherever it comes from, is this class's Dispose method's job, not this
        // assertion's (#198 review, round 6; corrected in round 7, finding 5 — this test's
        // declaration order is not proof of execution order).
        Assert.Equal(-1, ExternalTool.VeraPdfProbeTimeoutMsOverrideForTests);

        // Guards against inheriting a verdict some other call cached for "verapdf" before this
        // test's environment changes take effect, and against leaving this test's own verdict
        // behind for whatever runs next: nothing else in this project resolves "verapdf" today,
        // but nothing before this enforced that it stays that way (#198 review, round 4).
        ExternalTool.ResetIdentityCacheForTests("verapdf");

        var dir = Directory.CreateTempSubdirectory("vellumpdf-slow-verapdf-");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // `ping` as a sleep, not `timeout`: TIMEOUT.exe refuses to run at all against a
                // redirected stdin ("ERROR: Input redirection is not supported"), and RunProcess
                // always redirects stdin. 36 pings at ~1/second sleeps past the 30-second veraPDF
                // probe budget before this otherwise-correct banner is ever printed.
                File.WriteAllText(
                    Path.Combine(dir.FullName, "verapdf.bat"),
                    "@echo off\r\nping -n 36 127.0.0.1 >nul\r\necho veraPDF 1.30.2\r\n");
                Environment.SetEnvironmentVariable("VERAPDF_HOME", dir.FullName);
            }
            else
            {
                // Off Windows, ExternalTool.ResolveLauncher never reads VERAPDF_HOME for
                // "verapdf" — it resolves the bare name via PATH, exactly like ci.yml's own
                // `/bin/sh` shim. Putting a slow, executable "verapdf" ahead of the real PATH
                // reproduces the same "resolved to something slow" scenario as the Windows branch
                // above, through the mechanism this platform actually uses.
                var script = Path.Combine(dir.FullName, "verapdf");
                File.WriteAllText(script, "#!/bin/sh\nsleep 36\necho 'veraPDF 1.30.2'\n");
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                Environment.SetEnvironmentVariable("PATH", dir.FullName + Path.PathSeparator + savedPath);
            }

            var identity = ExternalTool.CheckIdentity("verapdf");

            Assert.Equal(ExternalTool.IdentityStatus.Unusable, identity.Status);
            Assert.True(identity.IsTimeout, "expected a timed-out probe to be reported as transient, not a definitive verdict.");
            Assert.False(identity.Cacheable, "a timed-out probe must not be cached — see ExternalTool.CheckIdentity.");

            // Asserts the actual budget ExternalTool ships (VeraPdfProbeTimeoutMs), not merely
            // that some timeout fired: a probe that fell back to the 10-second default would also
            // report "did not finish within 10000 ms" and pass a weaker assertion here even though
            // the 30-second veraPDF-specific budget had silently regressed away (#198 review,
            // round 4 — the prior version of this test asserted no specific number).
            Assert.Contains("did not finish within 30000 ms", identity.Detail, StringComparison.Ordinal);

            // The pre-fix message for this exact scenario: proves the timeout is no longer
            // misread as a non-zero exit code from the wrong tool.
            Assert.DoesNotContain("exited", identity.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERAPDF_HOME", savedHome);
            Environment.SetEnvironmentVariable("PATH", savedPath);
            ExternalTool.ResetIdentityCacheForTests("verapdf");

            // ExternalTool.RunProcess waits (bounded) for the killed process tree to actually
            // exit before returning, but a directory delete immediately afterward is still not
            // guaranteed race-free on every platform/filesystem combination, so retry briefly
            // rather than fail this test's cleanup on a lingering handle.
            DeleteDirectoryWithRetry(dir.FullName);
        }
    }

    // Writes a "verapdf" launcher that answers correctly but only after sleeping roughly
    // (pingCount - 1) seconds, the same technique
    // CheckIdentity_ForVerapdf_ReportsUnusableNotWrong_WhenTheProbeTimesOut above uses (see its own
    // comments for why `ping`, not `timeout`, and why the launch mechanism differs by platform),
    // factored out for the two round-5 tests below so each isn't a third copy of the same dozen
    // lines. Does not touch the existing test above, which keeps its own inline copy unchanged:
    // "do not weaken the existing 30-second test" (#198 review, round 5).
    private static void InstallSlowVerapdfShim(string dirPath, string? savedPath, int pingCount)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(dirPath, "verapdf.bat"),
                $"@echo off\r\nping -n {pingCount} 127.0.0.1 >nul\r\necho veraPDF 1.30.2\r\n");
            Environment.SetEnvironmentVariable("VERAPDF_HOME", dirPath);
        }
        else
        {
            var script = Path.Combine(dirPath, "verapdf");
            File.WriteAllText(script, $"#!/bin/sh\nsleep {pingCount - 1}\necho 'veraPDF 1.30.2'\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Environment.SetEnvironmentVariable("PATH", dirPath + Path.PathSeparator + savedPath);
        }
    }

    // #198 review, round 5, finding 3 ("the join"): CheckIdentity's IsTimeout flag and the
    // OracleGate call TryRun/EnsureUsable route it to were each covered separately.
    // CheckIdentity_ForVerapdf_ReportsUnusableNotWrong_WhenTheProbeTimesOut above covers the probe,
    // OracleGateTests.Transient_StillSkips_UnderEveryEscalationSwitch covers the gate, but nothing
    // exercised the join between them. Swapping the two branches inside TryRun's (now EnsureUsable's)
    // if/else and rebuilding passed the full suite before this test existed; this pins the routing
    // so that inversion fails here instead. CI=true is the escalation switch used because it is the
    // one a misconfigured build server sets without a human choosing to. Proving a timeout still
    // skips under it is the whole point of round 4's fix.
    //
    // Uses VeraPdfProbeTimeoutMsOverrideForTests to shrink the probe budget so this doesn't pay a
    // real 30-second timeout on top of the existing test's.
    [Fact]
    public void TryRun_ForATimedOutProbe_SkipsRatherThanFailing_UnderAnEscalationSwitch()
    {
        var savedCi = Environment.GetEnvironmentVariable("CI");
        var savedHome = Environment.GetEnvironmentVariable("VERAPDF_HOME");
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        ExternalTool.ResetIdentityCacheForTests("verapdf");

        var dir = Directory.CreateTempSubdirectory("vellumpdf-slow-verapdf-join-");
        try
        {
            // Set only after the temp directory above is safely created, so a throw from
            // CreateTempSubdirectory itself cannot leave this process-wide override shortened for
            // every later "verapdf" probe without ever reaching the finally block below that
            // resets it (#198 review, round 6).
            ExternalTool.VeraPdfProbeTimeoutMsOverrideForTests = 1_000;
            Environment.SetEnvironmentVariable("CI", "true");
            InstallSlowVerapdfShim(dir.FullName, savedPath, pingCount: 3);

            // SkipException propagates through both Assert.Throws and Record.Exception by xUnit v3
            // design (see this file's class doc above), so asserting a *skip* here must use a raw
            // try/catch: Assert.Throws<SkipException> would itself report as Skipped rather than
            // exercising the assertions below it.
            Exception? caught = null;
            try
            {
                ExternalTool.TryRun("verapdf", ["--version"], out _, out _, out _, out _);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.IsType<SkipException>(caught);

            // The exception type alone does not prove EnsureUsable routed through the "verapdf"
            // branch this test means to pin: a Transient skip for the wrong dependency would also
            // be a SkipException. Asserting "oracle 'verapdf'", not just "verapdf", closes a second
            // gap the same way: Transient's own detail text independently contains the substring
            // "verapdf" (in "'verapdf --version' did not finish..." and "set VERAPDF_HOME..."), so
            // a bare Assert.Contains("verapdf", ...) would still pass against a skip that named some
            // other dependency entirely (#198 review, round 6; tightened round 7, finding 8).
            Assert.Contains("oracle 'verapdf'", caught!.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", savedCi);
            Environment.SetEnvironmentVariable("VERAPDF_HOME", savedHome);
            Environment.SetEnvironmentVariable("PATH", savedPath);
            ExternalTool.VeraPdfProbeTimeoutMsOverrideForTests = -1;
            ExternalTool.ResetIdentityCacheForTests("verapdf");
            DeleteDirectoryWithRetry(dir.FullName);
        }
    }

    // #198 review, round 5, finding 2/3 ("the retry bound"): a probe that keeps timing out must
    // still escalate rather than skip forever. Attempts 1 and 2 keep today's behaviour exactly
    // (non-cacheable, IsTimeout), and the 3rd converts to a definitive, cacheable, non-timeout
    // verdict so EnsureUsable routes it to the escalating OracleGate.Unavailable instead of the
    // always-skipping OracleGate.Transient.
    //
    // Uses VeraPdfProbeTimeoutMsOverrideForTests so three consecutive timeouts cost roughly 2
    // seconds each instead of the real 30-second VeraPdfProbeTimeoutMs; three of those would add
    // about another 90 seconds on top of what the existing 30-second test already costs.
    [Fact]
    public void CheckIdentity_ForVerapdf_EscalatesAfterThreeConsecutiveTimeouts()
    {
        var savedHome = Environment.GetEnvironmentVariable("VERAPDF_HOME");
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        ExternalTool.ResetIdentityCacheForTests("verapdf");

        var dir = Directory.CreateTempSubdirectory("vellumpdf-slow-verapdf-retry-");
        try
        {
            // Set only after the temp directory above is safely created (#198 review, round 6;
            // see the join test above for why).
            ExternalTool.VeraPdfProbeTimeoutMsOverrideForTests = 1_000;
            InstallSlowVerapdfShim(dir.FullName, savedPath, pingCount: 3);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var identity = ExternalTool.CheckIdentity("verapdf");
                Assert.Equal(ExternalTool.IdentityStatus.Unusable, identity.Status);
                Assert.True(identity.IsTimeout, $"attempt {attempt} should still report a plain timeout, not an escalated verdict.");
                Assert.False(identity.Cacheable, $"attempt {attempt} must not be cached: a single slow sample must not poison later calls.");
            }

            var third = ExternalTool.CheckIdentity("verapdf");
            Assert.Equal(ExternalTool.IdentityStatus.Unusable, third.Status);
            Assert.False(third.IsTimeout, "the 3rd consecutive timeout should escalate to a definitive verdict, not report as a plain timeout.");
            Assert.True(third.Cacheable, "the escalated verdict must be cached, or a CI runner whose oracle is stuck re-probes it once per test case instead of failing once.");
            Assert.Contains("3 times consecutively", third.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERAPDF_HOME", savedHome);
            Environment.SetEnvironmentVariable("PATH", savedPath);
            ExternalTool.VeraPdfProbeTimeoutMsOverrideForTests = -1;
            ExternalTool.ResetIdentityCacheForTests("verapdf");
            DeleteDirectoryWithRetry(dir.FullName);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }
}
