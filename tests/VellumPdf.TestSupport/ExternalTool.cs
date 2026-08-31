// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace VellumPdf.TestSupport;

/// <summary>
/// Runs an external oracle CLI — qpdf, pdftotext, pdftoppm, pdfsig, or veraPDF — and captures its
/// output. Consolidates six process runners duplicated across the tree (#198): five
/// near-identical copies across the Barcodes, Kernel and Layout test projects, plus a sixth of
/// its own shape, the conformance suite's own veraPDF runner, which now calls only into this type
/// too but never shared the defect below — it already drained with a bounded <c>Wait(5_000)</c>.
/// The five near-identical copies drained both pipes concurrently before <see
/// cref="Process.WaitForExit()"/>, which is what avoids a deadlock on a report larger than the OS
/// pipe buffer; their shared defect was in what happened next — each then drained with an
/// unbounded, unconditional <c>GetAwaiter().GetResult()</c> ahead of the branch that kills a
/// timed-out process, so a child that hung without closing its pipes hung the test host forever.
/// This type bounds that post-exit drain at 5 seconds instead. The second defect is that every
/// copy but <c>LinearizationQpdfTests</c> (which tried a hardcoded absolute path first) resolved
/// its executable by a bare name, which is not deterministic even on one machine: on this
/// repository's own dev machine, a bare "pdftotext" resolves to poppler 25.07.0 launched from a
/// PowerShell session but to Xpdf 4.00 (no <c>-tsv</c> flag) launched from a Git Bash one, purely
/// because the two shells put different installs first on PATH.
/// </summary>
public static class ExternalTool
{
    private static readonly string CmdExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// Attempts to run <paramref name="tool"/> with <paramref name="arguments"/>, resolving the
    /// executable as described on <see cref="ResolveLauncher"/> and, for the five known oracle
    /// tools, verifying its reported identity first (see <see cref="EnsureUsable(string)"/>). A
    /// resolution that turns out not to be usable is gated before any output reaches the caller,
    /// rather than handed back as if it came from the tool the caller asked for. <see
    /// cref="EnsureUsable(string)"/> only returns when the tool checks out, so for the five known
    /// tools this method never returns <see langword="false"/>; a caller-side <c>if
    /// (!TryRun(...))</c> guard for one of them is
    /// unreachable; <see langword="false"/> is only still live for a tool name this type has no
    /// identity probe for (the barcode oracle's "python", for instance), where it means what it
    /// always meant: the executable itself could not be found or started. A run that exits non-zero
    /// still returns <see langword="true"/> with the outcome in the <c>out</c> parameters, so the
    /// caller's own assertion, not this helper, decides whether that counts as a failure.
    /// <paramref name="timedOut"/> is the one outcome the caller cannot infer from the others: it
    /// is set when the process itself had to be killed after <paramref name="timeoutMs"/>, or when
    /// it exited but the bounded drain of its output could not finish within 5 seconds. The
    /// second case still reports a genuine <paramref name="exitCode"/>, but with
    /// <paramref name="stdout"/> or <paramref name="stderr"/> substituted with an empty string that
    /// a caller asserting the *absence* of something (an error string, a warning) would otherwise
    /// accept as if the tool had truly produced none.
    /// </summary>
    public static bool TryRun(
        string tool,
        IReadOnlyList<string> arguments,
        out int exitCode,
        out string stdout,
        out string stderr,
        out bool timedOut,
        int timeoutMs = 30_000,
        Encoding? outputEncoding = null)
    {
        EnsureUsable(tool);

        return RunProcess(tool, arguments, out exitCode, out stdout, out stderr, out timedOut, timeoutMs, outputEncoding);
    }

    /// <summary>
    /// Probes <paramref name="tool"/> with <see cref="CheckIdentity"/> and gates the caller on the
    /// result. Before this method existed, three call sites each re-derived the same two-line
    /// if/else over <see cref="IdentityStatus"/> and <see cref="IdentityResult.IsTimeout"/>: <see
    /// cref="TryRun"/>, the conformance suite's own <c>VeraPdf.EnsureAvailable</c>, and
    /// <c>ExternalToolResolutionTests.Resolves_ToTheClaimedTool</c>.
    /// The third one got it wrong, reading only <see cref="IdentityResult.Status"/>, so a probe that
    /// had merely run out of time was sent to the escalating <see
    /// cref="OracleGate.Unavailable(string, string)"/> instead of the always-skipping <see
    /// cref="OracleGate.Transient(string, string)"/>, exactly the build-breaking-on-a-slow-sample
    /// defect round 4's own fix to <see cref="TryRun"/> exists to prevent. Piecemeal copies were what
    /// produced that defect, so the fix is not a fourth copy of the branch but this one method every
    /// consumer calls (#198 review, round 5).
    /// </summary>
    public static void EnsureUsable(string tool) => EnsureUsable(CheckIdentity(tool));

