// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Signing;

/// <summary>
/// Selects the PAdES conformance level to produce when signing.
/// Each level includes all lower levels.
/// </summary>
/// <remarks>
/// The <see cref="B_LT"/> and <see cref="B_LTA"/> levels write their DSS and archive-timestamp
/// data as incremental-update revisions. When signing a PDF/A document, those LTV revisions are
/// not validated against the PDF/A profile: PDF/A-2 does not define a <c>/DSS</c>, so the result
/// is a valid PAdES LTV signature whose LTV revisions are not guaranteed to remain PDF/A-2
/// conformant. The signature and its document <c>/ID</c> are preserved correctly.
/// </remarks>
public enum PadesLevel
{
    /// <summary>
    /// PAdES B-B (baseline). A PKCS#7/CAdES detached signature with no timestamp.
    /// This is the default level.
    /// </summary>
    B_B = 0,

    /// <summary>
    /// PAdES B-T (timestamp). Extends B-B by adding an RFC 3161 signature timestamp
    /// as an unsigned attribute (OID 1.2.840.113549.1.9.16.2.14) inside the CMS envelope.
    /// Requires <see cref="PdfSignatureSettings.TimestampClient"/> to be set.
    /// </summary>
    B_T = 1,

    /// <summary>
    /// PAdES B-LT (long-term). Extends B-T by appending a Document Security Store (DSS)
    /// incremental revision containing certificate chains and OCSP/CRL revocation evidence
    /// for each signature. Requires both <see cref="PdfSignatureSettings.TimestampClient"/>
    /// and <see cref="PdfSignatureSettings.RevocationClient"/> to be set.
    /// </summary>
    B_LT = 2,

    /// <summary>
    /// PAdES B-LTA (long-term with archive timestamp). Extends B-LT by appending a
    /// /DocTimeStamp archive document timestamp (<c>/SubFilter /ETSI.RFC3161</c>) as a
    /// further incremental revision covering all previously written bytes including the DSS.
    /// Requires both <see cref="PdfSignatureSettings.TimestampClient"/>
    /// and <see cref="PdfSignatureSettings.RevocationClient"/> to be set.
    /// </summary>
    B_LTA = 3,
}
