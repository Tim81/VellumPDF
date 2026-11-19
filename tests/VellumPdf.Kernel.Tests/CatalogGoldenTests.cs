// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
/// bytes, so each configuration below is pinned twice: by the SHA-256 of the whole file and by the
/// exact text of the catalog object.
/// </summary>
/// <remarks>
/// <para>
/// Reproducibility needs two pins. <see cref="PdfDocument.Timestamp"/> fixes the XMP, the file
/// identifier and the dates; <see cref="SignaturePlaceholderOptions.SigningTime"/> fixes the
/// signature dictionary's <c>/M</c>, which otherwise defaults to the wall clock.
/// </para>
/// <para>
/// The hashes differ by target framework: an empty page content stream is written as
/// <c>/Filter /FlateDecode /Length 0</c> with no data on net10.0 and as the eight bytes of an empty
/// zlib stream on net11.0, which moves every later offset. The catalog text is the same on both.
/// </para>
/// <para>
/// An encrypted file has no hash here: the file key, the <c>/U</c> and <c>/O</c> salts and every
/// initialisation vector come from the system random number generator, so the same document saved
/// twice differs in content and in length. Its catalog object and trailer are stable, and are pinned
/// structurally instead.
/// </para>
/// </remarks>
public sealed class CatalogGoldenTests
{
    private static readonly DateTimeOffset Pinned = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One configuration's expected output: SHA-256 (upper-case hex) and length for net10.0 and
    /// net11.0, and the exact catalog object text (<see langword="null"/> where the catalog sits
    /// inside an object stream and has no plain text).
    /// </summary>
    private sealed record Golden(string Sha10, int Length10, string Sha11, int Length11, string? CatalogText)
    {
#if NET11_0_OR_GREATER
        public string Sha => Sha11;
        public int Length => Length11;
#else
        public string Sha => Sha10;
        public int Length => Length10;
#endif
    }

