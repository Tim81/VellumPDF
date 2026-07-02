// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using VellumPdf.Conformance;
using VellumPdf.Conformance.Coverage;

namespace VellumPdf.Cli;

internal sealed class RunReport
{
    internal required string FilePath { get; init; }
    internal required string Profile { get; init; }
    internal required string ProfileSource { get; init; }
    internal required bool Conformant { get; init; }
    internal required List<PreflightAssertion> Failed { get; init; }
    internal required List<ConformanceCheck> Passed { get; init; }
    internal required List<ConformanceCheck> NotEvaluated { get; init; }
}

internal static class Formatter
{
    // ── ANSI helpers ──────────────────────────────────────────────────────────

    private const string Red = "\x1b[31m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Cyan = "\x1b[36m";
    private const string Bold = "\x1b[1m";
    private const string Reset = "\x1b[0m";

    private static string C(string code, string text, bool color) =>
        color ? $"{code}{text}{Reset}" : text;

    // ── Text format ───────────────────────────────────────────────────────────

    internal static void WriteText(
        TextWriter w,
        string toolVersion,
        IReadOnlyList<RunReport> reports,
        bool verbose,
        bool color,
        SeverityLevel minSeverity)
    {
        foreach (var r in reports)
        {
            var verdict = r.Conformant
                ? C(Green, "PASS", color)
                : C(Red, "FAIL", color);

            int errorCount = 0, warnCount = 0, infoCount = 0;
            foreach (var f in r.Failed)
            {
                switch (f.Severity)
                {
                    case PreflightSeverity.Error: errorCount++; break;
                    case PreflightSeverity.Warning: warnCount++; break;
                    case PreflightSeverity.Info: infoCount++; break;
                }
            }

            var profileLabel = C(Cyan, r.Profile, color);
            var fileLabel = C(Bold, r.FilePath, color);
            w.WriteLine($"{fileLabel} — {profileLabel}: {verdict} ({errorCount} errors, {warnCount} warnings)");

            if (!r.Conformant && r.Failed.Count > 0)
            {
                w.WriteLine();
                w.WriteLine(C(Bold, "FAILED:", color));
                foreach (var f in r.Failed)
                {
                    var sevLabel = f.Severity switch
                    {
                        PreflightSeverity.Error => C(Red, "ERROR", color),
                        PreflightSeverity.Warning => C(Yellow, "WARN", color),
                        _ => "INFO",
                    };
                    var objPart = f.ObjectRef is null ? "" : $" [{f.ObjectRef}]";
                    w.WriteLine($"  {sevLabel}  {f.RuleId}  ({f.Clause}){objPart}");
                    w.WriteLine($"       {f.Message}");
                }
            }

            // Passed summary
            w.WriteLine();
            if (r.Passed.Count > 0)
            {
                if (verbose)
                {
                    w.WriteLine(C(Green, $"PASSED ({r.Passed.Count} checks):", color));
                    foreach (var p in r.Passed)
                        w.WriteLine($"  {p.TestId}  ({p.Clause})");
                }
                else
                {
                    // Group by clause prefix
                    var clauses = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in r.Passed)
                        clauses.Add(p.Clause);
                    var clauseList = string.Join(", ", clauses);
                    w.WriteLine(C(Green, $"PASSED: {r.Passed.Count} checks", color) + $"  [{clauseList}]");
                }
            }

            // Not evaluated footer
            int partialCount = 0, deferredCount = 0, oosCount = 0;
            foreach (var n in r.NotEvaluated)
            {
                switch (n.Status)
                {
                    case CoverageStatus.Partial: partialCount++; break;
                    case CoverageStatus.Deferred: deferredCount++; break;
                    case CoverageStatus.OutOfScope: oosCount++; break;
                }
            }
            if (r.NotEvaluated.Count > 0)
            {
                w.WriteLine();
                w.WriteLine(C(Yellow, $"NOT FULLY EVALUATED: {partialCount} partial, {deferredCount} deferred, {oosCount} out-of-scope", color));
                w.WriteLine($"  (run --coverage {ProfileKey(r.Profile)} for per-check details)");
            }

            w.WriteLine();
        }
    }

    private static string ProfileKey(string profile) => profile.ToLowerInvariant() switch
    {
        "pdf/a-2b" => "2b",
        "pdf/a-2u" => "2u",
        "pdf/a-2a" => "2a",
        "pdf/ua-1" => "ua1",
        _ => profile,
    };

    // ── JSON format ───────────────────────────────────────────────────────────

    internal static void WriteJson(TextWriter w, string toolVersion, IReadOnlyList<RunReport> reports)
    {
        if (reports.Count == 1)
        {
            var dto = ToSingleDto(toolVersion, reports[0]);
            w.Write(JsonSerializer.Serialize(dto, CliJsonContext.Default.SingleReportDto));
        }
        else
        {
            var multi = new MultiReportDto
            {
                Results = reports.Select(r => ToSingleDto(toolVersion, r)).ToList(),
            };
            w.Write(JsonSerializer.Serialize(multi, CliJsonContext.Default.MultiReportDto));
        }
    }

