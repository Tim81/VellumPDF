// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Signature;

/// <summary>
/// ISO 19005-2 §6.4.3 (Digital signatures). Three sub-rules are implemented here:
/// <list type="bullet">
///   <item>§6.4.3-1: The /ByteRange shall cover the entire file excluding the /Contents hex token.</item>
///   <item>§6.4.3-2: The CMS SignedData shall include at least one X.509 certificate.</item>
///   <item>§6.4.3-3: The CMS SignedData shall contain exactly one SignerInfo.</item>
/// </list>
/// All rules are DEFENSIVE: a malformed or unrecognised structure suppresses the finding
/// (indeterminate → no false positive). CMS parsing uses the hand-written
/// <see cref="Asn1Reader"/> — no external dependencies.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.4.3. Clean-room: derived from the specification text
/// and RFC 5652, not from any third-party validation profile.
///
/// §6.4.3-1 scope note (Partial): the ByteRange check flags only the unambiguous,
/// revision-independent violations — the first segment not starting at byte 0, or the range
/// claiming more bytes than the file holds. The under-coverage case (the range ending before
/// EOF) is deferred, because a conformant PAdES B-LT/B-LTA signature legitimately ends before
/// EOF (a /DSS or document timestamp is appended afterwards) and veraPDF reports such files
/// compliant; distinguishing that from genuinely-uncovered trailing bytes needs revision-boundary
/// analysis. Also, signatures stored outside the AcroForm field tree (e.g. /Perms /DocMDP only,
/// without a /V reachable via the field tree) are not enumerated by the Reader.
/// </remarks>
internal sealed class SignatureRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.4.3";
    public string Clause => "ISO 19005-2:2011, 6.4.3";

    public void Evaluate(PreflightContext context)
    {
        var sigs = context.Reader.Signatures;
        if (sigs.Count == 0)
            return; // No signatures — nothing to check.

        var fileBytes = context.FileBytes;

        foreach (var sig in sigs)
        {
            // ── §6.4.3-1: ByteRange covers entire file (excluding /Contents token) ─
            CheckByteRange(context, sig, fileBytes);

            // ── §6.4.3-2 and §6.4.3-3: CMS structure ─────────────────────────────
            if (sig.Contents.Length > 0)
                CheckCms(context, sig);
        }
    }

    // ── §6.4.3-1 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that /ByteRange [a b c d] exactly covers the entire file:
    ///   a == 0, b+c+d == fileLength, and b+len(Contents token) == c.
    /// In other words, the excluded region is exactly [b .. c) and
    /// b + (c - b) + d == fileLength, i.e. c + d == fileLength.
    ///
    /// Per ISO 32000-1:2008 §12.8.1, the ByteRange is [offset0 length0 offset1 length1]:
    ///   Signed segment 0: bytes[0 .. b)
    ///   Excluded (Contents hex token): bytes[b .. c)
    ///   Signed segment 1: bytes[c .. c+d)
    ///   File total: c + d bytes.
    ///
    /// A valid ByteRange must satisfy:
    ///   br[0] == 0
    ///   br[1] > 0       (segment 0 is non-empty)
    ///   br[2] > br[1]   (excluded region is after segment 0)
    ///   br[3] > 0       (segment 1 is non-empty)
    ///   br[2] + br[3] == fileLength  (segment 1 ends exactly at EOF)
    /// </summary>
    private void CheckByteRange(PreflightContext context, PdfSignature sig, ReadOnlyMemory<byte> fileBytes)
    {
        var br = sig.ByteRange;

        // Guard: must have exactly 4 elements (malformed → skip, no finding).
        if (br.Length != 4)
            return;

        var a = br[0]; // segment 0 start (must be 0)
        var b = br[1]; // segment 0 length / Contents token start offset
        var c = br[2]; // segment 1 start offset (= b + Contents token byte length)
        var d = br[3]; // segment 1 length
        var fileLength = fileBytes.Length;

        // Basic sanity guards before arithmetic — negative or overflowing values → indeterminate.
        if (a < 0 || b <= 0 || c <= 0 || d <= 0)
            return;
        if ((long)c + d < 0) // overflow guard
            return;

        // Flag only UNAMBIGUOUS, revision-independent violations:
        //   * a != 0                — the first signed segment must start at the file beginning;
        //   * c + d  > fileLength   — the ByteRange claims more bytes than the file contains.
        //
        // The under-coverage case (c + d < fileLength) is DEFERRED, not flagged: it cannot be
        // distinguished from a valid later incremental revision without revision-boundary
        // analysis. PAdES B-LT appends a /DSS and B-LTA appends a document timestamp AFTER the
        // signature, so a conformant LTV signature's ByteRange legitimately ends before EOF —
        // veraPDF 1.30.2 reports such files compliant (see the Signed_*_BLTA oracle tests). Flagging
        // under-coverage would therefore over-reject valid LTV documents, so we do not.
        var impossible = a != 0 || ((long)c + d > fileLength);
        if (impossible)
        {
            context.Report(
                "ISO19005-2:6.4.3-1", Clause, PreflightSeverity.Error,
                "ByteRange array of the digital signature does not cover the entire file "
                + "(excluding the PDF Signature itself).");
        }
    }

    // ── §6.4.3-2 and §6.4.3-3 ────────────────────────────────────────────────

    private void CheckCms(PreflightContext context, PdfSignature sig)
    {
        var der = sig.Contents.Span;

        if (!Asn1Reader.TryParse(der, out var hasCertificates, out var signerInfoCount))
        {
            // Malformed or unrecognised DER → indeterminate for both -2 and -3. No finding.
            return;
        }

        // §6.4.3-2: at least one X.509 certificate must be present.
        if (!hasCertificates)
        {
            context.Report(
                "ISO19005-2:6.4.3-2", Clause, PreflightSeverity.Error,
                "The PKCS#7 digital signature does not include the signer's X.509 signing certificate.");
        }

        // §6.4.3-3: exactly one SignerInfo is required.
        if (signerInfoCount != 1)
        {
            context.Report(
                "ISO19005-2:6.4.3-3", Clause, PreflightSeverity.Error,
                $"The digital signature has {signerInfoCount} signer(s) instead of the required one.");
        }
    }
}
