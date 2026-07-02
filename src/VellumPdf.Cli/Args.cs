// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance;

namespace VellumPdf.Cli;

// Hand-rolled argument parser — no reflection, AOT-safe.

internal enum OutputFormat { Text, Json, Sarif }

internal enum SeverityLevel { Info, Warning, Error }

internal sealed class ParsedArgs
{
    // Positional: input paths/globs/"-"
    internal List<string> Inputs { get; } = new();

    // -p/--profile: profiles requested; empty = use auto
    internal List<PdfConformance> Profiles { get; } = new();
    internal bool ProfileAll { get; set; }
    internal bool ProfileAuto { get; set; }

    internal OutputFormat Format { get; set; } = OutputFormat.Text;
    internal string? OutputPath { get; set; }
    internal SeverityLevel Severity { get; set; } = SeverityLevel.Error;
    internal SeverityLevel FailOn { get; set; } = SeverityLevel.Error;
    internal bool Recurse { get; set; }
    internal bool Quiet { get; set; }
    internal bool Verbose { get; set; }
    internal bool NoColor { get; set; }
    internal bool ListProfiles { get; set; }
    internal bool Coverage { get; set; }
    // null = all profiles; set = specific profile
    internal PdfConformance? CoverageProfile { get; set; }
    internal bool Version { get; set; }
    internal bool Help { get; set; }
}

internal static class ArgParser
{
    // Returns null on success, or an error message string on failure.
    internal static string? TryParse(string[] args, ParsedArgs result)
    {
        var i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (arg == "-h" || arg == "--help")
            {
                result.Help = true;
                return null;
            }
            if (arg == "--version")
            {
                result.Version = true;
                return null;
            }
            if (arg == "--list-profiles")
            {
                result.ListProfiles = true;
                return null;
            }

            if (arg == "--coverage")
            {
                result.Coverage = true;
                // optional profile argument
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    i++;
                    var err = ParseProfile(args[i], out var p);
                    if (err is not null)
                        return err;
                    result.CoverageProfile = p;
                }
                return null;
            }

            if (arg == "-p" || arg == "--profile")
            {
                i++;
                if (i >= args.Length)
                    return $"Option {arg} requires an argument.";
                var err = ParseProfiles(args[i], result);
                if (err is not null)
                    return err;
                i++;
                continue;
            }

            if (arg.StartsWith("--profile=", StringComparison.Ordinal))
            {
                var val = arg["--profile=".Length..];
                var err = ParseProfiles(val, result);
                if (err is not null)
                    return err;
                i++;
                continue;
            }

            if (arg == "-f" || arg == "--format")
            {
                i++;
                if (i >= args.Length)
                    return $"Option {arg} requires an argument.";
                var err = ParseFormat(args[i], out var fmt);
                if (err is not null)
                    return err;
                result.Format = fmt;
                i++;
                continue;
            }

