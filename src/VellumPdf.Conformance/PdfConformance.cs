// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance;

/// <summary>
/// The conformance level a document is validated against.
/// </summary>
/// <remarks>
/// Each level corresponds to a published ISO standard. Rule coverage is being filled in
/// incrementally (issue #50); levels without a registered rule profile cause
/// <see cref="PdfPreflight.Validate(VellumPdf.Reader.PdfDocumentReader, PdfConformance)"/>
/// to throw <see cref="System.NotSupportedException"/>.
/// </remarks>
public enum PdfConformance
{
    /// <summary>PDF/A-2 conformance level B (basic) — ISO 19005-2.</summary>
    PdfA2B,

    /// <summary>PDF/A-2 conformance level U (Unicode) — ISO 19005-2.</summary>
    PdfA2U,

    /// <summary>PDF/A-2 conformance level A (accessible) — ISO 19005-2.</summary>
    PdfA2A,

    /// <summary>PDF/UA-1 (universal accessibility) — ISO 14289-1.</summary>
    PdfUA1,
}
