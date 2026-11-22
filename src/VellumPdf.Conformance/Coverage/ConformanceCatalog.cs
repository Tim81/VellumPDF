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

    /// <summary>
    /// The check is deliberately not implemented in-process — tracked in a follow-up issue. Distinct from
    /// Deferred (not-yet-done): the Note records the reason and the tracking issue. Excluded from the
    /// feasible-coverage denominator.
    /// </summary>
    OutOfScope,
}

/// <summary>
/// One conformance check, identified by its veraPDF-style test id (<c>clause-testNumber</c>, e.g.
/// <c>6.2.11.4.1-2</c>), the profiles it belongs to, and this library's implementation status. These
/// records make the rule set a first-class, countable artifact: coverage is the catalog grouped by
/// <see cref="Status"/>, and a (veraPDF-gated) test diffs the catalog's ids against the authoritative
/// profile so no rule can be silently missed.
/// </summary>
/// <remarks>
/// The test ids are veraPDF's <c>clause-testNumber</c> identifiers. The clause part is a clause
/// number of ISO 19005-2 or ISO 14289-1 as veraPDF's profile groups it, which is not always the
/// clause the standard puts the requirement in (#418, #428); the number after the dash is veraPDF's
/// own test number, which neither standard has. The id lists below are veraPDF's rule inventory, so
/// veraPDF profile content is embodied here as data. The titles, notes, and status are this
/// project's own assessment. Most of the rules behind these ids were written from veraPDF's
/// profiles and checked against veraPDF, because ISO 19005-2 was not held and ISO 14289-1 was
/// acquired after most of its rules were written. The exceptions say so in their remarks:
/// <c>FontEmbeddingRule</c> and <c>ActionRule</c> were re-derived from the clause text (#418),
/// <c>UaEncryptionPermissionsRule</c> was authored from ISO 32000-2, and
/// <c>A2aContentItemTaggingRule</c> has no veraPDF counterpart. The status says whether a rule
/// covers the veraPDF check, not that it agrees with the clause text: the 2026-11-21 audits (#418,
/// #428) found rules narrower than the clauses.
/// docs/pdf20-conformance.md records which citations are checkable against a held copy and which
/// are not.
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
    public CoverageSummary(int total, int implemented, int partial, int deferred, int outOfScope)
    {
        Total = total;
        Implemented = implemented;
        Partial = partial;
        Deferred = deferred;
        OutOfScope = outOfScope;
    }

    /// <summary>Total checks in the profile (Implemented + Partial + Deferred + OutOfScope).</summary>
    public int Total { get; }

    /// <summary>Fully implemented checks.</summary>
    public int Implemented { get; }

    /// <summary>Partially implemented checks.</summary>
    public int Partial { get; }

    /// <summary>Not-yet-implemented checks.</summary>
    public int Deferred { get; }

    /// <summary>Deliberately out-of-scope checks (tracked in follow-up issues); excluded from Percent.</summary>
    public int OutOfScope { get; }

    /// <summary>
    /// Implemented fraction of in-process-feasible checks (OutOfScope excluded; a Partial counts as one half).
    /// </summary>
    public double Percent => (Total - OutOfScope) == 0 ? 0 : 100.0 * (Implemented + 0.5 * Partial) / (Total - OutOfScope);
}