            if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                var val = arg["--format=".Length..];
                var err = ParseFormat(val, out var fmt);
                if (err is not null)
                    return err;
                result.Format = fmt;
                i++;
                continue;
            }

            if (arg == "-o" || arg == "--output")
            {
                i++;
                if (i >= args.Length)
                    return $"Option {arg} requires an argument.";
                result.OutputPath = args[i];
                i++;
                continue;
            }

            if (arg.StartsWith("--output=", StringComparison.Ordinal))
            {
                result.OutputPath = arg["--output=".Length..];
                i++;
                continue;
            }

            if (arg == "--severity")
            {
                i++;
                if (i >= args.Length)
                    return "--severity requires an argument.";
                var err = ParseSeverity(args[i], out var sev);
                if (err is not null)
                    return err;
                result.Severity = sev;
                i++;
                continue;
            }

            if (arg.StartsWith("--severity=", StringComparison.Ordinal))
            {
                var val = arg["--severity=".Length..];
                var err = ParseSeverity(val, out var sev);
                if (err is not null)
                    return err;
                result.Severity = sev;
                i++;
                continue;
            }

            if (arg == "--fail-on")
            {
                i++;
                if (i >= args.Length)
                    return "--fail-on requires an argument.";
                var err = ParseFailOn(args[i], out var sev);
                if (err is not null)
                    return err;
                result.FailOn = sev;
                i++;
                continue;
            }

            if (arg.StartsWith("--fail-on=", StringComparison.Ordinal))
            {
                var val = arg["--fail-on=".Length..];
                var err = ParseFailOn(val, out var sev);
                if (err is not null)
                    return err;
                result.FailOn = sev;
                i++;
                continue;
            }

            if (arg == "-r" || arg == "--recurse")
            {
                result.Recurse = true;
                i++;
                continue;
            }

            if (arg == "-q" || arg == "--quiet")
            {
                result.Quiet = true;
                i++;
                continue;
            }

            if (arg == "-v" || arg == "--verbose")
            {
                result.Verbose = true;
                i++;
                continue;
            }

            if (arg == "--no-color")
            {
                result.NoColor = true;
                i++;
                continue;
            }

            if (arg == "--")
            {
                // everything after is positional
                i++;
                while (i < args.Length)
                {
                    result.Inputs.Add(args[i]);
                    i++;
                }
                break;
            }

            if (arg.StartsWith('-'))
                return $"Unknown option: {arg}";

            result.Inputs.Add(arg);
            i++;
        }

        // Default profile = auto
        if (!result.ProfileAll && !result.ProfileAuto && result.Profiles.Count == 0)
            result.ProfileAuto = true;

        return null;
    }

    private static string? ParseProfiles(string value, ParsedArgs result)
    {
        // Supports comma-separated list and "all"/"auto"
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (string.Equals(part, "all", StringComparison.OrdinalIgnoreCase))
            {
                result.ProfileAll = true;
                continue;
            }
            if (string.Equals(part, "auto", StringComparison.OrdinalIgnoreCase))
            {
                result.ProfileAuto = true;
                continue;
            }
            var err = ParseProfile(part, out var p);
            if (err is not null)
                return err;
            if (p.HasValue && !result.Profiles.Contains(p.Value))
                result.Profiles.Add(p.Value);
        }
        return null;
    }

    private static string? ParseProfile(string value, out PdfConformance? profile)
    {
        profile = value.ToLowerInvariant() switch
        {
            "2b" => PdfConformance.PdfA2B,
            "2u" => PdfConformance.PdfA2U,
            "2a" => PdfConformance.PdfA2A,
            "ua1" => PdfConformance.PdfUA1,
            _ => null,
        };
        if (profile is null)
            return $"Unknown profile '{value}'. Valid values: 2b 2u 2a ua1 auto all.";
        return null;
    }

    private static string? ParseFormat(string value, out OutputFormat fmt)
    {
        fmt = value.ToLowerInvariant() switch
        {
            "text" => OutputFormat.Text,
            "json" => OutputFormat.Json,
            "sarif" => OutputFormat.Sarif,
            _ => (OutputFormat)(-1),
        };
        if ((int)fmt == -1)
            return $"Unknown format '{value}'. Valid values: text json sarif.";
        return null;
    }

    private static string? ParseSeverity(string value, out SeverityLevel sev)
    {
        sev = value.ToLowerInvariant() switch
        {
            "error" => SeverityLevel.Error,
            "warning" => SeverityLevel.Warning,
            "info" => SeverityLevel.Info,
            _ => (SeverityLevel)(-1),
        };
        if ((int)sev == -1)
            return $"Unknown severity '{value}'. Valid values: error warning info.";
        return null;
    }

    private static string? ParseFailOn(string value, out SeverityLevel sev)
    {
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            // none = never fail on severity (always exit 0 for conformance)
            sev = (SeverityLevel)(-1);
            return null;
        }
        return ParseSeverity(value, out sev);
    }
}
