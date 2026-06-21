// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Coverage;

/// <summary>How completely the library implements a given conformance check.</summary>
public enum CoverageStatus
{
    /// <summary>The check is fully implemented and (where a fixture exists) cross-validated against veraPDF.</summary>
    Implemented,

    /// <summary>The check is partially implemented — the common case is detected but some conditions are not yet.</summary>
    Partial,

    /// <summary>The check is not yet implemented; <see cref="ConformanceCheck.Note"/> records why (the parser or subsystem it needs).</summary>
    Deferred,
}

/// <summary>
/// One conformance check, identified by its veraPDF-style test id (<c>clause-testNumber</c>, e.g.
/// <c>6.2.11.4.1-2</c>), the profiles it belongs to, and this library's implementation status. These
/// records make the rule set a first-class, countable artifact: coverage is the catalog grouped by
/// <see cref="Status"/>, and a (veraPDF-gated) test diffs the catalog's ids against the authoritative
/// profile so no rule can be silently missed.
/// </summary>
/// <remarks>
/// The test ids are ISO 19005 / ISO 14289 clause references (facts); the titles, notes, and status
/// are this project's own assessment. No veraPDF profile content is embodied here — the clean-room
/// stance (rules authored from the ISO text, veraPDF used only as an oracle) is preserved.
/// </remarks>
public sealed class ConformanceCheck
{
    /// <summary>Creates a conformance check descriptor.</summary>
    public ConformanceCheck(
        string testId, IReadOnlyList<PdfConformance> profiles, string clause, CoverageStatus status, string? note = null)
    {
        TestId = testId;
        Profiles = profiles;
        Clause = clause;
        Status = status;
        Note = note;
    }

    /// <summary>The veraPDF-style test id, <c>clause-testNumber</c> (e.g. <c>6.2.11.4.1-2</c>).</summary>
    public string TestId { get; }

    /// <summary>The conformance profiles this check belongs to.</summary>
    public IReadOnlyList<PdfConformance> Profiles { get; }

    /// <summary>The ISO clause (the test id without its trailing test number).</summary>
    public string Clause { get; }

    /// <summary>This library's implementation status for the check.</summary>
    public CoverageStatus Status { get; }

    /// <summary>For a non-implemented check, the gap or the parser/subsystem it still needs.</summary>
    public string? Note { get; }
}

/// <summary>A coverage tally for one profile (counts of each <see cref="CoverageStatus"/>).</summary>
public sealed class CoverageSummary
{
    /// <summary>Creates a coverage tally.</summary>
    public CoverageSummary(int total, int implemented, int partial, int deferred)
    {
        Total = total;
        Implemented = implemented;
        Partial = partial;
        Deferred = deferred;
    }

    /// <summary>Total checks in the profile.</summary>
    public int Total { get; }

    /// <summary>Fully implemented checks.</summary>
    public int Implemented { get; }

    /// <summary>Partially implemented checks.</summary>
    public int Partial { get; }

    /// <summary>Not-yet-implemented checks.</summary>
    public int Deferred { get; }

    /// <summary>Implemented fraction, counting a <see cref="CoverageStatus.Partial"/> check as one half.</summary>
    public double Percent => Total == 0 ? 0 : 100.0 * (Implemented + 0.5 * Partial) / Total;
}

/// <summary>
/// The catalog of every veraPDF validation check across the conformance profiles the library targets
/// (PDF/A-2b, PDF/A-2u, PDF/A-2a, PDF/UA-1), each tagged with its implementation status. This is the
/// single source of truth for "how much of veraPDF is implemented".
/// </summary>
public static class ConformanceCatalog
{
    private static IReadOnlyList<ConformanceCheck>? _all;

    /// <summary>Every catalogued check, across all target profiles.</summary>
    // Lazily built so the static data arrays (declared below) are initialised first.
    public static IReadOnlyList<ConformanceCheck> All => _all ??= Build();

    /// <summary>The checks that belong to <paramref name="profile"/>.</summary>
    public static IEnumerable<ConformanceCheck> For(PdfConformance profile)
        => All.Where(c => c.Profiles.Contains(profile));

    /// <summary>The coverage tally for <paramref name="profile"/>.</summary>
    public static CoverageSummary Coverage(PdfConformance profile)
    {
        int impl = 0, part = 0, def = 0;
        foreach (var c in For(profile))
            switch (c.Status)
            {
                case CoverageStatus.Implemented: impl++; break;
                case CoverageStatus.Partial: part++; break;
                default: def++; break;
            }
        return new CoverageSummary(impl + part + def, impl, part, def);
    }