/// <summary>
/// The catalog of every veraPDF validation check across the conformance profiles the library targets
/// (PDF/A-2b, PDF/A-2u, PDF/A-2a, PDF/UA-1), each tagged with its implementation status. This is
/// this project's record of which veraPDF checks a rule covers. It does not record agreement with
/// the text of either standard (#418, #428).
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
        int impl = 0, part = 0, def = 0, oos = 0;
        foreach (var c in For(profile))
            switch (c.Status)
            {
                case CoverageStatus.Implemented: impl++; break;
                case CoverageStatus.Partial: part++; break;
                case CoverageStatus.Deferred: def++; break;
                case CoverageStatus.OutOfScope: oos++; break;
            }
        return new CoverageSummary(impl + part + def + oos, impl, part, def, oos);
    }

    // ── veraPDF rule inventory (clause-testNumber), per profile ──────────────────────────────────
    // These id lists are veraPDF's clause/test enumeration for each profile. PDF/A-2u and -2a are
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
    // subsystem still required (Deferred). The status is this library's own assessment. Oracle
    // fixtures exercise agreement with veraPDF on the Implemented set; agreement with the clause
    // text is not claimed (#418, #428).

    private static readonly Dictionary<string, string> PdfAPartial = new(StringComparer.Ordinal)
    {
        // Partial with an infeasible residual: the common path is implemented and veraPDF-verified,
        // but a predefined-CJK-CMap sub-condition can't be cross-validated clean-room (needs the Adobe
        // code->CID tables and a conformant CJK CIDFont oracle asset). Tracked in #139.
        ["6.1.13-10"] = "Identity and embedded-CMap CIDs resolved from content text-show operators are range-checked; predefined named-CMap character-collection maxima are not (needs the Adobe code->CID tables + a conformant CJK CIDFont oracle asset). Tracked in #139.",
        ["6.2.11.3.1-1"] = "Identity and embedded-CMap CIDSystemInfo compared; the predefined-CMap registry match is authored but not cross-validated (needs a conformant CJK CIDFont oracle asset). Tracked in #139.",
        // Partial: the 2026-11-21 audit (#418) found a construct that veraPDF refuses and the rule
        // accepts. Each note names it.
        ["6.1.2-1"] =
            "The byte after the version digit is not checked: a header %PDF-1.7x passes here and " +
            "fails veraPDF (#418).",
        ["6.1.13-2"] =
            "Content-stream operands are not checked: a 40-digit real passes here and fails " +
            "veraPDF (#418).",
        ["6.1.13-5"] =
            "Content-stream operands are not checked: a real below 1.175e-38 passes here and " +
            "fails veraPDF (#418).",
        ["6.2.3-1"] =
            "A DestOutputProfile of device class scnr passes here and fails veraPDF (#418).",
        ["6.2.4.3-2"] =
            "A DefaultRGB colour space on another page satisfies the check here, and veraPDF " +
            "refuses that file; a DeviceRGB colour used inside a form XObject, in a file with no " +
            "output intent, passes here and fails veraPDF (#418).",
        ["6.2.5-1"] =
            "Only ExtGStates that page content applies are checked: a /TR applied inside a form " +
            "passes here and fails veraPDF (#418).",
        ["6.2.6-1"] =
            "An image's /Intent is not checked: /Intent /Bogus passes here and fails veraPDF. A " +
            "/Bogus rendering intent set by the ri operator, or by /RI in an ExtGState applied " +
            "inside a form, passes here and fails veraPDF too (#418).",
        ["6.2.8-3"] =
            "An Interpolate true image drawn from a form XObject, and an inline image with /I " +
            "true, pass here and fail veraPDF (#418).",
        ["6.2.11.7.2-1"] =
            "Only Type 0 fonts using Identity-H or Identity-V that page content selects with Tf " +
            "are checked: a Type 3 font with glyph names outside the Adobe Glyph List and no " +
            "/ToUnicode passes here and fails veraPDF, and so does an Identity-H font with no " +
            "/ToUnicode shown inside a form XObject (#418).",
        ["6.2.11.8-1"] =
            "A .notdef glyph shown in text rendering mode 3 passes here and fails veraPDF (#418).",
        ["6.3.1-1"] = "An annotation of an unknown subtype passes here and fails veraPDF (#418).",
        ["6.3.3-1"] =
            "A rectangle with one zero dimension is exempt from the appearance requirement here, " +
            "where the clause needs both to be zero; such an annotation with no /AP passes here " +
            "and fails veraPDF (#418).",
        ["6.5.1-1"] =
            "Actions on outline items are not checked: a Launch action there passes here and " +
            "fails veraPDF (#418).",
        ["6.6.2.1-2"] =
            "Only the catalog, page, field and embedded-file packets are checked: a bytes " +
            "attribute in an image's /Metadata passes here and fails veraPDF (#418).",
        ["6.6.2.3.1-1"] =
            "Property names inside a predefined namespace are not checked: dc:fooBar passes here " +
            "and fails veraPDF (#418).",
        ["6.6.2.3.1-2"] =
            "Property names inside a predefined namespace are not checked: dc:fooBar passes here " +
            "and fails veraPDF (#418).",
        ["6.7.4-1"] =
            "/Lang in marked-content property lists is not checked: an invalid tag there passes " +
            "here and fails veraPDF (#418).",
        // Partial: the rule reads only what page content reaches, and the 2026-11-21 sweep (#418)
        // found veraPDF refusing the same construct inside a form XObject that page content draws.
        // The probe for each id put the construct in a form and, as a control, on the page itself;
        // veraPDF failed both and this library failed only the page. Type 3 glyph procedures,
        // patterns and annotation appearances were not probed.
        ["6.1.8-1"] =
            "Font and colourant names are read from the page's own resources only: a font whose " +
            "/BaseFont is not valid UTF-8, shown inside a form XObject, passes here and fails " +
            "veraPDF (#418).",
        ["6.1.13-8"] =
            "Only page content is checked for graphics-state nesting: 30 nested q operators " +
            "inside a form XObject pass here and fail veraPDF (#418).",
        ["6.1.13-9"] =
            "Only DeviceN spaces that page content selects with cs or CS are checked: one with " +
            "33 colourants, selected inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.4.2-1"] =
            "Only ICCBased spaces that page content selects with cs or CS are checked: one whose " +
            "profile has device class abst, selected inside a form XObject, passes here and " +
            "fails veraPDF (#418).",
        ["6.2.4.3-3"] =
            "A DeviceCMYK colour used inside a form XObject, in a file whose only output intent " +
            "is RGB, passes here and fails veraPDF (#418).",
        ["6.2.4.3-4"] =
            "A DeviceGray colour used inside a form XObject, in a file with no output intent, " +
            "passes here and fails veraPDF (#418).",
        ["6.2.4.4-1"] =
            "Only DeviceN spaces that page content selects with cs or CS are checked: one with a " +
            "spot colourant that is missing from /Colorants, selected inside a form XObject, " +
            "passes here and fails veraPDF (#418).",
        ["6.2.5-2"] =
            "Only ExtGStates that page content applies are checked: a /TR2 other than /Default, " +
            "applied inside a form, passes here and fails veraPDF (#418).",
        ["6.2.5-3"] =
            "Only ExtGStates that page content applies are checked: an /HTP applied inside a " +
            "form passes here and fails veraPDF (#418).",
        ["6.2.5-4"] =
            "Only ExtGStates that page content applies are checked: an /HT with a /HalftoneType " +
            "other than 1 or 5, applied inside a form, passes here and fails veraPDF (#418).",
        ["6.2.5-5"] =
            "Only ExtGStates that page content applies are checked: an /HT with a /HalftoneName, " +
            "applied inside a form, passes here and fails veraPDF (#418).",
        ["6.2.5-6"] =
            "Only ExtGStates that page content applies are checked: an /HT with a " +
            "/TransferFunction, applied inside a form, passes here and fails veraPDF (#418).",
        ["6.2.8-1"] =
            "Only images that page content draws directly are checked: an image with " +
            "/Alternates, drawn inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.8-2"] =
            "Only images that page content draws directly are checked: an image with /OPI, drawn " +
            "inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.8.3-1"] =
            "Only JPXDecode images that page content draws directly are checked: one with two " +
            "colour channels, drawn inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.8.3-2"] =
            "Only JPXDecode images that page content draws directly are checked: one with two " +
            "colr boxes and no APPROX of 1, drawn inside a form XObject, passes here and fails " +
            "veraPDF (#418).",
        ["6.2.8.3-3"] =
            "Only JPXDecode images that page content draws directly are checked: one with a colr " +
            "box of METH 4, drawn inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.8.3-4"] =
            "Only JPXDecode images that page content draws directly are checked: one in " +
            "enumerated colour space 19, drawn inside a form XObject, passes here and fails " +
            "veraPDF (#418).",
        ["6.2.8.3-5"] =
            "Only JPXDecode images that page content draws directly are checked: one with bit " +
            "depths 8, 8 and 7, drawn inside a form XObject, passes here and fails veraPDF " +
            "(#418).",
        ["6.2.9-1"] =
            "Only forms that page content draws directly are checked: a form with /OPI, /PS or " +
            "/Subtype2 /PS that is drawn from inside another form passes here and fails veraPDF " +
            "(#418).",
        ["6.2.9-2"] =
            "Only forms that page content draws directly are checked: a form with /Ref that is " +
            "drawn from inside another form passes here and fails veraPDF (#418).",
        ["6.2.9-3"] =
            "Only XObjects that page content draws directly are checked: a /Subtype /PS XObject " +
            "drawn from inside a form passes here and fails veraPDF (#418).",
        ["6.2.10-1"] =
            "Only ExtGStates that page content applies are checked: /BM /Bogus applied inside a " +
            "form passes here and fails veraPDF (#418).",
        ["6.2.11.2-1"] =
            "Only fonts that page content selects with Tf are checked: a font dictionary with no " +
            "/Type, shown inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.11.2-2"] =
            "Only fonts that page content selects with Tf are checked: a Type 0 font whose " +
            "descendant has /Subtype /Foo, shown inside a form XObject, passes here and fails " +
            "veraPDF (#418).",
        ["6.2.11.2-3"] =
            "Only fonts that page content selects with Tf are checked: a TrueType font with no " +
            "/BaseFont, shown inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.11.2-4"] =
            "Only fonts that page content selects with Tf are checked: a simple font with no " +
            "/FirstChar, shown inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.11.2-5"] =
            "Only fonts that page content selects with Tf are checked: a simple font with no " +
            "/LastChar, shown inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.11.2-6"] =
            "Only fonts that page content selects with Tf are checked: a simple font with no " +
            "/Widths, shown inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.11.2-7"] =
            "Only fonts that page content selects with Tf are checked: an embedded /FontFile3 " +
            "with /Subtype /Foo, in a font shown inside a form XObject, passes here and fails " +
            "veraPDF (#418).",
        ["6.2.11.3.2-1"] =
            "Only fonts that page content selects with Tf are checked: an embedded CIDFontType2 " +
            "with no /CIDToGIDMap, shown inside a form XObject, passes here and fails veraPDF " +
            "(#418).",
        ["6.2.11.3.3-1"] =
            "Only fonts that page content selects with Tf are checked: a Type 0 font whose " +
            "/Encoding names no predefined CMap, shown inside a form XObject, passes here and " +
            "fails veraPDF (#418).",
        ["6.2.11.3.3-2"] =
            "Only fonts that page content selects with Tf are checked: an embedded CMap whose " +
            "/WMode differs between its stream dictionary and its program, in a font shown " +
            "inside a form XObject, passes here and fails veraPDF (#418).",
        ["6.2.11.4.1-1"] =
            "Only fonts that page content selects with Tf are checked: a non-embedded Helvetica " +
            "shown only inside a form XObject passes here and fails veraPDF (#418).",
        ["6.2.11.4.1-2"] =
            "Only fonts that page content selects with Tf are checked: a code beyond the glyph " +
            "count of an embedded CIDFontType2, shown inside a form XObject, passes here and " +
            "fails veraPDF (#418).",
        ["6.2.11.4.2-1"] =
            "Only fonts that page content selects with Tf are checked: a Type 1 subset whose " +
            "/CharSet omits a glyph, shown inside a form XObject, passes here and fails veraPDF " +
            "(#418).",
        ["6.2.11.4.2-2"] =
            "Only fonts that page content selects with Tf are checked: an embedded CIDFontType2 " +
            "whose /CIDSet omits a shown CID, shown inside a form XObject, passes here and fails " +
            "veraPDF (#418).",
        ["6.2.11.5-1"] =
            "Only fonts that page content selects with Tf are checked: a TrueType font whose " +
            "/Widths differ from the embedded program, shown inside a form XObject, passes here " +
            "and fails veraPDF (#418).",
        ["6.2.11.6-1"] =
            "Only fonts that page content selects with Tf are checked: a non-symbolic TrueType " +
            "program with no cmap table, in a font shown inside a form XObject, passes here and " +
            "fails veraPDF (#418).",
        ["6.2.11.6-2"] =
            "Only fonts that page content selects with Tf are checked: a non-symbolic TrueType " +
            "font with /Encoding /MacExpertEncoding, shown inside a form XObject, passes here " +
            "and fails veraPDF (#418).",
        ["6.2.11.6-3"] =
            "Only fonts that page content selects with Tf are checked: a symbolic TrueType font " +
            "with an /Encoding entry, shown inside a form XObject, passes here and fails veraPDF " +
            "(#418).",
        ["6.2.11.6-4"] =
            "Only fonts that page content selects with Tf are checked: a symbolic TrueType font " +
            "whose program has several cmap subtables and no (3,0), shown inside a form XObject, " +
            "passes here and fails veraPDF (#418).",
        // 6.1.8-1 moved to Implemented (NameUtf8Rule: CheckStructureTypeNames now delegates to
        //   StructureTree.Analyze for a full-depth walk; oracle fixture pdfa2b-deep-struct-invalid-utf8
        //   confirmed veraPDF fires on the 2nd-level /S name; pdfa2b-deep-struct-valid-utf8 FP-safe).
        // 6.2.3-1 moved to Implemented (META batch, 2026-07-02): OutputIntentRule now validates the
        //   DestOutputProfile ICC header device class (prtr/mntr/scnr/spac) and major version < 5, in
        //   addition to the acsp signature and /N; positive-knowledge/FP-safe (fires only on a
        //   read device-class tag outside the permitted four or version >= 5), mirroring §6.2.4.2-1.
        // 6.2.4.2-2 moved to Implemented (Batch N3 continued, 2026-07-02): overprint state threaded
        //   into Form XObjects at every Do site; see PdfAOutOfScope comment for full detail.
        // 6.2.4.3-2/-3/-4 moved to Implemented (COL batch, 2026-07-02): DocumentDeviceColourTypes now
        //   also detects device colour reached via image XObject /ColorSpace entries (drawn images from
        //   page content and reachable non-page streams) and via the /Alternate space of named colour
        //   spaces selected by cs/CS operators (Separation, DeviceN, uncoloured Pattern base); the
        //   DefaultRGB/CMYK/Gray exemptions and per-type output-intent matching are unchanged.
        // 6.2.4.4-2 moved to Implemented (COL batch, 2026-07-02): SeparationConsistencyRule now
        //   collects Separations from drawn image /ColorSpace entries and from the /Alternate spaces
        //   of selected named colour spaces; FP-safety invariant preserved (pool only grows toward
        //   what veraPDF already compares).
        // 6.2.2-1 moved to Implemented (Batch N1 — ContentStreamOperatorRule now scans drawn Form
        //   XObjects, all CharProcs of Tf-selected Type 3 fonts, and all annotation /AP /N appearance
        //   streams via GetReachableContentStreams; reachability policy empirically confirmed against
        //   veraPDF 1.30.2 on 2026-06-23).
        // 6.1.10-1 moved to Implemented (Batch N2 — InlineImageFilterRule now scans drawn Form
        //   XObjects, all CharProcs of Tf-selected Type 3 fonts, and all annotation /AP /N appearance
        //   streams via GetReachableContentStreams; same reachability policy as ContentStreamOperatorRule).
        // 6.2.2-2 moved to Implemented (Batch N7, 2026-07-02): Pattern names (scn/SCN when active
        //   fill/stroke CS is literally "Pattern") and Properties names (BDC/DP named-resource form,
        //   two Name operands) are now detected in both the page-level pass (via ContentStreamUsage.Analyze,
        //   new UsedPatterns + UsedPropertiesNames) and the non-page pass (ScanStreamForInheritedResources,
        //   colour-space state tracking + prevName slot). FP-safety: Pattern fires only when current CS
        //   is literally "Pattern" (direct CS, not a named resource); Properties fires only when prevName
        //   != null (named-resource form, not inline dict); both fire only when the name IS defined in
        //   the ancestor /Pattern or /Properties subdictionary (inheritedResourceNames model). Oracle
        //   fixtures: pdfa2b-inherited-resource-pattern-violation (VIOLATING) and
        //   pdfa2b-inherited-resource-pattern-compliant (COMPLIANT). 2026-07-02.
        // 6.2.11.4.1-1/-2 and 6.2.11.5-1 moved to Implemented (Wave 2a+2b, 2026-07-02): glyph presence
        //   and width are checked across every embedded font path — CIDFontType2 (Identity or stream
        //   CIDToGIDMap), Type0 with an embedded non-Identity CMap, simple non-symbolic TrueType,
        //   CIDFontType0/CFF (Type2 charstring width interpreter + FDSelect/FDArray), and simple Type1
        //   (charstring hsbw/sbw). Widths are FontMatrix-scaled to PDF text space; an unresolvable
        //   width is skipped (FP-safe). Cross-validated against veraPDF 1.30.2.
        // 6.6.2.1-4 moved to Implemented (META batch, 2026-07-02): MetadataRule now runs the
        //   bytes/encoding/UTF-8 packet checks on the catalog /Metadata, every page /Metadata, every
        //   AcroForm field /Metadata (field tree incl. terminal-field /Kids), and every embedded-file
        //   stream /Metadata (name tree under /Names /EmbeddedFiles + annotation file attachments);
        //   deduplicated by object number.
        // 6.6.2.3.3-1 moved to Implemented: pdfaExtension:schemas rdf:Bag check + pdfaExtension
        //   prefix check added; probe-confirmed against veraPDF 1.30.2 (2026-06-23).
        // 6.6.2.3.3-5 moved to Implemented: pdfaSchema:property rdf:Seq check + null/pdfaSchema
        //   prefix check added (null-prefix leniency probe-confirmed 2026-06-23).
        // 6.6.2.3.3-6 moved to Implemented: pdfaSchema:valueType rdf:Seq check + null/pdfaSchema
        //   prefix check added (same null-prefix leniency as -5, probe-confirmed 2026-06-23).
        // 6.6.2.3.3-15 moved to Implemented: pdfaType:field rdf:Seq check + null/pdfaType
        //   prefix check added (same null-prefix leniency, probe-confirmed 2026-06-23).
        // 6.6.2.3.1-2 moved to Implemented: clean-room predefined property type table added for
        //   dc:, xmp:, pdf:, and pdfaid: namespaces (XMP Spec 2005 §8); FP-safe by construction —
        //   any property not in the table is skipped (unknown → no finding).
        // 6.6.2.3.3-8 moved to Implemented (META batch, 2026-07-02): ExtensionSchemaRule now enforces
        //   isValueTypeDefined on pdfaProperty:valueType — the declared type must be a predefined XMP
        //   value type, a container form (bag/seq/alt/Lang Alt) over a defined item type, or a custom
        //   value type declared in the same schema; else fires -8. Predefined set is the well-established
        //   XMP §8 catalogue (FP-safe: schema-declared custom types are always accepted).
        // 6.6.2.3.3-17 moved to Implemented (META batch, 2026-07-02): same isValueTypeDefined check on
        //   pdfaField:valueType (fires -17). Prefix check unchanged; the two conditions share the sub-clause.
        // 6.1.9-1 moved to Implemented (ObjectLayoutRule: endobj-EOL boundary checks added;
        //   scoped to newest-revision objects via Revisions; oracle fixture pdfa2b-endobj-bad-eol
        //   confirmed veraPDF fires; single-revision files have prevXrefEnd==0 so all objects are checked).
        // 6.1.13-10 moved to PdfAOutOfScope (predefined named-CMap character-collection maxima; tracked in Backlog issue TBD).
        // 6.2.11.3.1-1 moved to PdfAOutOfScope (predefined-CMap CIDSystemInfo registry table; tracked in Backlog issue TBD).
        // 6.7.2.2-1 moved to Implemented (A2aStructureTypeRule: isDefined co-fires with 6.7.3.4-1
        //   on the same element: a non-standard /S with no role-map path to a standard
        //   type. Standard types are inherently defined; the condition is co-occurring with
        //   6.7.3.4-1 and adds no additional code path. Oracle fixtures:
        //   pdfa2a-defined-type-unmapped (VIOLATING) and pdfa2a-defined-type-rolemapped (COMPLIANT)).
        // 6.8-5 moved to PdfAOutOfScope (embedded PDF/A-1 recursion; tracked in Backlog issue TBD).
        // 6.4.3-1 moved to Implemented (SignatureRule: under-coverage revision analysis added;
        //   /Perms /DocMDP signature enumeration added; gap [c+d, fileLength) checked against
        //   Revisions — a later xref in the gap is a legitimate PAdES B-LT/B-LTA revision and
        //   does not fire; trailing garbage with no revision in gap fires; deduplication by ByteRange key).
    };

    private static readonly Dictionary<string, string> PdfADeferred = new(StringComparer.Ordinal)
    {
        ["6.2.10-2"] =
            "No rule checks the page group colour space: a page that uses transparency, with no " +
            "PDF/A output intent and no page group colour space, passes here and fails veraPDF " +
            "(#418).",
        ["6.2.11.7.3-1"] =
            "No rule implements the Level A requirement that a private-use character has an " +
            "ActualText replacement (#418).",
        // 6.1.6-2 moved to Implemented (HexStringRule: byte scan runs before lazy object resolution;
        //   oracle fixture pdfa2b-hex-invalid-digit confirmed veraPDF fires on <1G0> in page dict;
        //   reader's InvalidDataException is caught by PdfPreflight.Validate outer catch after rule fires).
        // 6.1.8-1 moved to PdfAPartial (font BaseFont + colour colourant + structure-type names).
        // 6.1.12-2 moved to Implemented (DocMdpReferenceRule).
        // 6.2.4.2-2 moved to PdfAPartial (Batch N3), then Implemented (Batch N3 continued, 2026-07-02):
        //   OverprintRule now threads overprint state into Form XObjects via Do; see PdfAOutOfScope.
        // 6.2.8.3-1..-5: removed from Deferred; Jpeg2000Rule now implements all five for both
        // JP2 box files and raw codestreams. -2/-3/-4 correctly do not apply to raw codestreams
        // (which carry no colr boxes) — this is not a gap but correct per-spec scoping.
        // 6.4.3-1/-2/-3 moved to Implemented/Partial (SignatureRule).
        // 6.7.3.3-1 moved to Implemented (LogicalStructureRule: /StructTreeRoot presence check in
        //   the document catalog; veraPDF clause 6.7.3.3 testNumber 1 confirmed on the
        //   pdfa2a-no-structure oracle fixture — the only failed check on that document).
        // 6.7.3.4-1/-2/-3 moved to Implemented (A2aStructureTypeRule: structure-tree walker —
        //   non-standard type unmapped, circular role-map, standard type remapped to non-standard).
        // 6.7.4-1 moved to Implemented (A2aLangSyntaxRule: catalog /Lang + struct-elem /Lang syntax).
    };

    // PDF/A checks deliberately not implemented in-process (tracked in follow-up issues; excluded from
    // the feasible-coverage denominator).
    private static readonly Dictionary<string, string> PdfAOutOfScope = new(StringComparer.Ordinal)
    {
        // 6.1.13-10 and 6.2.11.3.1-1 are Partial (see PdfAPartial): their common Identity/embedded-CMap
        //   paths are implemented + veraPDF-verified; only the predefined-CJK-CMap residual is infeasible.
        ["6.8-5"] = "Out of scope (tracked in #140): embedded PDF/A-1 recursion needs a PDF/A-1 profile (not on roadmap); embedded PDF/A-2 is validated recursively.",
        // 6.2.4.2-2 moved to Implemented (Batch N3 continued, 2026-07-02): overprint state (OP/op/OPM,
        //   fill/stroke ICCBased-CMYK flags) is now threaded into Form XObjects at every Do site in the
        //   page-level scan, using the caller's current GState as the form's initial state. Form-local
        //   cs/gs operators override the inherited state; on return the caller's state is unchanged
        //   (implicit q/Q semantics). Recursion is depth-bounded and cycle-guarded by object number.
        //   CharProcs and annotation appearances remain isolated (FP-safe under-detection). Oracle
        //   fixtures: pdfa2b-overprint-inherited-violation (VIOLATING) and
        //   pdfa2b-overprint-inherited-compliant (COMPLIANT). Confirmed FP-safe against veraPDF 1.30.2.
    };

    // PDF/UA-1 checks a rule implements. Every other id is Partial, Deferred or OutOfScope; see the
    // maps below.
    private static readonly HashSet<string> PdfUaImplemented = new(StringComparer.Ordinal)
    {
        "5-1", "5-2",
        // UA batch — §5-3/5-4/5-5 XMP prefix checks (UaMetadataRule extended):
        "5-3", "5-4", "5-5",
        "6.1-1", "6.2-1", "7.1-4", "7.1-5", "7.1-6", "7.1-7", "7.1-8", "7.1-9", "7.1-10", "7.1-11", "7.1-12", "7.18.3-1",
        "7.2-29",
        // Batch B2 — §7.2 table, list, TOC containment:
        "7.2-3",   // UaTableContainmentRule: Table kids ∈ {TR, THead, TBody, TFoot, Caption}
        "7.2-4",   // UaTableContainmentRule: TR parent ∈ {Table, THead, TBody, TFoot}
        "7.2-5",   // UaTableContainmentRule: THead parent == Table
        "7.2-6",   // UaTableContainmentRule: TBody parent == Table
        "7.2-7",   // UaTableContainmentRule: TFoot parent == Table
        "7.2-8",   // UaTableContainmentRule: TH parent == TR
        "7.2-9",   // UaTableContainmentRule: TD parent == TR
        "7.2-10",  // UaTableContainmentRule: TR kids ∈ {TH, TD}
        "7.2-17",  // UaListContainmentRule: LI parent == L
        "7.2-18",  // UaListContainmentRule: LBody parent == LI
        "7.2-19",  // UaListContainmentRule: L kids ∈ {L, LI, Caption}
        "7.2-20",  // UaListContainmentRule: LI kids ∈ {Lbl, LBody}
        "7.2-26",  // UaTocContainmentRule: TOCI parent == TOC
        "7.2-27",  // UaTocContainmentRule: TOC kids ∈ {TOC, TOCI, Caption}
        "7.2-36",  // UaTableContainmentRule: THead kids ∈ {TR}
        "7.2-37",  // UaTableContainmentRule: TBody kids ∈ {TR}
        "7.2-38",  // UaTableContainmentRule: TFoot kids ∈ {TR}
        // Batch B4 — §7.2 count and caption-position rules:
        "7.2-11",  // UaTableCountRule: at most one THead child of Table
        "7.2-12",  // UaTableCountRule: at most one TFoot child of Table
        "7.2-13",  // UaTableCountRule: TFoot requires TBody sibling
        "7.2-14",  // UaTableCountRule: THead requires TBody sibling
        "7.2-39",  // UaTableCountRule: at most one Caption child of Table
        "7.2-16",  // UaTableCountRule: Caption must be first or last kid of Table
        "7.2-28",  // UaTocContainmentRule: Caption must be first kid of TOC
        "7.2-40",  // UaListContainmentRule: Caption must be first kid of L
        // Batch A2 additions:
        "7.10-1", "7.10-2",   // UaOptionalContentRule: OC config /Name (non-empty) and no /AS
        "7.11-1",              // UaEmbeddedFileRule: embedded-file /F and /UF requirement
        "7.15-1",              // UaXfaRule: dynamic XFA (dynamicRender == "required") forbidden
        "7.18.1-2",            // UaAnnotContentsRule: non-Widget visible annot needs /Contents or /Alt (direct struct-elem Alt now resolved via B5 ParentTree index)
        "7.18.1-3",            // UaFormFieldAltRule: form field needs /TU or Widget struct-elem /Alt
        "7.18.2-1",            // UaTrapNetAnnotRule: TrapNet annots forbidden unless hidden/outside-crop
        "7.18.4-2",            // UaFormStructElemRule: Form struct elem without /Role must have exactly one OBJR child
        "7.18.5-2",            // UaLinkAnnotRule: Link annots require non-empty /Contents
        "7.18.6.2-1",          // UaMediaClipRule: media clip data dict must have /CT
        "7.18.6.2-2",          // UaMediaClipRule: media clip data dict must have /Alt
        // 7.20-1 is Partial, see PdfUaPartial (UaReferenceXObjectRule)
        "7.20-2",              // UaFormXObjectSemanticParentRule: structure-linked form drawn from 2+ distinct pages
        // Batch A3 — font clauses:
        // 7.21.3.2-1 is Partial, see PdfUaPartial (UaCidToGidMapRule)
        // 7.21.6-3 is Partial, see PdfUaPartial (UaSymbolicFontRule)
        // Batch A6 — TrueType cmap / Differences-compliance:
        // 7.21.6-1 is Partial, see PdfUaPartial (UaTrueTypeCmapRule)
        // 7.21.6-2 is Partial, see PdfUaPartial (UaTrueTypeCmapRule)
        // 7.21.6-4 is Partial, see PdfUaPartial (UaTrueTypeCmapRule)
        // Batch A4 — font clauses (CMap, CharSet, CIDSet):
        // 7.21.3.3-1 is Partial, see PdfUaPartial (UaCMapRule)
        // 7.21.3.3-2 is Partial, see PdfUaPartial (UaCMapRule)
        // 7.21.3.3-3 is Partial, see PdfUaPartial (UaCMapRule)
        // 7.21.4.2-1 is Partial, see PdfUaPartial (UaType1CharSetRule)
        // 7.21.4.2-2 is Partial, see PdfUaPartial (UaCidSetRule)
        // Batch A5a — rendering-mode-scoped font embedding:
        // 7.21.4.1-1 is Partial, see PdfUaPartial (UaFontEmbeddingRule)
        // Batch A5b — glyph-level clauses (Identity-H/V CIDFontType2-Identity scope):
        // 7.21.8-1 is Partial, see PdfUaPartial (UaNotdefGlyphRule)
        // 7.21.7-2 is Partial, see PdfUaPartial (UaToUnicodeForbiddenRule)
        // §7.21.7-1 — used-code Unicode mapping (derived; simple-font AGL model, composite skipped):
        // 7.21.7-1 is Partial, see PdfUaPartial (UaToUnicodeCharMappingRule)
        // Batch A5c — glyph presence (Identity-H/V CIDFontType2-Identity, Tr-3-exempt):
        // 7.21.4.1-2 is Partial, see PdfUaPartial (UaGlyphPresenceRule)
        // Batch A5d — glyph width consistency (Identity-H/V CIDFontType2-Identity, Tr-3-exempt):
        // 7.21.5-1 is Partial, see PdfUaPartial (UaGlyphWidthRule)
        // Batch B3 — §7.3/§7.7 alt-text, §7.9 Note IDs, §7.4.4 heading structure:
        "7.3-1",               // UaAltTextRule: Figure element must have non-empty /Alt or /ActualText
        "7.7-1",               // UaAltTextRule: Formula element must have non-empty /Alt or /ActualText
        "7.9-1",               // UaNoteIdRule: Note element must have a non-empty /ID
        "7.9-2",               // UaNoteIdRule: Note /ID values must be unique
        "7.4.4-1",             // UaHeadingRule: at most one H child per element
        "7.4.4-2",             // UaHeadingRule: H forbidden when document also uses Hn
        "7.4.4-3",             // UaHeadingRule: Hn forbidden when document also uses H
        // Batch B5 — §7.18 annotation↔structure binding (ParentTree reverse index):
        "7.18.1-1",            // UaAnnotStructureRule: non-Widget/Link/PrinterMark annot must be in Annot tag
        "7.18.4-1",            // UaAnnotStructureRule: Widget annot must be in Form tag
        "7.18.5-1",            // UaAnnotStructureRule: Link annot must be in Link tag
        "7.18.8-1",            // UaAnnotStructureRule: PrinterMark must not be in structure tree
        // Batch B6 — §7.2 natural-language determination (struct-elem attributes, annot /Contents, form /TU):
        "7.2-21",              // UaNaturalLanguageRule: StructElem /ActualText → element or ancestor must have /Lang
        // Batch B7 — §7.2 table grid rules (intersection + column/row span consistency):
        "7.2-15",              // UaTableGridRule: no two cells overlap in the table grid (hasIntersection)
        // Batch B8 — §7.2 natural-language determination (outline, XMP lang-alt):
        "7.2-2",               // UaOutlineLangRule: non-empty /Outlines requires catalog /Lang
        "7.2-33",              // UaOutlineLangRule: XMP rdf:Alt with x-default requires catalog /Lang
        "7.2-41",              // UaTableGridRule: all columns span same number of rows (numberOfColumnWithWrongRowSpan)
        "7.2-42",              // UaTableGridRule: rows with same number of columns — wider-row branch (wrongColumnSpan != null)
        "7.2-43",              // UaTableGridRule: rows with same number of columns — narrower-row branch (wrongColumnSpan == null)
        "7.2-22",              // UaNaturalLanguageRule: StructElem /Alt → element or ancestor must have /Lang
        "7.2-23",              // UaNaturalLanguageRule: StructElem /E → element or ancestor must have /Lang
        "7.2-24",              // UaNaturalLanguageRule: Annot /Contents → direct struct-elem must have /Lang
        "7.2-25",              // UaNaturalLanguageRule: FormField /TU → associated struct-elem must have /Lang
        // Batch B10 — §7.4.2 heading nesting, §7.5 connected headers:
        "7.4.2-1",             // UaHeadingNestingRule: Hn heading levels must not skip (hasCorrectNestingLevel)
        // 7.5-1 is Partial, see PdfUaPartial (UaTableHeaderRule)
        "7.5-2",               // UaTableHeaderRule: TD /Headers references unknown IDs (hasConnectedHeader, unknownHeaders!='')
        // Batch C1 — §7.2 natural-language determination for marked-content sequences:
        // 7.2-30 is Partial, see PdfUaPartial (UaMarkedContentLangRule)
        // 7.2-31 is Partial, see PdfUaPartial (UaMarkedContentLangRule)
        // 7.2-32 is Partial, see PdfUaPartial (UaMarkedContentLangRule)
        // 7.2-34 is Partial, see PdfUaPartial (UaMarkedContentLangRule)
        // Batch C2 — §7.1 artifact/tagged-content nesting:
        // 7.1-1 is Partial, see PdfUaPartial (UaArtifactTaggingRule)
        // 7.1-2 is Partial, see PdfUaPartial (UaArtifactTaggingRule)
        // Batch C3 — §7.1-3 SESimpleContentItem: real content must be tagged or marked as Artifact:
        // 7.1-3 is Partial, see PdfUaPartial (UaSimpleContentItemRule)
        // §7.16-1 — encryption dictionary /P bit 10 (#138):
        "7.16-1",              // UaEncryptionPermissionsRule: /Encrypt /P bit 10 set
    };

    // PDF/UA-1 checks the rules cover only partially (the common case is detected; some conditions
    // need a subsystem we do not have yet). The note records the gap, mirroring PdfAPartial.
    private static readonly Dictionary<string, string> PdfUaPartial = new(StringComparer.Ordinal)
    {
        // 7.18.1-2 moved to PdfUaImplemented (Batch B5 — UaAnnotContentsRule now resolves the
        // direct enclosing struct element's /Alt via the ParentTree reverse index).
        // Partial with an infeasible residual (mirrors PDF/A-2 6.2.11.3.1-1): common path verified,
        // predefined-CJK-CMap sub-condition needs a conformant CJK CIDFont oracle asset. Tracked in #139.
        ["7.21.3.1-1"] = "Identity and embedded-CMap CIDSystemInfo compared; the predefined-CMap registry match is authored but not cross-validated (needs a conformant CJK CIDFont oracle asset). Tracked in #139.",
        // Partial: the 2026-11-21 audit (#428) found a construct that veraPDF refuses and the rule
        // accepts, or a font form or content stream the rule skips. Each note names it.
        ["7.21.4.1-1"] =
            "Type 0 fonts are skipped and only fonts that page content selects with Tf are " +
            "examined: a visible non-embedded CIDFontType2 passes here and fails veraPDF, and so " +
            "does a non-embedded simple font shown only inside a form XObject (#428).",
        ["7.21.7-1"] =
            "Type 0, Type 3 and MMType1 fonts and fonts with no /Encoding are skipped: a visible " +
            "non-embedded CIDFontType2 with no /ToUnicode passes here and fails veraPDF. A " +
            "simple font with a glyph name outside the Adobe Glyph List and no /ToUnicode, shown " +
            "inside a form XObject, passes here and fails veraPDF too (#428).",
        ["7.21.7-2"] =
            "Only Identity CIDFontType2 fonts that page content selects with Tf are examined: a " +
            "simple font whose /ToUnicode maps the shown code to U+0000 passes here and fails " +
            "veraPDF, and so does an Identity CIDFontType2 font with that mapping shown inside a " +
            "form XObject (#428).",
        ["7.21.8-1"] =
            "Only Identity CIDFontType2 fonts that page content selects with Tf are examined: a " +
            "/Differences entry that maps a shown code to .notdef in a simple font passes here " +
            "and fails veraPDF, and so does code 0 shown in an Identity CIDFontType2 font inside " +
            "a form XObject (#428).",
        ["7.21.4.1-2"] =
            "Only visible shows of Identity CIDFontType2 fonts are examined; simple and CFF " +
            "fonts are not (#428).",
        ["7.21.5-1"] =
            "Only Identity CIDFontType2 fonts are examined, and a width mismatch of 2 units " +
            "passes where 7.21.5 allows at most 1 (#428).",
        ["7.20-1"] =
            "Only forms that page content draws directly are checked: a /Ref in a form drawn " +
            "from inside another form passes here and fails veraPDF (#428).",
        ["7.21.3.3-3"] =
            "Only the usecmap operator is read, not the CMap stream's /UseCMap key: a /UseCMap " +
            "naming a CMap outside the predefined set passes here and fails veraPDF (#428).",
        ["7.5-1"] =
            "One TH with a /Scope satisfies every TD in its table: a table whose other TH lacks " +
            "/Scope, and whose TDs have no /Headers, passes here and fails veraPDF (#428).",
        // The next entries come from the 2026-11-21 form-XObject sweep (#428): each rule reads only
        // what page content reaches, and veraPDF refuses the same construct inside a form XObject
        // that page content draws. The probe put the construct in a form and, as a control, on the
        // page; veraPDF failed both and this library failed only the page.
        ["7.1-1"] =
            "Only page content streams are scanned: an Artifact sequence nested in a " +
            "structure-linked sequence, inside a form XObject, passes here and fails veraPDF " +
            "(#428).",
        ["7.1-2"] =
            "Only page content streams are scanned: a structure-linked sequence nested in an " +
            "Artifact sequence, inside a form XObject, passes here and fails veraPDF (#428).",
        ["7.2-30"] =
            "Only page content streams are scanned: a /Span sequence with /ActualText and no " +
            "determinable language, inside a form XObject, passes here and fails veraPDF (#428).",
        ["7.2-31"] =
            "Only page content streams are scanned: a /Span sequence with /Alt and no " +
            "determinable language, inside a form XObject, passes here and fails veraPDF (#428).",
        ["7.2-32"] =
            "Only page content streams are scanned: a /Span sequence with /E and no determinable " +
            "language, inside a form XObject, passes here and fails veraPDF (#428).",
        ["7.2-34"] =
            "Only page content streams are scanned: text shown inside a form XObject with no " +
            "determinable language passes here and fails veraPDF (#428).",
        ["7.21.3.2-1"] =
            "Only fonts that page content selects with Tf are examined: an embedded CIDFontType2 " +
            "with no /CIDToGIDMap, shown inside a form XObject, passes here and fails veraPDF " +
            "(#428).",
        ["7.21.3.3-1"] =
            "Only fonts that page content selects with Tf are examined: a Type 0 font whose " +
            "/Encoding names no predefined CMap, shown inside a form XObject, passes here and " +
            "fails veraPDF (#428).",
        ["7.21.3.3-2"] =
            "Only fonts that page content selects with Tf are examined: an embedded CMap whose " +
            "/WMode differs between its stream dictionary and its program, in a font shown " +
            "inside a form XObject, passes here and fails veraPDF (#428).",
        ["7.21.4.2-1"] =
            "Only fonts that page content selects with Tf are examined: a Type 1 subset whose " +
            "/CharSet omits a glyph, shown inside a form XObject, passes here and fails veraPDF " +
            "(#428).",
        ["7.21.4.2-2"] =
            "Only fonts that page content selects with Tf are examined: an embedded CIDFontType2 " +
            "whose /CIDSet omits a shown CID, shown inside a form XObject, passes here and fails " +
            "veraPDF (#428).",
        ["7.21.6-1"] =
            "Only fonts that page content selects with Tf are examined: a non-symbolic TrueType " +
            "program with no cmap table, in a font shown inside a form XObject, passes here and " +
            "fails veraPDF (#428).",
        ["7.21.6-2"] =
            "Only fonts that page content selects with Tf are examined: a non-symbolic TrueType " +
            "font with /Encoding /MacExpertEncoding, shown inside a form XObject, passes here " +
            "and fails veraPDF (#428).",
        ["7.21.6-3"] =
            "Only fonts that page content selects with Tf are examined: a symbolic TrueType font " +
            "with an /Encoding entry, shown inside a form XObject, passes here and fails veraPDF " +
            "(#428).",
        ["7.21.6-4"] =
            "Only fonts that page content selects with Tf are examined: a symbolic TrueType font " +
            "whose program has several cmap subtables and no (3,0), shown inside a form XObject, " +
            "passes here and fails veraPDF (#428).",
        ["7.1-3"] =
            "Only page content streams are scanned: untagged text drawn inside a form XObject, " +
            "outside marked content, passes here and fails veraPDF (#428).",
    };

    // PDF/UA-1 checks deliberately not implemented in-process (tracked in follow-up issues; excluded
    // from the feasible-coverage denominator).
    private static readonly Dictionary<string, string> PdfUaOutOfScope = new(StringComparer.Ordinal)
    {
        // 7.21.3.1-1 is Partial (see PdfUaPartial): common Identity/embedded-CMap path verified;
        //   only the predefined-CJK-CMap residual is infeasible.
        // 7.20-2 moved to PdfUaImplemented (UaFormXObjectSemanticParentRule: a Form XObject with
        //   /StructParents drawn via Do from 2+ distinct pages is always a structural violation —
        //   the form has exactly one /ParentTree MCID-array, so it can record only one set of
        //   structural parents; multi-page invocation is the unambiguous FP-safe firing condition.
        //   Forms without /StructParents and single-page invocations are silently skipped.)
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
            else if (PdfUaOutOfScope.TryGetValue(id, out var oosNote))
                checks.Add(new ConformanceCheck(id, [PdfConformance.PdfUA1], ClauseOf(id), CoverageStatus.OutOfScope, oosNote));
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
        // 5-3/5-4/5-5 moved to PdfUaImplemented (UA batch — UaMetadataRule extended with
        //   XmpReader.GetPrefixOfNamespace; fires when the PDF/UA-id namespace is bound to a
        //   non-pdfuaid, non-null prefix and the property is present).
        // 7.18.6.2-1/-2 moved to PdfUaImplemented (UA batch — UaMediaClipRule: walks Screen
        //   annotation rendition actions to media clip data dicts; checks /CT and /Alt).
        // 7.18.1-3 moved to PdfUaImplemented (UA batch — UaFormFieldAltRule: form field /TU
        //   OR Widget struct-elem /Alt; hidden/outside-crop exemptions via UaAnnotationHelper).
        // 7.18.4-2 moved to PdfUaImplemented (UA batch — UaFormStructElemRule: Form struct
        //   elem without /Role must have exactly one OBJR child; /Role attribute exempts).
        // 7.16-1 moved to PdfUaImplemented (UaEncryptionPermissionsRule: /Encrypt /P bit 10; #138
        //   also added the PdfPreflight/CLI password parameter that a password-protected document
        //   needs to reach rule evaluation at all).

        // §7.1 artifact/tagging rules — Batch C2:
        // 7.1-1 and 7.1-2 moved to PdfUaImplemented (Batch C2 — UaArtifactTaggingRule: Artifact
        //   BDC inside struct-linked ancestor; non-Artifact BDC with MCID in ParentTree inside
        //   Artifact ancestor. MarkedContentSequence extended with HasArtifactAncestor and
        //   AncestorMcid; empirically verified against veraPDF 1.30.2 probe series).
        // 7.1-3 moved to PdfUaImplemented (Batch C3 — UaSimpleContentItemRule: SESimpleContentItem
        //   operator set Tj/TJ/'/", S/s/f/F/f*/B/B*/b/b*, EI (at ID), image-Do, sh; fires when
        //   EffectiveMcid==null AND !IsInsideArtifact; verified FP-free against veraPDF 1.30.2
        //   battery: tagged-text, named-ref-BDC, artifact+decoration, image-figure, path-in-tagged,
        //   path-in-artifact, color/state/clip, form-Do, artifact-only, writer UA-1 doc).

        // §7.21 font deferred notes — Batch A3 assessment:
        // 7.21.4.1-1 moved to PdfUaImplemented (Batch A5a — UaFontEmbeddingRule, rendering-mode-scoped).
        // 7.21.4.1-2 moved to PdfUaImplemented (Batch A5c — UaGlyphPresenceRule, Tr-3-exempt).
        // 7.21.5-1 moved to PdfUaImplemented (Batch A5d — UaGlyphWidthRule, Identity-H CIDFontType2;
        //   Tr-3-exempt; cross-validated against veraPDF 1.30.2: violation fires, unused and Tr-3 silent).
        // 7.21.3.1-1 moved to PdfUaPartial (Batch A4 — UaCidSystemInfoRule).
        // 7.21.3.3-1/-2/-3 moved to PdfUaImplemented (Batch A4 — UaCMapRule).
        // 7.21.4.2-1 moved to PdfUaImplemented (Batch A4 — UaType1CharSetRule).
        // 7.21.4.2-2 moved to PdfUaImplemented (Batch A4 — UaCidSetRule).
        // 7.21.6-1/-2/-4 moved to PdfUaImplemented (Batch A6 — UaTrueTypeCmapRule: non-symbolic TrueType
        //   cmap structure and Differences-AGL compliance; verified FP-free against veraPDF 1.30.2).
        // 7.21.7-1 moved to PdfUaImplemented (UaToUnicodeCharMappingRule: used simple-font (Type1/TrueType)
        //   codes must have a Unicode value by /ToUnicode or by AGL glyph-name derivation (base encoding +
        //   /Differences, incl. uniXXXX/uXXXXXX). Fires only when a used code has a glyph name that is
        //   non-AGL-resolvable and no /ToUnicode maps it; composite Type0 fonts and no-/Encoding fonts are
        //   skipped (FP-safe under-detection of veraPDF's CID/program-encoding derivation).
        // 7.21.7-2 moved to PdfUaImplemented (Batch A5b — UaToUnicodeForbiddenRule, shown-glyph-scoped).
        // 7.21.8-1 moved to PdfUaImplemented (Batch A5b — UaNotdefGlyphRule, Identity-H scope).
        // 7.21.4.1-2 moved to PdfUaImplemented (Batch A5c — UaGlyphPresenceRule, Tr-3-exempt).
        // 7.1-5 moved to PdfUaImplemented (Batch B9 — UaNonStandardTypeRule, non-standard type
        //   unmapped; standard-type set empirically verified complete against veraPDF 1.30.2).
        // 7.1-6 moved to PdfUaImplemented (Batch B1 — UaRoleMapRule, circular role-map).
        // 7.1-7 moved to PdfUaImplemented (Batch B1 — UaRoleMapRule, standard-type remapped).
        // 7.1-12 moved to PdfUaImplemented (Batch B1 — UaStructElemParentRule, missing /P).
        // 7.2-3/-4/-5/-6/-7/-8/-9/-10/-17/-18/-19/-20/-26/-27/-36/-37/-38 moved to
        //   PdfUaImplemented (Batch B2 — UaTableContainmentRule, UaListContainmentRule,
        //   UaTocContainmentRule: table/list/TOC containment parent-type and kid-type rules).
        // 7.2-11/-12/-13/-14/-16/-28/-39/-40 moved to PdfUaImplemented (Batch B4 —
        //   UaTableCountRule: table count/caption-position; UaTocContainmentRule +7.2-28;
        //   UaListContainmentRule +7.2-40: TOC/list caption-position rules).
        // 7.3-1 moved to PdfUaImplemented (Batch B3 — UaAltTextRule: Figure alt-text).
        // 7.7-1 moved to PdfUaImplemented (Batch B3 — UaAltTextRule: Formula alt-text).
        // 7.9-1 moved to PdfUaImplemented (Batch B3 — UaNoteIdRule: Note non-empty /ID).
        // 7.9-2 moved to PdfUaImplemented (Batch B3 — UaNoteIdRule: Note unique /IDs).
        // 7.4.4-1/-2/-3 moved to PdfUaImplemented (Batch B3 — UaHeadingRule: H/Hn structure).
        // 7.18.1-2 moved to PdfUaImplemented (Batch B5 — UaAnnotContentsRule, struct-elem /Alt
        //   resolved via ParentTree reverse index; direct enclosing elem only, no ancestors).
        // 7.18.1-1 moved to PdfUaImplemented (Batch B5 — UaAnnotStructureRule: non-Widget/Link/
        //   PrinterMark annots must be in Annot tag).
        // 7.18.4-1 moved to PdfUaImplemented (Batch B5 — UaAnnotStructureRule: Widget must be
        //   in Form tag).
        // 7.18.5-1 moved to PdfUaImplemented (Batch B5 — UaAnnotStructureRule: Link must be in
        //   Link tag).
        // 7.18.8-1 moved to PdfUaImplemented (Batch B5 — UaAnnotStructureRule: PrinterMark must
        //   not be in the structure tree).
        // 7.2-21/-22/-23 moved to PdfUaImplemented (Batch B6 — UaNaturalLanguageRule: StructElem
        //   /ActualText, /Alt, /E require element or ancestor /Lang; gContainsCatalogLang
        //   short-circuit; empty /Lang () counts as containsLang=true per veraPDF probe).
        // 7.2-24 moved to PdfUaImplemented (Batch B6 — UaNaturalLanguageRule: Annot /Contents
        //   requires direct struct-elem /Lang via /StructParent→/ParentTree; gContainsCatalogLang
        //   short-circuit; annotation-dict /Lang does not satisfy per veraPDF probe).
        // 7.2-25 moved to PdfUaImplemented (Batch B6 — UaNaturalLanguageRule: FormField /TU
        //   requires Widget's struct-elem /Lang via /StructParent→/ParentTree; gContainsCatalogLang
        //   short-circuit; field-dict /Lang does not satisfy per veraPDF probe).
        // 7.2-15/-41/-42/-43 moved to PdfUaImplemented (Batch B7 — UaTableGridRule: table cell
        //   intersection check and column/row span consistency; skip-occupied-cell placement
        //   algorithm verified against veraPDF 1.30.2 probe series).
        // 7.2-2 moved to PdfUaImplemented (Batch B8 — UaOutlineLangRule: non-empty /Outlines
        //   requires catalog /Lang; fires only when /Outlines /First is present; gContainsCatalogLang
        //   short-circuit; no-outline case confirmed non-firing by veraPDF 1.30.2 probe).
        // 7.2-33 moved to PdfUaImplemented (Batch B8 — UaOutlineLangRule: XMP rdf:Alt with
        //   x-default requires catalog /Lang; any lang-alt with x-default triggers (not just
        //   dc:title); confirmed by veraPDF 1.30.2 probe; XmpReader.HasXDefaultLangAlt helper).
        // 7.4.2-1 moved to PdfUaImplemented (Batch B10 — UaHeadingNestingRule: heading levels must
        //   not skip; tracks previous heading level in document order; fires when n > prevLevel + 1;
        //   prevLevel starts at 0; empirically verified against veraPDF 1.30.2 probe series).
        // 7.5-1/-2 moved to PdfUaImplemented (Batch B10 — UaTableHeaderRule: TD connected-header
        //   check; scoped TH in same table satisfies all TDs; explicit /Headers resolved against TH
        //   /ID set; no-TH table passes (undefined, not false); empirically verified against
        //   veraPDF 1.30.2 probe series).
        // 7.2-30/-31/-32/-34 moved to PdfUaImplemented (Batch C1 — UaMarkedContentLangRule: /Span
        //   BDC with /ActualText, /Alt, /E, and text-show operators require determinable language;
        //   gContainsCatalogLang short-circuit; content-stream BMC/BDC/EMC stack + inline-dict
        //   property parser added to ContentStreamUsage; veraPDF 1.30.2 probe confirmed).
        // 7.1-1/-2 moved to PdfUaImplemented (Batch C2 — UaArtifactTaggingRule: Artifact BDC inside
        //   struct-linked ancestor; non-Artifact BDC with MCID in ParentTree inside Artifact ancestor;
        //   MarkedContentSequence extended with HasArtifactAncestor + AncestorMcid; verified against
        //   veraPDF 1.30.2 probe series).
        // 7.1-3 remains deferred (Batch C3 probe — operator set fully pinned; blocker is Properties
        //   named-reference BDC resolution, see PdfUaDeferredNote switch above for details).

        // 7.20-2 moved to PdfUaImplemented (UaFormXObjectSemanticParentRule: /StructParents-bearing
        //   Form XObject drawn from 2+ distinct pages → structural violation; /StructParents absence
        //   and single-page invocations silently skipped; FP-safe under-detection preserved).

        _ => "structure-tree walker",
    };

    private static ConformanceCheck MakePdfA(string id, PdfConformance[] profiles)
    {
        if (PdfAOutOfScope.TryGetValue(id, out var oosNote))
            return new ConformanceCheck(id, profiles, ClauseOf(id), CoverageStatus.OutOfScope, oosNote);
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
