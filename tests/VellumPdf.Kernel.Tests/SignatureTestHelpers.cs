// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.Pkcs;
using System.Text;

namespace VellumPdf.Kernel.Tests;

internal sealed record ContentsInfo(long PosLt, int TokenLen, string HexContent);

/// <summary>
/// Shared /ByteRange and /Contents parsing plus BCL <see cref="SignedCms.CheckSignature"/>
/// verification for signed-PDF test assertions. Used by <see cref="SignatureTests"/> and
/// <see cref="ExternalSignerChainTests"/>.
/// </summary>
internal static class SignatureTestHelpers
{
    /// <summary>
    /// Parses /ByteRange and /Contents from signed PDF bytes and performs BCL
    /// <see cref="SignedCms.CheckSignature"/> verification. Throws on any error.
    /// </summary>
    internal static void VerifySignatureOrThrow(byte[] signedBytes)
    {
        var (byteRange, contentsInfo) = ParseSignatureFields(signedBytes);

        // Reconstruct the signed content from the two ByteRange segments.
        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        // Decode the /Contents hex string to raw DER bytes.
        // The hex content includes the actual DER bytes followed by zero-padding.
        // SignedCms.Decode uses the DER length field to determine the actual size,
        // so passing the full padded buffer (including trailing zero bytes) is correct.
        var contentsBytes = Convert.FromHexString(contentsInfo.HexContent);

        // BCL verification: detached CMS, verify-signature-only (no chain).
        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(contentsBytes);
        // verifySignatureOnly=true skips certificate chain/trust validation —
        // appropriate for self-signed test certs.
        verify.CheckSignature(verifySignatureOnly: true);
    }

    /// <summary>
    /// Parses the /ByteRange array and /Contents hex string from the signed PDF bytes.
    /// Returns the four ByteRange values and the contents token info.
    /// </summary>
    internal static (long[] ByteRange, ContentsInfo Contents) ParseSignatureFields(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);

        // ── Parse /ByteRange [n0 n1 n2 n3] ─────────────────────────────────
        const string byteRangeMarker = "/ByteRange [";
        var brStart = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brStart >= 0, "/ByteRange not found in signed PDF");
        var brBracket = brStart + byteRangeMarker.Length - 1; // index of '['
        var brEnd = text.IndexOf(']', brBracket);
        Assert.True(brEnd >= 0, "/ByteRange closing ']' not found");
        var brContent = text[(brBracket + 1)..brEnd].Trim();
        var brParts = brContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, brParts.Length);
        var byteRange = brParts.Select(long.Parse).ToArray();

        // ── Parse /Contents <hex…> ──────────────────────────────────────────
        // Locate the '<' of the /Contents hex string by anchoring on /ByteRange:
        // the first '<' after the ByteRange ']' is the /Contents opening angle bracket.
        var posLt = text.IndexOf('<', brEnd);
        Assert.True(posLt >= 0, "/Contents '<' not found after /ByteRange in signed PDF");
        var cEnd = text.IndexOf('>', posLt);
        Assert.True(cEnd >= 0, "/Contents closing '>' not found");
        var hexContent = text[(posLt + 1)..cEnd];
        var tokenLen = 1 + hexContent.Length + 1; // '<' + hex + '>'

        return (byteRange, new ContentsInfo(posLt, tokenLen, hexContent));
    }
}