    // ── veraPDF rule inventory (clause-testNumber), per profile ──────────────────────────────────
    // These id lists are the ISO clause/test enumeration each profile validates. PDF/A-2u and -2a are
    // supersets of -2b (the deltas below); PDF/UA-1 is a separate standard. A veraPDF-gated test
    // confirms these lists match the bundled profiles exactly, so drift is caught.

    private static readonly string[] PdfA2bIds =
    [
        "6.1.2-1", "6.1.2-2", "6.1.3-1", "6.1.3-2", "6.1.3-3", "6.1.4-2", "6.1.6-1", "6.1.6-2",
        "6.1.7.1-1", "6.1.7.1-2", "6.1.7.1-3", "6.1.7.2-1", "6.1.8-1", "6.1.9-1", "6.1.10-1", "6.1.12-1",
        "6.1.12-2", "6.1.13-1", "6.1.13-2", "6.1.13-3", "6.1.13-4", "6.1.13-5", "6.1.13-7", "6.1.13-8",
        "6.1.13-9", "6.1.13-10", "6.1.13-11", "6.2.2-1", "6.2.2-2", "6.2.3-1", "6.2.3-2", "6.2.3-3",
        "6.2.4.2-1", "6.2.4.2-2", "6.2.4.3-2", "6.2.4.3-3", "6.2.4.3-4", "6.2.4.4-1", "6.2.4.4-2", "6.2.5-1",
        "6.2.5-2", "6.2.5-3", "6.2.5-4", "6.2.5-5", "6.2.5-6", "6.2.6-1", "6.2.8-1", "6.2.8-2",
        "6.2.8-3", "6.2.8-4", "6.2.8-5", "6.2.8.3-1", "6.2.8.3-2", "6.2.8.3-3", "6.2.8.3-4", "6.2.8.3-5",
        "6.2.9-1", "6.2.9-2", "6.2.9-3", "6.2.10-1", "6.2.10-2", "6.2.11.2-1", "6.2.11.2-2", "6.2.11.2-3",
        "6.2.11.2-4", "6.2.11.2-5", "6.2.11.2-6", "6.2.11.2-7", "6.2.11.3.1-1", "6.2.11.3.2-1", "6.2.11.3.3-1", "6.2.11.3.3-2",
        "6.2.11.3.3-3", "6.2.11.4.1-1", "6.2.11.4.1-2", "6.2.11.4.2-1", "6.2.11.4.2-2", "6.2.11.5-1", "6.2.11.6-1", "6.2.11.6-2",
        "6.2.11.6-3", "6.2.11.6-4", "6.2.11.8-1", "6.3.1-1", "6.3.2-1", "6.3.2-2", "6.3.3-1", "6.3.3-2",
        "6.3.3-3", "6.3.3-4", "6.4.1-1", "6.4.1-2", "6.4.1-3", "6.4.2-1", "6.4.2-2", "6.4.3-1",
        "6.4.3-2", "6.4.3-3", "6.5.1-1", "6.5.1-2", "6.5.2-1", "6.5.2-2", "6.6.2.1-1", "6.6.2.1-2",
        "6.6.2.1-3", "6.6.2.1-4", "6.6.2.1-5", "6.6.2.3.1-1", "6.6.2.3.1-2", "6.6.2.3.2-1", "6.6.2.3.3-1", "6.6.2.3.3-2",
        "6.6.2.3.3-3", "6.6.2.3.3-4", "6.6.2.3.3-5", "6.6.2.3.3-6", "6.6.2.3.3-7", "6.6.2.3.3-8", "6.6.2.3.3-9", "6.6.2.3.3-10",
        "6.6.2.3.3-11", "6.6.2.3.3-12", "6.6.2.3.3-13", "6.6.2.3.3-14", "6.6.2.3.3-15", "6.6.2.3.3-16", "6.6.2.3.3-17", "6.6.2.3.3-18",
        "6.6.4-1", "6.6.4-2", "6.6.4-3", "6.6.4-4", "6.6.4-5", "6.6.4-6", "6.6.4-7", "6.8-2",
        "6.8-5", "6.9-1", "6.9-2", "6.9-3", "6.9-4", "6.10-1", "6.10-2", "6.11-1",
    ];

    // PDF/A-2u = 2b + two ToUnicode tests; PDF/A-2a = 2u + one ToUnicode test and the §6.7 logical-
    // structure tests.
    private static readonly string[] PdfA2uDelta = ["6.2.11.7.2-1", "6.2.11.7.2-2"];

    private static readonly string[] PdfA2aDelta =
    [
        "6.2.11.7.3-1", "6.7.2.2-1", "6.7.3.3-1", "6.7.3.4-1", "6.7.3.4-2", "6.7.3.4-3", "6.7.4-1",
    ];

