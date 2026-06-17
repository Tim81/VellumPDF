// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Signing;

/// <summary>
/// Selects the PAdES conformance level to produce when signing.
/// Each level includes all lower levels.
/// </summary>
/// <remarks>
/// The <see cref="B_LT"/> and <see cref="B_LTA"/> levels write their DSS and archive-timestamp
/// data as incremental-update revisions. Signing a PDF/A-2b document at these levels preserves
/// PDF/A-2b conformance: the signed B-LT and B-LTA output validates against veraPDF's PDF/A-2b
/// profile (the <c>Signed_PdfA2b_BLT/BLTA_veraPdf_reportsCompliant</c> oracle tests gate this).
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
