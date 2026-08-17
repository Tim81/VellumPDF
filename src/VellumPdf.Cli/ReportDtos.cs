// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace VellumPdf.Cli;

// ── JSON output DTOs ──────────────────────────────────────────────────────────
// Explicit [JsonPropertyName] attributes to guarantee camelCase output that is
// AOT- and source-gen-safe regardless of PropertyNamingPolicy behavior.

internal sealed class FailedDto
{
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";
    [JsonPropertyName("clause")] public string Clause { get; init; } = "";
    [JsonPropertyName("severity")] public string Severity { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("objectRef")] public string? ObjectRef { get; init; }
}

/// <summary>A catalogued check, in whichever bucket the run put it.</summary>
internal sealed class CheckDto
{
    [JsonPropertyName("testId")] public string TestId { get; init; } = "";
    [JsonPropertyName("clause")] public string Clause { get; init; } = "";
}

internal sealed class NotEvaluatedDto
{
    [JsonPropertyName("testId")] public string TestId { get; init; } = "";
    [JsonPropertyName("clause")] public string Clause { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("note")] public string? Note { get; init; }
}

internal sealed class SummaryDto
{
    [JsonPropertyName("error")] public int Error { get; init; }
    [JsonPropertyName("warning")] public int Warning { get; init; }
    [JsonPropertyName("info")] public int Info { get; init; }
    [JsonPropertyName("passed")] public int Passed { get; init; }
    [JsonPropertyName("failedChecks")] public int FailedChecks { get; init; }
    [JsonPropertyName("inconclusive")] public int Inconclusive { get; init; }
    [JsonPropertyName("partial")] public int Partial { get; init; }
    [JsonPropertyName("deferred")] public int Deferred { get; init; }

    /// <summary>
    /// Every catalogued check for the profile. Equals passed + failedChecks + inconclusive +
    /// notEvaluated — a count of checks only. It used to add the assertion count to the check
    /// count, which double-counted an attributable failure and left an unattributable one showing
    /// up nowhere.
    /// </summary>
    [JsonPropertyName("total")] public int Total { get; init; }
}

// Single-file top-level shape (also used as the element in multi-file)
internal sealed class SingleReportDto
{
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("toolVersion")] public string ToolVersion { get; init; } = "";
    [JsonPropertyName("file")] public string File { get; init; } = "";
    [JsonPropertyName("profile")] public string Profile { get; init; } = "";
    [JsonPropertyName("profileSource")] public string ProfileSource { get; init; } = "";
    [JsonPropertyName("conformant")] public bool Conformant { get; init; }
    [JsonPropertyName("summary")] public SummaryDto Summary { get; init; } = new();
    [JsonPropertyName("failed")] public List<FailedDto> Failed { get; init; } = new();
    [JsonPropertyName("passed")] public List<CheckDto> Passed { get; init; } = new();
    [JsonPropertyName("failedChecks")] public List<CheckDto> FailedChecks { get; init; } = new();
    [JsonPropertyName("inconclusive")] public List<CheckDto> Inconclusive { get; init; } = new();
    [JsonPropertyName("notEvaluated")] public List<NotEvaluatedDto> NotEvaluated { get; init; } = new();
}

// Multi-result wrapper
internal sealed class MultiReportDto
{
    [JsonPropertyName("results")] public List<SingleReportDto> Results { get; init; } = new();
}

// ── SARIF 2.1.0 DTOs ─────────────────────────────────────────────────────────

internal sealed class SarifRoot
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Documents/CommitteeSpecifications/2.1.0/sarif-schema-2.1.0.json";

    [JsonPropertyName("version")] public string Version { get; init; } = "2.1.0";
    [JsonPropertyName("runs")] public List<SarifRun> Runs { get; init; } = new();
}

internal sealed class SarifToolDriver
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("rules")] public List<SarifRule> Rules { get; init; } = new();
}

internal sealed class SarifTool
{
    [JsonPropertyName("driver")] public SarifToolDriver Driver { get; init; } = new();
}

internal sealed class SarifRule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("shortDescription")] public SarifMessage ShortDescription { get; init; } = new();
}

internal sealed class SarifRun
{
    [JsonPropertyName("tool")] public SarifTool Tool { get; init; } = new();
    [JsonPropertyName("results")] public List<SarifResult> Results { get; init; } = new();
}

internal sealed class SarifResult
{
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";
    [JsonPropertyName("level")] public string Level { get; init; } = "";
    [JsonPropertyName("message")] public SarifMessage Message { get; init; } = new();
    [JsonPropertyName("locations")] public List<SarifLocation> Locations { get; init; } = new();
}

internal sealed class SarifLocation
{
    [JsonPropertyName("physicalLocation")] public SarifPhysicalLocation PhysicalLocation { get; init; } = new();
}

internal sealed class SarifPhysicalLocation
{
    [JsonPropertyName("artifactLocation")] public SarifArtifactLocation ArtifactLocation { get; init; } = new();
}

internal sealed class SarifArtifactLocation
{
    [JsonPropertyName("uri")] public string Uri { get; init; } = "";
}

internal sealed class SarifMessage
{
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

// ── Source-generated JsonSerializerContext ────────────────────────────────────

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SingleReportDto))]
[JsonSerializable(typeof(MultiReportDto))]
[JsonSerializable(typeof(SarifRoot))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