    private static readonly string[] PdfUa1Ids =
    [
        "5-1", "5-2", "5-3", "5-4", "5-5", "6.1-1", "6.2-1", "7.1-1",
        "7.1-2", "7.1-3", "7.1-4", "7.1-5", "7.1-6", "7.1-7", "7.1-8", "7.1-9",
        "7.1-10", "7.1-11", "7.1-12", "7.2-2", "7.2-3", "7.2-4", "7.2-5", "7.2-6",
        "7.2-7", "7.2-8", "7.2-9", "7.2-10", "7.2-11", "7.2-12", "7.2-13", "7.2-14",
        "7.2-15", "7.2-16", "7.2-17", "7.2-18", "7.2-19", "7.2-20", "7.2-21", "7.2-22",
        "7.2-23", "7.2-24", "7.2-25", "7.2-26", "7.2-27", "7.2-28", "7.2-29", "7.2-30",
        "7.2-31", "7.2-32", "7.2-33", "7.2-34", "7.2-36", "7.2-37", "7.2-38", "7.2-39",
        "7.2-40", "7.2-41", "7.2-42", "7.2-43", "7.3-1", "7.4.2-1", "7.4.4-1", "7.4.4-2",
        "7.4.4-3", "7.5-1", "7.5-2", "7.7-1", "7.9-1", "7.9-2", "7.10-1", "7.10-2",
        "7.11-1", "7.15-1", "7.16-1", "7.18.1-1", "7.18.1-2", "7.18.1-3", "7.18.2-1", "7.18.3-1",
        "7.18.4-1", "7.18.4-2", "7.18.5-1", "7.18.5-2", "7.18.6.2-1", "7.18.6.2-2", "7.18.8-1", "7.20-1",
        "7.20-2", "7.21.3.1-1", "7.21.3.2-1", "7.21.3.3-1", "7.21.3.3-2", "7.21.3.3-3", "7.21.4.1-1", "7.21.4.1-2",
        "7.21.4.2-1", "7.21.4.2-2", "7.21.5-1", "7.21.6-1", "7.21.6-2", "7.21.6-3", "7.21.6-4", "7.21.7-1",
        "7.21.7-2", "7.21.8-1",
    ];

    // ── Status classification ────────────────────────────────────────────────────────────────────
    // Anything in neither map below is Implemented. Notes record the gap (Partial) or the parser /
    // subsystem still required (Deferred). The status is this library's own assessment; behavioural
    // correctness of the Implemented set is what the veraPDF-cross-validated oracle fixtures prove.

    private static readonly Dictionary<string, string> PdfAPartial = new(StringComparer.Ordinal)
    {
        ["6.2.3-1"] = "DestOutputProfile signature/N checked; ICC device-class not parsed",
        ["6.2.4.3-2"] = "device-colour requires an output intent checked; DefaultRGB path not",
        ["6.2.4.3-3"] = "device-colour requires an output intent checked; DefaultCMYK path not",
        ["6.2.4.3-4"] = "page-content device grey covered; image/pattern colour not detected",
        ["6.2.11.4.1-1"] = "only the embedded Identity-H CIDFontType2 path is checked",
        ["6.2.11.4.1-2"] = "only the embedded Identity-H CIDFontType2 path is checked",
        ["6.2.11.5-1"] = "only the embedded Identity-H CIDFontType2 path is checked",
        ["6.6.2.1-4"] = "only the catalog XMP packet is validated, not every metadata stream",
        ["6.6.2.3.3-1"] = "pdfaExtension prefix/bag structure not fully validated",
        ["6.6.2.3.3-5"] = "property container is read but not validated as Seq Property",
        ["6.6.2.3.3-6"] = "valueType container is read but not validated as Seq ValueType",
        ["6.6.2.3.3-8"] = "property valueType presence checked, not that it is a defined type",
        ["6.6.2.3.3-15"] = "field container is read but not validated as Seq Field",
        ["6.6.2.3.3-17"] = "field valueType presence checked, not that it is a defined type",
        ["6.1.9-1"] = "object/generation/obj spacing + EOL checked; the endobj-EOL sub-conditions not",
        ["6.7.2.2-1"] = "StructTreeRoot presence checked; full structure-tree validation not",
    };

