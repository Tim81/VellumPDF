// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader;

/// <summary>
/// Parses a document's <c>/Encrypt</c> dictionary into a <see cref="StandardSecurityDecryptor"/> and
/// authenticates a supplied password against it. Kept apart from <see cref="PdfDocumentReader"/>'s
/// constructor so the dictionary-shape and password-retry logic can be read (and, incidentally,
/// tested) without the rest of the reader's bootstrap sequence.
/// </summary>
internal static class EncryptionSetup
{
    private static readonly PdfName _filterKey = new("Filter");
    private static readonly PdfName _vKey = new("V");
    private static readonly PdfName _rKey = new("R");
    private static readonly PdfName _oKey = new("O");
    private static readonly PdfName _uKey = new("U");
    private static readonly PdfName _oeKey = new("OE");
    private static readonly PdfName _ueKey = new("UE");
    private static readonly PdfName _pKey = new("P");
    private static readonly PdfName _lengthKey = new("Length");
    private static readonly PdfName _encryptMetadataKey = new("EncryptMetadata");
    private static readonly PdfName _stmFKey = new("StmF");
    private static readonly PdfName _strFKey = new("StrF");

    internal readonly struct Result
    {
        public required StandardSecurityDecryptor Decryptor { get; init; }
        public required byte[] FileKey { get; init; }
        public required Dictionary<string, CryptFilterMethod> CryptFilterTable { get; init; }
        public required bool EncryptMetadata { get; init; }
        public required bool IsOwnerAccess { get; init; }
        public required PdfCipherAlgorithm Cipher { get; init; }
        public required int KeyLengthBits { get; init; }
        public required PdfPermissions Permissions { get; init; }
    }

    /// <exception cref="UnsupportedPdfFeatureException">
    /// <c>/Filter</c> names a public-key handler (<c>/Adobe.PubSec</c>) or anything other than
    /// <c>/Standard</c>.
    /// </exception>
    /// <exception cref="PdfPasswordException">
    /// <paramref name="password"/> authenticates as neither the owner nor the user password.
    /// </exception>
    internal static Result Authenticate(PdfDictionary encryptDict, PdfDictionary trailer, string? password)
    {
        var filterName = (encryptDict.Get(_filterKey) as PdfName)?.Value;
        if (filterName == "Adobe.PubSec")
            throw new UnsupportedPdfFeatureException(
                "This document uses a public-key (/Adobe.PubSec) security handler, which VellumPdf.Reader " +
                "does not support; only the Standard security handler (/Filter /Standard) is supported.");
        if (filterName != "Standard")
        {
            throw new UnsupportedPdfFeatureException(
                $"/Encrypt /Filter /{filterName ?? "(missing)"} is not a security handler VellumPdf.Reader " +
                "supports; only /Standard is.");
        }

        var v = (int)RequireInt(encryptDict, _vKey, "/V");
        var r = (int)RequireInt(encryptDict, _rKey, "/R");
        var o = RequireBytes(encryptDict, _oKey, "/O");
        var u = RequireBytes(encryptDict, _uKey, "/U");
        var oe = TryGetBytes(encryptDict, _oeKey);
        var ue = TryGetBytes(encryptDict, _ueKey);
        var p = unchecked((int)RequireInt(encryptDict, _pKey, "/P"));
        var encryptMetadata = encryptDict.Get(_encryptMetadataKey) is not PdfBoolean emBool || emBool.Value;
        var id0 = GetId0(trailer);
        var keyLengthBytes = r >= 5 ? 32 : LegacyKeyLengthBytes(encryptDict, v);

        var cfTable = v >= 4
            ? CryptFilterResolver.BuildCfTable(encryptDict, null)
            : new Dictionary<string, CryptFilterMethod>(StringComparer.Ordinal);

        CryptFilterMethod streamFilter;
        CryptFilterMethod stringFilter;
        if (v < 4)
        {
            // Algorithm 1: V=1/V=2 predate /CF entirely; the implicit method is always RC4, for
            // both streams and strings, never AES.
            streamFilter = CryptFilterMethod.Rc4;
            stringFilter = CryptFilterMethod.Rc4;
        }
        else
        {
            var stmFName = (encryptDict.Get(_stmFKey) as PdfName)?.Value;
            var strFName = (encryptDict.Get(_strFKey) as PdfName)?.Value;
            streamFilter = CryptFilterResolver.ResolveNamedMethod(stmFName, cfTable);
            stringFilter = CryptFilterResolver.ResolveNamedMethod(strFName, cfTable);
        }

        var decryptor = new StandardSecurityDecryptor(
            v, r, keyLengthBytes, o, u, oe, ue, p, id0, encryptMetadata, streamFilter, stringFilter);

        if (!TryAuthenticate(decryptor, password, out var fileKey, out var isOwnerAccess))
        {
            throw new PdfPasswordException(
                "The supplied password does not authenticate as either the owner or the user password.");
        }

        return new Result
        {
            Decryptor = decryptor,
            FileKey = fileKey,
            CryptFilterTable = cfTable,
            EncryptMetadata = encryptMetadata,
            IsOwnerAccess = isOwnerAccess,
            Cipher = ToCipher(streamFilter),
            KeyLengthBits = keyLengthBytes * 8,
            Permissions = (PdfPermissions)p & PdfPermissions.All,
        };
    }

