// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.2.2 (Output intents). A document that paints device-dependent colour shall have a
/// PDF/A (<c>GTS_PDFA1</c>) output intent with a <c>/DestOutputProfile</c>; that profile shall be a
/// valid ICC profile stream with a component count (<c>/N</c>) of 1, 3, or 4; and when more than one
/// output intent carries a <c>/DestOutputProfile</c>, all shall reference the same profile stream.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.2 and ISO 32000-1:2008, 14.11.5 / 8.6.5.5. Clean-room:
/// derived from the specification text, not from any third-party validation profile.
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
    public string RuleId => "ISO19005-2:6.2.2-output-intent";

    public string Clause => "ISO 19005-2:2011, 6.2.2";

    private static readonly PdfName _outputIntents = new("OutputIntents");
    private static readonly PdfName _s = new("S");
    private static readonly PdfName _destOutputProfile = new("DestOutputProfile");

    public void Evaluate(PreflightContext context)
    {
        // Distinct DestOutputProfile object numbers, for the single-profile constraint.
        var profileRefs = new HashSet<int>();
        // Whether a PDF/A (GTS_PDFA1) output intent supplies a DestOutputProfile.
        var hasPdfAProfile = false;

        var intents = context.Resolve(context.Catalog.Get(_outputIntents)) as PdfArray;
        for (var i = 0; intents is not null && i < intents.Count; i++)
        {
            if (context.Resolve(intents[i]) is not PdfDictionary intent)
                continue;

            // /S may be an indirect reference (ISO 32000-1 §7.3.10), like every other value here.
            var isPdfA = context.Resolve(intent.Get(_s)) is PdfName { Value: "GTS_PDFA1" };
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

        // ISO 19005-2 §6.2.2: a document that paints device-dependent colour shall have a PDF/A
        // output intent (GTS_PDFA1) with a DestOutputProfile. Covers both the absence of any output
        // intent and a profile-less one (issues #122, #128).
        if (!hasPdfAProfile && context.DocumentUsesDeviceColour())
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The document paints device-dependent colour but has no PDF/A output intent "
                + "(GTS_PDFA1) with a DestOutputProfile.");
        }
    }

    // An ICC profile carries the file signature 'acsp' at byte offset 36 (ISO 15076-1, §7.2.4).
    private static bool HasIccSignature(byte[] icc)
        => icc.Length >= 40
            && icc[36] == (byte)'a' && icc[37] == (byte)'c'
            && icc[38] == (byte)'s' && icc[39] == (byte)'p';
}
