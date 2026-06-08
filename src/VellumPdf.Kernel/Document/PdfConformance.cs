// Copyright 2026 Timothy van der Ham (@Tim81)
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
/// <strong>This is a structural scaffold only.</strong> A conforming PDF/A file also
/// requires embedded fonts (no font substitution), an ICC output intent, and passes
/// a PDF/A validator. Those requirements are out of scope here and must be satisfied
/// by the caller.
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
}
