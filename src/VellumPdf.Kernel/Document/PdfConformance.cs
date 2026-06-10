// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// Requested PDF/A conformance level for the document.
///
/// <para>
/// Setting a non-<see cref="None"/> value instructs VellumPdf to:
/// <list type="bullet">
///   <item>Include the <c>pdfaid</c> XMP schema in the metadata stream.</item>
///   <item>Write a document <c>/ID</c> array in the trailer.</item>
///   <item>Set <c>/MarkInfo &lt;&lt; /Marked true &gt;&gt;</c> in the catalog.</item>
/// </list>
/// </para>
///
/// <para>
/// A fully conforming PDF/A file also requires:
/// <list type="bullet">
///   <item>Embedded fonts — use <c>Document.LoadTrueTypeFont</c> / <c>PdfDocument.UseTrueTypeFont</c>;
///         Standard-14 unembedded fonts are <strong>not</strong> valid in PDF/A
///         (ISO 19005-2 §6.3.3).</item>
///   <item>No encryption — PDF/A prohibits the <c>/Encrypt</c> dictionary
///         (ISO 19005-2 §6.3.1).</item>
///   <item>An sRGB ICC OutputIntent — emitted automatically by <c>PdfDocument.Save</c>
///         when <c>Conformance != None</c> (ISO 19005-2 §6.2.2).</item>
/// </list>
/// </para>
/// </summary>
public enum PdfConformance
{
    /// <summary>No conformance declaration. Metadata and /ID are still written when Info is set.</summary>
    None,

    /// <summary>PDF/A-2b — basic conformance (visual preservation).</summary>
    PdfA2b,

    /// <summary>PDF/A-2u — unicode conformance (visual preservation + text extraction).</summary>
    PdfA2u,

    /// <summary>PDF/A-2a — accessible conformance (structural tagging required).</summary>
    PdfA2a,

    /// <summary>
    /// PDF/UA-1 (ISO 14289-1) — universal accessibility. Requires tagging, catalog /Lang,
    /// a document title with /ViewerPreferences /DisplayDocTitle true, and decorative content
    /// marked as /Artifact. Distinct from PDF/A (uses the pdfuaid XMP schema, not pdfaid).
    /// </summary>
    PdfUA1,
}