    private static readonly Dictionary<string, string> PdfADeferred = new(StringComparer.Ordinal)
    {
        ["6.1.6-1"] = "byte-offset token scanner",
        ["6.1.6-2"] = "byte-offset token scanner",
        ["6.1.8-1"] = "byte-offset token scanner",
        ["6.1.7.2-1"] = "used-stream filter analysis",
        ["6.1.10-1"] = "inline-image content parser",
        ["6.1.12-2"] = "signature-reference traversal",
        ["6.1.13-8"] = "content-stream q/Q nesting counter",
        ["6.1.13-9"] = "used DeviceN colourant analysis",
        ["6.1.13-10"] = "CMap CID-range parser",
        ["6.2.2-1"] = "content-stream operator interpreter",
        ["6.2.2-2"] = "content-stream resource analysis",
        ["6.2.3-2"] = "object-graph: shared DestOutputProfile across intents",
        ["6.2.4.2-1"] = "ICC profile parser",
        ["6.2.4.2-2"] = "ExtGState overprint (OPM) analysis",
        ["6.2.4.4-1"] = "DeviceN/Separation colourant graph",
        ["6.2.4.4-2"] = "Separation tint/alternate consistency",
        ["6.2.5-6"] = "object-graph: halftone TransferFunction",
        ["6.2.8.3-1"] = "JPEG2000 codestream parser",
        ["6.2.8.3-2"] = "JPEG2000 codestream parser",
        ["6.2.8.3-3"] = "JPEG2000 codestream parser",
        ["6.2.8.3-4"] = "JPEG2000 codestream parser",
        ["6.2.8.3-5"] = "JPEG2000 codestream parser",
        ["6.2.11.2-1"] = "font-program parser",
        ["6.2.11.2-3"] = "font-program parser",
        ["6.2.11.2-5"] = "font-program parser",
        ["6.2.11.2-7"] = "font-program parser",
        ["6.2.11.3.1-1"] = "object-graph: CIDSystemInfo match",
        ["6.2.11.3.3-1"] = "CMap parser",
        ["6.2.11.3.3-2"] = "CMap parser",
        ["6.2.11.3.3-3"] = "CMap parser",
        ["6.2.11.4.2-1"] = "Type1 CharSet completeness (charstring parser)",
        ["6.4.3-1"] = "digital-signature parser",
        ["6.4.3-2"] = "digital-signature parser",
        ["6.4.3-3"] = "digital-signature parser",
        ["6.6.2.3.1-2"] = "XMP per-property value-type match",
        ["6.6.2.3.2-1"] = "extension-schema-container schema",
        ["6.8-5"] = "recursive embedded-file PDF/A validation",
        ["6.7.3.3-1"] = "structure-tree walker",
        ["6.7.3.4-1"] = "structure-tree walker",
        ["6.7.3.4-2"] = "structure-tree walker",
        ["6.7.3.4-3"] = "structure-tree walker",
        ["6.7.4-1"] = "structure-tree walker",
    };

    // PDF/UA-1: the few checks the current rules cover. Everything else needs the logical-structure
    // (tagged-content) walker, which does not yet exist.
    private static readonly HashSet<string> PdfUaImplemented = new(StringComparer.Ordinal)
    {
        "5-1", "5-2", "6.2-1", "7.1-8", "7.1-9", "7.1-10", "7.1-11", "7.18.3-1",
    };

    private static IReadOnlyList<ConformanceCheck> Build()
    {
        var checks = new List<ConformanceCheck>();

        foreach (var id in PdfA2bIds)
            checks.Add(MakePdfA(id, [PdfConformance.PdfA2B, PdfConformance.PdfA2U, PdfConformance.PdfA2A]));
        foreach (var id in PdfA2uDelta)
            checks.Add(MakePdfA(id, [PdfConformance.PdfA2U, PdfConformance.PdfA2A]));
        foreach (var id in PdfA2aDelta)
            checks.Add(MakePdfA(id, [PdfConformance.PdfA2A]));

        foreach (var id in PdfUa1Ids)
        {
            var implemented = PdfUaImplemented.Contains(id);
            checks.Add(new ConformanceCheck(
                id, [PdfConformance.PdfUA1], ClauseOf(id),
                implemented ? CoverageStatus.Implemented : CoverageStatus.Deferred,
                implemented ? null : "structure-tree walker"));
        }

        return checks;
    }

    private static ConformanceCheck MakePdfA(string id, PdfConformance[] profiles)
    {
        if (PdfADeferred.TryGetValue(id, out var reason))
            return new ConformanceCheck(id, profiles, ClauseOf(id), CoverageStatus.Deferred, reason);
        if (PdfAPartial.TryGetValue(id, out var note))
            return new ConformanceCheck(id, profiles, ClauseOf(id), CoverageStatus.Partial, note);
        return new ConformanceCheck(id, profiles, ClauseOf(id), CoverageStatus.Implemented);
    }

    private static string ClauseOf(string testId)
    {
        var dash = testId.LastIndexOf('-');
        return dash < 0 ? testId : testId[..dash];
    }
}
