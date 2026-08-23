// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader;

/// <summary>
/// Resolves which <see cref="CryptFilterMethod"/> applies to a given stream, honouring an explicit
/// <c>/Crypt</c> filter entry (ISO 32000-2 §7.4.10) or the <c>/EncryptMetadata</c> exemption where
/// either overrides the document-wide <c>/StmF</c>. <see cref="StandardSecurityDecryptor"/> only
/// ever sees the method it is told to use; deciding which one that is for a particular object is
/// this type's job, kept separate so it can be unit-tested without an encrypted document at all.
/// </summary>
internal static class CryptFilterResolver
{
    private static readonly PdfName _cf = new("CF");
    private static readonly PdfName _cfm = new("CFM");
    private static readonly PdfName _decodeParms = new("DecodeParms");
    private static readonly PdfName _dp = new("DP");
    private static readonly PdfName _name = new("Name");
    private static readonly PdfName _type = new("Type");
    private static readonly PdfName _f = new("F");

    /// <summary>
    /// Parses <c>/Encrypt /CF</c> into a name→method table. A <c>/CF</c> entry whose <c>/CFM</c> is
    /// missing, unrecognised, or not itself a dictionary maps to <see cref="CryptFilterMethod.Unsupported"/>
    /// rather than being omitted — an omitted entry and a present-but-broken one must fail the same
    /// way when named by <c>/StmF</c>, <c>/StrF</c>, or a <c>/Crypt</c> filter's <c>/Name</c>.
    /// </summary>
    internal static Dictionary<string, CryptFilterMethod> BuildCfTable(
        PdfDictionary encryptDict, Func<PdfObject?, PdfObject?>? resolve)
    {
        var table = new Dictionary<string, CryptFilterMethod>(StringComparer.Ordinal);
        if (Deref(resolve, encryptDict.Get(_cf)) is not PdfDictionary cfDict)
            return table;

        foreach (var kv in cfDict.Entries)
        {
            if (Deref(resolve, kv.Value) is not PdfDictionary filterDict)
            {
                table[kv.Key.Value] = CryptFilterMethod.Unsupported;
                continue;
            }

            var cfm = (Deref(resolve, filterDict.Get(_cfm)) as PdfName)?.Value;
            table[kv.Key.Value] = cfm switch
            {
                "None" or "Identity" => CryptFilterMethod.Identity,
                "V2" => CryptFilterMethod.Rc4,
                "AESV2" => CryptFilterMethod.Aes128,
                "AESV3" => CryptFilterMethod.Aes256,
                _ => CryptFilterMethod.Unsupported,
            };
        }

        return table;
    }

    /// <summary>
    /// Resolves a <c>/StmF</c>, <c>/StrF</c>, or <c>/Crypt</c>-filter <c>/Name</c> value to a method.
    /// <c>/Identity</c> is a reserved name (ISO 32000-2 Table 20) that never needs a <c>/CF</c> entry;
    /// a missing name defaults to it too (the documented default for <c>/StmF</c>/<c>/StrF</c>, and
    /// what a <c>/Crypt</c> filter's own <c>/DecodeParms</c> defaults to when <c>/Name</c> is absent).
    /// Anything else must be a key in <paramref name="cfTable"/> — a name that is not maps to
    /// <see cref="CryptFilterMethod.Unsupported"/>, not silently to <see cref="CryptFilterMethod.Identity"/>:
    /// treating an unrecognised name as Identity would hand back ciphertext as if it were plaintext
    /// with no error, for exactly the reason <see cref="CryptFilterMethod.Unsupported"/>'s own doc
    /// comment gives.
    /// </summary>
    internal static CryptFilterMethod ResolveNamedMethod(string? name, IReadOnlyDictionary<string, CryptFilterMethod> cfTable)
    {
        if (name is null or "Identity")
            return CryptFilterMethod.Identity;
        return cfTable.TryGetValue(name, out var method) ? method : CryptFilterMethod.Unsupported;
    }

