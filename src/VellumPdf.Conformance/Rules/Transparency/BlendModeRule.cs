// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Transparency;

/// <summary>
/// ISO 19005-2 §6.4 (Transparency). The current blend mode, set by the <c>/BM</c> entry of a
/// graphics-state parameter dictionary, shall be one of the standard separable or non-separable
/// blend modes defined in ISO 32000-1 (plus the deprecated <c>Compatible</c> alias). PDF/A-2
/// permits transparency, but a non-standard blend mode is not allowed.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.4 and ISO 32000-1:2008, 11.3.5 (Table 136). Clean-room:
/// derived from the specification text, not from any third-party validation profile.
/// <para>
/// This slice inspects the <c>/ExtGState</c> resources reachable through the page tree (including
/// inherited <c>/Resources</c>). Graphics states nested inside form XObjects, patterns, and
/// annotation appearance streams are validated in a later slice of #50c.
/// </para>
/// <para>
/// Known limitation (issue #127): §6.4 constrains the <em>current</em> blend mode — the one set by a
/// <c>gs</c> operator in a content stream. This rule flags a non-standard <c>/BM</c> present in any
/// <c>/ExtGState</c> resource without checking whether that graphics state is ever applied, so it can
/// over-flag an unused non-standard blend mode that veraPDF accepts (a false positive on contrived
/// input). Scoping to applied graphics states needs content-stream usage analysis, which the
/// validator does not yet perform; for an <c>/ExtGState</c> that is actually used — the common case —
/// the in-process and veraPDF verdicts agree.
/// </para>
/// </remarks>
internal sealed class BlendModeRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.4-blend-mode";

    public string Clause => "ISO 19005-2:2011, 6.4";

    private static readonly PdfName _extGState = new("ExtGState");
    private static readonly PdfName _bm = new("BM");

    private static readonly HashSet<string> _standardBlendModes = new(StringComparer.Ordinal)
    {
        "Normal", "Compatible", "Multiply", "Screen", "Overlay", "Darken", "Lighten",
        "ColorDodge", "ColorBurn", "HardLight", "SoftLight", "Difference", "Exclusion",
        "Hue", "Saturation", "Color", "Luminosity",
    };

    public void Evaluate(PreflightContext context)
    {
        // A graphics-state dictionary may be shared across pages; check each one only once.
        var checkedGStates = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_extGState)) is not PdfDictionary extGStates)
                continue;

            foreach (var entry in extGStates.Entries)
            {
                if (entry.Value is PdfIndirectReference r && !checkedGStates.Add(r.ObjectNumber))
                    continue;
                if (context.Resolve(entry.Value) is PdfDictionary gs)
                    CheckBlendMode(context, gs);
            }
        }
    }

    private void CheckBlendMode(PreflightContext context, PdfDictionary gs)
    {
        var bm = context.Resolve(gs.Get(_bm));
        switch (bm)
        {
            case null:
                return;
            case PdfName name:
                ReportIfInvalid(context, name);
                break;
            case PdfArray array:
                // An array lists fallbacks; every named entry must be a standard blend mode.
                for (var i = 0; i < array.Count; i++)
                    if (context.Resolve(array[i]) is PdfName candidate)
                        ReportIfInvalid(context, candidate);
                break;
        }
    }

    private void ReportIfInvalid(PreflightContext context, PdfName blendMode)
    {
        if (!_standardBlendModes.Contains(blendMode.Value))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"The blend mode /{blendMode.Value} is not one of the standard blend modes permitted in PDF/A-2.");
        }
    }
}
