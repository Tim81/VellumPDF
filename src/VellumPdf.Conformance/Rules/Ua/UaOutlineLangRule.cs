// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 natural-language determination rules (B8 batch):
/// <list type="bullet">
///   <item>7.2-2 (PDOutline): the document outline must have a determinable natural language —
///         satisfied by the catalog <c>/Lang</c>. Fires when the catalog has an <c>/Outlines</c>
///         entry with at least one outline item AND the catalog has no <c>/Lang</c>.</item>
///   <item>7.2-33 (XMPLangAlt): a metadata language-alternative array that carries an
///         <c>x-default</c> item must have a determinable language — satisfied by the
///         catalog <c>/Lang</c>. Fires when the catalog XMP contains any <c>rdf:Alt</c> with
///         an <c>x-default</c> item AND the catalog has no <c>/Lang</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>gContainsCatalogLang short-circuit (the dominant path).</strong>
/// ISO 14289-1 §7.2 already requires a non-empty catalog <c>/Lang</c>. Every conformant PDF/UA-1
/// file therefore has a catalog <c>/Lang</c>, which satisfies BOTH rules unconditionally
/// (<c>gContainsCatalogLang == true</c>). These rules can only fire in a document that already
/// fails 7.2-lang. When the catalog carries a non-empty <c>/Lang</c>, this rule emits nothing
/// and returns immediately.</para>
///
/// <para><strong>Clause semantics confirmed by veraPDF 1.30.2 probing:</strong></para>
/// <list type="bullet">
///   <item>7.2-2: fires only when the catalog has an <c>/Outlines</c> dict with at least one
///     outline item (<c>/First</c> present) AND the catalog has no <c>/Lang</c>. A document
///     without any <c>/Outlines</c> entry does NOT trigger 7.2-2 even when the catalog has no
///     <c>/Lang</c> — confirmed empirically.</item>
///   <item>7.2-33: fires when the catalog XMP contains any <c>rdf:Alt</c> language-alternative
///     array whose items include an <c>xml:lang="x-default"</c> entry AND the catalog has no
///     <c>/Lang</c>. Verified: the check applies to ANY lang-alt with x-default (dc:title,
///     dc:description, etc.) — not limited to dc:title. A lang-alt without x-default does NOT
///     trigger the rule. No XMP at all does NOT trigger the rule.</item>
/// </list>
///
/// <para>Clean-room: derived from ISO 14289-1:2014 §7.2 and empirically validated against
/// veraPDF 1.30.2. Not derived from any third-party implementation.</para>
/// </remarks>
internal sealed class UaOutlineLangRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-2-33";

    public string Clause => "ISO 14289-1:2014, 7.2";

    private static readonly PdfName _lang = new("Lang");
    private static readonly PdfName _outlines = new("Outlines");
    private static readonly PdfName _first = new("First");
    private static readonly PdfName _metadata = new("Metadata");

    public void Evaluate(PreflightContext context)
    {
        // ── gContainsCatalogLang short-circuit ────────────────────────────────
        // If the catalog has a non-empty /Lang, every lang-determination rule
        // passes unconditionally (gContainsCatalogLang == true in veraPDF terms).
        // This is the dominant, FP-safe path: a conformant PDF/UA-1 document
        // always has a catalog /Lang (UaLangRule), so this rule emits nothing.
        var catalogLangObj = context.Resolve(context.Catalog.Get(_lang));
        if (IsNonEmptyString(catalogLangObj))
            return;

        // ── 7.2-2: document outline requires determinable language ─────────────
        // Fire when the catalog has an /Outlines entry whose dict has a /First
        // child (i.e. the outline is non-empty) AND the catalog has no /Lang.
        Check72_2(context);

        // ── 7.2-33: XMP lang-alt x-default requires determinable language ─────
        // Fire when the catalog XMP contains any rdf:Alt with an x-default
        // xml:lang item AND the catalog has no /Lang.
        Check72_33(context);
    }

    private void Check72_2(PreflightContext context)
    {
        if (context.Resolve(context.Catalog.Get(_outlines)) is not PdfDictionary outlines)
            return; // no outline tree — rule does not apply

        // An outline dict without /First has no items — do not fire.
        if (outlines.Get(_first) is null)
            return;

        context.Report(
            "ISO14289-1:7.2-2",
            Clause,
            PreflightSeverity.Error,
            "The document outline (catalog /Outlines) requires a determinable natural language, "
            + "but the document catalog has no /Lang entry (ISO 14289-1:2014, 7.2, testNumber 2).");
    }

    private void Check72_33(PreflightContext context)
    {
        var stream = context.ResolveStream(context.Catalog.Get(_metadata));
        if (stream is null)
            return; // no metadata — 7.2-33 does not fire (no lang-alt to check)

        var bytes = context.DecodeStream(stream);
        if (bytes is null)
            return;

        var xmp = XmpReader.Parse(bytes);
        if (xmp is null)
            return; // malformed XMP — already caught by UaMetadataRule

        if (!XmpReader.HasXDefaultLangAlt(xmp))
            return; // no lang-alt with x-default item — rule does not apply

        context.Report(
            "ISO14289-1:7.2-33",
            Clause,
            PreflightSeverity.Error,
            "The catalog XMP metadata contains a language-alternative array (rdf:Alt) with an "
            + "x-default item, which requires a determinable natural language, but the document "
            + "catalog has no /Lang entry (ISO 14289-1:2014, 7.2, testNumber 33).");
    }

    // Returns true when the PDF object is a non-empty literal or hex string.
    private static bool IsNonEmptyString(PdfObject? obj) => obj switch
    {
        PdfLiteralString s => s.Bytes.Length > 0,
        PdfHexString h => h.Bytes.Length > 0,
        _ => false,
    };
}
