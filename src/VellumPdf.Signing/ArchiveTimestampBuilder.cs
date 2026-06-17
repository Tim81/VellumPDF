// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader;

namespace VellumPdf.Signing;

/// <summary>
/// Appends a PAdES B-LTA archive document timestamp (/DocTimeStamp) as a further
/// incremental revision over a B-LT PDF. The new revision contains a /DocTimeStamp
/// signature whose /SubFilter is /ETSI.RFC3161 and whose /Contents is a raw RFC 3161
/// token covering all bytes written so far (including the DSS revision).
/// </summary>
internal static class ArchiveTimestampBuilder
{
    // SHA-256 OID
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    /// <summary>
    /// Appends an archive document timestamp to <paramref name="ltvPdf"/> and returns
    /// the extended byte array.
    /// </summary>
    /// <param name="ltvPdf">
    /// A signed, B-LT PDF (byte array). Its existing signatures are not altered.
    /// </param>
    /// <param name="timestampClient">
    /// TSA client used to obtain the RFC 3161 token. Must not be null.
    /// </param>
    /// <param name="estimatedTokenSizeBytes">
    /// Reserved byte count for the /Contents hex string. Defaults to 32768 (32 KB),
    /// which is ample for a single-certificate TSA chain. Raise if the TSA embeds a
    /// longer chain and the size guard fires.
    /// </param>
    /// <returns>
    /// A new byte array containing <paramref name="ltvPdf"/> unchanged, followed by
    /// the incremental DocTimeStamp revision.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="ltvPdf"/> or <paramref name="timestampClient"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The TSA token is invalid, covers the wrong digest, or is larger than
    /// <paramref name="estimatedTokenSizeBytes"/>.
    /// </exception>
    internal static byte[] AddArchiveTimestamp(
        byte[] ltvPdf,
        ITimestampClient timestampClient,
        int estimatedTokenSizeBytes = 32768)
    {
        ArgumentNullException.ThrowIfNull(ltvPdf);
        ArgumentNullException.ThrowIfNull(timestampClient);

        using var reader = PdfReader.Open(ltvPdf);

        // ── Step 1: resolve the first page object number and dict ─────────────

        var pagesRaw = reader.Catalog.Get(PdfName.Pages)
            ?? throw new InvalidDataException("Malformed PDF: catalog is missing /Pages.");
        var pagesRef = pagesRaw as PdfIndirectReference
            ?? throw new InvalidDataException("Malformed PDF: /Pages is not an indirect reference.");
        var pagesDict = reader.Resolve(pagesRef.ObjectNumber) as PdfDictionary
            ?? throw new InvalidDataException("Malformed PDF: /Pages does not resolve to a dictionary.");

        var kidsRaw = pagesDict.Get(PdfName.Kids)
            ?? throw new InvalidDataException("Malformed PDF: page tree is missing /Kids.");
        var kidsArr = kidsRaw as PdfArray
            ?? throw new InvalidDataException("Malformed PDF: /Kids is not an array.");
        if (kidsArr.Count == 0)
            throw new InvalidDataException("Malformed PDF: /Kids array is empty.");

        var firstKidRaw = kidsArr[0];
        var firstPageRef = firstKidRaw as PdfIndirectReference
            ?? throw new InvalidDataException("Malformed PDF: first /Kids entry is not an indirect reference.");
        var firstPageDict = reader.Resolve(firstPageRef.ObjectNumber) as PdfDictionary
            ?? throw new InvalidDataException("Malformed PDF: first page does not resolve to a dictionary.");

        // ── Step 2: resolve the existing AcroForm /Fields ────────────────────

        var acroFormRaw = reader.Catalog.Get(new PdfName("AcroForm"))
            ?? throw new InvalidOperationException("PDF has no /AcroForm — cannot append DocTimeStamp field.");
        var acroFormDict = (acroFormRaw is PdfIndirectReference acroRef
            ? reader.Resolve(acroRef.ObjectNumber) as PdfDictionary
            : acroFormRaw as PdfDictionary)
            ?? throw new InvalidDataException("Malformed PDF: /AcroForm does not resolve to a dictionary.");

        var fieldsRaw = acroFormDict.Get(new PdfName("Fields"))
            ?? throw new InvalidDataException("Malformed PDF: /AcroForm has no /Fields.");
        var fieldsArr = (fieldsRaw is PdfIndirectReference fieldsRef
            ? reader.Resolve(fieldsRef.ObjectNumber) as PdfArray
            : fieldsRaw as PdfArray)
            ?? throw new InvalidDataException("Malformed PDF: /AcroForm /Fields does not resolve to an array.");

        // ── Step 3: assign new object numbers ────────────────────────────────

        var nextObjNum = reader.Size;
        var docTimeStampObjNum = nextObjNum++;
        var sigFieldObjNum = nextObjNum++;

        // ── Step 4: build the DocTimeStamp value dict (/V) ───────────────────

        var docTsDict = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("DocTimeStamp"))
            .Set(PdfName.Filter, new PdfName("Adobe.PPKLite"))
            .Set(new PdfName("SubFilter"), new PdfName("ETSI.RFC3161"))
            .Set(new PdfName("ByteRange"), new PdfRawBytesObject(PdfSignatureHelper.GetByteRangePlaceholderString()))
            .Set(PdfName.Contents, new PdfRawBytesObject(PdfSignatureHelper.GetContentsPlaceholder(estimatedTokenSizeBytes)));