    /// <summary>
    /// The effective crypt filter method for <paramref name="streamDict"/>. An explicit <c>/Crypt</c>
    /// filter — which ISO 32000-2 §7.4.10 requires to be the first entry of <c>/Filter</c> when
    /// present — overrides <paramref name="defaultStreamFilter"/> (the document-wide <c>/StmF</c>,
    /// or the implicit RC4 for V&lt;4) for this one object, via its <c>/DecodeParms</c> <c>/Name</c>.
    ///
    /// <para>
    /// <c>/EncryptMetadata</c> false is checked first and independently of <c>/Filter</c>: ISO
    /// 32000-2 §7.6.2 says the metadata stream is not encrypted in that case, but does not route the
    /// exemption through a <c>/Crypt</c> filter entry, and no producer this corpus was built against
    /// (qpdf; VellumPdf's own writer, see <c>PdfDocument.WriteAllWithEncryptExempt</c>) writes one —
    /// both instead simply never encrypt that one stream's body at write time. So the exemption is
    /// recognised structurally, by <c>/Type /Metadata</c> (ISO 32000-2 §14.3.2), the same way this
    /// stream is identified as the metadata stream at all.
    /// </para>
    /// </summary>
    internal static CryptFilterMethod ResolveStreamMethod(
        PdfDictionary streamDict,
        CryptFilterMethod defaultStreamFilter,
        IReadOnlyDictionary<string, CryptFilterMethod> cfTable,
        bool encryptMetadata,
        Func<PdfObject?, PdfObject?>? resolve,
        bool isCrossReferenceStream = false)
    {
        if (!encryptMetadata && IsMetadataStream(streamDict, resolve))
            return CryptFilterMethod.Identity;

        // Two exemptions the spec states as "shall not be encrypted", both checked before /Filter
        // for the same reason /EncryptMetadata is: neither is routed through a /Crypt filter entry.
        //
        // ISO 32000-1 §7.5.8.2: "The cross-reference stream shall not be encrypted"; the clause goes
        // on to forbid it a /Crypt filter outright. The caller decides this one, from the object
        // numbers XrefParser actually consumed as cross-reference streams — deliberately not from a
        // /Type /XRef entry, which the document's author controls and could put on a page's content
        // stream to have its ciphertext handed to a preflight rule as if it were the operators.
        // XrefParser itself never comes through here; it reads the stream before a decryptor exists.
        //
        // ISO 32000-1 §7.6.1: a stream whose data lives in an external file (/F) "shall not be
        // encrypted, since they are not part of the PDF file itself". The bytes between `stream` and
        // `endstream` are ignored for such a stream anyway (§7.3.8.2), but decrypting them turns a
        // legal document into a hard failure under AES, which rejects data that is not whole blocks.
        if (isCrossReferenceStream || IsExternalFileStream(streamDict, resolve))
            return CryptFilterMethod.Identity;

        if (FirstFilterName(streamDict, resolve) != "Crypt")
            return defaultStreamFilter;

        var parms = DecodeParmsForFirstFilter(streamDict, resolve);
        var name = (Deref(resolve, parms?.Get(_name)) as PdfName)?.Value;
        return ResolveNamedMethod(name, cfTable);
    }

    private static bool IsMetadataStream(PdfDictionary streamDict, Func<PdfObject?, PdfObject?>? resolve)
        => (Deref(resolve, streamDict.Get(_type)) as PdfName)?.Value == "Metadata";

    // /F is only an external-file specification when it is a file specification — a string or a
    // dictionary (ISO 32000-1 Table 5, 7.11.2). A stream dictionary is free to use /F for something
    // else entirely: an inline image's abbreviated /Filter is /F, so a number or a name here says
    // nothing about external data and must not exempt the stream.
    private static bool IsExternalFileStream(PdfDictionary streamDict, Func<PdfObject?, PdfObject?>? resolve)
        => Deref(resolve, streamDict.Get(_f)) is PdfLiteralString or PdfHexString or PdfDictionary;

    private static string? FirstFilterName(PdfDictionary dict, Func<PdfObject?, PdfObject?>? resolve)
    {
        var filterObj = Deref(resolve, dict.Get(PdfName.Filter));
        return filterObj switch
        {
            PdfName n => n.Value,
            PdfArray { Count: > 0 } arr => (Deref(resolve, arr[0]) as PdfName)?.Value,
            _ => null,
        };
    }

    private static PdfDictionary? DecodeParmsForFirstFilter(PdfDictionary dict, Func<PdfObject?, PdfObject?>? resolve)
    {
        var parmsObj = Deref(resolve, dict.Get(_decodeParms) ?? dict.Get(_dp));
        return parmsObj switch
        {
            PdfDictionary d => d,
            PdfArray { Count: > 0 } arr => Deref(resolve, arr[0]) as PdfDictionary,
            _ => null,
        };
    }

    private static PdfObject? Deref(Func<PdfObject?, PdfObject?>? resolve, PdfObject? obj)
        => resolve is null ? obj : resolve(obj);
}
