// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Unit-level tests of <see cref="CryptFilterResolver"/> against synthetic dictionaries — no PDF
/// file, no <see cref="StandardSecurityDecryptor"/> — so the crypt-filter-method resolution logic
/// (which stream gets Identity, which gets Unsupported, which named /CF entry applies) is pinned
/// independently of a full document round-trip.
/// </summary>
public sealed class CryptFilterResolverTests
{
    private static readonly PdfName _cfKey = new("CF");
    private static readonly PdfName _cfmKey = new("CFM");
    private static readonly PdfName _stdCf = new("StdCF");
    private static readonly PdfName _filterKey = PdfName.Filter;
    private static readonly PdfName _decodeParmsKey = new("DecodeParms");
    private static readonly PdfName _nameKey = new("Name");
    private static readonly PdfName _cryptFilterName = new("Crypt");

    [Fact]
    public void BuildCfTable_mapsEachCfmToItsMethod()
    {
        var encryptDict = new PdfDictionary().Set(_cfKey, new PdfDictionary()
            .Set(new PdfName("Rc4Filter"), new PdfDictionary().Set(_cfmKey, new PdfName("V2")))
            .Set(new PdfName("Aes128Filter"), new PdfDictionary().Set(_cfmKey, new PdfName("AESV2")))
            .Set(new PdfName("Aes256Filter"), new PdfDictionary().Set(_cfmKey, new PdfName("AESV3")))
            .Set(new PdfName("IdentityFilter"), new PdfDictionary().Set(_cfmKey, new PdfName("Identity")))
            // ISO 32000-2 Table 25 lists /None alongside /Identity as "the application shall not
            // decrypt data". Dropping it makes every stream of such a document throw.
            .Set(new PdfName("NoneFilter"), new PdfDictionary().Set(_cfmKey, new PdfName("None"))));

        var table = CryptFilterResolver.BuildCfTable(encryptDict, resolve: null);

        Assert.Equal(CryptFilterMethod.Rc4, table["Rc4Filter"]);
        Assert.Equal(CryptFilterMethod.Aes128, table["Aes128Filter"]);
        Assert.Equal(CryptFilterMethod.Aes256, table["Aes256Filter"]);
        Assert.Equal(CryptFilterMethod.Identity, table["IdentityFilter"]);
        Assert.Equal(CryptFilterMethod.Identity, table["NoneFilter"]);
    }

    /// <summary>
    /// A /CF entry that is present but not a dictionary, or a dictionary with no /CFM, has to fail
    /// the same way an absent one does. BuildCfTable's own doc comment says so; nothing checked it,
    /// and mapping either to Identity hands the caller ciphertext to read as content.
    /// </summary>
    [Theory]
    [InlineData("notADictionary")]
    [InlineData("noCfm")]
    public void BuildCfTable_presentButBrokenEntry_mapsToUnsupported(string shape)
    {
        var cf = new PdfDictionary();
        if (shape == "notADictionary")
            cf.Set(new PdfName("Broken"), new PdfInteger(5));
        else
            cf.Set(new PdfName("Broken"), new PdfDictionary());

        var table = CryptFilterResolver.BuildCfTable(new PdfDictionary().Set(_cfKey, cf), resolve: null);

        Assert.Equal(CryptFilterMethod.Unsupported, table["Broken"]);
    }

    [Fact]
    public void BuildCfTable_unrecognisedCfm_mapsToUnsupported()
    {
        var encryptDict = new PdfDictionary().Set(_cfKey, new PdfDictionary()
            .Set(new PdfName("Weird"), new PdfDictionary().Set(_cfmKey, new PdfName("SomeFutureAlgorithm"))));

        var table = CryptFilterResolver.BuildCfTable(encryptDict, resolve: null);

        Assert.Equal(CryptFilterMethod.Unsupported, table["Weird"]);
    }

    [Fact]
    public void ResolveNamedMethod_identityName_needsNoCfEntry()
    {
        var empty = new Dictionary<string, CryptFilterMethod>();
        Assert.Equal(CryptFilterMethod.Identity, CryptFilterResolver.ResolveNamedMethod("Identity", empty));
    }

    [Fact]
    public void ResolveNamedMethod_missingName_defaultsToIdentity()
    {
        // The documented default for /StmF and /StrF (ISO 32000-2 Table 20) when absent, and for a
        // /Crypt filter's own /DecodeParms /Name when that key is absent.
        var empty = new Dictionary<string, CryptFilterMethod>();
        Assert.Equal(CryptFilterMethod.Identity, CryptFilterResolver.ResolveNamedMethod(null, empty));
    }

    /// <summary>
    /// A /StmF (or /StrF, or /Crypt /Name) that names a /CF entry the document does NOT define maps
    /// to Unsupported — never silently to Identity, which would hand ciphertext back as plaintext
    /// with no error. This is the "loud failure" case #97's brief calls out by name.
    /// </summary>
    [Fact]
    public void ResolveNamedMethod_nameAbsentFromCfTable_mapsToUnsupported_notIdentity()
    {
        var table = new Dictionary<string, CryptFilterMethod> { ["StdCF"] = CryptFilterMethod.Aes128 };

        var method = CryptFilterResolver.ResolveNamedMethod("NotInTable", table);

        Assert.Equal(CryptFilterMethod.Unsupported, method);
        Assert.NotEqual(CryptFilterMethod.Identity, method);
    }

