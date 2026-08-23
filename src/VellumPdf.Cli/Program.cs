// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.Cli;
using VellumPdf.Conformance;
using VellumPdf.Conformance.Coverage;

// Main entry point — delegate to Run for testability.
return PreflightRunner.Run(args, Console.Out, Console.Error, null);

internal static class PreflightRunner
{
    // Version string resolved at startup from assembly metadata (AOT-safe: attributes are preserved).
    private static readonly string ToolVersion = GetVersion();

    private static string GetVersion()
    {
        var asm = typeof(Program).Assembly;
        var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr?.InformationalVersion is { Length: > 0 } v)
            return v;
        var name = asm.GetName();
        return name.Version?.ToString() ?? "0.0.0";
    }

    // The testable entry point. Returns an exit code (0/1/2).
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, Stream? stdin)
    {
        var parsed = new ParsedArgs();
        var parseErr = ArgParser.TryParse(args, parsed);

        // Honor NO_COLOR environment variable.
        var noColor = parsed.NoColor
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        // Color is enabled only when output goes to an interactive terminal (not a file or pipe),
        // NO_COLOR / --no-color is absent, and no -o/--output file was requested. In tests the
        // stdout is a StringWriter and Console.IsOutputRedirected is true, so color stays off.
        var useColor = !noColor && parsed.OutputPath is null && !Console.IsOutputRedirected;

        if (parseErr is not null)
        {
            stderr.WriteLine($"error: {parseErr}");
            stderr.WriteLine("Run 'vellum-preflight --help' for usage.");
            return ExitCodes.UsageError;
        }

        if (parsed.Help)
        {
            stdout.WriteLine(HelpText.Text);
            return ExitCodes.Ok;
        }

        if (parsed.Version)
        {
            stdout.WriteLine($"vellum-preflight {ToolVersion}");
            return ExitCodes.Ok;
        }

        if (parsed.ListProfiles)
        {
            stdout.WriteLine("Available profiles:");
            stdout.WriteLine("  2b   PDF/A-2 Level B");
            stdout.WriteLine("  2u   PDF/A-2 Level U");
            stdout.WriteLine("  2a   PDF/A-2 Level A");
            stdout.WriteLine("  ua1  PDF/UA-1");
            return ExitCodes.Ok;
        }

        if (parsed.Coverage)
        {
            Formatter.WriteCoverage(stdout, parsed.CoverageProfile, useColor);
            return ExitCodes.Ok;
        }

        if (parsed.Inputs.Count == 0)
        {
            stderr.WriteLine("error: no input files specified.");
            stderr.WriteLine("Run 'vellum-preflight --help' for usage.");
            return ExitCodes.UsageError;
        }

        // Expand inputs to resolved (filePath, profileSource) pairs.
        var filePaths = new List<string>();
        foreach (var input in parsed.Inputs)
        {
            if (input == "-")
            {
                filePaths.Add("-");
                continue;
            }

            var expanded = ExpandInput(input, parsed.Recurse);
            if (expanded.Count == 0)
            {
                stderr.WriteLine($"error: no files matched '{input}'.");
                return ExitCodes.UsageError;
            }
            filePaths.AddRange(expanded);
        }

        // Open output writer.
        TextWriter output = stdout;
        FileStream? outputFile = null;
        try
        {
            if (parsed.OutputPath is not null)
            {
                outputFile = new FileStream(parsed.OutputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                output = new StreamWriter(outputFile);
            }

            var allReports = new List<RunReport>();
            var anyIoError = false;

            foreach (var filePath in filePaths)
            {
                byte[] bytes;
                try
                {
                    bytes = LoadFile(filePath, stdin);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    stderr.WriteLine($"error: cannot read '{filePath}': {ex.Message}");
                    anyIoError = true;
                    continue;
                }

                // Resolve profiles for this file.
                IReadOnlyList<PdfConformance> profiles;
                string profileSource;
                try
                {
                    (profiles, profileSource) = ResolveProfiles(parsed, bytes, filePath, stderr);
                }
                catch (AutoNoClaim ex)
                {
                    stderr.WriteLine($"error: {ex.Message}");
                    return ExitCodes.UsageError;
                }

                if (profiles.Count == 0)
                {
                    anyIoError = true;
                    continue;
                }

                foreach (var profile in profiles)
                {
                    PreflightResult result;
                    try
                    {
                        result = PdfPreflight.Validate(bytes, profile);
                    }
                    catch (System.IO.InvalidDataException ex)
                    {
                        stderr.WriteLine($"error: '{filePath}' is not a valid PDF: {ex.Message}");
                        anyIoError = true;
                        continue;
                    }
                    // Also catches UnsupportedPdfFeatureException, which derives from
                    // NotSupportedException — so an unsupported security handler or crypt filter is
                    // reported as an error line rather than crashing, despite Validate documenting it
                    // as propagating.
                    catch (NotSupportedException ex)
                    {
                        stderr.WriteLine($"error: {ex.Message}");
                        anyIoError = true;
                        continue;
                    }
                    // A separate catch, not folded into the one above: PdfPasswordException does NOT
                    // derive from NotSupportedException on purpose (see its own doc comment) — a
                    // password-protected file is not an unsupported feature, and conflating the two
                    // here would mislabel it as one, undoing that distinction at the one place it
                    // would otherwise show up.
                    catch (VellumPdf.Reader.PdfPasswordException)
                    {
                        stderr.WriteLine(
                            $"error: '{filePath}' is password-protected; vellum-preflight has no way " +
                            "to supply a password yet.");
                        anyIoError = true;
                        continue;
                    }

                    var report = BuildReport(filePath, profile, profileSource, result, parsed);
                    allReports.Add(report);
                }
            }

            if (anyIoError && allReports.Count == 0)
                return ExitCodes.UsageError;

            // Write output.
            switch (parsed.Format)
            {
                case OutputFormat.Json:
                    Formatter.WriteJson(output, ToolVersion, allReports);
                    break;
                case OutputFormat.Sarif:
                    Formatter.WriteSarif(output, ToolVersion, allReports);
                    break;
                default:
                    if (!parsed.Quiet)
                        Formatter.WriteText(output, ToolVersion, allReports, parsed.Verbose, useColor, parsed.Severity);
                    break;
            }

            if (output is StreamWriter sw2)
                sw2.Flush();

            // Exit code: 1 if any report is non-conformant at the fail-on threshold.
            if (anyIoError)
                return ExitCodes.UsageError;

            foreach (var r in allReports)
            {
                if (!r.Conformant)
                    return ExitCodes.NonConformant;
            }

            return ExitCodes.Ok;
        }
        finally
        {
            outputFile?.Dispose();
        }
    }

    private sealed class AutoNoClaim(string message) : Exception(message);

    private static (IReadOnlyList<PdfConformance> Profiles, string Source) ResolveProfiles(
        ParsedArgs parsed,
        byte[] bytes,
        string filePath,
        TextWriter stderr)
    {
        if (parsed.ProfileAll)
        {
            return (
                new[] { PdfConformance.PdfA2B, PdfConformance.PdfA2U, PdfConformance.PdfA2A, PdfConformance.PdfUA1 },
                "all"
            );
        }

        if (parsed.Profiles.Count > 0 && !parsed.ProfileAuto)
        {
            return (parsed.Profiles, "explicit");
        }

        // auto (possibly combined with explicit)
        IReadOnlyList<PdfConformance> claimed;
        try
        {
            claimed = PdfPreflight.DetectClaimedProfiles(bytes);
        }
        catch (System.IO.InvalidDataException)
        {
            // Not a valid PDF — report as an IO error upstream.
            return (Array.Empty<PdfConformance>(), "auto");
        }

        if (claimed.Count == 0 && parsed.Profiles.Count == 0)
        {
            throw new AutoNoClaim(
                $"'{filePath}': no PDF/A or PDF/UA conformance claim found; specify -p to select a profile.");
        }

        // Merge claimed + any explicit profiles.
        var merged = new List<PdfConformance>(claimed);
        foreach (var p in parsed.Profiles)
        {
            if (!merged.Contains(p))
                merged.Add(p);
        }

        return (merged, claimed.Count > 0 ? "auto" : "explicit");
    }

    private static RunReport BuildReport(
        string filePath,
        PdfConformance profile,
        string profileSource,
        PreflightResult result,
        ParsedArgs parsed)
    {
        // Collect failed assertions filtered by --severity threshold.
        var failed = new List<PreflightAssertion>();
        foreach (var a in result.Assertions)
        {
            if (MeetsSeverityThreshold(a.Severity, parsed.Severity))
                failed.Add(a);
        }

        var catalog = ConformanceCatalog.For(profile);

        // Work out which catalogued checks the failing assertions withdraw.
        //
        // Error only, deliberately. A Warning does not mean the clause is violated — it means
        // something was noticed that does not affect conformance, and PreflightResult.IsCompliant
        // ignores it by definition. A2aContentItemTaggingRule surfaced this by reporting a Warning
        // at clause 6.7.3.3, which unclaimed 6.7.3.3-1 — the /StructTreeRoot presence check that
        // LogicalStructureRule had just passed on the same document.
        //
        // Two kinds of assertion, because the rule ids are not uniform. Some carry a veraPDF-style
        // test id (ISO19005-2:6.1.13-10 → "6.1.13-10"), which names one catalogued check exactly.
        // Most carry this library's own descriptive id (ISO19005-2:6.2.5-extgstate), which pins the
        // clause but not the test number within it. Only the first kind can be attributed.
        var catalogTestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in catalog)
            catalogTestIds.Add(check.TestId);

        var failingTestIds = new HashSet<string>(StringComparer.Ordinal);
        var failingClauses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in result.Assertions)
        {
            if (a.Severity != PreflightSeverity.Error)
                continue;

            var testId = StripPrefix(a.RuleId);
            if (catalogTestIds.Contains(testId))
                failingTestIds.Add(testId);
            else
                // Assertion.Clause is like "ISO 19005-2:2011, 6.2.8" — the clause number is the
                // part after the last ", ". ConformanceCheck.Clause is already in that short form.
                failingClauses.Add(ParseClauseNumber(a.Clause));
        }

        var passed = new List<ConformanceCheck>();
        var failedChecks = new List<ConformanceCheck>();
        var inconclusive = new List<ConformanceCheck>();
        var notEvaluated = new List<ConformanceCheck>();

        foreach (var check in catalog)
        {
            // Attribution first, and regardless of status: a Partial check whose test id fired was
            // evaluated and did fail, so calling it un-evaluated would understate the result.
            if (failingTestIds.Contains(check.TestId))
                failedChecks.Add(check);
            else if (check.Status != CoverageStatus.Implemented)
                notEvaluated.Add(check);
            else if (failingClauses.Contains(check.Clause))
                // Something in this clause failed but did not say what, so the check can be neither
                // claimed nor blamed. It used to be dropped from the report entirely — counted as
                // neither passed, failed, nor un-evaluated — which is what made the totals wrong.
                inconclusive.Add(check);
            else
                passed.Add(check);
        }

        // Conformant = no failures at/above --fail-on threshold.
        var conformant = true;
        if ((int)parsed.FailOn >= 0)
        {
            foreach (var a in result.Assertions)
            {
                if (MeetsSeverityThreshold(a.Severity, parsed.FailOn))
                {
                    conformant = false;
                    break;
                }
            }
        }

        return new RunReport
        {
            FilePath = filePath,
            Profile = Formatter.ProfileLabel(profile),
            ProfileSource = profileSource,
            Conformant = conformant,
            Failed = failed,
            Passed = passed,
            FailedChecks = failedChecks,
            Inconclusive = inconclusive,
            NotEvaluated = notEvaluated,
        };
    }

    // Maps a PreflightSeverity to whether it meets a SeverityLevel threshold.
    // Error >= Warning >= Info (lower index = higher severity in the enum).
    private static bool MeetsSeverityThreshold(PreflightSeverity assertionSev, SeverityLevel threshold)
    {
        // PreflightSeverity: Error=0, Warning=1, Info=2
        // SeverityLevel:     Info=0,  Warning=1, Error=2
        // We want: assertionSev >= threshold, where Error is highest.
        // Convert: Error→2, Warning→1, Info→0 for assertion; Error→2, Warning→1, Info→0 for threshold.
        var assertionRank = assertionSev switch
        {
            PreflightSeverity.Error => 2,
            PreflightSeverity.Warning => 1,
            _ => 0,
        };
        var thresholdRank = threshold switch
        {
            SeverityLevel.Error => 2,
            SeverityLevel.Warning => 1,
            _ => 0,
        };
        return assertionRank >= thresholdRank;
    }

    // Strip the "PREFIX:" part from a RuleId like "ISO19005-2:6.2.2-1" → "6.2.2-1".
    internal static string StripPrefix(string ruleId)
    {
        var colon = ruleId.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? ruleId[(colon + 1)..] : ruleId;
    }

    // Parse the clause number out of an assertion Clause string like "ISO 19005-2:2011, 6.2.8".
    // The clause number is the segment after the last ", " (trimmed). Falls back to the full
    // string if no ", " is present (e.g. plain "6.2.8" is returned as-is).
    internal static string ParseClauseNumber(string clause)
    {
        var sep = clause.LastIndexOf(", ", StringComparison.Ordinal);
        return sep >= 0 ? clause[(sep + 2)..].Trim() : clause.Trim();
    }

    private static byte[] LoadFile(string filePath, Stream? stdin)
    {
        if (filePath == "-")
        {
            var s = stdin ?? Console.OpenStandardInput();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        return File.ReadAllBytes(filePath);
    }

    private static List<string> ExpandInput(string input, bool recurse)
    {
        // If it's a direct file that exists, return it.
        if (File.Exists(input))
            return new List<string> { input };

        // If it's a directory, return all PDF files.
        if (Directory.Exists(input))
        {
            var searchOpt = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return new List<string>(Directory.GetFiles(input, "*.pdf", searchOpt));
        }

        // Try glob expansion.
        var dir = Path.GetDirectoryName(input);
        var pattern = Path.GetFileName(input);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        if (string.IsNullOrEmpty(pattern))
            pattern = "*.pdf";

        if (!Directory.Exists(dir))
            return new List<string>();

        var searchOption = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var matched = Directory.GetFiles(dir, pattern, searchOption);
        return new List<string>(matched);
    }
}

internal static class ExitCodes
{
    internal const int Ok = 0;
    internal const int NonConformant = 1;
    internal const int UsageError = 2;
}