    /// <summary>
    /// Gates the caller on an <paramref name="identity"/> already probed by <see
    /// cref="CheckIdentity"/>, rather than probing its tool again. This overload exists because
    /// <see cref="EnsureUsable(string)"/> used to be the only entry point, and a caller that had
    /// already called <see cref="CheckIdentity"/> itself (to assert on the verdict, say) had no way
    /// to hand that verdict in, so it called <see cref="EnsureUsable(string)"/> too, which probed a
    /// second time. For veraPDF, whose cold JVM start can take double digits of seconds (see <see
    /// cref="VeraPdfProbeTimeoutMs"/>'s own comment), the two probes can disagree: the first might
    /// time out while the second, launched moments later against an already-warm JVM, answers
    /// <c>Ok</c>. <c>ExternalToolResolutionTests.Resolves_ToTheClaimedTool</c> did exactly this,
    /// and when the two disagreed, its own gating branch's <c>return;</c> ran before the assertion
    /// below it, so xUnit recorded a passed test that had executed no assertion, the same failure
    /// mode #198 exists to catch, reproduced in the test meant to catch it (#198 review, round 6).
    /// This overload originally took the tool name as a second parameter alongside <paramref
    /// name="identity"/>, but nothing tied the two together — a caller could pass a verdict probed
    /// for one tool under another tool's name, and the mismatch would surface only in the resulting
    /// skip or failure message. <see cref="IdentityResult.Tool"/> now carries the name the verdict
    /// was actually probed for, so this overload reads it from there instead, which makes that
    /// mismatch unrepresentable (#198 review, round 7, finding 4). Returns normally, and is not
    /// itself <c>[DoesNotReturn]</c>, when <paramref name="identity"/> is <see
    /// cref="IdentityStatus.Ok"/> — which includes every tool this type has no identity probe for
    /// (see <see cref="ProbeIdentity"/>), since <see cref="CheckIdentity"/> always resolves those to
    /// <see cref="IdentityStatus.Ok"/>.
    /// </summary>
    public static void EnsureUsable(IdentityResult identity)
    {
        if (identity.Status == IdentityStatus.Ok)
            return;

        // A probe that timed out is not proof the tool is absent or wrong, only that this one
        // attempt did not finish, so it must not fail the build the way a confirmed-absent or
        // confirmed-wrong tool does; it always skips instead, unless CheckIdentity has already
        // escalated a run of consecutive timeouts to a definitive (non-timeout) verdict itself
        // (#198 review, round 4; the retry-then-escalate policy is round 5).
        if (identity.IsTimeout)
            OracleGate.Transient(identity.Tool, identity.Detail!);
        else
            OracleGate.Unavailable(identity.Tool, identity.Detail!);
    }

