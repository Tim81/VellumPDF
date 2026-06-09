// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Document;

namespace VellumPdf.Signing;

/// <summary>
/// Computes a PAdES/PKCS#7 detached CMS signature over unsigned PDF placeholder bytes
/// and patches the result in-place.
///
/// Algorithm:
/// 1. Locate the /Contents token in the byte stream.
/// 2. Compute ByteRange as the two segments that exclude the &lt;…&gt; token.
/// 3. Overwrite the /ByteRange placeholder digits in-place (fixed-width fields).
/// 4. Concatenate the two ByteRange segments and compute a detached SHA-256 CMS
///    signature using <see cref="SignedCms"/>.
/// 5. Hex-encode the DER signature and overwrite the /Contents placeholder in-place.
/// 6. Write the patched bytes to the output stream.
/// </summary>
internal static class PdfCmsSigner
{
    /// <summary>
    /// Signs a PDF document previously written to <paramref name="unsignedBytes"/> and writes
    /// the signed result to <paramref name="output"/>.
    /// </summary>
    internal static void Sign(
        byte[] unsignedBytes,
        PdfSignatureSettings settings,
        Stream output)
    {
        // ── Step 1: locate the /Contents placeholder ──────────────────────────
        var posContentsKey = IndexOf(unsignedBytes, PdfSignatureHelper.ContentsKey);
        if (posContentsKey < 0)
            throw new InvalidOperationException("PDF byte stream does not contain the /Contents placeholder. Internal error in signature field construction.");

        // posLt = index of the '<' that starts the hex string
        var posLt = posContentsKey + PdfSignatureHelper.ContentsKey.Length - 1; // last char of ContentsKey is '<'
        // contentsTokenLen = '<' + (EstimatedSignatureSizeBytes * 2 hex chars) + '>'
        var hexLen = settings.EstimatedSignatureSizeBytes * 2;
        var contentsTokenLen = 1 + hexLen + 1; // '<' + hex + '>'

        // Sanity: verify the token ends with '>'
        if (posLt + contentsTokenLen > unsignedBytes.Length || unsignedBytes[posLt + contentsTokenLen - 1] != (byte)'>')
            throw new InvalidOperationException("Contents placeholder has unexpected format (missing closing '>'). Internal error.");

        // ── Step 2: compute real ByteRange ────────────────────────────────────
        var fileLen = unsignedBytes.Length;
        long br0 = 0;
        long br1 = posLt;                         // bytes[0..posLt)
        long br2 = posLt + contentsTokenLen;       // bytes after '>'
        long br3 = fileLen - br2;                  // remaining length

        // ── Step 3: patch /ByteRange in-place ─────────────────────────────────
        PatchByteRange(unsignedBytes, br0, br1, br2, br3);

        // ── Step 4: build signed content (two segments concatenated) ─────────
        var signedContent = new byte[br1 + br3];
        Buffer.BlockCopy(unsignedBytes, (int)br0, signedContent, 0, (int)br1);
        Buffer.BlockCopy(unsignedBytes, (int)br2, signedContent, (int)br1, (int)br3);

        // ── Step 5: compute detached CMS signature ────────────────────────────
        var sig = ComputeCmsSignature(signedContent, settings);

        // ── Step 6: validate size and hex-encode into the /Contents placeholder ─
        if (sig.Length > settings.EstimatedSignatureSizeBytes)
            throw new InvalidOperationException(
                $"Computed CMS signature ({sig.Length} bytes) exceeds the reserved /Contents space " +
                $"({settings.EstimatedSignatureSizeBytes} bytes). " +
                "Increase PdfSignatureSettings.EstimatedSignatureSizeBytes.");

        PatchContents(unsignedBytes, posLt, hexLen, sig);

        // ── Step 7: write to output ────────────────────────────────────────────
        output.Write(unsignedBytes, 0, unsignedBytes.Length);
    }

    // ── Byte-range placeholder patching ───────────────────────────────────────

    /// <summary>
    /// Locates the /ByteRange placeholder in <paramref name="bytes"/> and overwrites
    /// its four decimal fields with the real values, keeping total byte length unchanged.
    /// </summary>
    private static void PatchByteRange(byte[] bytes, long v0, long v1, long v2, long v3)
    {
        var pos = IndexOf(bytes, PdfSignatureHelper.ByteRangePlaceholder);
        if (pos < 0)
            throw new InvalidOperationException("ByteRange placeholder not found in PDF bytes. Internal error.");

        // pos points to '['; skip it
        pos++; // now at first digit field
        pos = WriteFixedDecimal(bytes, pos, v0);
        pos++; // skip space
        pos = WriteFixedDecimal(bytes, pos, v1);
        pos++; // skip space
        pos = WriteFixedDecimal(bytes, pos, v2);
        pos++; // skip space
        WriteFixedDecimal(bytes, pos, v3);
    }

    /// <summary>
    /// Writes a decimal number left-padded with spaces into <paramref name="bytes"/> at
    /// <paramref name="offset"/>, occupying exactly <see cref="PdfSignatureHelper.ByteRangeFieldWidth"/> bytes.
    /// Returns the offset immediately after the written field.
    /// </summary>
    private static int WriteFixedDecimal(byte[] bytes, int offset, long value)
    {
        Span<byte> buf = stackalloc byte[PdfSignatureHelper.ByteRangeFieldWidth];
        buf.Fill((byte)' ');
        var v = value;
        for (var i = PdfSignatureHelper.ByteRangeFieldWidth - 1; i >= 0; i--)
        {
            buf[i] = (byte)('0' + v % 10);
            v /= 10;
            if (v == 0) break;
        }
        buf.CopyTo(bytes.AsSpan(offset, PdfSignatureHelper.ByteRangeFieldWidth));
        return offset + PdfSignatureHelper.ByteRangeFieldWidth;
    }

    // ── /Contents patching ────────────────────────────────────────────────────

    /// <summary>
    /// Overwrites the hex-encoded contents of the /Contents placeholder (between the
    /// angle brackets) with the hex encoding of <paramref name="sig"/>, zero-padded to
    /// <paramref name="hexLen"/> characters.
    /// </summary>
    private static void PatchContents(byte[] bytes, int posLt, int hexLen, byte[] sig)
    {
        var dst = bytes.AsSpan(posLt + 1, hexLen);
        var hexBytes = Convert.ToHexString(sig);
        var pos = 0;
        foreach (var c in hexBytes)
            dst[pos++] = (byte)c;
        while (pos < hexLen)
            dst[pos++] = (byte)'0';
    }

    // ── CMS signature computation ─────────────────────────────────────────────

    private static byte[] ComputeCmsSignature(byte[] signedContent, PdfSignatureSettings settings)
    {
        var signer = new CmsSigner(settings.Certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.WholeChain,
        };

        var signingTime = settings.SigningTime ?? DateTimeOffset.UtcNow;
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));

        var cms = new SignedCms(new ContentInfo(signedContent), detached: true);
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    // ── Byte-search helper ────────────────────────────────────────────────────

    /// <summary>Returns the index of the first occurrence of <paramref name="needle"/> in
    /// <paramref name="haystack"/>, or -1 if not found.</summary>
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        var span = haystack.AsSpan();
        var needleSpan = needle.AsSpan();
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (span[i..].StartsWith(needleSpan))
                return i;
        }
        return -1;
    }
}
