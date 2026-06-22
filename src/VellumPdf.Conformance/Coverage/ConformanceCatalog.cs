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
    // Lazily built so the static data arrays (declared below) are initialised first; built once even
    // under concurrent first access (the result is immutable either way).
    public static IReadOnlyList<ConformanceCheck> All
        => System.Threading.LazyInitializer.EnsureInitialized(ref _all, Build);

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
        ["6.1.8-1"] = "font BaseFont and colour colourant names checked (presence-based); structure-type names checked for direct /StructTreeRoot /K children only — deeper nesting not yet walked",
        ["6.2.3-1"] = "DestOutputProfile signature/N checked; ICC device-class not parsed",
        ["6.2.4.2-2"] = "page content-stream OPM/overprint/ICCBased-CMYK correlation checked with q/Q-stack interpreter; Form XObjects, Type 3 CharProcs, and annotation appearance streams not yet walked",
        ["6.2.4.3-2"] = "device-colour requires an output intent checked; DefaultRGB path not",
        ["6.2.4.3-3"] = "device-colour requires an output intent checked; DefaultCMYK path not",
        ["6.2.4.3-4"] = "page-content device grey covered; image/pattern colour not detected",
        ["6.2.4.4-2"] = "Separations selected by cs/CS operators and those in /Colorants of used DeviceN spaces are compared; Separations reachable only via Form XObjects, Type 3 CharProcs, annotation appearance streams, image /ColorSpace, or alternate spaces of other colour spaces are not yet walked",
        ["6.2.2-1"] = "page content-stream operators checked against ISO 32000-1; Form XObject / Type 3 CharProc / annotation appearance streams not yet walked",
        ["6.2.2-2"] = "page content streams checked (Font/XObject/ExtGState/ColorSpace/Shading); Pattern names (scn/SCN in Pattern colour space) and Properties names (BDC/DP with name operand) not detected — stateful colour-space tracking required; Form XObject / Type 3 / appearance streams not walked",
        ["6.2.11.4.1-1"] = "only the embedded Identity-H CIDFontType2 path is checked",
        ["6.2.11.4.1-2"] = "only the embedded Identity-H CIDFontType2 path is checked",
        ["6.2.11.5-1"] = "only the embedded Identity-H CIDFontType2 path is checked",
        ["6.6.2.1-4"] = "only the catalog XMP packet is validated, not every metadata stream",
        ["6.1.10-1"] = "inline images in page content streams checked; those in Form XObject / Type 3 CharProc / annotation appearance streams not walked",
        ["6.6.2.3.3-1"] = "pdfaExtension prefix/bag structure not fully validated",
        ["6.6.2.3.3-5"] = "property container is read but not validated as Seq Property",
        ["6.6.2.3.3-6"] = "valueType container is read but not validated as Seq ValueType",
        ["6.6.2.3.1-2"] = "extension-schema properties with primitive/container types (Text, Integer, Real, Boolean, Date, URI/URL, bag/seq/alt/Lang Alt) are type-checked; predefined XMP-Specification properties (dc:, xmp:, pdf:, pdfaid:, …) are deferred to avoid false-positives from an incomplete built-in type table; extension-schema properties whose declared type resolves to an unrecognised name (custom value types, XMP structure types) are also deferred",
        ["6.6.2.3.3-8"] = "property valueType presence checked, not that it is a defined type",
        ["6.6.2.3.3-15"] = "field container is read but not validated as Seq Field",
        ["6.6.2.3.3-17"] = "field valueType presence checked, not that it is a defined type",
        ["6.1.9-1"] = "object/generation/obj spacing + EOL checked; the endobj-EOL sub-conditions not",
        ["6.1.13-10"] = "embedded-CMap cidrange/cidchar CIDs resolved from content text-show operators; predefined named-CMap character-collection maxima deferred (no Adobe registry table)",
        ["6.2.11.3.1-1"] = "Identity and embedded-CMap CIDSystemInfo compared; predefined-CMap registry table deferred",
        ["6.7.2.2-1"] = "StructTreeRoot presence checked; full structure-tree validation not",
        ["6.8-5"] = "embedded PDF/A-2 validated recursively; embedded PDF/A-1 deferred (no PDF/A-1 profile)",
        ["6.4.3-1"] = "ByteRange unambiguous violations flagged (a!=0, or c+d>fileLength); the "
            + "under-coverage case (c+d<fileLength) is deferred to avoid over-rejecting conformant "
            + "PAdES B-LT/B-LTA signatures whose /DSS or document timestamp is appended after EOF; "
            + "signatures reachable only via /Perms /DocMDP (no AcroForm /V) are not enumerated",
    };

    private static readonly Dictionary<string, string> PdfADeferred = new(StringComparer.Ordinal)
    {
        ["6.1.6-2"] = "byte scan implemented, but the reader rejects an invalid hex digit before validation",
        // 6.1.8-1 moved to PdfAPartial (font BaseFont + colour colourant + structure-type names).
        // 6.1.12-2 moved to Implemented (DocMdpReferenceRule).
        // 6.2.4.2-2 moved to PdfAPartial; implemented via OverprintRule with page-content interpreter.
        // 6.2.8.3-1..-5: removed from Deferred; Jpeg2000Rule now implements all five for both
        // JP2 box files and raw codestreams. -2/-3/-4 correctly do not apply to raw codestreams
        // (which carry no colr boxes) — this is not a gap but correct per-spec scoping.
        // 6.4.3-1/-2/-3 moved to Implemented/Partial (SignatureRule).
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
        "5-1", "5-2", "6.1-1", "6.2-1", "7.1-4", "7.1-8", "7.1-9", "7.1-10", "7.1-11", "7.18.3-1",
        "7.2-29",
        // Batch A2 additions:
        "7.10-1", "7.10-2",   // UaOptionalContentRule: OC config /Name (non-empty) and no /AS
        "7.11-1",              // UaEmbeddedFileRule: embedded-file /F and /UF requirement
        "7.15-1",              // UaXfaRule: dynamic XFA (dynamicRender == "required") forbidden
        "7.18.2-1",            // UaTrapNetAnnotRule: TrapNet annots forbidden unless hidden/outside-crop
        "7.18.5-2",            // UaLinkAnnotRule: Link annots require non-empty /Contents
        "7.20-1",              // UaReferenceXObjectRule: Form XObjects shall not contain /Ref
        // Batch A3 — font clauses:
        "7.21.3.2-1",          // UaCidToGidMapRule: embedded CIDFontType2 must have /CIDToGIDMap
        "7.21.6-3",            // UaSymbolicFontRule: symbolic TrueType must have no /Encoding
        // Batch A4 — font clauses (CMap, CharSet, CIDSet):
        "7.21.3.3-1",          // UaCMapRule: composite /Encoding must be predefined or embedded CMap
        "7.21.3.3-2",          // UaCMapRule: embedded CMap WMode dict must equal program WMode
        "7.21.3.3-3",          // UaCMapRule: usecmap-referenced name must be a predefined CMap
        "7.21.4.2-1",          // UaType1CharSetRule: Type1 subset CharSet must list all glyphs
        "7.21.4.2-2",          // UaCidSetRule: CIDFontType2 subset CIDSet must list all CIDs
        // Batch A5a — rendering-mode-scoped font embedding:
        "7.21.4.1-1",          // UaFontEmbeddingRule: non-embedded simple font drawn visibly
        // Batch A5b — glyph-level clauses (Identity-H/V CIDFontType2-Identity scope):
        "7.21.8-1",            // UaNotdefGlyphRule: shown code 0x0000 == .notdef forbidden
        "7.21.7-2",            // UaToUnicodeForbiddenRule: shown glyph mapped to U+0000/FEFF/FFFE
    };

    // PDF/UA-1 checks the rules cover only partially (the common case is detected; some conditions
    // need a subsystem we do not have yet). The note records the gap, mirroring PdfAPartial.
    private static readonly Dictionary<string, string> PdfUaPartial = new(StringComparer.Ordinal)
    {
        ["7.18.1-2"] = "non-Widget annotations with no /StructParent are checked for a non-empty "
            + "/Contents or /Alt; an annotation bound into the structure tree is skipped because its "
            + "alternate text may live in the enclosing structure element's /Alt, which needs the "
            + "structure-tree walker to resolve (skipping avoids over-rejecting conformant tagged annotations)",
        // Batch A4:
        ["7.21.3.1-1"] = "Identity and embedded-CMap CIDSystemInfo compared; predefined-CMap "
            + "registry table deferred (mirrors PDF/A-2 §6.2.11.3.1-1 partial scope)",
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
            if (PdfUaImplemented.Contains(id))
                checks.Add(new ConformanceCheck(id, [PdfConformance.PdfUA1], ClauseOf(id), CoverageStatus.Implemented));
            else if (PdfUaPartial.TryGetValue(id, out var note))
                checks.Add(new ConformanceCheck(id, [PdfConformance.PdfUA1], ClauseOf(id), CoverageStatus.Partial, note));
            else
                checks.Add(new ConformanceCheck(
                    id, [PdfConformance.PdfUA1], ClauseOf(id), CoverageStatus.Deferred, PdfUaDeferredNote(id)));
        }

        return checks;
    }

    private static string PdfUaDeferredNote(string id) => id switch
    {
        "5-3" or "5-4" or "5-5" => "prefix-aware XMP parsing (XmpReader matches by namespace URI, not prefix)",
        "7.16-1" => "encrypted-document support: the reader does not surface the P permission bits for encrypted files",
        "7.18.6.2-1" or "7.18.6.2-2" => "media clip data dictionary traversal (requires walking Screen-annotation rendition actions)",

        // §7.21 font deferred notes — Batch A3 assessment:
        // 7.21.4.1-1 moved to PdfUaImplemented (Batch A5a — UaFontEmbeddingRule, rendering-mode-scoped).
        "7.21.4.1-2" or "7.21.5-1" =>
            "glyph presence and width checks: only the Identity-H CIDFontType2 path is currently covered "
            + "by the PDF/A-2 GlyphPresenceRule; full UA-1 coverage requires the Tr 3 exemption and "
            + "coverage of simple fonts (which need encoding+cmap resolution) — deferred to avoid FP",
        // 7.21.3.1-1 moved to PdfUaPartial (Batch A4 — UaCidSystemInfoRule).
        // 7.21.3.3-1/-2/-3 moved to PdfUaImplemented (Batch A4 — UaCMapRule).
        // 7.21.4.2-1 moved to PdfUaImplemented (Batch A4 — UaType1CharSetRule).
        // 7.21.4.2-2 moved to PdfUaImplemented (Batch A4 — UaCidSetRule).
        "7.21.6-1" or "7.21.6-2" or "7.21.6-4" =>
            "TrueType non-symbolic cmap / Differences-compliance checks: §7.21.6-2 requires "
            + "differencesAreUnicodeCompliant (every /Differences glyph name must resolve to Unicode — "
            + "FP-prone without a complete AGL table); §7.21.6-1 and §7.21.6-4 check the embedded cmap "
            + "subtable structure (overlaps with PDF/A-2 §6.2.11.6-1/-4 in FontStructureRule); "
            + "deferred to avoid false positives from incomplete glyph-name resolution",
        "7.21.7-1" =>
            "glyph-level ToUnicode presence (veraPDF's Glyph.toUnicode model derives Unicode from font "
            + "encoding for standard-encoded simple fonts, so requiring a /ToUnicode stream would "
            + "over-reject conformant WinAnsi/MacRoman simple fonts; deferred until the glyph-level "
            + "Unicode-derivation model is fully understood for all font types)",
        // 7.21.7-2 moved to PdfUaImplemented (Batch A5b — UaToUnicodeForbiddenRule, shown-glyph-scoped).
        // 7.21.8-1 moved to PdfUaImplemented (Batch A5b — UaNotdefGlyphRule, Identity-H scope).

        _ => "structure-tree walker",
    };

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
