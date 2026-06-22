// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.8-1. Font names (the <c>/BaseFont</c> value of every font dictionary reachable
/// through a page's resource dictionary), names of colourants in Separation and DeviceN colour
/// spaces, and structure type names (<c>/S</c> entries in structure elements), after expansion of any
/// <c>#XX</c> hex-escape sequences, shall be valid UTF-8 byte sequences.
/// </summary>
/// <remarks>
/// <para>
/// Authored from ISO 19005-2:2011, §6.1.8 and ISO 32000-1:2008, §7.3.5. Clean-room: derived from
/// the specification text, not from any third-party validation profile.
/// </para>
/// <para>
/// The parser (<see cref="VellumPdf.Reader.PdfDocumentReader"/>) decodes each PDF name's
/// <c>#XX</c> sequences to their raw bytes and then stores them as a Latin-1 string in
/// <see cref="PdfName.Value"/> (one byte → one char, losslessly). To recover the raw expanded
/// bytes for a UTF-8 validity test, the bytes are re-encoded as
/// <c>Encoding.Latin1.GetBytes(name.Value)</c>; those bytes are then tested with a strict
/// <c>System.Text.Encoding.UTF8</c> decoder that throws on invalid sequences. Pure-ASCII names
/// (U+0000–U+007F) are always valid UTF-8 and are fast-pathed before the encoding round-trip.
/// </para>
/// <para>
/// <b>Scoping (empirically verified against veraPDF 1.30.2):</b> the rule is <em>presence-based</em>
/// — all fonts and colour spaces found in each page's resource dictionary are checked, whether or
/// not the page content actually selects them. Structure elements are checked by walking the
/// <c>/StructTreeRoot /K</c> tree. The check is NOT gated on content-stream usage.
/// </para>
/// <para>
/// <b>Font names:</b> only <c>/BaseFont</c> is checked; the <c>/FontName</c> entry of a
/// <c>/FontDescriptor</c> is not checked (veraPDF 1.30.2 does not flag an invalid FontDescriptor
/// FontName when BaseFont is valid — verified empirically).
/// </para>
/// <para>
/// <b>Colour names:</b> Separation colourant names (element 1 of a <c>[/Separation name …]</c>
/// array) and DeviceN colourant names (every name in element 1 of a
/// <c>[/DeviceN [names …] …]</c> array) are checked for every colour space entry in
/// <c>/Resources /ColorSpace</c>.
/// </para>
/// <para>
/// <b>Structure type names:</b> the <c>/S</c> value of every <c>/StructElem</c> reachable from
/// the document catalog's <c>/StructTreeRoot</c> is checked (shallow walk: direct and array-of
/// children). Structure elements found only in a deeper nesting are not yet traversed — this is
/// a partial implementation of the structure-type branch.
/// </para>
/// </remarks>
internal sealed class NameUtf8Rule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.8-1";

    public string Clause => "ISO 19005-2:2011, 6.1.8";

    private static readonly PdfName _colorSpace = new("ColorSpace");
    private static readonly PdfName _structTreeRoot = new("StructTreeRoot");
    private static readonly PdfName _k = new("K");
    private static readonly PdfName _s = new("S");

    public void Evaluate(PreflightContext context)
    {
        // Rules are shared singletons evaluated per document, so all mutable state must be local
        // to this call (no instance fields) to stay reentrant under concurrent validation.
        var reported = new HashSet<string>(StringComparer.Ordinal);

        // ── 1. Font /BaseFont names (presence-based across all pages) ────────────────────────────
        CheckFontNames(context, reported);

        // ── 2. Colour space colourant names (Separation + DeviceN) ──────────────────────────────
        CheckColourSpaceNames(context, reported);

        // ── 3. Structure type names (/S in StructElem) ──────────────────────────────────────────
        CheckStructureTypeNames(context, reported);
    }

    // §6.1.8-1 (font names): check /BaseFont of every font reachable from page resources.
    // Presence-based — checked regardless of whether the page content selects the font.
    private void CheckFontNames(PreflightContext context, HashSet<string> reported)
    {
        var seenFonts = new HashSet<int>();
        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fonts)
                continue;
            foreach (var entry in fonts.Entries)
            {
                // Deduplicate font dictionaries shared by indirect reference across pages.
                if (entry.Value is PdfIndirectReference r && !seenFonts.Add(r.ObjectNumber))
                    continue;
                if (context.Resolve(entry.Value) is not PdfDictionary font)
                    continue;

                if (context.Resolve(font.Get(PdfName.BaseFont)) is PdfName baseFont)
                    ReportIfInvalid(context, reported, baseFont.Value);

                // Composite (Type 0) fonts embed a CIDFont array in /DescendantFonts;
                // each descendant also carries a /BaseFont.
                if (context.Resolve(font.Get(PdfName.Subtype)) is PdfName { Value: "Type0" }
                    && context.Resolve(font.Get(new PdfName("DescendantFonts"))) is PdfArray descendants)
                {
                    for (var i = 0; i < descendants.Count; i++)
                    {
                        if (context.Resolve(descendants[i]) is not PdfDictionary cidFont)
                            continue;
                        if (context.Resolve(cidFont.Get(PdfName.BaseFont)) is PdfName cidBase)
                            ReportIfInvalid(context, reported, cidBase.Value);
                    }
                }
            }
        }
    }

    // §6.1.8-1 (colour names): check Separation and DeviceN colourant names across all pages.
    // Presence-based — all colour spaces in /Resources /ColorSpace are checked, used or not.
    private void CheckColourSpaceNames(PreflightContext context, HashSet<string> reported)
    {
        var seenCs = new HashSet<int>();
        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_colorSpace)) is not PdfDictionary colorSpaces)
                continue;
            foreach (var entry in colorSpaces.Entries)
            {
                if (entry.Value is PdfIndirectReference r && !seenCs.Add(r.ObjectNumber))
                    continue;
                if (context.Resolve(entry.Value) is not PdfArray csArray || csArray.Count < 2)
                    continue;

                var csType = context.Resolve(csArray[0]) as PdfName;
                if (csType is null)
                    continue;

                if (csType.Value == "Separation")
                {
                    // [/Separation colourantName altSpace tintTransform]
                    if (context.Resolve(csArray[1]) is PdfName colourant)
                        ReportIfInvalid(context, reported, colourant.Value);
                }
                else if (csType.Value == "DeviceN")
                {
                    // [/DeviceN [name1 name2 …] altSpace tintTransform attrs?]
                    if (context.Resolve(csArray[1]) is PdfArray names)
                    {
                        for (var i = 0; i < names.Count; i++)
                        {
                            if (context.Resolve(names[i]) is PdfName name)
                                ReportIfInvalid(context, reported, name.Value);
                        }
                    }
                }
            }
        }
    }

    // §6.1.8-1 (structure type names): check /S of every reachable structure element.
    // Walks one level of /StructTreeRoot /K children (arrays or single elements).
    private void CheckStructureTypeNames(PreflightContext context, HashSet<string> reported)
    {
        try
        {
            if (context.Resolve(context.Catalog.Get(_structTreeRoot)) is not PdfDictionary root)
                return;
            WalkStructure(context, root, new HashSet<int>(), 0, reported);
        }
        catch
        {
            // Malformed structure tree must not produce a spurious finding.
        }
    }

    // Maximum depth to guard against pathological or cyclic structure trees.
    private const int MaxStructDepth = 512;

    private void WalkStructure(
        PreflightContext context, PdfDictionary node, HashSet<int> visited, int depth, HashSet<string> reported)
    {
        if (depth > MaxStructDepth)
            return;

        // Check /S on this node if it is a StructElem.
        if (context.Resolve(node.Get(PdfName.Type)) is PdfName { Value: "StructElem" }
            || node.Get(_s) is not null) // fall back: any node with /S is treated as a StructElem
        {
            if (context.Resolve(node.Get(_s)) is PdfName sType)
                ReportIfInvalid(context, reported, sType.Value);
        }

        // Walk children via /K.
        var k = context.Resolve(node.Get(_k));
        switch (k)
        {
            case PdfDictionary child:
                WalkStructChild(context, child, visited, depth, reported);
                break;
            case PdfArray children:
                for (var i = 0; i < children.Count; i++)
                {
                    if (context.Resolve(children[i]) is PdfDictionary childDict)
                        WalkStructChild(context, childDict, visited, depth, reported);
                }
                break;
        }
    }

    private void WalkStructChild(
        PreflightContext context, PdfDictionary child, HashSet<int> visited, int depth, HashSet<string> reported)
    {
        // Only descend into structure elements (skip MCID integer leaf entries etc.).
        if (context.Resolve(child.Get(PdfName.Type)) is not PdfName { Value: "StructElem" }
            && child.Get(_s) is null)
            return;
        // Cycle guard via object number.
        if (child.Get(PdfName.Type) is PdfIndirectReference r && !visited.Add(r.ObjectNumber))
            return;
        WalkStructure(context, child, visited, depth + 1, reported);
    }

    // Returns true when the Latin-1 string, re-encoded as raw bytes, is valid UTF-8.
    // Pure-ASCII strings (all chars ≤ 0x7F) are trivially valid and fast-pathed.
    private static bool IsValidNameUtf8(string latinValue)
    {
        // Fast path: all characters are ASCII (value ≤ 127) → always valid UTF-8.
        foreach (var ch in latinValue)
            if (ch > 0x7F)
                goto slowPath;
        return true;

    slowPath:
        // Recover the original expanded bytes (1 Latin-1 char = 1 byte, lossless).
        var bytes = System.Text.Encoding.Latin1.GetBytes(latinValue);
        try
        {
            // A strict UTF-8 decoder that throws on any invalid byte sequence.
            _ = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetCharCount(bytes);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    private void ReportIfInvalid(PreflightContext context, HashSet<string> reported, string nameValue)
    {
        if (IsValidNameUtf8(nameValue))
            return;
        // Report each distinct bad name value once per validation pass.
        if (!reported.Add(nameValue))
            return;
        context.Report(
            RuleId,
            Clause,
            PreflightSeverity.Error,
            $"The name value {nameValue} does not represent a correct UTF-8 character sequence (§6.1.8).");
    }
}
