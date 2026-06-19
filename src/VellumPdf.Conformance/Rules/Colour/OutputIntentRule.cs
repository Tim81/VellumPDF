// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.2.2 (Output intents). Validates the structure of the document's PDF/A output
/// intent(s): a <c>GTS_PDFA1</c> output intent shall have a <c>/DestOutputProfile</c>; that
/// profile shall be a valid ICC profile stream with a component count (<c>/N</c>) of 1, 3, or 4;
/// and when more than one output intent carries a <c>/DestOutputProfile</c>, all shall reference
/// the same profile stream.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.2 and ISO 32000-1:2008, 14.11.5 / 8.6.5.5. Clean-room:
/// derived from the specification text, not from any third-party validation profile.
/// <para>
/// This rule validates output intents that are present. Whether an output intent is <em>required</em>
/// (i.e. whether device-dependent colour is actually used) depends on content-stream analysis,
/// which lands in a later colour-rule slice of #50c.
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
        if (context.Resolve(context.Catalog.Get(_outputIntents)) is not PdfArray intents)
            return;

        // Distinct DestOutputProfile object numbers, for the single-profile constraint.
        var profileRefs = new HashSet<int>();

        for (var i = 0; i < intents.Count; i++)
        {
            if (context.Resolve(intents[i]) is not PdfDictionary intent)
                continue;

            var isPdfA = intent.Get(_s) is PdfName { Value: "GTS_PDFA1" };
            var destRaw = intent.Get(_destOutputProfile);

            if (destRaw is null)
            {
                if (isPdfA)
                {
                    context.Report(
                        RuleId,
                        Clause,
                        PreflightSeverity.Error,
                        "A GTS_PDFA1 output intent shall contain a DestOutputProfile entry.");
                }
                continue;
            }

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

            if (stream.Dictionary.Get(PdfName.N) is PdfInteger n && n.Value is not (1 or 3 or 4))
            {
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"The DestOutputProfile /N shall be 1, 3, or 4 (found {n.Value}).");
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
    }

    // An ICC profile carries the file signature 'acsp' at byte offset 36 (ISO 15076-1, §7.2.4).
    private static bool HasIccSignature(byte[] icc)
        => icc.Length >= 40
            && icc[36] == (byte)'a' && icc[37] == (byte)'c'
            && icc[38] == (byte)'s' && icc[39] == (byte)'p';
}
