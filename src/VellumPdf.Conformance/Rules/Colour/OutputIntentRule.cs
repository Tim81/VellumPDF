// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.2.3 (Output intents) and §6.2.4.3 (device colour spaces). A document that paints
/// device-dependent colour shall have a PDF/A (<c>GTS_PDFA1</c>) output intent (§6.2.4.3); the
/// output intent's <c>/DestOutputProfile</c> shall be a valid ICC profile stream with a component
/// count (<c>/N</c>) of 1, 3, or 4; and when more than one output intent carries a
/// <c>/DestOutputProfile</c>, all shall reference the same profile stream (§6.2.3).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.3 and 6.2.4.3 and ISO 32000-1:2008, 14.11.5 / 8.6.5.5. Clean-room:
/// derived from the specification text and empirical veraPDF-oracle probing (2026-06-23), not from any
/// third-party validation profile.
/// <para>
/// Per-type §6.2.4.3 semantics (confirmed empirically against veraPDF 1.30.2, 2026-06-23):
/// <list type="bullet">
///   <item><description>DeviceRGB: satisfied by a <c>/DefaultRGB</c> colour space in the page's
///   <c>/Resources /ColorSpace</c> dict, OR a PDF/A output intent whose <c>/DestOutputProfile</c>
///   is an ICC profile with data colour space <c>'RGB '</c> (offset 16).</description></item>
///   <item><description>DeviceCMYK: satisfied by <c>/DefaultCMYK</c>, OR a <c>'CMYK'</c>
///   output-intent ICC profile.</description></item>
///   <item><description>DeviceGray: satisfied by <c>/DefaultGray</c>, OR ANY PDF/A output intent
///   (the colour space of the intent's ICC profile does not matter for Gray).</description></item>
/// </list>
/// </para>
/// <para>
/// The <c>/DestOutputProfile</c> requirement is scoped to documents that paint device-dependent
/// colour (detected via <see cref="ContentStreamUsage"/>); veraPDF does not require an output intent
/// when no device colour is used, so neither does this rule (issue #128). Device colour reached only
/// through images, form XObjects, or patterns is not yet detected. The remaining requirements (a
/// present profile's ICC validity, <c>/N</c>, and the single-shared-profile constraint) are checked
/// unconditionally.
/// </para>
/// </remarks>
internal sealed class OutputIntentRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.3-output-intent";

    public string Clause => "ISO 19005-2:2011, 6.2.3";

    private static readonly PdfName _outputIntents = new("OutputIntents");
    private static readonly PdfName _s = new("S");
    private static readonly PdfName _destOutputProfile = new("DestOutputProfile");
    private static readonly PdfName _destOutputProfileRef = new("DestOutputProfileRef");
    private static readonly PdfName _colorSpace = new("ColorSpace");
    private static readonly PdfName _resources = new("Resources");
    private static readonly PdfName _defaultRgb = new("DefaultRGB");
    private static readonly PdfName _defaultCmyk = new("DefaultCMYK");
    private static readonly PdfName _defaultGray = new("DefaultGray");

    public void Evaluate(PreflightContext context)
    {
        // Distinct DestOutputProfile object numbers, for the single-profile constraint.
        var profileRefs = new HashSet<int>();
        // Whether a PDF/A (GTS_PDFA1) output intent supplies a DestOutputProfile.
        var hasPdfAProfile = false;
        // ICC colour space tags of PDF/A output-intent profiles found ('RGB ', 'CMYK', 'GRAY').
        // Populated from the ICC header data-colour-space field at byte offset 16 (ISO 15076-1 §7.2.6).
        var intentColourSpaces = new HashSet<string>(StringComparer.Ordinal);

        var intents = context.Resolve(context.Catalog.Get(_outputIntents)) as PdfArray;
        for (var i = 0; intents is not null && i < intents.Count; i++)
        {
            if (context.Resolve(intents[i]) is not PdfDictionary intent)
                continue;

            // /S may be an indirect reference (ISO 32000-1 §7.3.10), like every other value here.
            var s = (context.Resolve(intent.Get(_s)) as PdfName)?.Value;
            var isPdfA = s == "GTS_PDFA1";

            // §6.2.3-3: a PDF/X output intent shall not carry the DestOutputProfileRef key (ISO
            // 15930-7:2010, Annex A). This is independent of DestOutputProfile, so it is checked first.
            if (s == "GTS_PDFX" && intent.Get(_destOutputProfileRef) is not null)
                context.Report(
                    "ISO19005-2:6.2.3-3-dest-output-profile-ref",
                    Clause,
                    PreflightSeverity.Error,
                    "A PDF/X output intent contains a /DestOutputProfileRef key, which is not permitted in PDF/A-2.");

            var destRaw = intent.Get(_destOutputProfile);

            // A profile-less output intent characterises no colour; the device-colour-requires-an-
            // output-intent check after the loop catches a document that actually needs one.
            if (destRaw is null)
                continue;

            if (isPdfA)
                hasPdfAProfile = true;

            if (destRaw is PdfIndirectReference destRef)
                profileRefs.Add(destRef.ObjectNumber);

            var stream = context.ResolveStream(destRaw);
            if (stream is null)
            {
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    "The DestOutputProfile shall be an ICC profile stream.");
                continue;
            }

            if (context.Resolve(stream.Dictionary.Get(PdfName.N)) is { } nObj)
            {
                var n = nObj switch
                {
                    PdfInteger ni => (double)ni.Value,
                    PdfReal nr => nr.Value,
                    _ => double.NaN,
                };
                // /N is an integer that shall be 1, 3, or 4; a non-integral real (e.g. 3.9) is invalid.
                if (!double.IsNaN(n) && (n != Math.Floor(n) || (n != 1 && n != 3 && n != 4)))
                {
                    context.Report(
                        RuleId,
                        Clause,
                        PreflightSeverity.Error,
                        $"The DestOutputProfile /N shall be the integer 1, 3, or 4 (found {n}).");
                }
            }

            var icc = context.DecodeStream(stream);
            if (icc is null || !HasIccSignature(icc))
            {
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    "The DestOutputProfile shall be a valid ICC profile (missing 'acsp' signature).");
                // Invalid profile: do not attempt per-type colour-space matching. The §6.2.3 error
                // already covers this profile; firing §6.2.4.3 additionally would be double-reporting
                // and risks false positives on malformed-but-present intents.
            }
            else if (isPdfA)
            {
                // Read the ICC data colour space from the profile header at byte offset 16 (4 bytes,
                // ISO 15076-1:2010 §7.2.6). Recognised values: 'RGB ', 'CMYK', 'GRAY'.
                // Only add when the tag is a known value — unknown/zero tags are ignored (lenient,
                // FP-safe: we fire only when we POSITIVELY know the colour space is wrong).
                var cs = ReadIccDataColourSpace(icc);
                if (cs is "RGB " or "CMYK" or "GRAY")
                    intentColourSpaces.Add(cs);
            }
        }

        if (profileRefs.Count > 1)
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "When multiple output intents specify a DestOutputProfile, they shall all "
                + "reference the same ICC profile stream.");
        }

        // ISO 19005-2 §6.2.4.3: per-type device colour space requirements.
        // Each device colour type is satisfied independently by either a matching Default* colour space
        // in the page resources OR the appropriate output intent (with colour-matching for RGB/CMYK,
        // any intent for Gray). Empirically confirmed against veraPDF 1.30.2, 2026-06-23.
        var (usesRgb, usesCmyk, usesGray) = context.DocumentDeviceColourTypes();

        if (usesRgb || usesCmyk || usesGray)
        {
            // Collect Default* colour space presence across all pages.
            var (hasDefaultRgb, hasDefaultCmyk, hasDefaultGray) = CollectDefaultColourSpaces(context);

            // §6.2.4.3-2: DeviceRGB — satisfied by DefaultRGB OR an RGB-profile output intent.
            // We fire only when we POSITIVELY know the requirement is unmet: no intent at all, OR
            // we could read at least one intent colour space and none was RGB. When we have an
            // intent but could not determine its colour space (unknown ICC tag), we stay silent
            // (FP-safe: the §6.2.3 ICC-validity error covers the malformed profile).
            var rgbIntentSatisfied = hasPdfAProfile && (intentColourSpaces.Count == 0 || intentColourSpaces.Contains("RGB "));
            if (usesRgb && !hasDefaultRgb && !rgbIntentSatisfied)
            {
                context.Report(
                    "ISO19005-2:6.2.4.3-2-device-rgb",
                    "ISO 19005-2:2011, 6.2.4.3",
                    PreflightSeverity.Error,
                    "The document paints DeviceRGB but has neither a /DefaultRGB colour space nor a "
                    + "PDF/A output intent (GTS_PDFA1) with an RGB DestOutputProfile.");
            }

            // §6.2.4.3-3: DeviceCMYK — satisfied by DefaultCMYK OR a CMYK-profile output intent.
            var cmykIntentSatisfied = hasPdfAProfile && (intentColourSpaces.Count == 0 || intentColourSpaces.Contains("CMYK"));
            if (usesCmyk && !hasDefaultCmyk && !cmykIntentSatisfied)
            {
                context.Report(
                    "ISO19005-2:6.2.4.3-3-device-cmyk",
                    "ISO 19005-2:2011, 6.2.4.3",
                    PreflightSeverity.Error,
                    "The document paints DeviceCMYK but has neither a /DefaultCMYK colour space nor a "
                    + "PDF/A output intent (GTS_PDFA1) with a CMYK DestOutputProfile.");
            }

            // §6.2.4.3-4: DeviceGray — satisfied by DefaultGray OR ANY PDF/A output intent.
            if (usesGray && !hasDefaultGray && !hasPdfAProfile)
            {
                context.Report(
                    "ISO19005-2:6.2.4.3-4-device-gray",
                    "ISO 19005-2:2011, 6.2.4.3",
                    PreflightSeverity.Error,
                    "The document paints DeviceGray but has neither a /DefaultGray colour space nor a "
                    + "PDF/A output intent (GTS_PDFA1) with a DestOutputProfile.");
            }
        }
    }

    // Returns which Default* colour space names are present in ANY page's /Resources /ColorSpace dict.
    // A Default* entry in page resources satisfies the §6.2.4.3 requirement for all pages that use
    // the corresponding device colour type (empirically confirmed: veraPDF evaluates presence in
    // page resources, not document-wide or in inherited resources only — 2026-06-23).
    private static (bool DefaultRgb, bool DefaultCmyk, bool DefaultGray) CollectDefaultColourSpaces(
        PreflightContext context)
    {
        bool rgb = false, cmyk = false, gray = false;
        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, _resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_colorSpace)) is not PdfDictionary csDict)
                continue;
            if (csDict.Get(_defaultRgb) is not null) rgb = true;
            if (csDict.Get(_defaultCmyk) is not null) cmyk = true;
            if (csDict.Get(_defaultGray) is not null) gray = true;
        }
        return (rgb, cmyk, gray);
    }

    // Reads the ICC data colour space tag from the profile header at byte offset 16 (4 bytes).
    // Returns the 4-byte ASCII tag (e.g. "RGB ", "CMYK", "GRAY") or null when unreadable.
    // ISO 15076-1:2010 §7.2.6: the data colour space field is a 4-character ASCII signature.
    private static string? ReadIccDataColourSpace(byte[] icc)
    {
        if (icc.Length < 20) return null;
        return System.Text.Encoding.ASCII.GetString(icc, 16, 4);
    }

    // An ICC profile carries the file signature 'acsp' at byte offset 36 (ISO 15076-1, §7.2.4).
    private static bool HasIccSignature(byte[] icc)
        => icc.Length >= 40
            && icc[36] == (byte)'a' && icc[37] == (byte)'c'
            && icc[38] == (byte)'s' && icc[39] == (byte)'p';
}
