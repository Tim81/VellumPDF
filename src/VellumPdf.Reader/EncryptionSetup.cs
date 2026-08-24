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
    private static readonly PdfName _effKey = new("EFF");
    private static readonly PdfName _cfmKey = new("CFM");
    private static readonly PdfName _permsKey = new("Perms");
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

        public required CryptFilterMethod? EmbeddedFileFilter { get; init; }

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
        // simple. BuildCfTable takes the resolver as well, for the one level this copy does not
        // reach: the values INSIDE a /CF entry, where an indirect /CFM would otherwise read as a
        // missing one and turn every stream in the document into Unsupported.
        encryptDict = DereferenceValues(encryptDict, resolve);

        var v = (int)RequireInt(encryptDict, _vKey, "/V");
        var r = (int)RequireInt(encryptDict, _rKey, "/R");

        // ISO 32000-1 Table 20 forbids both of these values, and they are not the same failure.
        //
        // /V 3 names "an unpublished algorithm that permits encryption key lengths ranging from 40 to
        // 128 bits", and the row ends "This value shall not appear in a conforming PDF file" — so the
        // document is non-conforming either way. Reported as unsupported rather than malformed
        // because the distinction that helps a caller holding one is "this library cannot read it",
        // not "your file is broken": the bytes may be perfectly good to a tool that has the
        // algorithm, and no clean-room implementation can ever acquire it.
        //
        // /V 0 is "an algorithm that is undocumented. This value shall not be used", and Table 20
        // also makes it the default when /V is absent. There is no algorithm to name, so this is a
        // malformed encryption dictionary rather than a feature to implement.
        if (v == 3)
        {
            throw new UnsupportedPdfFeatureException(
                "/Encrypt /V 3 names the unpublished algorithm ISO 32000-1 Table 20 reserves for it, "
                + "which no clean-room implementation can provide.");
        }

        if (v == 0)
        {
            throw new InvalidDataException(
                "Malformed PDF: /Encrypt /V 0 is the undocumented algorithm ISO 32000-1 Table 20 says "
                + "shall not be used, so there is no algorithm to apply.");
        }
        var o = RequireBytes(encryptDict, _oKey, "/O");
        var u = RequireBytes(encryptDict, _uKey, "/U");
        var oe = TryGetBytes(encryptDict, _oeKey);
        var ue = TryGetBytes(encryptDict, _ueKey);
        var p = unchecked((int)RequireInt(encryptDict, _pKey, "/P"));
        // "Optional; meaningful only when the value of V is 4" (ISO 32000-1 Table 20's standard
        // handler row). Below that the entry says nothing, and honouring a stray copy of it — a
        // producer downgrading a document and carrying the key across — would hand the metadata
        // stream back as ciphertext. StandardSecurityDecryptor already gates the key-derivation half
        // on the revision, so reading it here without a gate is the two halves disagreeing.
        var encryptMetadata = v < 4
            || encryptDict.Get(_encryptMetadataKey) is not PdfBoolean emBool
            || emBool.Value;
        var id0 = GetId0(trailer);
        var keyLengthBytes = r >= 5 ? 32 : LegacyKeyLengthBytes(encryptDict, v, r);

        var cfTable = v >= 4
            ? CryptFilterResolver.BuildCfTable(encryptDict, resolve)
            : new Dictionary<string, CryptFilterMethod>(StringComparer.Ordinal);

        CryptFilterMethod streamFilter;
        CryptFilterMethod stringFilter;
        CryptFilterMethod? embeddedFileFilter;
        if (v < 4)
        {
            // Algorithm 1: V=1/V=2 predate /CF entirely; the implicit method is always RC4, for
            // both streams and strings, never AES.
            streamFilter = CryptFilterMethod.Rc4;
            stringFilter = CryptFilterMethod.Rc4;

            // Table 20 makes /EFF meaningful only at /V 4 and above. Null, not Rc4: a non-null value
            // here would send every embedded file stream down the /EFF path on a document that never
            // mentioned /EFF at all.
            embeddedFileFilter = null;
        }
        else
        {
            var stmFName = (encryptDict.Get(_stmFKey) as PdfName)?.Value;
            var strFName = (encryptDict.Get(_strFKey) as PdfName)?.Value;
            streamFilter = CryptFilterResolver.ResolveNamedMethod(stmFName, cfTable);
            stringFilter = CryptFilterResolver.ResolveNamedMethod(strFName, cfTable);

            // /EFF names the crypt filter for embedded file streams, and is what makes "encrypt only
            // the attachments" expressible: /StmF and /StrF Identity with /EFF naming a real filter
            // (ISO 32000-1 §7.6.1). Null where the document declares none, so an embedded file stream
            // takes /StmF like every other stream — which is what the same clause says.
            embeddedFileFilter = encryptDict.Get(_effKey) is PdfName eff
                ? CryptFilterResolver.ResolveNamedMethod(eff.Value, cfTable)
                : null;
        }

        var decryptor = new StandardSecurityDecryptor(
            v, r, keyLengthBytes, o, u, oe, ue, p, id0, encryptMetadata, streamFilter, stringFilter);

        if (!TryAuthenticate(decryptor, password, out var fileKey, out var isOwnerAccess))
        {
            throw new PdfPasswordException(
                "The supplied password does not authenticate as either the owner or the user password.");
        }

        // An unresolvable /StrF is fatal here, while an unresolvable /StmF is left to fail at decode
        // (see StmFNamingUndefinedCfEntry_opensButThrowsOnDecode). The asymmetry is deliberate: a
        // document whose streams cannot be decrypted still has readable strings — /Info, the
        // structure tree, every name and date — but one whose STRINGS cannot be decrypted has
        // nothing to offer, and every one of them would come back as ciphertext with nothing to
        // report it. ISO 32000-2 Table 20 makes a /StrF naming an absent /CF entry an error.
        if (stringFilter == CryptFilterMethod.Unsupported)
        {
            var strFName = (encryptDict.Get(_strFKey) as PdfName)?.Value;

            // Two failures wear the same CryptFilterMethod, and they are not the same kind of file:
            // a /StrF naming a /CF entry the document never defines is malformed, while one naming a
            // /CFM this library does not implement is a valid document beyond our reach — the
            // distinction /V 3 already draws.
            throw strFName is not null && cfTable.ContainsKey(strFName)
                ? new UnsupportedPdfFeatureException(
                    $"/Encrypt /StrF names the crypt filter '{strFName}', whose /CFM this library does "
                    + "not implement, so no string in the document can be decrypted.")
                : new InvalidDataException(
                    "Malformed PDF: /Encrypt /StrF names a /CF entry the document does not define, so "
                    + "no string in the document can be decrypted.");
        }

        // ISO 32000-2 §7.6.4.4.12, Algorithm 13. At R<=4 /P is an input to Algorithm 2, so editing it
        // breaks authentication on its own; at R>=5 the file key is random and the dictionary's /P is
        // unprotected, and /Perms carries the copy that was sealed under that key when the document
        // was written. Where the two disagree, the sealed one is the document's real permission set
        // and the dictionary's is whatever the last editor put there.
        //
        // Reported, not refused. qpdf, poppler and pdfium all read a document whose /Perms disagrees,
        // so rejecting it would make this the only library that cannot open the file at all — while
        // taking the dictionary's word would hand the caller permissions someone else chose.
        var perms = TryGetBytes(encryptDict, _permsKey);
        var authenticatedP = perms is not null ? decryptor.RecoverAuthenticatedPermissions(fileKey, perms) : null;

        return new Result
        {
            Decryptor = decryptor,
            FileKey = fileKey,
            CryptFilterTable = cfTable,
            EncryptMetadata = encryptMetadata,
            IsOwnerAccess = isOwnerAccess,
            Cipher = ToCipher(streamFilter),
            EmbeddedFileFilter = embeddedFileFilter,
            StringCipher = ToCipher(stringFilter),
            KeyLengthBits = keyLengthBytes * 8,
            Permissions = (PdfPermissions)(authenticatedP ?? p) & PdfPermissions.All,
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
    private static int LegacyKeyLengthBytes(PdfDictionary encryptDict, int v, int r)
    {
        if (v == 1)
            return 5;

        if (r == 2)
            return 5;

        if (v >= 4 && CryptFilterKeyLengthBytes(encryptDict) is { } fromFilter)
            return fromFilter;

        // 40 bits is Table 20's default for the top-level entry, and it is the right default only
        // where that entry is the one in force. At /V 4 the crypt filter is, so a document with no
        // usable /Length anywhere is 128-bit — the shortest key any V4 cipher uses.
        var defaultBits = v >= 4 ? 128 : 40;
        var bits = encryptDict.Get(_lengthKey) is PdfInteger li ? (int)li.Value : defaultBits;
        if (bits % 8 != 0 || bits is < 40 or > 128)
        {
            throw new InvalidDataException(
                $"Malformed PDF: /Encrypt /Length {bits} is not a multiple of 8 in the range 40 to 128.");
        }

        return bits / 8;
    }

    // The key length the crypt filter in force implies, in bytes, or null when the document says
    // nothing usable and the caller should fall back.
    //
    // Both /Length entries are OPTIONAL — Table 20's and Table 25's alike — so a conformant /V 4
    // document may carry neither, and a reader that treats that as "40 bits" rejects the correct
    // password on a file every other reader opens. The cipher itself settles it in that case:
    // AESV3 is 256-bit by definition, AESV2 128-bit, and V2 (RC4 under a crypt filter) is 128-bit
    // in every V4 producer's output.
    private static int? CryptFilterKeyLengthBytes(PdfDictionary encryptDict)
    {
        if (encryptDict.Get(_cfKey) is not PdfDictionary cf)
            return null;

        // /StmF first, then /StrF — and Identity on either is "no crypt filter", not a name to look
        // up, so a document that encrypts only its strings still finds its /StrF entry here.
        var name = FilterName(encryptDict, _stmFKey) ?? FilterName(encryptDict, _strFKey);

        PdfDictionary? filter = null;
        if (name is not null)
            filter = cf.Get(new PdfName(name)) as PdfDictionary;
        else if (cf.Entries.Count == 1)
            filter = cf.Entries.Single().Value as PdfDictionary;

        if (filter is null)
            return null;

        var impliedByCipher = (filter.Get(_cfmKey) as PdfName)?.Value switch
        {
            "AESV3" => 32,
            "AESV2" => 16,
            "V2" => 16,
            _ => (int?)null,
        };

        if (filter.Get(_lengthKey) is not PdfInteger length)
            return impliedByCipher;

        // Table 25 measures this in bytes, but producers that copy the top-level entry's units write
        // bits, and the two ranges cannot overlap: a legal byte count is 5..32, a legal bit count is
        // 40..256. A value that is neither, or that the cipher cannot use, is the document
        // contradicting itself — the cipher wins, since it is what will actually be applied.
        var declared = (int)length.Value switch
        {
            >= 5 and <= 32 and var bytes => bytes,
            >= 40 and <= 256 and var bits when bits % 8 == 0 => bits / 8,
            _ => (int?)null,
        };

        if (declared is null)
            return impliedByCipher;

        // A declared length the cipher cannot use is the document contradicting itself, and the
        // cipher wins because it is what will actually be applied. Only RC4 has a range to declare:
        // ISO 32000-1 Table 20 allows it 40 to 128 bits, while AES-128 and AES-256 each have exactly
        // one legal key size.
        var cfm = (filter.Get(_cfmKey) as PdfName)?.Value;
        return cfm switch
        {
            "AESV3" => declared == 32 ? declared : 32,
            "AESV2" => declared == 16 ? declared : 16,
            "V2" => declared is >= 5 and <= 16 ? declared : impliedByCipher,
            _ => declared,
        };
    }

    // The /StmF or /StrF name, or null when it is absent or Identity — neither of which names a /CF
    // entry to look up (ISO 32000-2 Table 20: Identity is reserved and needs no entry).
    private static string? FilterName(PdfDictionary encryptDict, PdfName key)
        => (encryptDict.Get(key) as PdfName)?.Value is { } name and not "Identity" ? name : null;

    private static PdfCipherAlgorithm ToCipher(CryptFilterMethod method) => method switch
    {
        CryptFilterMethod.Identity => PdfCipherAlgorithm.Identity,
        CryptFilterMethod.Rc4 => PdfCipherAlgorithm.Rc4,
        CryptFilterMethod.Aes128 => PdfCipherAlgorithm.Aes128,
        CryptFilterMethod.Aes256 => PdfCipherAlgorithm.Aes256,
        _ => PdfCipherAlgorithm.Unsupported,
    };
}