    private static SingleReportDto ToSingleDto(string toolVersion, RunReport r)
    {
        int errorCount = 0, warnCount = 0, infoCount = 0;
        foreach (var f in r.Failed)
        {
            switch (f.Severity)
            {
                case PreflightSeverity.Error: errorCount++; break;
                case PreflightSeverity.Warning: warnCount++; break;
                case PreflightSeverity.Info: infoCount++; break;
            }
        }

        int partialCount = 0, deferredCount = 0;
        foreach (var n in r.NotEvaluated)
        {
            switch (n.Status)
            {
                case CoverageStatus.Partial: partialCount++; break;
                case CoverageStatus.Deferred: deferredCount++; break;
            }
        }

        return new SingleReportDto
        {
            Tool = "vellum-preflight",
            ToolVersion = toolVersion,
            File = r.FilePath,
            Profile = r.Profile,
            ProfileSource = r.ProfileSource,
            Conformant = r.Conformant,
            Summary = new SummaryDto
            {
                Error = errorCount,
                Warning = warnCount,
                Info = infoCount,
                Passed = r.Passed.Count,
                Partial = partialCount,
                Deferred = deferredCount,
                Total = r.Failed.Count + r.Passed.Count + r.NotEvaluated.Count,
            },
            Failed = r.Failed.Select(f => new FailedDto
            {
                RuleId = f.RuleId,
                Clause = f.Clause,
                Severity = f.Severity.ToString().ToUpperInvariant(),
                Message = f.Message,
                ObjectRef = f.ObjectRef,
            }).ToList(),
            Passed = r.Passed.Select(p => new PassedDto
            {
                TestId = p.TestId,
                Clause = p.Clause,
            }).ToList(),
            NotEvaluated = r.NotEvaluated.Select(n => new NotEvaluatedDto
            {
                TestId = n.TestId,
                Clause = n.Clause,
                Status = n.Status.ToString(),
                Note = n.Note,
            }).ToList(),
        };
    }

    // ── SARIF 2.1.0 format ────────────────────────────────────────────────────

    internal static void WriteSarif(TextWriter w, string toolVersion, IReadOnlyList<RunReport> reports)
    {
        // Collect all unique rules across all reports
        var ruleSet = new Dictionary<string, SarifRule>(StringComparer.Ordinal);
        var sarifResults = new List<SarifResult>();

        foreach (var r in reports)
        {
            foreach (var f in r.Failed)
            {
                if (!ruleSet.ContainsKey(f.RuleId))
                {
                    ruleSet[f.RuleId] = new SarifRule
                    {
                        Id = f.RuleId,
                        ShortDescription = new SarifMessage { Text = f.Clause },
                    };
                }

                var level = f.Severity switch
                {
                    PreflightSeverity.Error => "error",
                    PreflightSeverity.Warning => "warning",
                    _ => "note",
                };

                sarifResults.Add(new SarifResult
                {
                    RuleId = f.RuleId,
                    Level = level,
                    Message = new SarifMessage { Text = f.Message },
                    Locations = new List<SarifLocation>
                    {
                        new()
                        {
                            PhysicalLocation = new SarifPhysicalLocation
                            {
                                ArtifactLocation = new SarifArtifactLocation
                                {
                                    Uri = PathToUri(r.FilePath),
                                },
                            },
                        },
                    },
                });
            }
        }

        var root = new SarifRoot
        {
            Runs = new List<SarifRun>
            {
                new()
                {
                    Tool = new SarifTool
                    {
                        Driver = new SarifToolDriver
                        {
                            Name = "vellum-preflight",
                            Version = toolVersion,
                            Rules = ruleSet.Values.ToList(),
                        },
                    },
                    Results = sarifResults,
                },
            },
        };

        w.Write(JsonSerializer.Serialize(root, CliJsonContext.Default.SarifRoot));
    }

    private static string PathToUri(string path)
    {
        if (path == "-")
            return "stdin:///";
        try
        {
            var full = Path.GetFullPath(path);
            return new Uri(full).AbsoluteUri;
        }
        catch
        {
            return path;
        }
    }

    // ── Coverage output ───────────────────────────────────────────────────────

    internal static void WriteCoverage(TextWriter w, PdfConformance? profile, bool color)
    {
        var profiles = profile.HasValue
            ? new[] { profile.Value }
            : new[] { PdfConformance.PdfA2B, PdfConformance.PdfA2U, PdfConformance.PdfA2A, PdfConformance.PdfUA1 };

        foreach (var p in profiles)
        {
            var label = ProfileLabel(p);
            var summary = ConformanceCatalog.Coverage(p);
            w.WriteLine(C(Bold, $"Coverage: {label}", color));
            w.WriteLine($"  Total:       {summary.Total}");
            w.WriteLine($"  Implemented: {summary.Implemented}");
            w.WriteLine($"  Partial:     {summary.Partial}");
            w.WriteLine($"  Deferred:    {summary.Deferred}");
            w.WriteLine($"  OutOfScope:  {summary.OutOfScope}");
            w.WriteLine($"  Coverage:    {summary.Percent:F1}%");
            w.WriteLine();

            foreach (var check in ConformanceCatalog.For(p))
            {
                var statusLabel = check.Status switch
                {
                    CoverageStatus.Implemented => C(Green, "IMPL", color),
                    CoverageStatus.Partial => C(Yellow, "PART", color),
                    CoverageStatus.Deferred => C(Yellow, "DEFR", color),
                    CoverageStatus.OutOfScope => "OOS ",
                    _ => "    ",
                };
                var note = check.Note is null ? "" : $"  # {check.Note}";
                w.WriteLine($"  {statusLabel}  {check.TestId,-20}  {check.Clause}{note}");
            }
            w.WriteLine();
        }
    }

    internal static string ProfileLabel(PdfConformance p) => p switch
    {
        PdfConformance.PdfA2B => "PDF/A-2b",
        PdfConformance.PdfA2U => "PDF/A-2u",
        PdfConformance.PdfA2A => "PDF/A-2a",
        PdfConformance.PdfUA1 => "PDF/UA-1",
        _ => p.ToString(),
    };
}
