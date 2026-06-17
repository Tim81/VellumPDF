// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Signing;

/// <summary>
/// Builds a PAdES B-LT Document Security Store (DSS) and appends it to a
/// signed PDF as an incremental revision, without altering the signed bytes.
/// </summary>
internal static class DssBuilder
{
    // OID 1.2.840.113549.1.9.16.2.14 — id-aa-signatureTimeStampToken (RFC 3161 unsigned attribute)
    private const string SignatureTimestampOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>
    /// Appends a /DSS (Document Security Store) incremental revision to an already-signed
    /// PDF, embedding certificate chains and revocation evidence (OCSP/CRL) for each signature.
    /// </summary>
    /// <param name="signedPdf">The byte array of a fully signed PDF document.</param>
    /// <param name="revocationClient">
    /// Provides OCSP responses and/or CRLs for each non-self-issued certificate.
    /// </param>
    /// <returns>
    /// A new byte array containing the original bytes plus the incremental DSS revision.
    /// The existing signature(s) remain cryptographically intact.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="signedPdf"/> contains no signatures.
    /// </exception>
    internal static byte[] AddLongTermValidation(byte[] signedPdf, IRevocationClient revocationClient)
    {
        ArgumentNullException.ThrowIfNull(signedPdf);
        ArgumentNullException.ThrowIfNull(revocationClient);

        using var reader = PdfReader.Open(signedPdf);

        if (reader.Signatures.Count == 0)
            throw new InvalidOperationException("Document has no signatures to add LTV to.");

        // ── Step 1: collect certificates and revocation evidence per signature ────

        // Deduplication maps: key = DER bytes (as hex for equality), value = canonical byte array
        var allCerts = new Dictionary<string, byte[]>();
        var allOcsps = new Dictionary<string, byte[]>();
        var allCrls = new Dictionary<string, byte[]>();

        // Per-signature VRI data: key = uppercase hex SHA-1 of /Contents DER
        var vriData = new Dictionary<string, VriEntry>();

        // X509Certificate2 instances decoded from the CMS wrap unmanaged handles; collect
        // them so they can be disposed after use rather than waiting for finalization.
        var ownedCerts = new List<X509Certificate2>();

        foreach (var sig in reader.Signatures)
        {
            if (sig.Contents.IsEmpty)
                continue;

            var contentsDer = sig.Contents.ToArray();

            // VRI key = uppercase hex of SHA-1 over the /Contents DER bytes
            var sha1Hash = SHA1.HashData(contentsDer);
            var vriKey = Convert.ToHexString(sha1Hash);

            // Decode the signature CMS. A parameterless SignedCms handles both a detached
            // signature (the original /Contents) and an embedded-content CMS (an archive
            // DocTimeStamp's RFC-3161 token, whose certs we also want in the DSS).
            var outerCms = new SignedCms();
            outerCms.Decode(contentsDer);

            // Gather signer certificates (chain embedded via WholeChain)
            var sigCerts = new List<X509Certificate2>(outerCms.Certificates.Cast<X509Certificate2>());
            ownedCerts.AddRange(sigCerts);

            // Decode the timestamp token if present, gather TSA chain
            if (outerCms.SignerInfos.Count > 0)
            {
                var si = outerCms.SignerInfos[0];
                foreach (CryptographicAttributeObject attr in si.UnsignedAttributes)
                {
                    if (attr.Oid.Value == SignatureTimestampOid && attr.Values.Count > 0)
                    {
                        var tokenDer = attr.Values[0].RawData;
                        try
                        {
                            var tst = new SignedCms();
                            tst.Decode(tokenDer);
                            foreach (X509Certificate2 tsaCert in tst.Certificates)
                            {
                                sigCerts.Add(tsaCert);
                                ownedCerts.Add(tsaCert);
                            }
                        }
                        catch (CryptographicException)
                        {
                            // Malformed timestamp token — skip its certs
                        }
                        break; // only one timestamp attribute expected
                    }
                }
            }

            // Remove duplicate certs within this signature's chain
            var dedupedSigCerts = DedupeCerts(sigCerts);

            // Fetch revocation data for each non-self-issued cert
            var vriCertKeys = new List<string>();
            var vriOcspKeys = new List<string>();
            var vriCrlKeys = new List<string>();

            foreach (var cert in dedupedSigCerts)
            {
                // Add cert to document-wide set (TryAdd is a no-op for duplicates).
                var certKey = ToHexKey(cert.RawData);
                allCerts.TryAdd(certKey, cert.RawData);
                vriCertKeys.Add(certKey);

                // Skip self-signed roots
                if (IsSelfIssued(cert))
                    continue;

                // Find issuer in the set
                var issuer = FindIssuer(cert, dedupedSigCerts);
                if (issuer is null)
                    continue;

                RevocationData revData;
                try
                {
                    revData = revocationClient.GetRevocationData(cert, issuer);
                }
                catch (Exception)
                {
                    continue;
                }

                if (revData.Ocsp is { } ocspBytes)
                {
                    var ocspArr = ocspBytes.ToArray();
                    var ocspKey = ToHexKey(ocspArr);
                    allOcsps.TryAdd(ocspKey, ocspArr);
                    vriOcspKeys.Add(ocspKey);
                }

                if (revData.Crl is { } crlBytes)
                {
                    var crlArr = crlBytes.ToArray();
                    var crlKey = ToHexKey(crlArr);
                    allCrls.TryAdd(crlKey, crlArr);
                    vriCrlKeys.Add(crlKey);
                }
            }

            vriData[vriKey] = new VriEntry(vriCertKeys, vriOcspKeys, vriCrlKeys);
        }

        foreach (var c in ownedCerts)
            c.Dispose();

        // ── Step 2: assign object numbers and build the indirect object list ─────

        var nextObjNum = reader.Size;
        var newObjects = new List<(int ObjectNumber, PdfObject Value)>();

        // Assign object numbers to cert streams
        var certObjNums = new Dictionary<string, int>();
        foreach (var (key, der) in allCerts)
        {
            certObjNums[key] = nextObjNum++;
            newObjects.Add((certObjNums[key], new UncompressedPdfStream(der)));
        }

        // Assign object numbers to OCSP streams
        var ocspObjNums = new Dictionary<string, int>();
        foreach (var (key, der) in allOcsps)
        {
            ocspObjNums[key] = nextObjNum++;
            newObjects.Add((ocspObjNums[key], new UncompressedPdfStream(der)));
        }

        // Assign object numbers to CRL streams
        var crlObjNums = new Dictionary<string, int>();
        foreach (var (key, der) in allCrls)
        {
            crlObjNums[key] = nextObjNum++;
            newObjects.Add((crlObjNums[key], new UncompressedPdfStream(der)));
        }

        // ── Step 3: build /DSS dictionary ─────────────────────────────────────────

        var dssDict = new PdfDictionary();
        dssDict.Set(new PdfName("Type"), new PdfName("DSS"));

        // /Certs — document-wide union
        if (certObjNums.Count > 0)
        {
            var certsArray = new PdfArray();
            foreach (var objNum in certObjNums.Values)
                certsArray.Add(new PdfIndirectReference(objNum));
            dssDict.Set(new PdfName("Certs"), certsArray);
        }

        // /OCSPs — document-wide union
        if (ocspObjNums.Count > 0)
        {
            var ocspsArray = new PdfArray();
            foreach (var objNum in ocspObjNums.Values)
                ocspsArray.Add(new PdfIndirectReference(objNum));
            dssDict.Set(new PdfName("OCSPs"), ocspsArray);
        }

        // /CRLs — document-wide union
        if (crlObjNums.Count > 0)
        {
            var crlsArray = new PdfArray();
            foreach (var objNum in crlObjNums.Values)
                crlsArray.Add(new PdfIndirectReference(objNum));
            dssDict.Set(new PdfName("CRLs"), crlsArray);
        }

        // /VRI — per-signature subdictionaries
        var vriDict = new PdfDictionary();
        foreach (var (vriKey, entry) in vriData)
        {
            var vriEntry = new PdfDictionary();
            vriEntry.Set(new PdfName("Type"), new PdfName("VRI"));

            if (entry.CertKeys.Count > 0)
            {
                var certArr = new PdfArray();
                foreach (var ck in entry.CertKeys)
                    if (certObjNums.TryGetValue(ck, out var n))
                        certArr.Add(new PdfIndirectReference(n));
                if (certArr.Count > 0)
                    vriEntry.Set(new PdfName("Cert"), certArr);
            }

            if (entry.OcspKeys.Count > 0)
            {
                var ocspArr = new PdfArray();
                foreach (var ok in entry.OcspKeys)
                    if (ocspObjNums.TryGetValue(ok, out var n))
                        ocspArr.Add(new PdfIndirectReference(n));
                if (ocspArr.Count > 0)
                    vriEntry.Set(new PdfName("OCSP"), ocspArr);
            }

            if (entry.CrlKeys.Count > 0)
            {
                var crlArr = new PdfArray();
                foreach (var ck in entry.CrlKeys)
                    if (crlObjNums.TryGetValue(ck, out var n))
                        crlArr.Add(new PdfIndirectReference(n));
                if (crlArr.Count > 0)
                    vriEntry.Set(new PdfName("CRL"), crlArr);
            }

            vriDict.Set(new PdfName(vriKey), vriEntry);
        }
        dssDict.Set(new PdfName("VRI"), vriDict);

        var dssObjNum = nextObjNum++;
        newObjects.Add((dssObjNum, dssDict));

        // ── Step 4: clone the catalog and point /DSS at the new DSS object ────────

        var catalogRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalogObjNum = catalogRef.ObjectNumber;
        var newCatalog = reader.Catalog.ShallowCopy();
        newCatalog.Set(new PdfName("DSS"), new PdfIndirectReference(dssObjNum));
        newObjects.Add((catalogObjNum, newCatalog));

        // ── Step 5: sort by object number and append as an incremental revision ───

        newObjects.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));
        return reader.AppendRevision(newObjects);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static bool IsSelfIssued(X509Certificate2 cert) =>
        cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData.AsSpan());

    private static X509Certificate2? FindIssuer(X509Certificate2 cert, IReadOnlyList<X509Certificate2> candidates)
    {
        var issuerRaw = cert.IssuerName.RawData;
        foreach (var candidate in candidates)
        {
            if (candidate.SubjectName.RawData.AsSpan().SequenceEqual(issuerRaw.AsSpan()))
                return candidate;
        }
        return null;
    }

    private static List<X509Certificate2> DedupeCerts(IEnumerable<X509Certificate2> certs)
    {
        var seen = new HashSet<string>();
        var result = new List<X509Certificate2>();
        foreach (var cert in certs)
        {
            var key = ToHexKey(cert.RawData);
            if (seen.Add(key))
                result.Add(cert);
        }
        return result;
    }

    private static string ToHexKey(byte[] data) => Convert.ToHexString(data);

    private sealed record VriEntry(List<string> CertKeys, List<string> OcspKeys, List<string> CrlKeys);
}
