// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// Requested PDF/A conformance level for the document.
///
/// <para>
/// Setting a non-<see cref="None"/> value instructs VellumPdf to:
/// <list type="bullet">
///   <item><b>§6.6.4</b> — include the <c>pdfaid</c> XMP schema in the metadata stream. The schema
///         uses the namespace <c>http://www.aiim.org/pdfa/ns/id/</c> and carries <c>part</c> (2 for
///         this part), <c>conformance</c> (<c>A</c>, <c>B</c> or <c>U</c>), and the optional
///         <c>amd</c> and <c>corr</c> amendment and corrigendum identifiers. The clause is explicit
///         that these values do <em>not</em> by themselves determine conformance: that is settled by
///         Clause 5. They are a claim, not a proof.</item>
///   <item><b>§6.1.3</b> — write a document <c>/ID</c> array in the trailer.</item>
///   <item><b>§6.7.2.2</b> — set <c>/MarkInfo &lt;&lt; /Marked true &gt;&gt;</c> in the catalog. Note
///         this is a Level A requirement only: §6.7.1 says subclause 6.7 applies only to files
///         meeting Level A conformance, and that Level B and Level U may ignore it. Writing it for
///         <see cref="PdfA2b"/> and <see cref="PdfA2u"/> is permitted but not required.</item>
/// </list>
/// </para>
///
/// <para>
/// A fully conforming PDF/A file also requires:
/// <list type="bullet">
///   <item><b>§6.2.11.4.1</b> — embed the program of every font used to render text, and use only
///         programs that may be embedded for unlimited, universal rendering. Use
///         <c>Document.LoadTrueTypeFont</c> / <c>PdfDocument.UseTrueTypeFont</c>; the unembedded
///         Standard-14 faces are <strong>not</strong> valid in PDF/A.</item>
///   <item><b>§6.1.3</b> — no encryption. <c>PdfDocument.Save</c> refuses rather than emit an
///         encrypted file while <c>PdfDocument.Conformance</c> is set.</item>
///   <item><b>§6.2.3</b> — an ICC OutputIntent, <em>conditionally</em>: §6.2.4.3 is what makes one
///         mandatory, when uncalibrated device colour spaces are used. Its <c>/S</c> value stays
///         <c>GTS_PDFA1</c> in part 2, so there is no <c>GTS_PDFA2</c> to look for.
///         <c>PdfDocument.Save</c> emits an sRGB OutputIntent whenever
///         <c>PdfDocument.Conformance</c> is set, which satisfies the condition without having to
///         detect device colour use.</item>
/// </list>
/// </para>
///
/// <para>
/// Clause 5 defines the levels purely by exclusion, so the difference between them is small and
/// exact. Level A (§5.2) meets every requirement. Level B (§5.3) meets every requirement except
/// §6.2.11.7, Unicode character maps, and §6.7, Logical structure. Level U (§5.4) meets every
/// requirement except §6.7. Two further consequences of §5.1 are worth knowing: a conforming file
/// may use any valid ISO 32000-1 feature the standard does not explicitly forbid, so the rule set is
/// a deny list rather than an allow list; and the header version may be any value from 1.0 to 1.7,
/// with that value explicitly not usable in determining conformance.
/// </para>
///
/// <para>
/// Clause references are to ISO 19005-2:2011 and were re-derived against the standard's own text
/// (#418). Several of them previously carried ISO 19005-<em>1</em> numbering, under which clause 6.3
/// is Fonts; in ISO 19005-2 clause 6.3 is Annotations.
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
