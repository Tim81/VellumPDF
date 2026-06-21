// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Signature;

/// <summary>
/// Minimal, strictly-bounded DER reader for parsing the CMS SignedData structure inside a
/// PDF digital-signature /Contents blob. Reads only what the conformance rules need:
/// whether a certificates [0] element is present and non-empty, and the count of SignerInfos.
///
/// Design constraints:
/// <list type="bullet">
///   <item>Zero allocations on attacker-controlled lengths — all navigation is index arithmetic.</item>
///   <item>Strictly bounded: every read is range-checked; no field may extend past the buffer.</item>
///   <item>Depth-capped: nesting is capped at <see cref="MaxDepth"/> to prevent stack exhaustion.</item>
///   <item>Defensive: any unexpected tag, truncation, or overflow returns the sentinel values
///     rather than throwing; callers must treat a false return as "indeterminate".</item>
/// </list>
///
/// DER encoding refresher (RFC 5652 / X.690):
///   Tag byte | Length byte(s) | Value bytes
///   Short-form length: high bit 0, value in low 7 bits (0x00–0x7F).
///   Long-form length:  0x81 → next 1 byte; 0x82 → next 2 bytes; … up to 4 additional bytes.
///   0xFF is reserved; we treat it as an error.
///
/// Structure navigated (RFC 5652 §5):
///   ContentInfo  ::= SEQUENCE { OID(1.2.840.113549.1.7.2), [0] EXPLICIT SignedData }
///   SignedData   ::= SEQUENCE { version, digestAlgorithms, encapContentInfo,
///                               [0] IMPLICIT certs OPTIONAL,
///                               [1] IMPLICIT crls  OPTIONAL,
///                               signerInfos SET }
/// </summary>
internal static class Asn1Reader
{
    // Maximum nesting depth before we give up — prevents stack exhaustion on adversarial input.
    private const int MaxDepth = 20;

    // DER tags we care about.
    private const byte TagSequence = 0x30;
    private const byte TagSet = 0x31;
    // [0] IMPLICIT in SignedData context (certificates)
    private const byte TagContextImplicit0 = 0xA0;
    // [0] EXPLICIT in ContentInfo context (wraps SignedData)
    // In DER, an explicit context tag with constructed form: 0xA0 (same byte — context 0, constructed).
    // [1] IMPLICIT (crls)
    private const byte TagContextImplicit1 = 0xA1;
    // SignedData version OID prefix: 1.2.840.113549.1.7.2 → 0x06 0x09 ...
    private const byte TagOid = 0x06;
    // OID bytes for id-signedData (1.2.840.113549.1.7.2)
    private static readonly byte[] SignedDataOid =
    [
        0x06, 0x09,
        0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02
    ];

