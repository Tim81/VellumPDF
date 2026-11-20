// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Annotations;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Reader;
using VellumPdf.TestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Pins the bytes of the document catalog as <see cref="PdfDocument.Save(Stream)"/> and
/// <see cref="PdfDocument.PrepareForSigning"/> write it (#606). The two paths build the catalog in
/// separate blocks of <c>PdfDocument</c> and they do not agree. <c>Save</c> writes <c>/AcroForm</c>
/// only for a document with form fields, and after <c>/StructTreeRoot</c>; the signing path always
/// writes one, because it holds the signature field, and puts it straight after <c>/Metadata</c>.
/// <c>Save</c> also reserves the output-intent objects before the outline tree and the signing path
/// reserves them after it, so a document with both an outline and a PDF/A claim numbers them
/// differently on each path. A refactor that merges the blocks has to keep every one of those
/// bytes, so each classic-xref configuration below is pinned twice: by the SHA-256 of the whole
/// file, normalised as described below, and by the exact text of the catalog object.
/// </summary>
/// <remarks>
/// <para>
/// Reproducibility needs two pins. <see cref="PdfDocument.Timestamp"/> fixes the XMP, the file
/// identifier and the dates; <see cref="SignaturePlaceholderOptions.SigningTime"/> fixes the
/// signature dictionary's <c>/M</c>, which otherwise defaults to the wall clock.
/// </para>
/// <para>
/// The hash is taken over a normalised file, so it does not move with the runtime's Deflate
/// output, which the library does not control. The two target frameworks already differ: an empty
/// page content stream is written as <c>/Filter /FlateDecode /Length 0</c> with no data on net10.0
/// and as the eight bytes of an empty zlib stream on net11.0, which moves every later offset.
/// <see cref="Normalise"/> inflates every <c>/FlateDecode</c> stream, sets its
/// <c>/Length</c> to the inflated length, and replaces what depends on a byte offset or on a
/// compressed size: the cross-reference entries, <c>startxref</c>, the trailer or
/// cross-reference stream <c>/Prev</c> offset (not an outline item's <c>/Prev N 0 R</c>), the
/// linearization dictionary's <c>/L</c>, <c>/H</c>, <c>/E</c> and <c>/T</c> (its <c>/O</c> and
/// <c>/N</c> stay pinned), and the contents of a hint stream or a cross-reference stream.
/// Everything else is hashed as written, so object order, object numbers, every dictionary key
/// and every stream's decoded content stay pinned; what the normalisation drops,
/// <c>qpdf --check</c> checks in the same file. One hash therefore serves both target frameworks.
/// </para>
/// <para>
/// An encrypted file has no hash here: the file key, the <c>/U</c> and <c>/O</c> salts and every
/// initialisation vector come from the system random number generator, so the same document saved
/// twice differs in content and can differ in length. Its catalog object and trailer are stable,
/// and are pinned structurally instead.
/// </para>
/// </remarks>
public sealed class CatalogGoldenTests
{
    private static readonly DateTimeOffset Pinned = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One configuration's expected output: the SHA-256 (upper-case hex) of the normalised file,
    /// and the exact catalog object text (<see langword="null"/> where the catalog sits inside an
    /// object stream and has no plain text).
    /// </summary>
    private sealed record Golden(string Sha, string? CatalogText);

