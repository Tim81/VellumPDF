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
    private static readonly PdfName _cfKey = new("CF");
    private static readonly PdfName _permsKey = new("Perms");
    private static readonly PdfName _effKey = new("EFF");
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

        public required PdfCipherAlgorithm StringCipher { get; init; }
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
    internal static Result Authenticate(
        PdfDictionary encryptDict,
        PdfDictionary trailer,
        string? password,
        Func<PdfObject?, PdfObject?>? resolve = null)
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

        // §7.6.1 requires only the STRINGS in the encryption dictionary to be direct objects, so
        // /V, /R, /P, /Length, /CF, /StmF and /StrF may all legally be indirect references. Reading
        // them raw made a conformant file fail — /P as a reference threw "missing or not an
        // integer" at Open, and an indirect /CF produced an empty filter table, which opened fine
        // and then threw on the first stream. Working on a dereferenced copy keeps every read below
        // (and BuildCfTable's, which takes no resolver) simple.
        encryptDict = DereferenceValues(encryptDict, resolve);

        var v = (int)RequireInt(encryptDict, _vKey, "/V");
        var r = (int)RequireInt(encryptDict, _rKey, "/R");

        // /V 3 is a well-defined value — ISO 32000-1 Table 20 reserves it for "an unpublished
        // algorithm that permits encryption key lengths ranging from 40 to 128 bits" — so a file
        // using it is valid, merely beyond what a clean-room implementation can support. Reporting
        // it as malformed would tell the user their good file is corrupt; the same distinction the
        // handler already draws for /Filter /Adobe.PubSec.
        if (v == 3)
        {
            throw new UnsupportedPdfFeatureException(
                "/Encrypt /V 3 uses the unpublished algorithm ISO 32000-1 Table 20 reserves for it, "
                + "which this library does not implement.");
        }
        var o = RequireBytes(encryptDict, _oKey, "/O");
        var u = RequireBytes(encryptDict, _uKey, "/U");
        var oe = TryGetBytes(encryptDict, _oeKey);
        var ue = TryGetBytes(encryptDict, _ueKey);
        var p = unchecked((int)RequireInt(encryptDict, _pKey, "/P"));
        var encryptMetadata = encryptDict.Get(_encryptMetadataKey) is not PdfBoolean emBool || emBool.Value;
        var id0 = GetId0(trailer);
        var keyLengthBytes = r >= 5 ? 32 : LegacyKeyLengthBytes(encryptDict, v, r);

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

        // ISO 32000-2 §7.6.4.4.12, Algorithm 13. At R<=4 /P is an input to Algorithm 2, so editing it
        // breaks authentication on its own; at R>=5 the file key is random and /P is protected ONLY
        // by /Perms, which encrypts a copy of it under that key. Without this check a byte-level edit
        // to /P — a 12-byte patch that leaves the cross-reference table intact — silently escalates
        // every permission bit, and this library would report the attacker's values as the
        // document's. /Perms absent is not treated as a failure: it cannot be verified either way,
        // and R5 predates the entry being universal.
        // /EFF names the crypt filter for EMBEDDED FILE streams, which ISO 32000-1 §7.6.1 allows a
        // document to encrypt on their own — /StmF and /StrF Identity, /EFF naming a real filter, so
        // that only attachments are protected. This handler does not implement that: it would decode
        // every embedded file stream with /StmF's method and return ciphertext as the file.
        // Refusing is the only honest answer until per-stream /EFF selection exists.
        var effName = (encryptDict.Get(_effKey) as PdfName)?.Value;
        if (effName is not null and not "Identity"
            && effName != (encryptDict.Get(_stmFKey) as PdfName)?.Value)
        {
            throw new UnsupportedPdfFeatureException(
                $"/Encrypt /EFF names the crypt filter '{effName}' for embedded file streams, which "
                + "differs from /StmF. Encrypting embedded files separately is not implemented.");
        }

        // An unresolvable /StrF is fatal here, while an unresolvable /StmF is left to fail at decode
        // (see StmFNamingUndefinedCfEntry_opensButThrowsOnDecode). The asymmetry is deliberate: a
        // document whose streams cannot be decrypted still has readable strings — /Info, the
        // structure tree, every name and date — but one whose STRINGS cannot be decrypted has
        // nothing to offer, and every one of them would otherwise come back as ciphertext with no
        // error anywhere. ISO 32000-2 Table 20 makes a /StrF naming an absent /CF entry an error.
        if (stringFilter == CryptFilterMethod.Unsupported)
        {
            throw new InvalidDataException(
                "Malformed PDF: /Encrypt /StrF names a /CF entry the document does not define, or a "
                + "/CFM this handler does not implement, so no string in the document can be decrypted.");
        }

        var perms = TryGetBytes(encryptDict, _permsKey);
        if (r >= 5 && perms is not null && !decryptor.VerifyPermissions(fileKey, perms))
        {
            throw new InvalidDataException(
                "Malformed PDF: /Encrypt /Perms does not decrypt to the document's /P value. The "
                + "permission bits have been altered since the file was encrypted (ISO 32000-2 "
                + "§7.6.4.4.12, Algorithm 13).");
        }

        return new Result
        {
            Decryptor = decryptor,
            FileKey = fileKey,
            CryptFilterTable = cfTable,
            EncryptMetadata = encryptMetadata,
            IsOwnerAccess = isOwnerAccess,
            Cipher = ToCipher(streamFilter),
            StringCipher = ToCipher(stringFilter),
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

    // The file key length, in bytes, for V<5. Three separate rules, and reading only the top-level
    // /Length gets two of them wrong:
    //
    //   V=1  — always 40-bit RC4, whatever /Length says (ISO 32000-1 Table 20).
    //   R=2  — Algorithm 2 step (i): "n shall always be 5 for security handlers of revision 2".
    //          The revision overrides the length, so /V 2 /R 2 /Length 128 is still a 5-byte key.
    //   V=4  — Table 20 scopes the top-level /Length to "only if V is 2 or 3"; the length that
    //          applies is the crypt filter's own (Table 25), which the standard security handler
    //          writes in BYTES. A conformant V=4 file may carry no top-level /Length at all, and
    //          defaulting it to 40 bits there rejects the correct password on a file every other
    //          reader opens.
    // A shallow copy with every indirect value replaced by what it resolves to, plus one level down
    // into /CF's per-filter dictionaries — the only nested dictionaries the handler reads. The
    // STRINGS (/O, /U, /OE, /UE, /Perms) are required to be direct and are copied across untouched,
    // so this never resolves anything that could need decrypting.
    private static PdfDictionary DereferenceValues(PdfDictionary encryptDict, Func<PdfObject?, PdfObject?>? resolve)
    {
        if (resolve is null)
            return encryptDict;

        var copy = new PdfDictionary();
        foreach (var (key, value) in encryptDict.Entries)
        {
            var resolved = value is PdfIndirectReference ? resolve(value) : value;
            if (key.Equals(_cfKey) && resolved is PdfDictionary cf)
            {
                var cfCopy = new PdfDictionary();
                foreach (var (filterName, filterValue) in cf.Entries)
                {
                    cfCopy.Set(
                        filterName,
                        (filterValue is PdfIndirectReference ? resolve(filterValue) : filterValue) ?? PdfNull.Instance);
                }

                resolved = cfCopy;
            }

            copy.Set(key, resolved ?? PdfNull.Instance);
        }

        return copy;
    }

    private static int LegacyKeyLengthBytes(PdfDictionary encryptDict, int v, int r)
    {
        if (v == 1)
            return 5;

        if (r == 2)
            return 5;

        if (v >= 4 && CryptFilterKeyLengthBytes(encryptDict) is { } fromFilter)
            return fromFilter;

        var bits = encryptDict.Get(_lengthKey) is PdfInteger li ? (int)li.Value : 40;
        if (bits % 8 != 0 || bits is < 40 or > 128)
        {
            throw new InvalidDataException(
                $"Malformed PDF: /Encrypt /Length {bits} is not a multiple of 8 in the range 40 to 128.");
        }

        return bits / 8;
    }

    // The /Length of the crypt filter /StmF names (falling back to /StrF, then to the sole entry if
    // there is exactly one). Table 25 measures it in bytes, but producers that copy the top-level
    // entry's units write bits, and the two ranges do not overlap: a legal byte count is 5..32 and a
    // legal bit count is 40..256, so a value above 32 can only be bits.
    private static int? CryptFilterKeyLengthBytes(PdfDictionary encryptDict)
    {
        if (encryptDict.Get(_cfKey) is not PdfDictionary cf)
            return null;

        var name = (encryptDict.Get(_stmFKey) as PdfName)?.Value
            ?? (encryptDict.Get(_strFKey) as PdfName)?.Value;

        PdfDictionary? filter = null;
        if (name is not null and not "Identity")
            filter = cf.Get(new PdfName(name)) as PdfDictionary;
        else if (name is null && cf.Entries.Count == 1)
            filter = cf.Entries.Single().Value as PdfDictionary;

        if (filter?.Get(_lengthKey) is not PdfInteger length)
            return null;

        var value = (int)length.Value;
        return value switch
        {
            >= 5 and <= 32 => value,
            >= 40 and <= 256 when value % 8 == 0 => value / 8,
            _ => null,
        };
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
