// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Signing;

/// <summary>
/// Low-level byte-patching helpers shared by <see cref="PdfCmsSigner"/> and
/// <see cref="ArchiveTimestampBuilder"/>. Locates the unique /ByteRange placeholder,
/// computes the real four-element ByteRange, patches it in-place, and hex-encodes
/// the token payload into the /Contents hex string.
/// </summary>
internal static class SignaturePlaceholderPatcher
{
    /// <summary>
    /// Counts the number of non-overlapping occurrences of the /ByteRange placeholder in
    /// <paramref name="bytes"/>, then locates the one occurrence and returns the byte index
    /// of the '<c>&lt;</c>' that opens the immediately following /Contents hex token.
    /// </summary>
    /// <param name="bytes">PDF byte array containing exactly one ByteRange placeholder.</param>
    /// <param name="estimatedTokenSizeBytes">The reserved /Contents size in bytes; its hex length is twice this.</param>
    /// <param name="hexLen">
    /// On success, set to <c>estimatedTokenSizeBytes * 2</c> — the number of hex characters
    /// in the /Contents token (excluding angle brackets).
    /// </param>
    /// <returns>The byte index of the '<c>&lt;</c>' character that opens the /Contents hex token.</returns>
    /// <exception cref="InvalidOperationException">
    /// The placeholder is absent or appears more than once, or the /Contents '<c>&lt;</c>' is missing,
    /// or the token does not end with '<c>&gt;</c>'.
    /// </exception>
    internal static int LocateContentsToken(byte[] bytes, int estimatedTokenSizeBytes, out int hexLen)
    {
        var brMatchCount = CountOf(bytes, PdfSignatureHelper.ByteRangePlaceholder);
        if (brMatchCount == 0)
            throw new InvalidOperationException(
                "PDF byte stream does not contain the /ByteRange placeholder. Internal error in signature field construction.");
        if (brMatchCount > 1)
            throw new InvalidOperationException(
                $"PDF byte stream contains {brMatchCount} occurrences of the /ByteRange placeholder; expected exactly 1. Internal error.");

        var posBrEnd = IndexOf(bytes, PdfSignatureHelper.ByteRangePlaceholder)
                       + PdfSignatureHelper.ByteRangePlaceholder.Length;

        var posLt = IndexOfByte(bytes, (byte)'<', posBrEnd);
        if (posLt < 0)
            throw new InvalidOperationException(
                "No '<' found after /ByteRange placeholder — /Contents hex string missing. Internal error.");

        hexLen = estimatedTokenSizeBytes * 2;
        var contentsTokenLen = 1 + hexLen + 1; // '<' + hex + '>'

        if (posLt + contentsTokenLen > bytes.Length || bytes[posLt + contentsTokenLen - 1] != (byte)'>')
            throw new InvalidOperationException(
                "Contents placeholder has unexpected format (missing closing '>'). Internal error.");

        return posLt;
    }

    /// <summary>
    /// Computes the real four-element /ByteRange from the location of the /Contents token
    /// and the total file length, then patches the /ByteRange placeholder in-place.
    /// </summary>
    /// <returns>
    /// The four ByteRange values: <c>(br0, br1, br2, br3)</c> where
    /// <c>br1 = posLt</c> (length of the first signed segment) and
    /// <c>br2 = posLt + tokenLen</c> (start of the second signed segment).
    /// </returns>
    internal static (long Br0, long Br1, long Br2, long Br3) ComputeAndPatchByteRange(
        byte[] bytes, int posLt, int hexLen)
    {
        var contentsTokenLen = 1 + hexLen + 1;
        long br0 = 0;
        long br1 = posLt;
        long br2 = posLt + contentsTokenLen;
        long br3 = bytes.Length - br2;

        PatchByteRange(bytes, br0, br1, br2, br3);
        return (br0, br1, br2, br3);
    }

    /// <summary>
    /// Builds the two-segment signed content from the four ByteRange values.
    /// </summary>
    internal static byte[] BuildSignedContent(byte[] bytes, long br0, long br1, long br2, long br3)
    {
        var signedContent = new byte[br1 + br3];
        Buffer.BlockCopy(bytes, (int)br0, signedContent, 0, (int)br1);
        Buffer.BlockCopy(bytes, (int)br2, signedContent, (int)br1, (int)br3);
        return signedContent;
    }

    /// <summary>
    /// Hex-encodes <paramref name="payload"/> into the /Contents placeholder at
    /// <paramref name="posLt"/>, zero-padding to <paramref name="hexLen"/> characters.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="payload"/> is longer than the reserved space
    /// (<paramref name="hexLen"/> / 2 bytes).
    /// </exception>
    internal static void PatchContents(byte[] bytes, int posLt, int hexLen, byte[] payload, string sizeLabel)
    {
        var reserveBytes = hexLen / 2;
        if (payload.Length > reserveBytes)
            throw new InvalidOperationException(
                $"Computed {sizeLabel} ({payload.Length} bytes) exceeds the reserved /Contents space " +
                $"({reserveBytes} bytes). " +
                "Increase the estimatedTokenSizeBytes parameter.");

        var dst = bytes.AsSpan(posLt + 1, hexLen);
        var hexStr = Convert.ToHexString(payload);
        var pos = 0;
        foreach (var c in hexStr)
            dst[pos++] = (byte)c;
        while (pos < hexLen)
            dst[pos++] = (byte)'0';
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static void PatchByteRange(byte[] bytes, long v0, long v1, long v2, long v3)
    {
        var pos = IndexOf(bytes, PdfSignatureHelper.ByteRangePlaceholder);
        if (pos < 0)
            throw new InvalidOperationException("ByteRange placeholder not found in PDF bytes. Internal error.");

        pos++; // skip '['
        pos = WriteFixedDecimal(bytes, pos, v0);
        pos++; // skip space
        pos = WriteFixedDecimal(bytes, pos, v1);
        pos++; // skip space
        pos = WriteFixedDecimal(bytes, pos, v2);
        pos++; // skip space
        WriteFixedDecimal(bytes, pos, v3);
    }

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

    internal static int IndexOfByte(byte[] haystack, byte needle, int start)
    {
        for (var i = start; i < haystack.Length; i++)
        {
            if (haystack[i] == needle)
                return i;
        }
        return -1;
    }

    internal static int IndexOf(byte[] haystack, byte[] needle)
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

    internal static int CountOf(byte[] haystack, byte[] needle)
    {
        var count = 0;
        var span = haystack.AsSpan();
        var needleSpan = needle.AsSpan();
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (span[i..].StartsWith(needleSpan))
            {
                count++;
                i += needle.Length - 1;
            }
        }
        return count;
    }
}