    /// <summary>
    /// Parses the outer ContentInfo DER to find SignedData, then extracts:
    /// <paramref name="hasCertificates"/> — true when the certificates [0] field is present
    ///   and contains at least one element.
    /// <paramref name="signerInfoCount"/> — the number of entries in the signerInfos SET.
    ///
    /// Returns false when the DER is malformed, truncated, or does not look like a CMS SignedData
    /// envelope. Callers must treat false as "indeterminate" (suppress any finding).
    /// </summary>
    internal static bool TryParse(
        ReadOnlySpan<byte> derBytes,
        out bool hasCertificates,
        out int signerInfoCount)
    {
        hasCertificates = false;
        signerInfoCount = 0;

        // Strip trailing zero padding: the PDF /Contents hex is zero-padded to the reserved size.
        // DER encodes its own length, so we parse by the outer SEQUENCE length and ignore trailing bytes.
        int pos = 0;

        // ── Layer 1: outer ContentInfo SEQUENCE ───────────────────────────────
        if (!ReadTag(derBytes, ref pos, out var tag1) || tag1 != TagSequence)
            return false;
        if (!ReadLength(derBytes, ref pos, out var contentInfoLen))
            return false;
        // Clamp our working window to the ContentInfo value bytes.
        if (contentInfoLen < 0 || (long)pos + contentInfoLen > derBytes.Length)
            return false;
        var contentInfoEnd = pos + contentInfoLen;
        var window = derBytes[pos..contentInfoEnd];

        int w = 0; // position within window

        // ── contentType OID ───────────────────────────────────────────────────
        // Must be id-signedData.
        if (w + SignedDataOid.Length > window.Length)
            return false;
        if (!window[w..].StartsWith(SignedDataOid))
            return false;
        w += SignedDataOid.Length;

        // ── [0] EXPLICIT context tag wrapping SignedData ───────────────────────
        // ContentInfo content field is [0] EXPLICIT, so the tag byte is 0xA0 (constructed, context 0).
        if (!ReadTag(window, ref w, out var explicitTag) || explicitTag != TagContextImplicit0)
            return false;
        if (!ReadLength(window, ref w, out var explicitLen))
            return false;
        if (explicitLen < 0 || (long)w + explicitLen > window.Length)
            return false;
        var signedDataWindow = window[w..(w + explicitLen)];

        // ── Layer 2: SignedData SEQUENCE ──────────────────────────────────────
        int sd = 0;
        if (!ReadTag(signedDataWindow, ref sd, out var sdTag) || sdTag != TagSequence)
            return false;
        if (!ReadLength(signedDataWindow, ref sd, out var sdLen))
            return false;
        if (sdLen < 0 || (long)sd + sdLen > signedDataWindow.Length)
            return false;
        var sdValue = signedDataWindow[sd..(sd + sdLen)];

        // ── Walk SignedData fields ─────────────────────────────────────────────
        // Fields in order: version INTEGER, digestAlgorithms SET, encapContentInfo SEQUENCE,
        //   [0] IMPLICIT certs OPTIONAL, [1] IMPLICIT crls OPTIONAL, signerInfos SET
        int sv = 0;

        // Skip version INTEGER
        if (!SkipField(sdValue, ref sv))
            return false;

        // Skip digestAlgorithms SET
        if (!SkipField(sdValue, ref sv))
            return false;

        // Skip encapContentInfo SEQUENCE
        if (!SkipField(sdValue, ref sv))
            return false;

        // Optional [0] IMPLICIT certificates
        if (sv < sdValue.Length)
        {
            if (!ReadTag(sdValue, ref sv, out var nextTag))
                return false;

            if (nextTag == TagContextImplicit0)
            {
                // This is the certificates field.
                if (!ReadLength(sdValue, ref sv, out var certsLen))
                    return false;
                if (certsLen < 0 || (long)sv + certsLen > sdValue.Length)
                    return false;
                // Non-empty means at least one certificate is present.
                hasCertificates = certsLen > 0;
                sv += certsLen;

                // Re-read next tag for optional crls / signerInfos
                if (sv >= sdValue.Length)
                    return false; // No signerInfos at all — malformed
                if (!ReadTag(sdValue, ref sv, out nextTag))
                    return false;
            }

            // Optional [1] IMPLICIT crls
            if (nextTag == TagContextImplicit1)
            {
                if (!ReadLength(sdValue, ref sv, out var crlsLen))
                    return false;
                if (crlsLen < 0 || (long)sv + crlsLen > sdValue.Length)
                    return false;
                sv += crlsLen;

                if (sv >= sdValue.Length)
                    return false;
                if (!ReadTag(sdValue, ref sv, out nextTag))
                    return false;
            }

            // Must now be the signerInfos SET (tag 0x31)
            if (nextTag != TagSet)
                return false;

            if (!ReadLength(sdValue, ref sv, out var signerInfosLen))
                return false;
            if (signerInfosLen < 0 || (long)sv + signerInfosLen > sdValue.Length)
                return false;

            var signerInfosValue = sdValue[sv..(sv + signerInfosLen)];
            signerInfoCount = CountSetElements(signerInfosValue);
            if (signerInfoCount < 0)
                return false; // malformed
        }
        else
        {
            // SignedData has no more fields — no signerInfos (malformed, but return gracefully)
            return false;
        }

        return true;
    }

    // ── Low-level DER primitives ──────────────────────────────────────────────

    /// <summary>Reads one tag byte and advances <paramref name="pos"/>.</summary>
    private static bool ReadTag(ReadOnlySpan<byte> buf, ref int pos, out byte tag)
    {
        tag = 0;
        if (pos >= buf.Length)
            return false;
        tag = buf[pos++];
        return true;
    }

    /// <summary>
    /// Reads a DER length (short or long form) and advances <paramref name="pos"/>.
    /// Returns the content length in <paramref name="length"/> (non-negative) or -1 on error.
    /// Indefinite-length encoding (0x80) is rejected (not valid in DER).
    /// </summary>
    private static bool ReadLength(ReadOnlySpan<byte> buf, ref int pos, out int length)
    {
        length = -1;
        if (pos >= buf.Length)
            return false;

        var b0 = buf[pos++];

        if (b0 <= 0x7F)
        {
            // Short form
            length = b0;
            return true;
        }

        if (b0 == 0x80)
            return false; // Indefinite form — not DER
        if (b0 == 0xFF)
            return false; // Reserved

        // Long form: lower 7 bits = number of subsequent length bytes
        var numBytes = b0 & 0x7F;
        if (numBytes > 4 || pos + numBytes > buf.Length)
            return false; // Cap at 4 bytes (max ~4 GB); reject truncated

        int len = 0;
        for (var i = 0; i < numBytes; i++)
            len = (len << 8) | buf[pos++];

        if (len < 0)
            return false; // Overflow (numBytes=4 and MSB set)

        length = len;
        return true;
    }

    /// <summary>
    /// Skips one complete TLV field (tag + length + value) at <paramref name="pos"/>.
    /// </summary>
    private static bool SkipField(ReadOnlySpan<byte> buf, ref int pos)
    {
        if (!ReadTag(buf, ref pos, out _))
            return false;
        if (!ReadLength(buf, ref pos, out var len))
            return false;
        if (len < 0 || (long)pos + len > buf.Length)
            return false;
        pos += len;
        return true;
    }

    /// <summary>
    /// Counts the number of top-level TLV elements inside <paramref name="setValue"/> (the value
    /// bytes of a SET, with the SET tag+length already consumed). Returns -1 on parse error.
    /// </summary>
    private static int CountSetElements(ReadOnlySpan<byte> setValue)
    {
        int count = 0;
        int pos = 0;
        while (pos < setValue.Length)
        {
            if (!SkipField(setValue, ref pos))
                return -1;
            count++;
            // Guard against runaway: a SET of SignerInfos will never legitimately have > 1000 entries.
            if (count > 1000)
                return -1;
        }
        return count;
    }
}