    [Fact]
    public void ResolveStreamMethod_noExplicitCryptFilter_usesDocumentWideDefault()
    {
        var streamDict = new PdfDictionary().Set(_filterKey, new PdfName("FlateDecode"));
        var table = new Dictionary<string, CryptFilterMethod>();

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes256, table, encryptMetadata: true, resolve: null);

        Assert.Equal(CryptFilterMethod.Aes256, method);
    }

    /// <summary>
    /// An explicit /Crypt filter with /DecodeParms /Name /Identity overrides the document-wide
    /// default and means this ONE stream is not encrypted at all (ISO 32000-2 §7.4.10) — even
    /// though the document-wide default here is AES-256, which would corrupt this stream's already
    /// non-ciphertext bytes if applied.
    /// </summary>
    [Fact]
    public void ResolveStreamMethod_explicitCryptIdentity_overridesDocumentWideDefault()
    {
        var streamDict = new PdfDictionary()
            .Set(_filterKey, _cryptFilterName)
            .Set(_decodeParmsKey, new PdfDictionary().Set(_nameKey, new PdfName("Identity")));
        var table = new Dictionary<string, CryptFilterMethod>();

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes256, table, encryptMetadata: true, resolve: null);

        Assert.Equal(CryptFilterMethod.Identity, method);
    }

    [Fact]
    public void ResolveStreamMethod_explicitCryptNamingRealCfEntry_usesThatEntry()
    {
        var streamDict = new PdfDictionary()
            .Set(_filterKey, new PdfArray([_cryptFilterName, new PdfName("FlateDecode")]))
            .Set(_decodeParmsKey, new PdfArray(
                [new PdfDictionary().Set(_nameKey, _stdCf), PdfNull.Instance]));
        var table = new Dictionary<string, CryptFilterMethod> { ["StdCF"] = CryptFilterMethod.Aes128 };

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Rc4, table, encryptMetadata: true, resolve: null);

        Assert.Equal(CryptFilterMethod.Aes128, method);
    }

    [Fact]
    public void ResolveStreamMethod_explicitCryptNamingUndefinedCfEntry_isUnsupported()
    {
        var streamDict = new PdfDictionary()
            .Set(_filterKey, _cryptFilterName)
            .Set(_decodeParmsKey, new PdfDictionary().Set(_nameKey, new PdfName("Ghost")));
        var table = new Dictionary<string, CryptFilterMethod> { ["StdCF"] = CryptFilterMethod.Aes128 };

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes128, table, encryptMetadata: true, resolve: null);

        Assert.Equal(CryptFilterMethod.Unsupported, method);
    }

    /// <summary>
    /// The whole crypt filter mechanism is a <c>/V</c> 4 feature — Table 20 marks <c>/CF</c>,
    /// <c>/StmF</c>, <c>/StrF</c> and <c>/EFF</c> "meaningful only when the value of V is 4" — so
    /// below that there is no <c>/CF</c> for a <c>/Crypt</c> specifier's <c>/Name</c> to resolve
    /// against and the specifier says nothing. Honouring it there leaves an RC4-encrypted stream
    /// undecrypted, on a document qpdf reads correctly.
    /// </summary>
    [Theory]
    [InlineData(true, "Identity")]   // /V 4: the specifier is read and wins
    [InlineData(false, "Rc4")]       // below it: ignored, and /StmF applies
    public void ResolveStreamMethod_cryptSpecifier_isReadOnlyWhereCryptFiltersAreMeaningful(
        bool cryptFiltersInForce, string expectedName)
    {
        var expected = Enum.Parse<CryptFilterMethod>(expectedName);

        var streamDict = new PdfDictionary()
            .Set(_filterKey, new PdfArray([new PdfName("Crypt")]))
            .Set(new PdfName("DecodeParms"), new PdfDictionary().Set(new PdfName("Name"), new PdfName("Identity")));

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Rc4, new Dictionary<string, CryptFilterMethod>(StringComparer.Ordinal),
            encryptMetadata: true, resolve: null, cryptFiltersInForce: cryptFiltersInForce);

        Assert.Equal(expected, method);
    }

    /// <summary>
    /// A stream's own <c>/Crypt</c> specifier outranks the <c>/EncryptMetadata</c> flag: Table 20's
    /// <c>/StmF</c> row excepts streams carrying one from the document-wide routing, while Table 21,
    /// where the flag lives, only says a reader SHOULD respect it, and §7.6.5's own example writes both — the specifier
    /// being the operative half. A document that says "leave the metadata encrypted" that specifically
    /// means it.
    /// </summary>
    [Fact]
    public void ResolveStreamMethod_cryptSpecifier_outranksTheEncryptMetadataFlag()
    {
        var streamDict = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("Metadata"))
            .Set(_filterKey, new PdfArray([new PdfName("Crypt")]))
            .Set(new PdfName("DecodeParms"), new PdfDictionary().Set(new PdfName("Name"), new PdfName("StdCF")));
        var table = new Dictionary<string, CryptFilterMethod>(StringComparer.Ordinal)
        {
            ["StdCF"] = CryptFilterMethod.Aes128,
        };

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes128, table, encryptMetadata: false, resolve: null,
            isDocumentMetadataStream: true);

        Assert.Equal(CryptFilterMethod.Aes128, method);
    }

    /// <summary>
    /// <c>/EFF</c> applies to embedded file streams and nothing else. Dropping the check makes it the
    /// document-wide filter — which in the arrangement it exists for (<c>/StmF /Identity</c> with
    /// <c>/EFF</c> naming a real filter) would "decrypt" every page's content into noise — and
    /// loosening it to "has a <c>/Type</c>" catches object streams and XObjects.
    /// </summary>
    [Theory]
    [InlineData("EmbeddedFile", "Aes128")]
    [InlineData("ObjStm", "Identity")]
    [InlineData("XObject", "Identity")]
    [InlineData(null, "Identity")]
    public void ResolveStreamMethod_effAppliesToEmbeddedFileStreamsAlone(string? type, string expectedName)
    {
        var expected = Enum.Parse<CryptFilterMethod>(expectedName);

        var streamDict = new PdfDictionary();
        if (type is not null)
            streamDict.Set(new PdfName("Type"), new PdfName(type));

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Identity, new Dictionary<string, CryptFilterMethod>(StringComparer.Ordinal),
            encryptMetadata: true, resolve: null, embeddedFileFilter: CryptFilterMethod.Aes128);

        Assert.Equal(expected, method);
    }

    /// <summary>
    /// <c>/DP</c> is the abbreviated form of <c>/DecodeParms</c> (ISO 32000-1 Table 5). The resolver
    /// accepts it, so something has to say so — otherwise the alias is code nothing depends on and
    /// nothing would notice going away.
    /// </summary>
    [Fact]
    public void ResolveStreamMethod_cryptFilterWithTheAbbreviatedDpKey_isRead()
    {
        var streamDict = new PdfDictionary()
            .Set(_filterKey, new PdfName("Crypt"))
            .Set(new PdfName("DP"), new PdfDictionary().Set(new PdfName("Name"), new PdfName("StdCF")));
        var table = new Dictionary<string, CryptFilterMethod>(StringComparer.Ordinal)
        {
            ["StdCF"] = CryptFilterMethod.Aes128,
        };

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Rc4, table, encryptMetadata: true, resolve: null);

        Assert.Equal(CryptFilterMethod.Aes128, method);
    }

    [Fact]
    public void ResolveStreamMethod_documentMetadataStreamWithEncryptMetadataFalse_isIdentity()
    {
        var streamDict = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("Metadata"))
            .Set(_filterKey, new PdfName("FlateDecode"));
        var table = new Dictionary<string, CryptFilterMethod>();

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes256, table, encryptMetadata: false, resolve: null,
            isDocumentMetadataStream: true);

        Assert.Equal(CryptFilterMethod.Identity, method);
    }

    /// <summary>
    /// ISO 32000-2 Table 21 scopes <c>/EncryptMetadata</c> to the DOCUMENT-level metadata stream —
    /// the object the catalog's <c>/Metadata</c> names. A page's or an XObject's metadata carries the
    /// same <c>/Type</c> and stays encrypted, which is what qpdf's <c>--cleartext-metadata</c>
    /// produces; exempting it hands its ciphertext back as content.
    /// </summary>
    [Fact]
    public void ResolveStreamMethod_componentMetadataStreamWithEncryptMetadataFalse_isStillDecrypted()
    {
        var streamDict = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("Metadata"))
            .Set(_filterKey, new PdfName("FlateDecode"));
        var table = new Dictionary<string, CryptFilterMethod>();

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes256, table, encryptMetadata: false, resolve: null,
            isDocumentMetadataStream: false);

        Assert.Equal(CryptFilterMethod.Aes256, method);
    }

    [Fact]
    public void ResolveStreamMethod_metadataStreamWithEncryptMetadataTrue_usesDocumentWideDefault()
    {
        var streamDict = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("Metadata"))
            .Set(_filterKey, new PdfName("FlateDecode"));
        var table = new Dictionary<string, CryptFilterMethod>();

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes256, table, encryptMetadata: true, resolve: null);

        Assert.Equal(CryptFilterMethod.Aes256, method);
    }

    [Fact]
    public void ResolveStreamMethod_nonMetadataStream_ignoresEncryptMetadataFalse()
    {
        var streamDict = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("XObject"))
            .Set(_filterKey, new PdfName("FlateDecode"));
        var table = new Dictionary<string, CryptFilterMethod>();

        var method = CryptFilterResolver.ResolveStreamMethod(
            streamDict, CryptFilterMethod.Aes256, table, encryptMetadata: false, resolve: null);

        Assert.Equal(CryptFilterMethod.Aes256, method);
    }
}