    // The catalog object text is Latin-1 as written; "þÿ\0e\0n" is the UTF-16BE string for "en".
    private static readonly Dictionary<string, Golden> SaveGoldens = new()
    {
        ["plain"] = new(
            "D3B7403960738BF223352FB2C3B41FA1A56E0334FD185A4E8EC4E387B198C704", 1446,
            "19FD512B9280D8657B9488097D4E698C4FE0F2DCCDD3CFA0C010F5FC3931C416", 1454,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n>>\nendobj"),
        ["outline"] = new(
            "DAD50926A96CB23D7B54FBC5E9CA7E3244412BEE1A2511B6C1650BCB800C9D13", 1675,
            "9F0108C7913E88F75D77EC75E3C5E2A30EE0456ECA176C722E4DEFCC46568833", 1683,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n/Outlines 8 0 R\n/PageMode /UseOutlines\n>>\nendobj"),
        ["tagged"] = new(
            "A6E08830FF3A4D7C22D17E4FB737D70E047188BD38E5950F93925D1598B38151", 1747,
            "BAD58EFC1115E2A3CC142F336E81398CEEC7FF56124DFD83840EF528FCC67D22", 1755,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 9 0 R\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 7 0 R\n>>\nendobj"),
        ["pdfa2u"] = new(
            "EBA9D8AA937522BE0A5A82BA88C520FA3F9E111448547B7A69F00E1C4A459C80", 2721,
            "48709744F4FDC8802FE778D1CF3BA6502B7995ABFB32CB182976F080683BFFD3", 2729,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n/MarkInfo <<\n/Marked true\n>>\n/OutputIntents [8 0 R]\n>>\nendobj"),
        ["pdfua1"] = new(
            "A71646C224F110AA4320E5FA03DA407A8EC5016CA252A306D208E92E4EAD4AF9", 3086,
            "80416D381961886D3E9BD7AA5D3831EF739774AD42E12709C50CF40B14B4917D", 3094,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 9 0 R\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 7 0 R\n/OutputIntents [11 0 R]\n/Lang (\u00FE\u00FF\0e\0n)\n/ViewerPreferences <<\n/DisplayDocTitle true\n>>\n>>\nendobj"),
        ["form"] = new(
            "DD2004ECBF06EB08360ACCBF87D302A8A6FC330C8BA53DFF46BBC1BFE8889410", 2281,
            "95C128C09A4F2352AC8F7F22846816E0270B6F507D7759B3994513EF10CCE8BC", 2289,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 10 0 R\n/AcroForm <<\n/Fields [9 0 R]\n/NeedAppearances false\n/DR <<\n/Font <<\n/Helv 6 0 R\n/ZaDb 7 0 R\n>>\n>>\n/DA (/Helv 0 Tf 0 g)\n>>\n>>\nendobj"),
        ["outline-pdfa2u"] = new(
            "72ED988396C014C7BDC5B3931FC3C6154ACAF58B0C7B597F34E0477C6067B11D", 2955,
            "5BEB191919719A0FF92AE5077AEDA3A29237E0913961C52285630253DC8E3A1B", 2963,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 6 0 R\n/Outlines 10 0 R\n/PageMode /UseOutlines\n/MarkInfo <<\n/Marked true\n>>\n/OutputIntents [8 0 R]\n>>\nendobj"),
        ["linearized"] = new(
            "A8202FC588CA7925DC9B7E574A9A749A9F37A1A47DEC496851C56A866BA3095F", 1885,
            "8F598D3957CD7F33F08EDC5E8A7F92CBC05CE47DE2EE9E3B375A77FD7516FD3E", 1894,
            "2 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 5 0 R\n>>\nendobj"),
        ["objstm"] = new(
            "1CA3A482623F64F33E23F257BD6DF6045DC6365683302023CF1A78CD618304D0", 1398,
            "11CA32D43304DA830DED7786FEC8556C6BDFA39A3480D6ED5FE83C4A81E71BAA", 1406,
            null),
    };

    private static readonly Dictionary<string, Golden> SignGoldens = new()
    {
        ["plain"] = new(
            "03456A2CEC99160624FAD8DF050274A618A9A1D47A3C75181C85B50649D11D7D", 18234,
            "0983C06B5654C424F260B5BD437D605CE9A5238192CD0DD8EC96495BA6923968", 18242,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n>>\nendobj"),
        ["outline"] = new(
            "BED24DE0ACF917BAA4C16C11CC96C3FB5951760D4D04223909F1F3D362C5AA03", 18468,
            "E8760F7739F94E56B1DFB1F9C827B190C2A33B020FB86EFB95C9C224DCEAAF5B", 18476,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/Outlines 10 0 R\n/PageMode /UseOutlines\n>>\nendobj"),
        ["tagged"] = new(
            "32C29B1AC3929192377E3897427DF207B86104F8EB28562B85F47106CEA0EF00", 18539,
            "A12DC4C1D26B3C5A7BF6841549F4B5E19CA1767170A6375937B78738E8EBE8DB", 18547,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 11 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 9 0 R\n>>\nendobj"),
        ["pdfa2u"] = new(
            "790BCD2D0A81ADD9D2A98F956C46A425AAD6D08D46D01B25E86C70E99A5C23D0", 19513,
            "A4B157D7E31573C371A95E4D64B9746C3D42132E1995EEFD64491B9091600828", 19521,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 8 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/MarkInfo <<\n/Marked true\n>>\n/OutputIntents [10 0 R]\n>>\nendobj"),
        ["pdfua1"] = new(
            "40C689E8CE62DE5C55A2CA0BA587D921E56716F03F1777708BAAD67EBB85F37E", 19887,
            "3F735FCDE995ABDBF8C49711AADEDD950C093C187AD675C46A1E56E96948FE03", 19895,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 11 0 R\n/AcroForm <<\n/Fields [7 0 R]\n/SigFlags 3\n>>\n/MarkInfo <<\n/Marked true\n>>\n/StructTreeRoot 9 0 R\n/OutputIntents [13 0 R]\n/Lang (\u00FE\u00FF\0e\0n)\n/ViewerPreferences <<\n/DisplayDocTitle true\n>>\n>>\nendobj"),
        ["form"] = new(
            "1F1266B5C02D5A89C26B033AE40154291BB408B9B815EC8BF26A5836F5397BD1", 19038,
            "F5D7D9B7817BE52C7F70C14A807B0E8E4A87CE00B52429BB219B085BC1F968CC", 19046,
            "5 0 obj\n<<\n/Type /Catalog\n/Pages 3 0 R\n/Metadata 12 0 R\n/AcroForm <<\n/Fields [11 0 R 7 0 R]\n/NeedAppearances false\n/DR <<\n/Font <<\n/Helv 8 0 R\n/ZaDb 9 0 R\n>>\n>>\n/DA (/Helv 0 Tf 0 g)\n/SigFlags 3\n>>\n>>\nendobj"),
        ["outline-pdfa2u"] = new(
            "417267EA893A6B94F2A79DD3A2320C6BE90D1AE773A469B65102C9154D7C4516", 19747,
            "E479713CA40692E646BB4114830DDEA1A5ABA87829E5EE39F073C71E7A10888E", 19755,
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
            case "outline-pdfa2u":
                // The one configuration in which the two paths number the output-intent object differently.
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

    private static string CatalogObject(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var matches = Regex.Matches(text, @"\d+ 0 obj\n<<\n/Type /Catalog\n.*?\nendobj", RegexOptions.Singleline);
        Assert.Single(matches);
        return matches[0].Value;
    }

    private static void AssertMatches(string label, Golden golden, byte[] first, byte[] second)
    {
        var shaFirst = Sha256(first);
        var shaSecond = Sha256(second);
        Assert.True(shaFirst == shaSecond, $"{label}: two builds in one process differ ({shaFirst} vs {shaSecond}); a golden cannot be pinned.");
        Assert.True(golden.Sha == shaFirst, $"{label}: expected sha256 {golden.Sha}, computed {shaFirst}.");
        Assert.True(golden.Length == first.Length, $"{label}: expected {golden.Length} bytes, computed {first.Length}.");
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
        // The signing path writes one fixed layout; the flag is accepted and has no effect (a shared catalog
        // builder must not make the signing catalog vary with it, #606).
        var plain = Sign("plain");
        var linearized = Sign("plain", d => d.Linearize = true);
        Assert.True(Sha256(plain) == Sha256(linearized), "Linearize changed the signing path's bytes.");
        Assert.True(SignGoldens["plain"].Sha == Sha256(linearized), $"computed {Sha256(linearized)}");
    }

    [Fact]
    public void Save_withObjectStreams_keepsTheCatalogKeysInOrder()
    {
        // The catalog is compressed into an object stream, so there is no plain text to compare; read
        // it back and assert key order and every value.
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

    [Fact]
    public void Save_withEncryption_keepsTheCatalogObjectAndTrailer()
    {
        // No hash: see the type remarks. The catalog holds no string, so its text is not encrypted and
        // is stable; so are the object count and the /Encrypt reference.
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

    // Asserts on qpdf's own verdict line rather than the exit code: --check exits 0 for files it
    // merely warns about.
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