    // The catalog object text is Latin-1 as written; "þÿ\0e\0n" is the UTF-16BE string for "en".
    private static readonly Dictionary<string, Golden> SaveGoldens = new()
    {
        ["plain"] = new(
            "27A0AE4F14A455A5A5B6F2FB5FE8CB5AE10529207D4E07EFE69ADE8D7C719B61",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n>>\nendobj"),
        ["outline"] = new(
            "852224F5B9DBE009F1DDAAA09E12202CA7C174F48EB728D296101E0480F3AAD3",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n/Outlines 8 0 R\n/PageMode /UseOutlines\n>>\nendobj"),
        ["tagged"] = new(
            "F48BAA375C14444797EBED06B7F6F9482C602FEFA4E662E3B7A1186E675292BA",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 9 0 R\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 7 0 R\n>>\nendobj"),
        ["pdfa2u"] = new(
            "4A7AB67B78496976DFDB8829826141AAA96CFB85E02335E3F3E894011494716E",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n/MarkInfo <<\n/Marked true\n>>\n/OutputIntents [8 0 R]\n>>\nendobj"),
        ["pdfua1"] = new(
            "8C75752DF6D452D77562D33D26FFA8CAE5E930E01B5AFF49E7C91AE1EFA26971",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 9 0 R\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 7 0 R\n/OutputIntents [11 0 R]\n/Lang (\u00FE\u00FF\0e\0n)\n/ViewerPreferences <<\n/DisplayDocTitle true\n>>\n>>\nendobj"),
        ["form"] = new(
            "DDE804CBD90758592C4995A53D28BC18E1641CC2BC37D9D6767BB97EE0D095FF",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 10 0 R\n/AcroForm <<\n/Fields [9 0 R]\n/NeedAppearances false\n/DR <<\n/Font <<\n/Helv 6 0 R\n/ZaDb 7 0 R\n>>\n>>\n/DA (/Helv 0 Tf 0 g)\n>>\n>>\nendobj"),
        ["lang"] = new(
            "B913B1B4ACE57FD9FE1EB6B506D1016363CA5F765D88EB7091FFC1B3616B423C",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n/Lang (\u00FE\u00FF\0e\0n)\n>>\nendobj"),
        ["everything"] = new(
            "E43F27B235F2C1FA4F405FA2C7FFE65B5726BE856DC0111C97C6AE15721176A4",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 13 0 R\n/Outlines 17 0 R\n/PageMode /UseOutlines\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 7 0 R\n/AcroForm <<\n/Fields [12 0 R]\n/NeedAppearances false\n/DR <<\n/Font <<\n/Helv 9 0 R\n/ZaDb 10 0 R\n>>\n>>\n/DA (/Helv 0 Tf 0 g)\n>>\n/OutputIntents [15 0 R]\n/Lang (\u00FE\u00FF\0e\0n)\n/ViewerPreferences <<\n/DisplayDocTitle true\n>>\n>>\nendobj"),
        ["outline-pdfa2u"] = new(
            "21238A6A96E54C96E36C1EC884BF056CCFE8DF51C9ACA8112C4E27286A02E446",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n/Outlines 10 0 R\n/PageMode /UseOutlines\n/MarkInfo <<\n/Marked true\n>>\n/OutputIntents [8 0 R]\n>>\nendobj"),
        ["linearized"] = new(
            "67BE09CA85A64320B725122DC9492CD17D6D55312526ACAEA9A9FF1E98F75C36",
            "2 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 5 0 R\n>>\nendobj"),
        ["objstm"] = new(
            "980C7AF6BEC4C6235C3E7928DC0AE339AD34EA08E0D148B1E5AB14628AED4EA8",
            null),
    };

    private static readonly Dictionary<string, Golden> SignGoldens = new()
    {
        ["plain"] = new(
            "67CDCDA5E54A4C4AB61656643F4EC41EF9107301F37A9E71C2CCF73DEC0EF760",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n>>\nendobj"),
        ["outline"] = new(
            "6C2607A7727298E53A03B1297861E0DB2FE3B2950CC0491C91B770B1591D1EB0",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/Outlines 10 0 R\n/PageMode /UseOutlines\n>>\nendobj"),
        ["tagged"] = new(
            "BD45B32C5D8CBD6CB10F3522A970046C94CA65FD2EA75116457AEB2CC03D047E",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 11 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 9 0 R\n>>\nendobj"),
        ["pdfa2u"] = new(
            "BC338C7A3A18909CBEC63B4E2C9817A353EE0EA446678BDE47046F7B33494229",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/MarkInfo <<\n/Marked true\n>>\n/OutputIntents [10 0 R]\n>>\nendobj"),
        ["pdfua1"] = new(
            "045DC5F418BCB21D9313EDA5EA6C49F594410CC902B45775BA4B0CCFF9803AF9",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 11 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 9 0 R\n/OutputIntents [13 0 R]\n/Lang (\u00FE\u00FF\0e\0n)\n/ViewerPreferences <<\n/DisplayDocTitle true\n>>\n>>\nendobj"),
        ["form"] = new(
            "15F36522C07DA2599C47F8C53AFE5782330090157A963486E216549E5E08D7CF",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 12 0 R\n/AcroForm <<\n/Fields [11 0 R 7 0 R]\n/NeedAppearances false\n/DR <<\n/Font <<\n/Helv 8 0 R\n/ZaDb 9 0 R\n>>\n>>\n/DA (/Helv 0 Tf 0 g)\n/SigFlags 3\n>>\n>>\nendobj"),
        ["lang"] = new(
            "FC84865289F24A234E1BCAC6C2DF972BBE442992CA26D620AE859DE03EA2A532",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/Lang (\u00FE\u00FF\0e\0n)\n>>\nendobj"),
        ["everything"] = new(
            "47E5E48C1842E9B346C3E504377723FD1E6EBFA0D651B04A5E1369BEE25DEB0C",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 15 0 R\n/AcroForm <<\n/Fields [14 0 R 7 0 R]\n/NeedAppearances false\n/DR <<\n/Font <<\n/Helv 11 0 R\n/ZaDb 12 0 R\n>>\n>>\n/DA (/Helv 0 Tf 0 g)\n/SigFlags 3\n>>\n/Outlines 17 0 R\n/PageMode /UseOutlines\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 9 0 R\n/OutputIntents [19 0 R]\n/Lang (\u00FE\u00FF\0e\0n)\n/ViewerPreferences <<\n/DisplayDocTitle true\n>>\n>>\nendobj"),
        ["outline-pdfa2u"] = new(
            "D78353650BDC53DBE97D5721B0363C0AC63638F29D34069AF9EB91D18C03BB86",
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/Outlines 10 0 R\n/PageMode /UseOutlines\n/MarkInfo <<\n/Marked true\n>>\n/OutputIntents [12 0 R]\n>>\nendobj"),
    };

    public static TheoryData<string> SaveConfigurations => [.. SaveGoldens.Keys];

    public static TheoryData<string> SaveConfigurationsWithPlainCatalog => [.. SaveGoldens.Where(kv => kv.Value.CatalogText is not null).Select(kv => kv.Key)];

    public static TheoryData<string> SignConfigurations => [.. SignGoldens.Keys];

    private static PdfDocument Build(string configuration)
    {
        var document = new PdfDocument { Timestamp = Pinned };
        document.Info.Title = "T";
        var page = document.AddPage();
        switch (configuration)
        {
            case "plain":
                break;
            case "outline":
                document.AddOutlineEntry(new PdfOutlineEntry { Title = "A", DestPage = page, DestTop = 700 });
                break;
            case "tagged":
                document.Tagged = true;
                break;
            case "pdfa2u":
                document.Conformance = PdfConformance.PdfA2u;
                break;
            case "pdfua1":
                document.Conformance = PdfConformance.PdfUA1;
                document.Tagged = true;
                document.Language = "en";
                break;
            case "form":
                document.AddTextField(page, "f1", new PdfRectangle(10, 10, 100, 30));
                break;
            case "lang":
                // /Lang with no conformance level: written whenever Language is set.
                document.Language = "en";
                break;
            case "everything":
                // Every optional catalog key in one dictionary, so the order of any two shows.
                document.AddOutlineEntry(new PdfOutlineEntry { Title = "A", DestPage = page, DestTop = 700 });
                document.AddTextField(page, "f1", new PdfRectangle(10, 10, 100, 30));
                document.Conformance = PdfConformance.PdfUA1;
                document.Tagged = true;
                document.Language = "en";
                break;
            case "outline-pdfa2u":
                // The one configuration in which the two paths number the output-intent object
                // differently.
                document.AddOutlineEntry(new PdfOutlineEntry { Title = "A", DestPage = page, DestTop = 700 });
                document.Conformance = PdfConformance.PdfA2u;
                break;
            case "linearized":
                document.Linearize = true;
                break;
            case "objstm":
                document.UseObjectStreams = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration), configuration, null);
        }
        return document;
    }

    private static byte[] Save(string configuration)
    {
        using var ms = new MemoryStream();
        Build(configuration).Save(ms);
        return ms.ToArray();
    }

    private static byte[] Sign(string configuration, Action<PdfDocument>? tweak = null)
    {
        var document = Build(configuration);
        tweak?.Invoke(document);
        return document.PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = Pinned });
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // An object whose dictionary is followed by a stream. The dictionary may not cross an endobj
    // or a stream keyword, so it is always the one dictionary of one object.
    private static readonly Regex StreamObject = new(
        @"(?<head>\d+ 0 obj\n)(?<dict><<(?:(?!\nstream\n|endobj).)*?>>)(?<mid>\nstream\n)(?<data>.*?)(?<tail>\nendstream)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The file with everything that depends on the runtime's compressor removed, as the type
    /// remarks describe, as Latin-1 text (one char per byte).
    /// </summary>
    private static string Normalise(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        text = StreamObject.Replace(text, m =>
        {
            var dictionary = m.Groups["dict"].Value;
            if (!dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
                return m.Value;
            string inflated;
            if (Regex.IsMatch(dictionary, @"/S \d+"))
                inflated = "<hint stream>";
            else if (dictionary.Contains("/Type /XRef", StringComparison.Ordinal))
                inflated = "<cross-reference stream>";
            else
                inflated = Inflate(Encoding.Latin1.GetBytes(m.Groups["data"].Value));
            dictionary = Regex.Replace(dictionary, @"/Length \d+", "/Length " + inflated.Length);
            dictionary = dictionary.Replace("/Filter /FlateDecode", "/Filter /FlateDecode (inflated)", StringComparison.Ordinal);
            return m.Groups["head"].Value + dictionary + m.Groups["mid"].Value + inflated + m.Groups["tail"].Value;
        });
        text = Regex.Replace(text, @"\d{10} (\d{5}) ([nf])", "# $1 $2");
        text = Regex.Replace(text, @"startxref\n\d+", "startxref\n#");
        text = Regex.Replace(text, @"/Prev \d+(?!\d| 0 R)", "/Prev #");
        text = Regex.Replace(text, @"/L \d+ /H \[\d+ \d+\] /O (\d+) /E \d+ /N (\d+) /T \d+", "/L # /H [# #] /O $1 /E # /N $2 /T #");
        return text;
    }

    // An empty stream has no zlib header on net10.0, so there is nothing to inflate.
    private static string Inflate(byte[] data)
    {
        if (data.Length == 0)
            return string.Empty;
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    private static string NormalisedSha256(byte[] pdf) => Sha256(Encoding.Latin1.GetBytes(Normalise(pdf)));

    private static string CatalogObject(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var matches = Regex.Matches(text, @"\d+ 0 obj\n<<\n/Type /Catalog\n.*?\nendobj", RegexOptions.Singleline);
        Assert.Single(matches);
        return matches[0].Value;
    }

    private static void AssertMatches(string label, Golden golden, byte[] first, byte[] second)
    {
        var shaFirst = NormalisedSha256(first);
        var shaSecond = NormalisedSha256(second);
        Assert.True(shaFirst == shaSecond, $"{label}: two builds in one process differ after normalising ({shaFirst} vs {shaSecond}); a golden cannot be pinned.");
        Assert.True(golden.Sha == shaFirst, $"{label}: expected normalised sha256 {golden.Sha}, computed {shaFirst}.");
    }

    [Theory]
    [MemberData(nameof(SaveConfigurations))]
    public void Save_matchesTheGoldenBytes(string configuration)
        => AssertMatches("Save " + configuration, SaveGoldens[configuration], Save(configuration), Save(configuration));

    [Theory]
    [MemberData(nameof(SignConfigurations))]
    public void PrepareForSigning_matchesTheGoldenBytes(string configuration)
        => AssertMatches("PrepareForSigning " + configuration, SignGoldens[configuration], Sign(configuration), Sign(configuration));

    [Theory]
    [MemberData(nameof(SaveConfigurationsWithPlainCatalog))]
    public void Save_catalogObjectIsWrittenInTheExpectedKeyOrder(string configuration)
        => Assert.Equal(SaveGoldens[configuration].CatalogText, CatalogObject(Save(configuration)));

    [Theory]
    [MemberData(nameof(SignConfigurations))]
    public void PrepareForSigning_catalogObjectIsWrittenInTheExpectedKeyOrder(string configuration)
        => Assert.Equal(SignGoldens[configuration].CatalogText, CatalogObject(Sign(configuration)));

    [Fact]
    public void PrepareForSigning_ignoresLinearize()
    {
        // The signing path writes one fixed layout; the flag is accepted and has no effect (a
        // shared catalog builder must not make the signing catalog vary with it, #606).
        var plain = Sign("plain");
        var linearized = Sign("plain", d => d.Linearize = true);
        Assert.True(Sha256(plain) == Sha256(linearized), "Linearize changed the signing path's bytes.");
        Assert.True(SignGoldens["plain"].Sha == NormalisedSha256(linearized), $"computed {NormalisedSha256(linearized)}");
    }

    [Fact]
    public void Save_withObjectStreams_keepsTheCatalogKeysInOrder()
    {
        // The catalog is compressed into an object stream, so there is no plain text to compare;
        // read it back and assert key order and every value.
        var pdf = Save("objstm");
        Assert.DoesNotContain("/Type /Catalog", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        var catalog = PdfReader.Open(pdf).Catalog;
        Assert.Equal(["Type", "Pages", "Metadata"], catalog.Entries.Select(e => e.Key.Value).ToArray());
        Assert.Equal(new PdfName("Catalog"), catalog.Get(PdfName.Type));
        var pages = Assert.IsType<PdfIndirectReference>(catalog.Get(PdfName.Pages));
        var metadata = Assert.IsType<PdfIndirectReference>(catalog.Get(new PdfName("Metadata")));
        Assert.Equal(3, pages.ObjectNumber);
        Assert.Equal(6, metadata.ObjectNumber);
    }

    // The catalog builder's seam (#606): one entry, appended last, on both paths.
    private static PdfDictionary ExtraEntry() => new PdfDictionary().Set(new PdfName("Extra"), new PdfInteger(7));

    private static string WithoutExtra(string catalogText)
        => catalogText.Replace("/Extra 7\n", string.Empty, StringComparison.Ordinal);

    [Theory]
    [InlineData("plain")]
    [InlineData("everything")]
    [InlineData("outline-pdfa2u")]
    public void Save_catalogBuilderSeam_appendsTheEntryLast(string configuration)
    {
        var document = Build(configuration);
        document.CatalogExtraEntries = ExtraEntry();
        using var ms = new MemoryStream();
        document.Save(ms);
        var text = CatalogObject(ms.ToArray());
        Assert.EndsWith("/Extra 7\n>>\nendobj", text, StringComparison.Ordinal);
        Assert.Equal(SaveGoldens[configuration].CatalogText, WithoutExtra(text));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("everything")]
    [InlineData("outline-pdfa2u")]
    public void PrepareForSigning_catalogBuilderSeam_appendsTheEntryLast(string configuration)
    {
        var text = CatalogObject(Sign(configuration, d => d.CatalogExtraEntries = ExtraEntry()));
        Assert.EndsWith("/Extra 7\n>>\nendobj", text, StringComparison.Ordinal);
        Assert.Equal(SignGoldens[configuration].CatalogText, WithoutExtra(text));
    }

    [Fact]
    public void Save_catalogBuilderSeam_reachesACatalogInAnObjectStream()
    {
        var document = Build("objstm");
        document.CatalogExtraEntries = ExtraEntry();
        using var ms = new MemoryStream();
        document.Save(ms);
        var catalog = PdfReader.Open(ms.ToArray()).Catalog;
        Assert.Equal(["Type", "Pages", "Metadata", "Extra"], catalog.Entries.Select(e => e.Key.Value).ToArray());
        Assert.Equal(7, Assert.IsType<PdfInteger>(catalog.Get(new PdfName("Extra"))).Value);
    }

    [Fact]
    public void Save_afterAPreconditionRefusal_writesOnceAndMatchesTheGolden()
    {
        // A null stream is refused before the document is marked written, so the retry saves.
        var document = Build("outline-pdfa2u");
        Assert.Throws<ArgumentNullException>(() => document.Save(null!));
        using var ms = new MemoryStream();
        document.Save(ms);
        Assert.Equal(SaveGoldens["outline-pdfa2u"].Sha, NormalisedSha256(ms.ToArray()));
    }

    [Fact]
    public void PrepareForSigning_afterAPreconditionRefusal_writesOnceAndMatchesTheGolden()
    {
        var document = Build("outline-pdfa2u");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => document.PrepareForSigning(new SignaturePlaceholderOptions { SignaturePage = -1, SigningTime = Pinned }));
        var pdf = document.PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = Pinned });
        Assert.Equal(SignGoldens["outline-pdfa2u"].Sha, NormalisedSha256(pdf));
    }

    [Fact]
    public void Save_withEncryption_keepsTheCatalogObjectAndTrailer()
    {
        // No hash: see the type remarks. The catalog holds no string, so its text is not
        // encrypted and is stable; so are the object count and the /Encrypt reference.
        var document = Build("plain");
        document.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });
        using var ms = new MemoryStream();
        document.Save(ms);
        var pdf = ms.ToArray();

        Assert.Equal("5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n>>\nendobj", CatalogObject(pdf));
        var text = Encoding.Latin1.GetString(pdf);
        var trailer = text[text.LastIndexOf("trailer\n", StringComparison.Ordinal)..];
        Assert.Contains("/Size 8\n/Root 5 0 R\n/Info 4 0 R\n", trailer, StringComparison.Ordinal);
        Assert.Contains("/Encrypt 7 0 R\n", trailer, StringComparison.Ordinal);

        var catalog = PdfReader.Open(pdf, new PdfReaderOptions { Password = "u" }).Catalog;
        Assert.Equal(["Type", "Pages", "Metadata"], catalog.Entries.Select(e => e.Key.Value).ToArray());
        Assert.Equal(3, Assert.IsType<PdfIndirectReference>(catalog.Get(PdfName.Pages)).ObjectNumber);
        Assert.Equal(6, Assert.IsType<PdfIndirectReference>(catalog.Get(new PdfName("Metadata"))).ObjectNumber);
    }

    [Theory]
    [MemberData(nameof(SaveConfigurations))]
    public void Save_passesQpdfCheck(string configuration) => AssertQpdfCheckClean(Save(configuration), password: null);

    [Theory]
    [MemberData(nameof(SignConfigurations))]
    public void PrepareForSigning_passesQpdfCheck(string configuration) => AssertQpdfCheckClean(Sign(configuration), password: null);

    [Fact]
    public void Save_withEncryption_passesQpdfCheck()
    {
        var document = Build("plain");
        document.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });
        using var ms = new MemoryStream();
        document.Save(ms);
        AssertQpdfCheckClean(ms.ToArray(), password: "u");
    }

    // Asserts the exit code (qpdf exits 2 for an error and 3 for a warning) and qpdf's own
    // verdict line, so a change to either shows here.
    private static void AssertQpdfCheckClean(byte[] pdf, string? password)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vellum_catalog_golden_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "catalog.pdf");
            File.WriteAllBytes(path, pdf);
            List<string> args = password is null ? ["--check", path] : ["--password=" + password, "--check", path];
            ExternalTool.TryRun("qpdf", args, out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
            Assert.Contains("No syntax or stream encoding errors found", stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
