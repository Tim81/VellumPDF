// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts; // PredefinedCMaps
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.3.1 test 1 (Type0 CIDSystemInfo compatibility). The <c>CIDSystemInfo</c>
/// of a composite font's descendant <c>CIDFont</c> and its <c>CMap</c> must be compatible:
/// their <c>/Registry</c> and <c>/Ordering</c> values must be identical and
/// <c>CIDFont.Supplement</c> must be ≤ <c>CMap.Supplement</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.21.3.1 and cross-validated against veraPDF 1.30.2 (clause
/// 7.21.3.1, testNumber 1). The PDF/UA-1 predicate is identical to the PDF/A-2 §6.2.11.3.1-1
/// predicate, so this rule reuses the same logic as <c>FontStructureRule.CheckCidSystemInfo</c>.
/// <para>
/// <strong>Scope (Partial — same as the PDF/A-2 rule):</strong>
/// <list type="bullet">
///   <item><c>Identity-H</c> / <c>Identity-V</c>: always conformant — no check.</item>
///   <item>Other predefined named CMaps (e.g. <c>/UniGB-UCS2-H</c>): the registry table for these
///   CMaps is not embedded in the library, so this path is deferred — no finding is generated.
///   This mirrors the PDF/A-2 §6.2.11.3.1-1 deferral.</item>
///   <item>Embedded CMap streams (non-Identity, non-predefined-name <c>/Encoding</c> that resolves
///   to an indirect reference): the two <c>CIDSystemInfo</c> dictionaries are compared and a
///   finding is emitted if they disagree.</item>
/// </list>
/// </para>
/// <para>
/// Only fonts actually selected via a <c>Tf</c> operator are evaluated (usage-scoped, matching
/// veraPDF). An embedded CMap whose own <c>/CMapName</c> is <c>Identity-H</c> / <c>Identity-V</c>
/// is also exempt, consistent with veraPDF's exemption.
/// </para>
/// <para>
/// veraPDF probe: a Type0 font with an embedded CMap whose CIDSystemInfo /Registry differs from the
/// CIDFont's → veraPDF fires clause 7.21.3.1-1. The same font with matching CIDSystemInfo → veraPDF
/// accepts. Cross-validated using the existing §6.2.11.3.1-1 oracle-fixture construction.
/// </para>
/// </remarks>
internal sealed class UaCidSystemInfoRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.3.1-1";

    public string Clause => "ISO 14289-1:2014, 7.21.3.1";

    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _cidSystemInfo = new("CIDSystemInfo");
    private static readonly PdfName _registry = new("Registry");
    private static readonly PdfName _ordering = new("Ordering");
    private static readonly PdfName _supplement = new("Supplement");
    private static readonly PdfName _cmapName = new("CMapName");

    // The predefined CMap names of ISO 32000-1 Table 118 — single shared copy (see PredefinedCMaps).
    private static readonly IReadOnlySet<string> _predefinedCMaps = PredefinedCMaps.Names;

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateUsedFonts())
        {
            if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
                continue;

            CheckCidSystemInfo(context, font);
        }
    }

    private void CheckCidSystemInfo(PreflightContext context, PdfDictionary font)
    {
        var rawEncoding = font.Get(_encoding);
        var encoding = context.Resolve(rawEncoding);

        // Identity-H / Identity-V: always conformant — no check.
        if (encoding is PdfName { Value: "Identity-H" or "Identity-V" })
            return;

        // Any other predefined name (not a stream reference): deferred, no finding.
        if (encoding is PdfName { } namedEncoding && _predefinedCMaps.Contains(namedEncoding.Value))
            return;

        // Only proceed if /Encoding resolves to an embedded CMap stream (indirect reference to stream).
        if (context.ResolveStream(rawEncoding) is not { } cmapStream)
            return;

        // An embedded CMap whose own /CMapName is Identity-H/V is exempt.
        if (context.Resolve(cmapStream.Dictionary.Get(_cmapName)) is PdfName { Value: "Identity-H" or "Identity-V" })
            return;

        // Read CIDSystemInfo from the CMap stream's dictionary.
        if (context.Resolve(cmapStream.Dictionary.Get(_cidSystemInfo)) is not PdfDictionary cmapSi)
            return;
        var cmapRegistry = PdfStringToLatin1(context, cmapSi.Get(_registry));
        var cmapOrdering = PdfStringToLatin1(context, cmapSi.Get(_ordering));
        var cmapSupplement = (context.Resolve(cmapSi.Get(_supplement)) as PdfInteger)?.Value;

        // Get the descendant CIDFont's CIDSystemInfo.
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont)
            return;
        if (context.Resolve(cidFont.Get(_cidSystemInfo)) is not PdfDictionary cidSi)
            return;
        var cidRegistry = PdfStringToLatin1(context, cidSi.Get(_registry));
        var cidOrdering = PdfStringToLatin1(context, cidSi.Get(_ordering));
        var cidSupplement = (context.Resolve(cidSi.Get(_supplement)) as PdfInteger)?.Value;

        // All four required values must be present.
        if (cmapRegistry is null || cmapOrdering is null || cmapSupplement is null
            || cidRegistry is null || cidOrdering is null || cidSupplement is null)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A composite font's CIDSystemInfo or its CMap's CIDSystemInfo is missing a required "
                + "/Registry, /Ordering, or /Supplement entry (§7.21.3.1).");
            return;
        }

        if (!string.Equals(cidRegistry, cmapRegistry, StringComparison.Ordinal)
            || !string.Equals(cidOrdering, cmapOrdering, StringComparison.Ordinal)
            || cidSupplement.Value > cmapSupplement.Value)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "CIDSystemInfo entries of the CIDFont and CMap dictionaries of a Type 0 font are not "
                + $"compatible (CIDSystemInfo Ordering = {cidOrdering}, CMap Ordering = {cmapOrdering}, "
                + $"CIDSystemInfo Registry = {cidRegistry}, CMap Registry = {cmapRegistry}, "
                + $"CIDSystemInfo Supplement = {cidSupplement}, CMap Supplement = {cmapSupplement}) "
                + "(§7.21.3.1).");
        }
    }

    private static string? PdfStringToLatin1(PreflightContext context, PdfObject? raw)
    {
        var bytes = context.Resolve(raw) switch
        {
            PdfLiteralString s => s.Bytes,
            PdfHexString h => h.Bytes,
            _ => (ReadOnlyMemory<byte>?)null,
        };
        return bytes is { } b ? System.Text.Encoding.Latin1.GetString(b.Span) : null;
    }
}