    /// <summary>
    /// Tries <paramref name="password"/>, in each candidate byte encoding, as the OWNER password
    /// first and the user password second.
    ///
    /// <para>
    /// ISO 32000-1 prescribes no order — its exact framing (§7.6.3.3) is that the handler "uses the
    /// algorithms 6 and 7 that follow, to determine whether a supplied password string is the
    /// correct user or owner password", not that either is tried first. Owner-first is this
    /// implementation's choice, for two reasons that hold regardless of what order the spec might
    /// have picked: a password that is legitimately both (StandardSecurityHandler happily produces
    /// one when <see cref="PdfEncryptionSettings.OwnerPassword"/> is left null) reports as the
    /// higher-privilege access; and at R&lt;=4, Algorithm 7 recovers the user password FROM the
    /// owner one before re-deriving the same file key Algorithm 6 would confirm, so an owner
    /// password inherently also grants user access — reporting "user" for it would be strictly less
    /// informative than "owner" is.
    /// </para>
    /// </summary>
    private static bool TryAuthenticate(
        StandardSecurityDecryptor decryptor, string? password, out byte[] fileKey, out bool isOwnerAccess)
    {
        foreach (var passwordBytes in CandidatePasswordEncodings(password, decryptor.R))
        {
            if (decryptor.TryComputeFileKeyFromOwnerPassword(passwordBytes, out var ownerKey))
            {
                fileKey = ownerKey;
                isOwnerAccess = true;
                return true;
            }

            if (decryptor.TryComputeFileKeyFromUserPassword(passwordBytes, out var userKey))
            {
                fileKey = userKey;
                isOwnerAccess = false;
                return true;
            }
        }

        fileKey = [];
        isOwnerAccess = false;
        return false;
    }

    // UTF-8 first, then PDFDocEncoding on failure. Only meaningfully different for R<=4 with a
    // non-ASCII password: R>=5 always uses UTF-8 (ISO 32000-2 §7.6.4.3), and PDFDocEncoding agrees
    // with UTF-8-of-Latin1 for every ASCII password — which is everything the committed corpus
    // exercises, so the PDFDocEncoding branch itself has no fixture pinning it.
    private static IEnumerable<byte[]> CandidatePasswordEncodings(string? password, int r)
    {
        yield return StandardSecurityHandler.PasswordBytes(password);
        if (r <= 4 && PdfDocEncoding.TryEncode(password, out var docEncoded))
            yield return docEncoded;
    }

    private static long RequireInt(PdfDictionary dict, PdfName key, string label)
        => dict.Get(key) is PdfInteger i
            ? i.Value
            : throw new InvalidDataException($"Malformed PDF: /Encrypt {label} is missing or not an integer.");

    private static byte[] RequireBytes(PdfDictionary dict, PdfName key, string label)
        => TryGetBytes(dict, key)
            ?? throw new InvalidDataException($"Malformed PDF: /Encrypt {label} is missing or not a string.");

    private static byte[]? TryGetBytes(PdfDictionary dict, PdfName key) => dict.Get(key) switch
    {
        PdfHexString h => h.Bytes.ToArray(),
        PdfLiteralString l => l.Bytes.ToArray(),
        _ => null,
    };

    private static byte[] GetId0(PdfDictionary trailer)
    {
        if (trailer.Get(PdfName.ID) is not PdfArray { Count: > 0 } idArr)
            return [];

        return idArr[0] switch
        {
            PdfHexString h => h.Bytes.ToArray(),
            PdfLiteralString l => l.Bytes.ToArray(),
            _ => [],
        };
    }

    // /Length is in BITS at the top level (unlike a /CF sub-dictionary's own /Length, which ISO
    // 32000-2 Table 21 measures in bytes — this implementation does not read a per-filter override,
    // since every committed fixture's /CF entry either omits /Length or agrees with the top-level
    // one, so that override path is unpinned).
    private static int LegacyKeyLengthBytes(PdfDictionary encryptDict, int v)
    {
        if (v == 1)
            return 5; // Algorithm 1: V=1 is always 40-bit RC4 regardless of /Length.

        var bits = encryptDict.Get(_lengthKey) is PdfInteger li ? (int)li.Value : 40;
        if (bits % 8 != 0 || bits is < 40 or > 128)
        {
            throw new InvalidDataException(
                $"Malformed PDF: /Encrypt /Length {bits} is not a multiple of 8 in the range 40 to 128.");
        }

        return bits / 8;
    }

    private static PdfCipherAlgorithm ToCipher(CryptFilterMethod method) => method switch
    {
        CryptFilterMethod.Identity => PdfCipherAlgorithm.Identity,
        CryptFilterMethod.Rc4 => PdfCipherAlgorithm.Rc4,
        CryptFilterMethod.Aes128 => PdfCipherAlgorithm.Aes128,
        CryptFilterMethod.Aes256 => PdfCipherAlgorithm.Aes256,
        _ => PdfCipherAlgorithm.Unsupported,
    };
}
