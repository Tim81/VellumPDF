// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.2.5 (Graphics state) and §6.2.6 (Rendering intents). An <c>/ExtGState</c> shall
/// not contain a <c>/TR</c> (transfer function) or <c>/HTP</c> key, and any <c>/TR2</c> shall have
/// the value <c>/Default</c> (§6.2.5). A rendering intent — set by an <c>/ExtGState</c>'s <c>/RI</c>
/// or by the <c>ri</c> content operator — shall be one of the four standard intents
/// (<c>RelativeColorimetric</c>, <c>AbsoluteColorimetric</c>, <c>Perceptual</c>, <c>Saturation</c>)
/// defined in ISO 32000-1 (§6.2.6).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.5 and 6.2.6, and ISO 32000-1:2008, 8.6.5.8 (Table 70).
/// Clean-room: derived from the specification text, not from any third-party validation profile.
/// <para>
/// Like <see cref="Transparency.BlendModeRule"/>, the <c>/ExtGState</c> checks are scoped to the
/// graphics states a page actually applies (via a <c>gs</c> operator, determined by
/// <see cref="ContentStreamUsage"/>): an <c>/ExtGState</c> resource that is never applied is not the
/// current state and is not a violation, matching veraPDF (issues #127, #128). The <c>ri</c> operator
/// intents are read from the same content scan. The halftone-dictionary constraints (§6.2.5 t4–t6,
/// the <c>/HT</c> subdictionary) need halftone parsing and are deferred; an image XObject's
/// <c>/Intent</c> entry is a separate, deferred vector.
/// </para>
/// </remarks>
internal sealed class GraphicsStateRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.5-extgstate";

    public string Clause => "ISO 19005-2:2011, 6.2.5";

    private static readonly PdfName _extGState = new("ExtGState");
    private static readonly PdfName _tr = new("TR");
    private static readonly PdfName _tr2 = new("TR2");
    private static readonly PdfName _htp = new("HTP");
    private static readonly PdfName _ri = new("RI");
    private static readonly PdfName _ht = new("HT");
    private static readonly PdfName _halftoneType = new("HalftoneType");
    private static readonly PdfName _halftoneName = new("HalftoneName");
    private static readonly PdfName _transferFunction = new("TransferFunction");

    private static readonly HashSet<string> _processColorants = new(StringComparer.Ordinal)
    {
        "Cyan", "Magenta", "Yellow", "Black",
    };

    private static readonly HashSet<string> _standardIntents = new(StringComparer.Ordinal)
    {
        "RelativeColorimetric", "AbsoluteColorimetric", "Perceptual", "Saturation",
    };

    public void Evaluate(PreflightContext context)
    {
        var checkedGStates = new HashSet<int>();
        // Each distinct non-standard rendering intent is reported once, regardless of how many pages
        // (via the `ri` operator) or graphics states (via /RI) set it.
        var reportedIntents = new HashSet<string>(StringComparer.Ordinal);

        foreach (var page in context.EnumeratePages())
        {
            var usage = ContentStreamUsage.Analyze(context, page);

            // §6.2.6: rendering intents set by the `ri` content operator.
            foreach (var intent in usage.RenderingIntents)
                CheckIntent(context, intent, reportedIntents);

            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_extGState)) is not PdfDictionary extGStates)
                continue;

            foreach (var entry in extGStates.Entries)
            {
                if (!usage.AppliedExtGStates.Contains(entry.Key.Value))
                    continue;
                if (entry.Value is PdfIndirectReference r && !checkedGStates.Add(r.ObjectNumber))
                    continue;
                if (context.Resolve(entry.Value) is PdfDictionary gs)
                    CheckGState(context, gs, reportedIntents);
            }
        }
    }

    private void CheckGState(PreflightContext context, PdfDictionary gs, HashSet<string> reportedIntents)
    {
        // §6.2.5-1: an ExtGState shall not contain the TR (transfer function) key.
        if (gs.Get(_tr) is not null)
            Report(context, "6.2.5-transfer-function", "6.2.5",
                "An applied ExtGState contains the /TR (transfer function) key, which is not permitted in PDF/A-2.");

        // §6.2.5-2: a TR2 key, if present, shall have the value /Default.
        if (gs.Get(_tr2) is not null && context.Resolve(gs.Get(_tr2)) is not PdfName { Value: "Default" })
            Report(context, "6.2.5-transfer-function-2", "6.2.5",
                "An applied ExtGState contains a /TR2 key with a value other than /Default, which is not permitted in PDF/A-2.");

        // §6.2.5-3: an ExtGState shall not contain the HTP (halftone phase) key.
        if (gs.Get(_htp) is not null)
            Report(context, "6.2.5-halftone-phase", "6.2.5",
                "An applied ExtGState contains the /HTP key, which is not permitted in PDF/A-2.");

        // §6.2.6: a rendering intent set via the ExtGState /RI shall be one of the standard intents.
        if (context.Resolve(gs.Get(_ri)) is PdfName intent)
            CheckIntent(context, intent.Value, reportedIntents);

        // §6.2.5: a halftone (dictionary or stream) shall have HalftoneType 1 or 5 and no HalftoneName.
        if (context.Resolve(gs.Get(_ht)) is PdfDictionary halftone)
        {
            if (context.Resolve(halftone.Get(_halftoneType)) is PdfInteger type && type.Value != 1 && type.Value != 5)
                Report(context, "6.2.5-halftone-type", "6.2.5",
                    $"An applied ExtGState's halftone has /HalftoneType {type.Value}; PDF/A-2 permits only 1 or 5.");
            if (halftone.Get(_halftoneName) is not null)
                Report(context, "6.2.5-halftone-name", "6.2.5",
                    "An applied ExtGState's halftone contains a /HalftoneName entry, which is not permitted in PDF/A-2.");

            // §6.2.5-6: TransferFunction rules per colorant:
            //   colorantName == 'Default' → exempt;
            //   null or standard process colorant (Cyan/Magenta/Yellow/Black) → TransferFunction MUST be absent;
            //   other (spot/custom) → TransferFunction MUST be present.
            // A type-1 halftone is used directly (colorantName == null); for type-5 each sub-entry's key is the colorantName.
            CheckHalftoneTransferFunction(context, halftone, colorantName: null);
        }
    }

    private static void CheckHalftoneTransferFunction(
        PreflightContext context, PdfDictionary halftone, string? colorantName, int depth = 0)
    {
        var halftoneTypeObj = context.Resolve(halftone.Get(_halftoneType));
        var halftoneTypeValue = (halftoneTypeObj as PdfInteger)?.Value ?? 1;

        // A type-5 halftone's colorant entries are themselves halftones of type 1/6/10/16 — never
        // type 5 — so only the top-level halftone is expanded. The depth guard means a malformed file
        // nesting a type-5 inside a type-5 is treated as a leaf rather than recursing without bound.
        if (halftoneTypeValue == 5 && depth == 0)
        {
            // Type-5: iterate colorant entries; skip the structural keys.
            foreach (var entry in halftone.Entries)
            {
                var key = entry.Key.Value;
                if (key is "Type" or "HalftoneType" or "HalftoneName")
                    continue;
                // Each value is a halftone sub-dictionary (or stream) keyed by colorant name.
                if (context.Resolve(entry.Value) is PdfDictionary subHalftone)
                    CheckHalftoneTransferFunction(context, subHalftone, colorantName: key, depth: depth + 1);
            }
        }
        else
        {
            // Type-1 (or unknown type): colorantName comes from the caller (null for a top-level halftone).
            // /Default is always exempt.
            if (colorantName == "Default")
                return;

            var hasTf = halftone.Get(_transferFunction) is not null;

            if (colorantName == null || _processColorants.Contains(colorantName))
            {
                // Standard process colorants and null (type-1) must NOT have a TransferFunction.
                if (hasTf)
                    Report(context, "6.2.5-halftone-transfer", "6.2.5",
                        colorantName == null
                            ? "An applied ExtGState's halftone contains a /TransferFunction entry, which is not permitted in PDF/A-2."
                            : $"An applied ExtGState's type-5 halftone entry /{colorantName} contains a /TransferFunction entry, which is not permitted in PDF/A-2.");
            }
            else
            {
                // Spot / other colorant — TransferFunction MUST be present.
                if (!hasTf)
                    Report(context, "6.2.5-halftone-transfer", "6.2.5",
                        $"An applied ExtGState's type-5 halftone entry /{colorantName} is missing a required /TransferFunction entry.");
            }
        }
    }

    private static void CheckIntent(PreflightContext context, string intent, HashSet<string> reportedIntents)
    {
        if (!_standardIntents.Contains(intent) && reportedIntents.Add(intent))
            Report(context, "6.2.6-rendering-intent", "6.2.6",
                $"The rendering intent /{intent} is not one of the four standard rendering intents permitted in PDF/A-2.");
    }

    private static void Report(PreflightContext context, string ruleSuffix, string clauseSuffix, string message)
        => context.Report(
            $"ISO19005-2:{ruleSuffix}", $"ISO 19005-2:2011, {clauseSuffix}", PreflightSeverity.Error, message);
}