        // ── Step 5: build the field/widget annotation dict ───────────────────

        var sigFieldDict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Widget"))
            .Set(new PdfName("FT"), new PdfName("Sig"))
            .Set(new PdfName("T"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes("DocTimeStamp1")))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]))
            .Set(new PdfName("F"), new PdfInteger(132))
            .Set(new PdfName("P"), firstPageRef)
            .Set(new PdfName("V"), new PdfIndirectReference(docTimeStampObjNum));

        // ── Step 6: updated catalog — clone + update inline AcroForm /Fields ──

        if (reader.Trailer.Get(PdfName.Root) is not PdfIndirectReference catalogRef)
            throw new InvalidDataException("Malformed PDF: trailer /Root is not an indirect reference.");
        var catalogObjNum = catalogRef.ObjectNumber;

        // Clone the fields array, appending the new widget ref.
        var newFields = new PdfArray();
        for (var i = 0; i < fieldsArr.Count; i++)
            newFields.Add(fieldsArr[i]);
        newFields.Add(new PdfIndirectReference(sigFieldObjNum));

        // Clone the acroForm dict, updating /Fields (keep /SigFlags 3).
        var newAcroForm = acroFormDict.ShallowCopy();
        newAcroForm.Set(new PdfName("Fields"), newFields);
        newAcroForm.Set(new PdfName("SigFlags"), new PdfInteger(3));

        // Clone the catalog, replacing inline /AcroForm.
        var newCatalog = reader.Catalog.ShallowCopy();
        newCatalog.Set(new PdfName("AcroForm"), newAcroForm);

        // ── Step 7: updated first page — clone + append widget to /Annots ─────

        var firstPageObjNum = firstPageRef.ObjectNumber;
        var newFirstPage = firstPageDict.ShallowCopy();

        var existingAnnotsRaw = firstPageDict.Get(new PdfName("Annots"));
        PdfArray newAnnots;
        if (existingAnnotsRaw is null)
        {
            newAnnots = new PdfArray();
        }
        else
        {
            var existingAnnots = (existingAnnotsRaw is PdfIndirectReference annRef
                ? reader.Resolve(annRef.ObjectNumber) as PdfArray
                : existingAnnotsRaw as PdfArray)
                ?? new PdfArray();

            newAnnots = new PdfArray();
            for (var i = 0; i < existingAnnots.Count; i++)
                newAnnots.Add(existingAnnots[i]);
        }
        newAnnots.Add(new PdfIndirectReference(sigFieldObjNum));
        newFirstPage.Set(new PdfName("Annots"), newAnnots);

        // ── Step 8: build the incremental revision with placeholders ──────────

        var newObjects = new List<(int ObjectNumber, PdfObject Value)>
        {
            (docTimeStampObjNum, docTsDict),
            (sigFieldObjNum, sigFieldDict),
            (firstPageObjNum, newFirstPage),
            (catalogObjNum, newCatalog),
        };
        newObjects.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));

        var withPlaceholders = reader.AppendRevision(newObjects);

        // ── Step 9: locate the /ByteRange placeholder in the NEW revision ─────
        // The existing signature(s)' /ByteRange values are already patched (real digits);
        // only the new DocTimeStamp has the placeholder, so LocateContentsToken finds it.
        var posLt = SignaturePlaceholderPatcher.LocateContentsToken(withPlaceholders, estimatedTokenSizeBytes, out var hexLen);

        // ── Step 10: compute and patch /ByteRange ─────────────────────────────
        var (br0, br1, br2, br3) = SignaturePlaceholderPatcher.ComputeAndPatchByteRange(withPlaceholders, posLt, hexLen);

        // ── Step 11: hash the signed content and get TSA token ────────────────
        var signedContent = SignaturePlaceholderPatcher.BuildSignedContent(withPlaceholders, br0, br1, br2, br3);
        var digest = SHA256.HashData(signedContent);

        var tokenDer = timestampClient.GetTimestampToken(digest, HashAlgorithmName.SHA256);

        // Validate the token before embedding it.
        if (!Rfc3161TimestampToken.TryDecode(tokenDer, out var token, out _))
            throw new InvalidOperationException(
                "Timestamp client returned data that is not a valid RFC 3161 token.");

        var tokenInfo = token!.TokenInfo;
        if (tokenInfo.HashAlgorithmId.Value != Sha256Oid
            || !tokenInfo.GetMessageHash().Span.SequenceEqual(digest))
            throw new InvalidOperationException(
                "The RFC 3161 timestamp token does not cover the expected digest.");

        // ── Step 12: patch /Contents ──────────────────────────────────────────
        SignaturePlaceholderPatcher.PatchContents(withPlaceholders, posLt, hexLen, tokenDer, "RFC 3161 timestamp token");

        return withPlaceholders;
    }
}