    private static bool RunProcess(
        string tool,
        IReadOnlyList<string> arguments,
        out int exitCode,
        out string stdout,
        out string stderr,
        out bool timedOut,
        int timeoutMs,
        Encoding? outputEncoding,
        bool logInvocation = true)
    {
        exitCode = -1;
        stdout = string.Empty;
        stderr = string.Empty;
        timedOut = false;

        var launcher = ResolveLauncher(tool);
        var psi = new ProcessStartInfo(launcher.File)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (outputEncoding is not null)
        {
            psi.StandardOutputEncoding = outputEncoding;
            psi.StandardErrorEncoding = outputEncoding;
        }

        if (launcher.ViaCmd)
        {
            // cmd.exe does not parse its command line the way CommandLineToArgvW does, so
            // routing its arguments through ArgumentList — which escapes for the latter — does
            // not protect against cmd's own metacharacters: an argument containing '&' starts a
            // second command, and VERAPDF_HOME containing a space plus a quoted argument that
            // also has a space makes cmd strip the outer quotes and break the line (both
            // reproduced). The standard workaround is the `cmd /c ""bat" "arg1" "arg2""` form,
            // built as a single pre-quoted string and passed via Arguments, which bypasses
            // .NET's own escaping entirely.
            //
            // This form is still not a full cmd-safe quoting implementation, and deliberately so:
            // an argument ending in a backslash merges into the next one's opening quote (cmd
            // reads the escaped `\"` as a literal quote, not an argument boundary), an argument
            // containing `"` breaks the batch file's own argument parsing (exit 255, reproduced),
            // and a `%VAR%` inside an argument is expanded by cmd before the batch file sees it.
            // None of veraPDF's own arguments (`--flavour`, a flavour name, `--format`, `text`, a
            // filesystem path) produce any of these shapes, so fixing this now would be defending
            // against an input this caller never sends; if a future caller needs to pass one of
            // these shapes to a `.bat`-launched tool, that quoting has to be built then.
            var quoted = launcher.Prefix.Concat(arguments).Select(a => $"\"{a}\"");
            psi.Arguments = $"/c \"{string.Join(' ', quoted)}\"";
        }
        else
        {
            foreach (var a in launcher.Prefix)
                psi.ArgumentList.Add(a);
            foreach (var a in arguments)
                psi.ArgumentList.Add(a);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // Not installed, or not resolvable on this machine.
            return false;
        }

        if (process is null)
            return false;

        // Logged only once a process has actually launched: a probe (see ProbeIdentityCore,
        // ProbePopplerOnlyFlag) passes logInvocation: false, and a launch that never started —
        // Process.Start returning null or throwing Win32Exception above — is not an oracle having
        // run, so it must not count as one either (#228 review).
        if (logInvocation)
            LogInvocation(tool, arguments);

        using (process)
        {
            // A tool that prompts (rather than reading canned input) should see EOF, not
            // whatever the test host's own stdin happens to be.
            process.StandardInput.Close();

            // Drain both pipes concurrently BEFORE waiting: a report larger than the OS pipe
            // buffer would otherwise block the child on write while this thread blocks in
            // WaitForExit, deadlocking both sides.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                if (!process.WaitForExit(timeoutMs))
                {
                    timedOut = true;
                    try
                    {
                        process.Kill(entireProcessTree: true);

                        // Kill(bool) asks the OS to tear the tree down and returns immediately:
                        // TerminateProcess itself is asynchronous. Without the wait below, a
                        // caller that acts on the killed process's files right after TryRun
                        // returns (deleting the temp directory a test pointed a tool's *_HOME at,
                        // say) can race a handle the dying process, or an unreaped grandchild of
                        // it, still holds open (reproduced: a cmd.exe /c-launched .bat with a
                        // still-running child under it). It's the WaitForExit(5_000) below that is
                        // bounded, not Kill itself: Kill(entireProcessTree: true) has no timeout
                        // of its own (measured: 45-80 ms for a 1-2 process tree, ~100 s for a
                        // pathological ~5000-process tree no real oracle here produces), so a hang
                        // inside Kill can still make this method run long; only the wait for the
                        // OS to finish tearing the tree down is capped, the way the pre-#198
                        // unbounded drain was not (#198 review, round 5, correcting a prior
                        // version of this comment that claimed the whole block was bounded).
                        process.WaitForExit(5_000);
                    }
                    catch
                    {
                        // Best effort: the process may have exited in the race between
                        // WaitForExit's false result and this call, or Kill(bool)/the WaitForExit
                        // above can themselves throw tearing down a process tree. Either way there
                        // is nothing more this method can do about it.
                    }

                    return true;
                }

                // The process has exited; bound the drain too, so a grandchild that inherited the
                // pipes and outlives the parent cannot make this call hang indefinitely waiting on
                // a read that will never finish. The 5-second budget below is shared between both
                // streams, not applied to each in turn — a stdout read that uses the whole budget
                // leaves none for stderr, rather than letting the pair cost up to 10 seconds.
                var drainDeadline = Environment.TickCount64 + 5_000;

                // A drain that hits this bound also counts as timedOut even though exitCode below
                // is a real, trustworthy value — otherwise a caller asserting the absence of a
                // string in stdout/stderr would pass on the substituted empty string. The
                // StreamReader for a stream still being drained is disposed on the way out
                // regardless (see the finally block): the pending read then faults instead of
                // hanging, which is what ObserveAndForget exists to swallow, but if a grandchild
                // still holds the pipe's write end open, the OS-level pipe itself outlives this
                // call — the hang the old, unbounded drain suffered from becomes a leaked handle
                // instead of a hang, not something this method can also close.
                //
                // Wait(int) itself can throw AggregateException when the awaited read faults
                // (rather than merely running past the budget) — a broken pipe, for instance. That
                // must not propagate out of RunProcess: a caller one level up is ProbeIdentity,
                // whose result gets wrapped in a Lazy<IdentityResult> that caches an exception
                // permanently once thrown, which would poison every later identity check for this
                // tool for the rest of the process (#198 review, round 4). Treating a faulted read
                // the same as an unfinished one keeps the contract simple: a caller cannot tell the
                // two apart from the out parameters, and does not need to.
                try
                {
                    if (stdoutTask.Wait(RemainingMs(drainDeadline)))
                        stdout = stdoutTask.Result;
                    else
                        timedOut = true;
                }
                catch (AggregateException)
                {
                    timedOut = true;
                }

                try
                {
                    if (stderrTask.Wait(RemainingMs(drainDeadline)))
                        stderr = stderrTask.Result;
                    else
                        timedOut = true;
                }
                catch (AggregateException)
                {
                    timedOut = true;
                }

                exitCode = process.ExitCode;
                return true;
            }
            finally
            {
                // Observed on every exit path above, including the two returns inside the try,
                // so a fault surfacing after this method returns cannot escape as an unobserved
                // task exception. The StreamReaders are disposed here too — leaving them open
                // leaked two handles per invocation (measured: +83 over 40 runs).
                ObserveAndForget(stdoutTask);
                ObserveAndForget(stderrTask);
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }
        }
    }

    private static int RemainingMs(long deadline) => (int)Math.Max(0, deadline - Environment.TickCount64);

    // ── Identity verification (#198 review: the resolution theory below gated only itself) ──

    /// <summary>Whether <see cref="CheckIdentity"/> confirmed a tool's identity or found a reason not to trust it.</summary>
    public enum IdentityStatus { Ok, Unusable }

    /// <summary>
    /// The outcome of <see cref="CheckIdentity"/>. <see cref="Detail"/> is <see langword="null"/>
    /// for <see cref="IdentityStatus.Ok"/> and non-null for <see cref="IdentityStatus.Unusable"/>.
    /// It names what was found: absent, timed out (or timed out too many times in a row; see <see
    /// cref="ConsecutiveTimeoutEscalationBound"/>), or resolved to something else. Where there is
    /// a fix, it names that too (#198 review, round 7, finding 6, correcting a round-6 rewrite
    /// that read the fix as a fourth list item). <see cref="Cacheable"/> and <see
    /// cref="IsTimeout"/> only matter for <see cref="IdentityStatus.Unusable"/>: a wrong banner,
    /// a non-zero version exit, an unresolvable <c>*_HOME</c>, or a missing poppler-only flag are
    /// definitive verdicts about the resolved executable and stay cached for the process; a probe
    /// that could not even start the executable, or that ran past its budget on the first or
    /// second consecutive attempt, proves nothing about the executable's identity, so <see
    /// cref="CheckIdentity"/> does not cache either. The next call gets a fresh attempt rather
    /// than inheriting one bad sample for the rest of the run. <see cref="IsTimeout"/>
    /// additionally skips <see cref="OracleGate.Unavailable(string, string)"/>'s escalation under
    /// CI in favour of <see
    /// cref="OracleGate.Transient(string, string)"/>, which always skips (see <see
    /// cref="EnsureUsable(IdentityResult)"/>): a start failure for one of the five known tools is
    /// still a real, actionable absence even though it is not worth caching (#198 review, round
    /// 4). That leniency is itself bounded: a probe that times out on <see
    /// cref="ConsecutiveTimeoutEscalationBound"/> consecutive attempts is no longer distinguishable
    /// from a tool that is genuinely wrong or absent, so <see cref="ProbeIdentity"/> converts that
    /// Nth verdict into a definitive, <see cref="Cacheable"/> <see cref="IdentityStatus.Unusable"/>
    /// with <see cref="IsTimeout"/> <see langword="false"/>. It escalates under CI exactly like a
    /// confirmed-wrong tool, and is cached so a CI runner whose oracle is genuinely stuck pays the
    /// probe budget a bounded number of times, not once per test case (#198 review, round 5; see
    /// that review round for the failure mode this closes: round 4's fix alone let a persistently
    /// slow oracle skip 273 times in a row rather than fail once). <see cref="Tool"/> names the
    /// tool this verdict was probed for; <see cref="CheckIdentity"/> stamps it on before returning,
    /// so a caller holding an <see cref="IdentityResult"/> already knows which tool it describes
    /// and <see cref="EnsureUsable(IdentityResult)"/> needs no separate name that could name a
    /// different tool than the one actually probed (#198 review, round 7, finding 4).
    /// </summary>
    public readonly record struct IdentityResult(IdentityStatus Status, string? Detail, bool Cacheable = true, bool IsTimeout = false, string Tool = "")
    {
        internal static readonly IdentityResult Ok = new(IdentityStatus.Ok, null);

        internal static IdentityResult Unusable(string detail) => new(IdentityStatus.Unusable, detail);

        /// <summary>
        /// A verdict that proves nothing about the tool's identity because the probe itself could
        /// not run to completion for a reason other than timing out (the executable would not even
        /// start, or a follow-up probe against an already-confirmed banner failed), so not cached,
        /// and, unlike <see cref="Transient"/>, does not carry <see cref="IsTimeout"/>, so <see
        /// cref="EnsureUsable(IdentityResult)"/> still routes it to the escalating <see
        /// cref="OracleGate.Unavailable(string, string)"/>. Named for what it means (round 5's
        /// reviewer suggestion) rather than reusing <see cref="Transient"/> for both cases: a
        /// prior version of this type had one factory covering both, parameterised by a bool
        /// default of <see langword="false"/> for the non-timeout case, which is exactly the shape
        /// that let a probe-result routing site collapse the distinction and misroute a timeout
        /// (#198 review, round 5, finding 1).
        /// </summary>
        internal static IdentityResult NotCacheable(string detail) => new(IdentityStatus.Unusable, detail, Cacheable: false);

        /// <summary>
        /// A verdict for a probe that ran past its budget: not cached, and <see
        /// cref="IsTimeout"/> so <see cref="EnsureUsable(IdentityResult)"/> routes it to <see
        /// cref="OracleGate.Transient(string, string)"/> instead of the escalating <see
        /// cref="OracleGate.Unavailable(string, string)"/>. Only for the timeout case; see <see
        /// cref="NotCacheable"/> for a probe that failed to complete for some other reason.
        /// </summary>
        internal static IdentityResult Transient(string detail) => new(IdentityStatus.Unusable, detail, Cacheable: false, IsTimeout: true);
    }

    private sealed record IdentityProbe(string[] VersionArgs, string ExpectedBannerPrefix, string HomeVariable);

    // veraPDF is a JVM process launched fresh for every probe, so its startup cost dwarfs the
    // other four tools' — all native executables with no comparable cost — even before CI adds a
    // container on top (ci.yml installs a `/bin/sh` shim around `docker run verapdf/cli`; the
    // Windows `.bat` launcher below is a separate, developer-machine path that ResolveBatViaHome
    // only takes under OperatingSystem.IsWindows() and CI does not use). The old, shared
    // 10-second budget read a slow-but-fine start as "resolved to something that exited -1", i.e.
    // the wrong tool, against a verapdf.bat that took 14 seconds to answer on one cold run; three
    // warm runs of the same .bat on this machine landed between 0.7 and 1.6 seconds, wide enough
    // swings either side of 10 seconds that only a dedicated, more generous budget for this one
    // tool is reliable.
    private const int DefaultProbeTimeoutMs = 10_000;
    private const int VeraPdfProbeTimeoutMs = 30_000;

    // Test-only seam (#198 review, round 5): VellumPdf.TestSupport.Tests needs to force several
    // consecutive veraPDF timeouts to exercise ConsecutiveTimeoutEscalationBound without paying
    // VeraPdfProbeTimeoutMs (30 s, real wall-clock) on every one of them. Three real timeouts
    // would add roughly another 90 seconds to the suite on top of the ~30 s
    // CheckIdentity_ForVerapdf_ReportsUnusableNotWrong_WhenTheProbeTimesOut already costs. Left at
    // its default (-1, meaning "use VeraPdfProbeTimeoutMs") by every test but the retry-bound one,
    // which resets it in a `finally` block; this is process-wide static state, same as
    // IdentityCache. Does not touch VeraPdfProbeTimeoutMs itself, so the existing 30-second
    // regression test, which asserts the literal "30000 ms" specifically to catch that budget
    // regressing away (#198 review, round 4), is unaffected by this override existing at all.
    internal static int VeraPdfProbeTimeoutMsOverrideForTests { get; set; } = -1;

    private static int EffectiveVeraPdfProbeTimeoutMs
        => VeraPdfProbeTimeoutMsOverrideForTests > 0 ? VeraPdfProbeTimeoutMsOverrideForTests : VeraPdfProbeTimeoutMs;

    // A single slow sample must still just skip (see the non-timeout reset in ProbeIdentity below),
    // but a probe that times out on every attempt is indistinguishable from a tool that is truly
    // wrong or absent, and re-probing it once per test case pays the full probe budget every time:
    // the veraPDF cross-validation collection alone is 273 cases (VeraPdfOracleTests, documented in
    // OracleTests.cs), each gated through VeraPdf.EnsureAvailable, so a CI runner whose veraPDF
    // stays slow would otherwise re-probe (and skip) 273 times instead of failing once. 3 is the
    // smallest bound that still tolerates one or two merely-unlucky samples before treating a run
    // of timeouts as a definitive verdict (#198 review, round 5). This counts consecutive probes,
    // not test cases or calls: CheckIdentity's Lazy<IdentityResult> (see IdentityCache below)
    // collapses every caller racing for the same tool's verdict into one probe, so many test cases
    // sharing one slow tool still only advance this count by one per attempt (#198 review, round 6).
    private const int ConsecutiveTimeoutEscalationBound = 3;

    private static readonly ConcurrentDictionary<string, Lazy<IdentityResult>> IdentityCache = new();

    // Counts consecutive timed-out probes per tool, independently of IdentityCache: a Transient
    // verdict is evicted from IdentityCache the moment CheckIdentity reads it (see below), so this
    // is the only place that streak is remembered between calls. A plain ConcurrentDictionary<string,
    // int> (rather than IdentityCache's Lazy<T> wrapper) is enough here: not because AddOrUpdate and
    // TryRemove are individually atomic per key (two atomic operations do not make an atomic pair),
    // but because CheckIdentity's own Lazy<IdentityResult> serialises every probe for one tool, so
    // nothing else can be inside ProbeIdentity for that tool between the AddOrUpdate below and a
    // later TryRemove — while IdentityCache keeps exactly one Lazy per tool alive for the probe's
    // duration (#198 review, round 6, correcting the per-key-atomicity claim this comment made
    // before; round 7, finding 8, correcting this comment's own overreach: the Lazy alone is not
    // what keeps that one-per-tool property, CheckIdentity evicting only after lazy.Value has
    // already returned is). ResetIdentityCacheForTests is the exception: it can remove a tool's
    // entry while another thread's lazy.Value call is still mid-probe, so a CheckIdentity call
    // racing that reset can start a second, concurrent probe for the same tool through a fresh
    // Lazy (reproduced: two 10-second probes overlapping through it finish in roughly 13, not 20,
    // seconds). Not reachable in this tree today — the method is internal, and every caller sits in
    // the same DisableParallelization=true collection as the probes it resets — but the exception
    // is a real gap in this type's own guarantee, not a hypothetical one. Unlike ProbeIdentity,
    // this dictionary has no expensive work of its own to deduplicate across racing callers, so it
    // needs none of Lazy<T>'s own guarantee.
    private static readonly ConcurrentDictionary<string, int> ConsecutiveTimeouts = new();

    /// <summary>
    /// Verifies that <paramref name="tool"/> resolves to what it claims to be, caching a
    /// definitive verdict for the lifetime of the process — each of the five known oracle tools is
    /// probed with its own version flag at most once per test run in the common case, the pattern
    /// the conformance suite's own <c>VeraPdf.IsAvailable</c> uses for a single tool. <see
    /// cref="Lazy{T}"/> as the cached value (rather than a plain
    /// <c>ConcurrentDictionary&lt;string, IdentityResult&gt;</c>) keeps that "at most once" true
    /// under xUnit's default parallel test execution, where two tests can both reach for "qpdf"
    /// before either has cached a result. A verdict that is not <see
    /// cref="IdentityResult.Cacheable"/> (see <see cref="ProbeIdentity"/>) is evicted right after
    /// this call reads it, so the next caller re-probes instead of reusing it. One exception:
    /// once a run of consecutive timeouts reaches <see cref="ConsecutiveTimeoutEscalationBound"/>,
    /// <see cref="ProbeIdentity"/> hands back a verdict that <em>is</em> cacheable, precisely so a
    /// persistently stuck oracle stops paying the probe budget on every later call instead of
    /// re-probing (and re-timing-out) once per test case (#198 review, round 5). An unrecognized
    /// tool name (the barcode oracle's "python", for instance) has no version probe and always
    /// resolves <see cref="IdentityStatus.Ok"/> — there is nothing this type knows how to check.
    /// Public so <c>ExternalToolResolutionTests</c> can exercise the probe directly instead of
    /// re-deriving its own copy of the banner and flag checks. Stamps <see
    /// cref="IdentityResult.Tool"/> with <paramref name="tool"/> before returning, so the verdict
    /// carries its own tool name and <see cref="EnsureUsable(IdentityResult)"/> cannot be pointed
    /// at the wrong one (#198 review, round 7, finding 4).
    /// </summary>
    public static IdentityResult CheckIdentity(string tool)
    {
        var lazy = IdentityCache.GetOrAdd(tool, static t => new Lazy<IdentityResult>(() => ProbeIdentity(t)));
        IdentityResult result;
        try
        {
            result = lazy.Value;
        }
        catch
        {
            // Lazy<T>'s default thread-safety mode caches a thrown exception permanently, which
            // would otherwise poison every later call for this tool for the rest of the process.
            // RunProcess is expected not to throw (see its own drain-fault handling), but removing
            // this exact instance here is defence-in-depth in case some other path still does.
            IdentityCache.TryRemove(new KeyValuePair<string, Lazy<IdentityResult>>(tool, lazy));
            throw;
        }

        if (!result.Cacheable)
            IdentityCache.TryRemove(new KeyValuePair<string, Lazy<IdentityResult>>(tool, lazy));

        // ProbeIdentity below never sets IdentityResult.Tool itself — it is reached through the
        // Lazy<IdentityResult> keyed by "tool" here, so this call site is the one place that
        // already knows, for certain, which tool the cached verdict belongs to.
        return result with { Tool = tool };
    }

    /// <summary>
    /// Clears any cached identity verdict for <paramref name="tool"/>, and its consecutive-timeout
    /// streak (see <see cref="ConsecutiveTimeouts"/>), so the next <see cref="CheckIdentity"/> call
    /// re-probes it from scratch as though it were the first ever call. Neither <see
    /// cref="IdentityCache"/> nor <see cref="ConsecutiveTimeouts"/> has another reset path, since
    /// both are meant to live for the process, but a test that deliberately points a known tool's
    /// <c>*_HOME</c> (or <c>PATH</c>) at a fixture it controls, the way
    /// <c>ExternalToolTests.CheckIdentity_ForVerapdf_ReportsUnusableNotWrong_WhenTheProbeTimesOut</c>
    /// does, needs to guarantee it is not reading a verdict, or inheriting a timeout streak, a
    /// different call left behind: nothing else in this project resolves "verapdf" today, but
    /// nothing before this method enforced that it stays that way (#198 review, round 4). Clearing
    /// the streak too (round 5) is what keeps that same regression test deterministic regardless of
    /// what ran before it: without it, a prior test's timeouts could carry this test's own single
    /// timeout past <see cref="ConsecutiveTimeoutEscalationBound"/> and flip its expected <see
    /// cref="IdentityResult.IsTimeout"/> Transient verdict into an escalated one. Internal, not
    /// private — <c>ExternalToolTests</c> is the only intended caller (see the internal test-only
    /// <c>InternalsVisibleTo</c> grant).
    /// </summary>
    internal static void ResetIdentityCacheForTests(string tool)
    {
        IdentityCache.TryRemove(tool, out _);
        ConsecutiveTimeouts.TryRemove(tool, out _);
    }

    /// <summary>
    /// Runs <see cref="ProbeIdentityCore"/> and applies the consecutive-timeout retry-then-escalate
    /// policy on top of it (#198 review, round 5): any outcome other than a timeout resets <see
    /// cref="ConsecutiveTimeouts"/> for <paramref name="tool"/> back to zero (a tool that answers,
    /// even with the wrong banner, is not accumulating evidence against itself). A timeout instead
    /// increments it, and once the count reaches <see cref="ConsecutiveTimeoutEscalationBound"/>
    /// the timeout verdict is replaced with a definitive, cacheable one so <see
    /// cref="EnsureUsable(IdentityResult)"/> escalates it under CI instead of skipping it forever.
    /// </summary>
    private static IdentityResult ProbeIdentity(string tool)
    {
        var result = ProbeIdentityCore(tool);
        if (!result.IsTimeout)
        {
            ConsecutiveTimeouts.TryRemove(tool, out _);
            return result;
        }

        var consecutive = ConsecutiveTimeouts.AddOrUpdate(tool, 1, static (_, count) => count + 1);
        if (consecutive < ConsecutiveTimeoutEscalationBound)
            return result;

        // The Nth consecutive timeout in a row: no longer worth treating as one unlucky sample. The
        // verdict returned below is Cacheable, so IdentityCache keeps it and CheckIdentity never
        // calls ProbeIdentity for this tool again for the rest of the process — barring a later
        // ResetIdentityCacheForTests call, which the escalation-bound regression test calls in its
        // own finally block precisely so this verdict does not outlive that one test (#198 review,
        // round 7, finding 8). There is no other recovery for this streak to resume counting from,
        // unlike a Transient verdict, which is evicted every time it is read. Clearing the streak
        // here just returns ConsecutiveTimeouts to the same "nothing recorded" state CheckIdentity
        // found it in before the first attempt (#198 review, round 6, correcting a prior version
        // of this comment that described a recovery path the Cacheable flag on this exact verdict
        // rules out).
        ConsecutiveTimeouts.TryRemove(tool, out _);
        return IdentityResult.Unusable(
            $"timed out {consecutive} times consecutively probing its identity, last attempt: {result.Detail}");
    }

    private static IdentityResult ProbeIdentityCore(string tool)
    {
        var probe = tool switch
        {
            "qpdf" => new IdentityProbe(["--version"], "qpdf version ", "QPDF_HOME"),
            "pdftotext" => new IdentityProbe(["-v"], "pdftotext version ", "POPPLER_HOME"),
            "pdftoppm" => new IdentityProbe(["-v"], "pdftoppm version ", "POPPLER_HOME"),
            "pdfsig" => new IdentityProbe(["-v"], "pdfsig version ", "POPPLER_HOME"),
            "verapdf" => new IdentityProbe(["--version"], "veraPDF ", "VERAPDF_HOME"),
            _ => null,
        };
        if (probe is null)
            return IdentityResult.Ok;

        // A *_HOME that is set but does not resolve to a real file is a misconfiguration, not an
        // "unset" signal — falling back to the bare name here would go straight back to the PATH
        // ambiguity this whole mechanism exists to remove (reproduced: POPPLER_HOME pointed at
        // the poppler install root, whose executables actually nest under Library\bin on
        // Windows, silently fell back to Xpdf on PATH).
        var launcher = ResolveLauncher(tool);
        if (launcher.UnresolvedHomeDetail is { } detail)
            return IdentityResult.Unusable(detail);

        var probeTimeoutMs = tool == "verapdf" ? EffectiveVeraPdfProbeTimeoutMs : DefaultProbeTimeoutMs;

        // A process that would not even start is not evidence of the tool's identity either — it
        // says nothing about what would happen on a retry (a transient lock, a momentarily
        // exhausted process table) — so this is not cached (see CheckIdentity). Not a timeout, so
        // it does not count toward ConsecutiveTimeouts either (see ProbeIdentity).
        if (!RunProcess(tool, probe.VersionArgs, out var exit, out var stdout, out var stderr, out var timedOut, probeTimeoutMs, null, logInvocation: false))
            return IdentityResult.NotCacheable("not installed, or not resolvable on this machine");

        // A timed-out probe is not evidence of anything about the tool's identity — exitCode above
        // is left at its RunProcess-initial -1 whether the process was killed for running past
        // probeTimeoutMs or genuinely exited with -1, and the two must not be conflated into a
        // false "wrong tool" diagnosis (#198 review: reproduced against a verapdf.bat answering
        // after 14 seconds against the old 10-second budget shared by every tool). Transient, not
        // Unusable: this is not cached, and EnsureUsable routes it to OracleGate.Transient rather
        // than OracleGate.Unavailable so one slow sample cannot fail the build the way a
        // confirmed-wrong tool does (#198 review, round 4). The one exception is when ProbeIdentity
        // finds this is the Nth one of these in a row, in which case it escalates the result before
        // returning it to the caller (#198 review, round 5).
        if (timedOut)
        {
            return IdentityResult.Transient(
                $"'{tool} {string.Join(' ', probe.VersionArgs)}' did not finish within {probeTimeoutMs} ms "
                + $"— set {probe.HomeVariable} to a real {tool} install, or its start-up is slower than that");
        }

        var banner = stdout + stderr;

        // The version banner alone does not discriminate poppler from Xpdf: Xpdf 4.00's own
        // pdftotext answers '-v' with "pdftotext version 4.00", which contains the same
        // "pdftotext version " prefix poppler's does (measured) — exactly why the -tsv/-png
        // follow-up probe below exists, rather than stopping here. Xpdf's pdftotext does exit
        // non-zero (99, measured) on '-v' where poppler's exits 0, so a non-zero version-probe
        // exit is itself evidence of the wrong tool even before the banner is inspected; a
        // matching exit code and banner are not evidence of the right one.
        if (exit != 0)
        {
            return IdentityResult.Unusable(
                $"resolved to something that exited {exit} on '{string.Join(' ', probe.VersionArgs)}' "
                + $"(first line: '{FirstLine(banner)}') — set {probe.HomeVariable} to a real {tool} install");
        }

        if (!banner.Contains(probe.ExpectedBannerPrefix, StringComparison.Ordinal))
        {
            return IdentityResult.Unusable(
                $"resolved to something other than {tool} (first line: '{FirstLine(banner)}') — "
                + $"set {probe.HomeVariable} to a real {tool} install");
        }

        return tool switch
        {
            "pdftotext" => ProbePopplerOnlyFlag(tool, "-tsv", probe.HomeVariable, banner),
            "pdftoppm" => ProbePopplerOnlyFlag(tool, "-png", probe.HomeVariable, banner),
            _ => IdentityResult.Ok,
        };
    }

    // -tsv (pdftotext) and -png (pdftoppm) are each the one flag poppler's -h lists that Xpdf's
    // does not — the only way to tell the two apart for a tool whose version banner and exit
    // code are otherwise indistinguishable between the two codebases.
    private static IdentityResult ProbePopplerOnlyFlag(string tool, string popplerOnlyFlag, string homeVariable, string banner)
    {
        if (!RunProcess(tool, ["-h"], out _, out var helpOut, out var helpErr, out var timedOut, DefaultProbeTimeoutMs, null, logInvocation: false))
            return IdentityResult.NotCacheable($"resolved for its version flag but not for '-h' — inconsistent {tool} install; check {homeVariable}");

        if (timedOut)
        {
            return IdentityResult.Transient(
                $"'{tool} -h' did not finish within {DefaultProbeTimeoutMs} ms — set {homeVariable} to a real {tool} install");
        }

        var helpText = helpOut + helpErr;
        if (!helpText.Contains(popplerOnlyFlag, StringComparison.Ordinal))
        {
            return IdentityResult.Unusable(
                $"resolved to '{FirstLine(banner)}', which is Xpdf, not poppler (no {popplerOnlyFlag} "
                + $"support) — set {homeVariable} to a poppler install");
        }

        return IdentityResult.Ok;
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first.TrimEnd('\r') : text;

    // ── Launcher resolution ──────────────────────────────────────────────────────────────────

    private readonly record struct Launcher(string File, string[] Prefix, bool ViaCmd, string? UnresolvedHomeDetail);

    /// <summary>
    /// Resolves the launcher for a known oracle tool. qpdf, pdftotext, pdftoppm and pdfsig are
    /// always resolved through an explicit <c>*_HOME</c> environment variable first — never a bare
    /// name as the primary answer, because PATH order alone already picks the wrong
    /// <c>pdftotext</c> depending on which shell launched the test host (see the type doc).
    /// veraPDF only gets that same <c>*_HOME</c>-first treatment on Windows, where it needs one to
    /// find its <c>.bat</c> launcher at all (see below); on every other platform, including CI's
    /// ubuntu-latest runner, <c>VERAPDF_HOME</c> is not read here and veraPDF resolves by bare name
    /// only, same as an unset <c>*_HOME</c> would for the other four. The bare name remains the
    /// fallback when the variable is unset (or not read), which is what a package-manager install
    /// already on PATH (CI's case, for all five) needs; when the variable is set but does not
    /// resolve, <see cref="Launcher.UnresolvedHomeDetail"/> is populated instead of silently
    /// falling back (see <see cref="ProbeIdentity"/>).
    ///
    /// <c>CreateProcess</c> with <c>UseShellExecute=false</c> only auto-appends ".exe" to a bare
    /// name; veraPDF's Windows installer instead ships a ".bat" launcher, which needs the command
    /// interpreter to run at all, so once resolved it is routed through <c>cmd.exe</c> (anchored
    /// to <see cref="Environment.SystemDirectory"/> rather than resolved bare, since a PATH
    /// without System32 on it would otherwise make a present veraPDF report as unavailable). The
    /// other four ship a ".exe" the runtime can invoke directly.
    /// </summary>
    private static Launcher ResolveLauncher(string tool) => tool switch
    {
        "verapdf" => ResolveBatViaHome("VERAPDF_HOME", "verapdf.bat", fallback: "verapdf"),
        "qpdf" => ResolveExeViaHome("QPDF_HOME", "qpdf"),
        "pdftotext" or "pdftoppm" or "pdfsig" => ResolveExeViaHome("POPPLER_HOME", tool),

        // Deliberately just a bare name for anything else, with no identity probe over it in
        // ProbeIdentity. The barcode decode oracle's "python" is the one caller that reaches this
        // arm today (OracleGate.IsBarcodeDecodeDependency separately names "python" and
        // "zxing-cpp" as its known dependencies, a second place the same five-ish tool names are
        // enumerated that this arm does not attempt to reconcile with). Widening the probe set to
        // cover it is out of scope here (#198 review, round 5).
        _ => new Launcher(tool, [], false, null),
    };

    // "*_HOME" is ambiguous between "the directory holding the executable" and "the install
    // root" — qpdf's own MSVC build ships as <root>/bin/qpdf.exe, so a QPDF_HOME set to <root>
    // needs the "bin" probe, not just the direct one. A Windows poppler install adds a third
    // shape: the winget package (oschwartz10612.Poppler) nests its binaries under
    // <root>/Library/bin, the conda-forge packaging convention. All three are accepted rather
    // than picking one and leaving the others to fall through to the ambiguous bare-name lookup
    // this whole mechanism exists to avoid.
    private static Launcher ResolveExeViaHome(string homeVariable, string toolName)
    {
        if (Environment.GetEnvironmentVariable(homeVariable) is { Length: > 0 } home)
        {
            var exeName = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
            var candidates = new[]
            {
                Path.Combine(home, exeName),
                Path.Combine(home, "bin", exeName),
                Path.Combine(home, "Library", "bin", exeName),
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return new Launcher(candidate, [], false, null);
            }

            // Listing all three shapes verbatim is only useful when each is actually plausible:
            // when *_HOME already IS the bin directory (e.g. set to "...\Library\bin"), the "bin"
            // and "Library\bin" shapes above nest a second copy of that segment under a directory
            // that was never going to exist ("...\Library\bin\bin\...",
            // "...\Library\bin\Library\bin\..."), which reads as three missing files instead of
            // naming the one real candidate that just doesn't have this exe in it. Restricting the
            // message to candidates whose parent directory exists keeps the impossible shapes out
            // of it; if none of the three parents exist (home itself is wrong), all three are
            // listed since none is more plausible than another.
            var plausible = candidates.Where(c => Directory.Exists(Path.GetDirectoryName(c))).ToArray();
            var listed = plausible.Length > 0 ? plausible : candidates;

            return new Launcher(toolName, [], false,
                $"{homeVariable} is set to '{home}' but none of these exist: "
                + string.Join(", ", listed.Select(c => $"'{c}'")));
        }

        return new Launcher(toolName, [], false, null);
    }

    private static Launcher ResolveBatViaHome(string homeVariable, string batName, string fallback)
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable(homeVariable) is { Length: > 0 } home)
        {
            var bat = Path.Combine(home, batName);
            if (File.Exists(bat))
                return new Launcher(CmdExePath, [bat], ViaCmd: true, null);

            return new Launcher(fallback, [], false, $"{homeVariable} is set to '{home}' but '{bat}' does not exist");
        }

        return new Launcher(fallback, [], false, null);
    }

    private static void ObserveAndForget(Task task)
        => _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    // ── Invocation logging (#228) ────────────────────────────────────────────────────────────

    // #227 made a missing oracle tool fail loudly instead of passing vacuously, but it did
    // nothing for a tool that is present and installed yet never actually gets called — a
    // disabled filter, a gate condition that stopped matching, a refactor that quietly drops the
    // call. That case is invisible from the test report: pass/skip counts are identical whether
    // veraPDF validated 273 documents or zero, and only wall-clock time gives it away (#228).
    // Counting invocations closes that gap, but the count has to be assembled workflow-side, not
    // in-process: CI runs each test assembly as its own process, and there is no ordering
    // guarantee that would let an in-process "assert the count on the last test" work. So this
    // just appends one line per call and leaves the counting and the floor to ci.yml.
    //
    // Only a call that actually launched a process is logged (see the logInvocation: false
    // arguments from ProbeIdentityCore and ProbePopplerOnlyFlag, and the log call's position
    // after RunProcess's own "did the process start" checks). Identity probes run once per tool
    // per process and a failed launch never touched the oracle at all, so counting either would
    // let a tool that stopped running mask the loss behind probe traffic that has nothing to do
    // with whether the oracle validated anything (#228 review).
    //
    // ORACLE_INVOCATION_LOG is unset on every local run, and reading it once into a static field
    // that is null in that case keeps this a strict no-op off CI: nothing below the null check
    // ever touches the filesystem.
    private static readonly string? InvocationLogPath = ResolveInvocationLogPath();

    // RunProcess can run concurrently on multiple threads within one process — xunit
    // parallelizes test collections by default, the same reason CheckIdentity's own
    // IdentityCache exists — so appends need serializing even though the log path is already
    // unique per OS process.
    private static readonly Lock InvocationLogLock = new();

    private static string? ResolveInvocationLogPath()
    {
        var basePath = Environment.GetEnvironmentVariable("ORACLE_INVOCATION_LOG");
        if (string.IsNullOrEmpty(basePath))
            return null;

        // Suffixed with the process id, not shared, because CI runs each test assembly as its
        // own process (Barcodes.Tests, Kernel.Tests, Layout.Tests, ...): File.AppendAllText's
        // internal lock only serializes writers within one process, so several processes
        // appending to one shared path would still interleave or truncate each other's writes.
        var path = $"{basePath}.{Environment.ProcessId}";

        // Create the parent directory and prove the path is actually writable once, here, at
        // type-init — not on the first real LogInvocation call, which is reached from deep
        // inside RunProcess on whatever oracle test happens to run first. A misconfigured
        // ORACLE_INVOCATION_LOG (a parent directory that doesn't exist, say) previously surfaced
        // as a bare DirectoryNotFoundException out of that unrelated test, and every other test
        // sharing this process failed the same way once InvocationLogPath's initializer had
        // already thrown (#228 review). Failing loudly here instead, with a message that names
        // the variable, turns that into one diagnosable error instead of a wall of unrelated
        // failures.
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(path, string.Empty);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ORACLE_INVOCATION_LOG is set to '{basePath}', but the invocation log at "
                + $"'{path}' could not be created or written: {ex.Message}", ex);
        }

        return path;
    }

    private static void LogInvocation(string tool, IReadOnlyList<string> arguments)
    {
        if (InvocationLogPath is null)
            return;

        var firstArgument = arguments.Count > 0 ? arguments[0] : string.Empty;
        var line = $"{tool}\t{firstArgument}{Environment.NewLine}";
        lock (InvocationLogLock)
        {
            File.AppendAllText(InvocationLogPath, line);
        }
    }
}
