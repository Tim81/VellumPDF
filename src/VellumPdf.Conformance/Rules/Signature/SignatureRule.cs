// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
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
/// Signatures are enumerated from two sources: the AcroForm field tree (/AcroForm /Fields),
/// and the catalog /Perms /DocMDP entry (ISO 32000-1 §12.8.2.2). The same signature dictionary
/// may be reachable from both; deduplication is by /ByteRange + /Contents identity.
///
/// §6.4.3-1 under-coverage (c+d &lt; fileLength): the uncovered tail is checked against the
/// revision list. If any revision's XrefOffset falls within the gap [c+d, fileLength), the tail
/// is a legitimate incremental update and the check does not fire. Otherwise the tail is
/// trailing garbage and the check fires. This correctly handles PAdES B-LT/B-LTA (whose /DSS
/// or document timestamp forms a valid later revision) while catching genuinely uncovered bytes.
/// </remarks>
internal sealed class SignatureRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.4.3";
    public string Clause => "ISO 19005-2:2011, 6.4.3";

    private static readonly PdfName _perms = new("Perms");
    private static readonly PdfName _docMdp = new("DocMDP");

    public void Evaluate(PreflightContext context)
    {
        var sigs = CollectAllSignatures(context);
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

    // Collects signatures from both the AcroForm field tree and the catalog /Perms /DocMDP.
    // Deduplication: a signature appearing in both sources is only checked once.
    private static IReadOnlyList<PdfSignature> CollectAllSignatures(PreflightContext context)
    {
        // Start with AcroForm signatures (already collected by the reader).
        var acroSigs = context.Reader.Signatures;

        // Build a deduplication key from (ByteRange[0], ByteRange[1], ByteRange[2], ByteRange[3])
        // — sufficient to identify a unique signature position in the file.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in acroSigs)
            seen.Add(SigKey(s));

        List<PdfSignature>? extra = null;

        // /Perms /DocMDP: ISO 32000-1 §12.8.2.2 — a DocMDP permission dictionary whose
        // value is the signature dictionary for the document modification detection signature.
        if (context.Resolve(context.Catalog.Get(_perms)) is PdfDictionary perms)
        {
            if (context.Resolve(perms.Get(_docMdp)) is PdfDictionary docMdpSig)
            {
                var sig = ExtractSignature(docMdpSig);
                if (sig is not null && seen.Add(SigKey(sig)))
                {
                    extra ??= [];
                    extra.Add(sig);
                }
            }
        }

        if (extra is null)
            return acroSigs;

        var all = new List<PdfSignature>(acroSigs.Count + extra.Count);
        all.AddRange(acroSigs);
        all.AddRange(extra);
        return all;
    }

    private static string SigKey(PdfSignature sig)
    {
        var br = sig.ByteRange.Span;
        return br.Length == 4
            ? $"{br[0]}:{br[1]}:{br[2]}:{br[3]}"
            : string.Empty;
    }

    // Extracts a PdfSignature from a raw signature dictionary (without going through the reader's
    // AcroForm path). Returns null for a dictionary that cannot be parsed as a valid signature.
    private static PdfSignature? ExtractSignature(PdfDictionary sigDict)
    {
        PdfName? subFilter = null;
        if (sigDict.Get(new PdfName("SubFilter")) is PdfName sfName)
            subFilter = sfName;

        var brObj = sigDict.Get(new PdfName("ByteRange"));
        long[] byteRange = [];
        if (brObj is PdfArray brArr)
        {
            byteRange = new long[brArr.Count];
            for (var i = 0; i < brArr.Count; i++)
            {
                if (brArr[i] is PdfInteger pi)
                    byteRange[i] = pi.Value;
            }
        }

        ReadOnlyMemory<byte> contents = ReadOnlyMemory<byte>.Empty;
        if (sigDict.Get(PdfName.Contents) is PdfHexString hexStr)
            contents = hexStr.Bytes;

        if (contents.IsEmpty && byteRange.Length == 0)
            return null;

        return new PdfSignature(subFilter, byteRange, contents, signingTime: null);
    }

    // ── §6.4.3-1 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that /ByteRange [a b c d] covers the file appropriately.
    ///
    /// Per ISO 32000-1:2008 §12.8.1, the ByteRange is [offset0 length0 offset1 length1]:
    ///   Signed segment 0: bytes[0 .. b)
    ///   Excluded (Contents hex token): bytes[b .. c)
    ///   Signed segment 1: bytes[c .. c+d)
    ///
    /// Unconditional violations (fired regardless of revision count):
    ///   a != 0                — first signed segment must start at byte 0.
    ///   c + d &gt; fileLength — ByteRange claims more bytes than the file contains.
    ///
    /// Under-coverage (c + d &lt; fileLength): the gap [c+d, fileLength) is checked against
    /// the revision list. If any revision's XrefOffset lies in the gap, the tail is a
    /// legitimate later incremental revision and the check does not fire (correct for PAdES
    /// B-LT/B-LTA). If no revision's XrefOffset lies in the gap, the tail is trailing garbage
    /// and the check fires.
    /// </summary>
    private void CheckByteRange(PreflightContext context, PdfSignature sig, ReadOnlyMemory<byte> fileBytes)
    {
        var br = sig.ByteRange.Span;

        // Guard: must have exactly 4 elements (malformed → skip, no finding).
        if (br.Length != 4)
            return;

        var a = br[0]; // segment 0 start (must be 0)
        var b = br[1]; // segment 0 length / Contents token start offset
        var c = br[2]; // segment 1 start offset (= b + Contents token byte length)
        var d = br[3]; // segment 1 length
        long fileLength = fileBytes.Length;

        // Basic sanity guards before arithmetic — negative or overflowing values → indeterminate.
        if (a < 0 || b <= 0 || c <= 0 || d <= 0)
            return;

        // The values are long now, so this addition can only overflow on a value no real file has;
        // the guard stays because /ByteRange comes from an untrusted document.
        var cdSum = c + d;
        if (cdSum < 0) // overflow guard
            return;

        if (a != 0 || cdSum > fileLength)
        {
            context.Report(
                "ISO19005-2:6.4.3-1", Clause, PreflightSeverity.Error,
                "ByteRange array of the digital signature does not cover the entire file "
                + "(excluding the PDF Signature itself).");
            return;
        }

        // Under-coverage: the signed range ends before EOF. Check whether the gap contains
        // a valid revision's xref offset. If so, it is a legitimate later incremental update
        // (e.g. a PAdES B-LT /DSS block or a B-LTA document timestamp revision) and we do
        // not fire. If no revision's xref falls in the gap, the tail bytes are not part of
        // any revision known to the cross-reference chain — trailing garbage → violation.
        if (cdSum == fileLength)
            return; // exact coverage — compliant

        // Stays long rather than narrowing. The cast that used to be here was safe only because the
        // cdSum > fileLength branch above returns first, which bounds it by an int — a coupling
        // that would break silently the moment either guard moved.
        var gapStart = cdSum;
        foreach (var rev in context.Revisions)
        {
            if (rev.XrefOffset >= gapStart && rev.XrefOffset < fileLength)
                return; // a later revision occupies the gap — legitimate, do not fire
        }

        context.Report(
            "ISO19005-2:6.4.3-1", Clause, PreflightSeverity.Error,
            "ByteRange array of the digital signature does not cover the entire file "
            + "(excluding the PDF Signature itself).");
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
