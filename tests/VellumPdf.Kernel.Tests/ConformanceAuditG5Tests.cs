// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.IO;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Signing;
using VellumPdf.TestSupport;
using LayoutDocument = VellumPdf.Layout.Document;
using Scan = VellumPdf.Kernel.Tests.AuditScanner;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// The ISO audit of file structure and metadata (group G5), one test per clause. The clause
/// inventory, the feature matrix and the verdicts are in <c>docs/conformance-v2.4.md</c>. Every
/// fixture is generated here with the fonts in the tree, and each test first checks that the
/// construct is in the bytes (read by <see cref="AuditScanner"/>, which does not use the library's
/// Reader) before it asserts values against the clause. A requirement the code violates has a test
/// that is skipped, with the gap's label in the reason, so the assertion is ready for the fix.
/// </summary>
/// <remarks>
/// ISO 19005-2 is not held in the repository: its clause numbers come from the maintainer's NEN
/// Connect reading, and the test names use them as written there. The other standards are the
/// copies in <c>docs/iso</c>.
/// </remarks>
public sealed class ConformanceAuditG5Tests
{
    public enum FixturePath
    {
        Layout,
        Kernel,
        ObjectStreams,
        Linearized,
        Prepared,
        Signed,
    }

    private static readonly DateTimeOffset Pinned = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    private static readonly PdfConformance[] Claims =
        [PdfConformance.None, PdfConformance.PdfA2b, PdfConformance.PdfA2u, PdfConformance.PdfA2a, PdfConformance.PdfUA1];

    private static readonly PdfConformance[] PdfAClaims =
        [PdfConformance.PdfA2b, PdfConformance.PdfA2u, PdfConformance.PdfA2a];

    public static TheoryData<PdfConformance, FixturePath> AllFixtures()
    {
        var data = new TheoryData<PdfConformance, FixturePath>();
        foreach (var c in Claims)
            foreach (var p in Enum.GetValues<FixturePath>())
                data.Add(c, p);
        return data;
    }

    // Prepared is the unsigned placeholder file: its /ByteRange holds 9999999999 until the signature
    // patches it in, so it is not a file anybody is handed and no validator is run on it.
    public static TheoryData<PdfConformance, FixturePath> DeliveredFixtures()
    {
        var data = new TheoryData<PdfConformance, FixturePath>();
        foreach (var c in Claims)
            foreach (var p in Enum.GetValues<FixturePath>().Where(p => p != FixturePath.Prepared))
                data.Add(c, p);
        return data;
    }

    public static TheoryData<PdfConformance, FixturePath> OracleFixtures()
    {
        var data = new TheoryData<PdfConformance, FixturePath>();
        foreach (var c in Claims.Where(c => c != PdfConformance.None))
            foreach (var p in Enum.GetValues<FixturePath>().Where(p => p != FixturePath.Prepared))
                data.Add(c, p);
        return data;
    }

    public static TheoryData<PdfConformance> PdfAClaimData()
    {
        var data = new TheoryData<PdfConformance>();
        foreach (var c in PdfAClaims)
            data.Add(c);
        return data;
    }

    public static TheoryData<PdfConformance> ClaimData()
    {
        var data = new TheoryData<PdfConformance>();
        foreach (var c in Claims)
            data.Add(c);
        return data;
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static byte[] DejaVu => EmbeddedFontFixtures.Load("DejaVuSans.ttf");

    private static readonly Lazy<X509Certificate2> Certificate = new(() =>
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=VellumPdf audit", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // The validity window follows the pinned instant, so no assertion depends on the wall clock.
        return request.CreateSelfSigned(Pinned.AddDays(-1), Pinned.AddYears(1));
    });

    private static readonly ConcurrentDictionary<(PdfConformance, FixturePath), byte[]> Cache = new();
    private static readonly ConcurrentDictionary<(PdfConformance, FixturePath), Scan.Parsed> ScanCache = new();

    private static byte[] Fixture(PdfConformance claim, FixturePath path) =>
        Cache.GetOrAdd((claim, path), key => Build(key.Item1, key.Item2));

    private static Scan.Parsed Parsed(PdfConformance claim, FixturePath path) =>
        ScanCache.GetOrAdd((claim, path), key => Scan.Parse(Fixture(key.Item1, key.Item2)));

    private static LayoutDocument NewLayout(PdfConformance claim)
    {
        var doc = new LayoutDocument
        {
            Conformance = claim,
            Timestamp = Pinned,
            DocumentId = PinnedId,
            Language = "en-US",
        };
        doc.Info.Title = "G5 audit";
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVu), FontSize = 11 };
        doc.Add(new Heading("Heading", style) { Level = 0 });
        doc.Add(new Paragraph("Hello world", style));
        return doc;
    }

    // The text is inside an artifact, so a PDF/UA-1 claim has no untagged real content, and the
    // fixture asks nothing of the structure tree: G5 audits the file around the content.
    private static PdfDocument NewKernel(PdfConformance claim, bool objectStreams = false, bool linearize = false)
    {
        var doc = new PdfDocument
        {
            Conformance = claim,
            Timestamp = Pinned,
            DocumentId = PinnedId,
            Language = "en-US",
            UseObjectStreams = objectStreams,
            Linearize = linearize,
        };
        doc.Info.Title = "G5 audit";
        DrawHello(doc);

        // A bookmark, a named destination and a page label are the catalog entries the shared builder
        // (#635, #626) writes for Save and for the signing path, so every fixture carries them.
        var page = doc.Pages[0];
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Start", DestPage = page, DestTop = 700 });
        doc.AddNamedDestination("audit-dest", page, 0, 700);
        doc.AddPageLabelRange(0, PdfPageLabelStyle.Decimal);
        return doc;
    }

    private static void DrawHello(PdfDocument doc)
    {
        var handle = doc.UseTrueTypeFont(DejaVu);
        var page = doc.AddPage();
        doc.RegisterEmbeddedFontUsage(page, handle);
        var glyphs = new ushort[5];
        var count = handle.GetGlyphIds("Hello", glyphs);
        new PdfCanvas(page)
            .BeginArtifactMarkedContent()
            .BeginText()
            .SetFontByName(handle.ResourceName, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowGlyphs(glyphs.AsSpan(0, count))
            .EndText()
            .EndMarkedContent()
            .Finish();
    }

    private static byte[] Save(PdfDocument doc)
    {
        using (doc)
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }
    }

    private static byte[] Save(LayoutDocument doc)
    {
        using (doc)
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }
    }

    private static byte[] Build(PdfConformance claim, FixturePath path)
    {
        switch (path)
        {
            case FixturePath.Layout:
                return Save(NewLayout(claim));
            case FixturePath.Kernel:
                return Save(NewKernel(claim));
            case FixturePath.ObjectStreams:
                return Save(NewKernel(claim, objectStreams: true));
            case FixturePath.Linearized:
                return Save(NewKernel(claim, linearize: true));
            case FixturePath.Prepared:
                using (var doc = NewKernel(claim))
                    return doc.PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = Pinned });
            default:
                using (var doc = NewKernel(claim))
                {
                    using var ms = new MemoryStream();
                    doc.Sign(ms, new PdfSignatureSettings { Certificate = Certificate.Value, SigningTime = Pinned });
                    return ms.ToArray();
                }
        }
    }

    private static string Flavour(PdfConformance claim) => claim switch
    {
        PdfConformance.PdfA2b => "2b",
        PdfConformance.PdfA2u => "2u",
        PdfConformance.PdfA2a => "2a",
        PdfConformance.PdfUA1 => "ua1",
        _ => throw new ArgumentOutOfRangeException(nameof(claim), claim, "No veraPDF flavour for this claim."),
    };

    // ── oracles ───────────────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vellum-audit-g5-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    /// <summary>veraPDF's verdict as the failed rules, read from its XML report and not from the exit code.</summary>
    private static (bool Compliant, List<string> FailedRules) VeraPdf(string flavour, byte[] pdf)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        ExternalTool.TryRun("verapdf", ["--flavour", flavour, path],
            out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);
        Assert.False(timedOut, "veraPDF timed out.");
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
        Assert.Contains("<validationReport", report);
        var failed = new List<string>();
        foreach (Match m in Regex.Matches(report, "<rule\\b[^>]*>"))
        {
            if (!m.Value.Contains("status=\"failed\"", StringComparison.Ordinal))
                continue;
            var clause = Regex.Match(m.Value, "clause=\"([^\"]*)\"").Groups[1].Value;
            var test = Regex.Match(m.Value, "testNumber=\"([^\"]*)\"").Groups[1].Value;
            failed.Add($"{clause}-{test}");
        }
        failed.Sort(StringComparer.Ordinal);
        return (report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), failed);
    }

    private static string QpdfCheck(byte[] pdf)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut, "qpdf timed out.");
        Assert.True(exit == 0, $"qpdf --check exited {exit}.\n{stdout}\n{stderr}");
        return stdout;
    }

    // ── small readers over the scanned file ──────────────────────────────────

    private static string Decode(Scan.PStr s)
    {
        var b = s.Bytes;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        return Encoding.Latin1.GetString(b);
    }

    private static IEnumerable<Scan.PDict> AllDictionaries(Scan.Parsed p)
    {
        var stack = new Stack<object?>();
        foreach (var o in p.Objects)
            stack.Push(o.Value);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case Scan.PDict d:
                    yield return d;
                    foreach (var (_, v) in d.Entries)
                        stack.Push(v);
                    break;
                case List<object?> l:
                    foreach (var v in l)
                        stack.Push(v);
                    break;
            }
        }
    }

    private static XElement Description(byte[] xmp)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, CheckCharacters = true, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(xmp), settings);
        var doc = XDocument.Load(reader);
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        return doc.Descendants(rdf + "Description").Single();
    }

    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace PdfNs = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace PdfaId = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly XNamespace PdfuaId = "http://www.aiim.org/pdfua/ns/id/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    private static string Packet(Scan.Parsed p) => Encoding.UTF8.GetString(p.MetadataXml);

    // ── ISO 19005-2, 6.1.2; ISO 32000-1, 7.5.2; ISO 32000-2, 7.5.2; ISO 14289-1, 6.1 ───────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes(PdfConformance claim, FixturePath path)
    {
        var bytes = Fixture(claim, path);

        // The construct: a header line, then a comment line, at the start of the file.
        Assert.True(bytes.Length > 16);
        Assert.Equal((byte)'%', bytes[0]);

        // 7.5.2 of ISO 32000-1 and ISO 19005-2, 6.1.2: "%PDF-1.n", n from 0 to 7, one EOL, then a
        // comment of four bytes above 127. A file under no claim names 2.0 (ISO 32000-2, 7.5.2).
        var expected = claim == PdfConformance.None ? "%PDF-2.0\n" : "%PDF-1.7\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(bytes, 0, 9));
        Assert.Equal((byte)'%', bytes[9]);
        for (var i = 10; i < 14; i++)
            Assert.True(bytes[i] > 127, $"byte {i} of the file is {bytes[i]}");
        Assert.Equal((byte)'\n', bytes[14]);

        // ISO 14289-1, 6.1: a PDF/UA-1 file's version is 1.0 to 1.7 and does not decide conformance.
        if (claim == PdfConformance.PdfUA1)
            Assert.Matches(@"^%PDF-1\.[0-7]\n", Encoding.ASCII.GetString(bytes, 0, 9));
    }

    // ── ISO 19005-2, 6.1.3; ISO 32000-1, 7.5.5 and 14.4; ISO 32000-2, Table 15 ──────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var trailer = p.Trailer;

        // The construct: a trailer (or cross-reference stream dictionary) with /Root and /Size.
        Assert.IsType<Scan.PRef>(trailer["Root"]);
        Assert.NotNull(trailer.Int("Size"));

        // 6.1.3: /ID is present; 14.4: an array of two byte strings, equal when a file is first
        // written; ISO 32000-2 Table 15: each string at least 16 bytes. No /Encrypt.
        var id = Assert.IsType<List<object?>>(trailer["ID"]);
        Assert.Equal(2, id.Count);
        var first = Assert.IsType<Scan.PStr>(id[0]);
        var second = Assert.IsType<Scan.PStr>(id[1]);
        Assert.True(first.IsHex);
        Assert.Equal(PinnedId, first.Bytes);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.False(trailer.Has("Encrypt"));

        // Every trailer of the file, not only the first: a linearized file has two, and each that
        // carries an /ID carries the same two strings.
        foreach (var t in p.AllTrailers.Where(t => t.Has("ID")))
        {
            var pair = Assert.IsType<List<object?>>(t["ID"]);
            Assert.Equal(PinnedId, ((Scan.PStr)pair[0]!).Bytes);
            Assert.Equal(PinnedId, ((Scan.PStr)pair[1]!).Bytes);
        }
        Assert.Equal(path == FixturePath.Linearized ? 2 : 1, p.AllTrailers.Count());

        // 6.1.3: nothing follows the last %%EOF but one optional end-of-line marker.
        var text = p.Text;
        var eof = text.LastIndexOf("%%EOF", StringComparison.Ordinal);
        Assert.True(eof > 0);
        Assert.Equal("\n", text[(eof + 5)..]);
    }

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Iso32000_1_14_4_DerivedIdIsSixteenBytesAndFollowsTheTimestampAndTitle(PdfConformance claim)
    {
        PdfDocument Make(string title, DateTimeOffset ts)
        {
            var doc = new PdfDocument { Conformance = claim, Timestamp = ts };
            doc.Info.Title = title;
            doc.AddPage();
            return doc;
        }

        var a = Scan.Parse(Save(Make("one", Pinned))).Trailer;
        var b = Scan.Parse(Save(Make("one", Pinned))).Trailer;
        var c = Scan.Parse(Save(Make("two", Pinned))).Trailer;
        var d = Scan.Parse(Save(Make("one", Pinned.AddSeconds(1)))).Trailer;

        static byte[] Permanent(Scan.PDict t) => ((Scan.PStr)((List<object?>)t["ID"]!)[0]!).Bytes;
        static byte[] Changing(Scan.PDict t) => ((Scan.PStr)((List<object?>)t["ID"]!)[1]!).Bytes;

        Assert.Equal(16, Permanent(a).Length);
        Assert.Equal(Permanent(a), Changing(a));
        Assert.Equal(Permanent(a), Permanent(b));
        Assert.NotEqual(Permanent(a), Permanent(c));
        Assert.NotEqual(Permanent(a), Permanent(d));
    }

    [Fact]
    public void Iso32000_1_14_4_DocumentIdOfAnyOtherLengthThanSixteenIsRefusedAtTheSetter()
    {
        using var doc = new PdfDocument();
        var refusal = Assert.Throws<ArgumentException>(() => doc.DocumentId = new byte[15]);
        Assert.Equal("value", refusal.ParamName);
        Assert.Throws<ArgumentException>(() => doc.DocumentId = new byte[17]);
        doc.DocumentId = new byte[16];
        Assert.Equal(16, doc.DocumentId!.Length);

        using var layout = new LayoutDocument();
        Assert.Throws<ArgumentException>(() => layout.DocumentId = new byte[8]);
    }

    [Theory]
    [MemberData(nameof(PdfAClaimData))]
    public void Iso19005_2_6_1_3_EncryptIsRefusedAtSaveForEveryPdfALevelOnEverySavePath(PdfConformance claim)
    {
        var settings = new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" };

        using var kernel = new PdfDocument { Conformance = claim };
        kernel.AddPage();
        kernel.Encrypt(settings);
        Assert.Contains("19005-2", Assert.Throws<InvalidOperationException>(() => kernel.Save(new MemoryStream())).Message);

        using var layout = new LayoutDocument { Conformance = claim };
        layout.Add("x");
        layout.Encrypt(settings);
        Assert.Throws<InvalidOperationException>(() => layout.Save(new MemoryStream()));

        // The signing path refuses any encryption, so a signed file never carries /Encrypt either.
        using var signed = new PdfDocument { Conformance = claim };
        signed.AddPage();
        signed.Encrypt(settings);
        Assert.Throws<NotSupportedException>(() => signed.PrepareForSigning(new SignaturePlaceholderOptions()));
    }

    [Fact]
    public void Iso14289_1_7_16_PdfUA1MayBeEncryptedOnlyWithExtractAndThenTheTrailerHoldsAnId()
    {
        using var refused = new PdfDocument { Conformance = PdfConformance.PdfUA1 };
        refused.AddPage();
        refused.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o", Permissions = PdfPermissions.Print });
        Assert.Throws<InvalidOperationException>(() => refused.Save(new MemoryStream()));

        var doc = NewKernel(PdfConformance.PdfUA1);
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o", Permissions = PdfPermissions.All });
        var bytes = Save(doc);
        var text = Encoding.Latin1.GetString(bytes);
        var trailer = text[text.LastIndexOf("trailer", StringComparison.Ordinal)..];
        Assert.Contains("/Encrypt ", trailer);

        // ISO 32000-2 Table 15: with /Encrypt the /ID array and both strings are direct hex strings.
        Assert.Matches(@"/ID \[<[0-9A-F]{32}> <[0-9A-F]{32}>\]", trailer);

        // 7.16 (veraPDF 7.16-1): the encryption dictionary has /P with bit 10 (value 512) set, whether
        // the caller allowed everything, only extraction, or (outside PDF/UA-1) nothing of the kind.
        foreach (var (claim, permissions) in new[]
        {
            (PdfConformance.PdfUA1, PdfPermissions.All),
            (PdfConformance.PdfUA1, PdfPermissions.Extract),
            (PdfConformance.None, PdfPermissions.Print),
        })
        {
            var d = NewKernel(claim);
            d.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o", Permissions = permissions });
            var scanned = Scan.Parse(Save(d));
            var encrypt = (Scan.PDict)scanned.Resolve(scanned.Trailer["Encrypt"])!;
            var p = encrypt.Int("P")!.Value;
            Assert.Equal(512, p & 512);
        }
    }

    // ── ISO 19005-2, 6.1.4; ISO 32000-1, 7.5.4 and 7.5.5 ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_4_XrefKeywordAndSubsectionHeaderAreSeparatedByOneEol(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        if (path == FixturePath.ObjectStreams)
        {
            // No xref keyword in a file that uses a cross-reference stream (ISO 32000-1, 7.5.8.1).
            Assert.Empty(p.Xrefs);
            Assert.DoesNotContain("\nxref", p.Text);
            return;
        }

        // The construct: at least one classic section.
        Assert.NotEmpty(p.Xrefs);
        foreach (var section in p.Xrefs)
        {
            Assert.Equal("xref\n", section.KeywordLine);
            Assert.NotEmpty(section.Subsections);
            foreach (var sub in section.Subsections)
                Assert.Matches(@"^\d+ \d+\n$", sub.HeaderLine);
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso32000_1_7_5_4_XrefEntriesAreTwentyBytesAndPointAtTheirObjects(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        Assert.Empty(p.Problems);
        if (path == FixturePath.ObjectStreams)
        {
            // 7.5.8: the entries are fields of W widths, and a type-1 entry's offset is the object's.
            var xrefObject = p.Objects.Single(o => o.Dict?.Name("Type") == "XRef");
            var w = xrefObject.Dict!.Array("W")!.Select(v => int.Parse(((Scan.PNum)v!).Raw, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(3, w.Length);
            var rows = Scan.Inflate(xrefObject.StreamData!);
            var size = (int)xrefObject.Dict.Int("Size")!.Value;
            var rowSize = w.Sum();
            Assert.Equal(size * rowSize, rows.Length);
            for (var n = 1; n < size; n++)
            {
                var row = rows.AsSpan(n * rowSize, rowSize);
                var type = row[0];
                long field2 = 0;
                for (var i = 0; i < w[1]; i++)
                    field2 = (field2 << 8) | row[w[0] + i];
                if (type == 1)
                    Assert.Equal(field2, p.Objects.Single(o => o.Number == n && !o.InObjectStream).Offset);
                else if (type == 2)
                    Assert.Contains(p.Objects, o => o.Number == (int)field2 && o.Dict?.Name("Type") == "ObjStm");
                else
                    Assert.Fail($"object {n} has cross-reference type {type}");
            }
            Assert.Equal(p.Objects.Max(o => o.Number) + 1, size);
            return;
        }

        // The construct: entries in the section, each exactly 20 bytes (ISO 32000-1, 7.5.4); the
        // scanner already refused any entry of another form (Problems is empty above).
        var inUse = new Dictionary<int, long>();
        foreach (var section in p.Xrefs)
        {
            foreach (var e in section.Entries)
            {
                Assert.Equal(20, e.Raw.Length);
                if (e.InUse)
                    inUse[e.Number] = e.Offset;
            }
        }
        Assert.NotEmpty(inUse);

        // Object 0 heads the free list with generation 65535, in a section that begins at 0.
        var head = p.Xrefs.First(s => s.Subsections[0].First == 0).Entries[0];
        Assert.Equal("0000000000 65535 f\r\n", head.Raw);

        // Every in-use entry points at the "N 0 obj" it names.
        var objects = p.Objects.Where(o => !o.InObjectStream).ToDictionary(o => o.Number);
        foreach (var (number, offset) in inUse)
        {
            Assert.True(objects.TryGetValue(number, out var o), $"object {number} is in the table but not in the body");
            Assert.Equal(o!.Offset, offset);
            Assert.StartsWith($"{number} 0 obj\n", Encoding.Latin1.GetString(p.Bytes, (int)offset, 20));
        }
        Assert.Equal(objects.Count, inUse.Count);

        // 7.5.5: the trailer's /Size is one more than the highest object number, startxref names
        // the table (the first-page section in a linearized file), and the last line is %%EOF.
        Assert.Equal(objects.Keys.Max() + 1, p.Trailer.Int("Size"));
        var last = p.StartXrefs[^1];
        var target = path == FixturePath.Linearized ? p.Xrefs[0].Offset : p.Xrefs[^1].Offset;
        Assert.Equal(target, last);
        Assert.StartsWith("xref", Encoding.ASCII.GetString(p.Bytes, (int)last, 4));
    }

    // ── ISO 19005-2, 6.1.6; ISO 32000-1, 7.3.4.3 ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_6_HexStringsHaveAnEvenNumberOfDigits(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var hex = p.Tokens.Where(t => t.Kind == 'S' && t.Raw == "<").ToList();

        // The construct: the trailer /ID is two hex strings, and the signing paths add /Contents.
        Assert.True(hex.Count >= 2);
        Assert.All(hex, t => Assert.Equal(0, t.HexDigits % 2));
    }

    // ── ISO 19005-2, 6.1.7.1 and 6.1.7.2; ISO 32000-1, 7.3.8 and 7.4 ───────────────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_7_1_StreamKeywordLengthAndEndstreamFollowTheSyntax(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var streams = p.Objects.Where(o => o.StreamData is not null).ToList();

        // The construct: streams, each with a direct /Length. The scanner takes the body to be
        // /Length bytes after "stream" and an LF or CRLF, then requires an EOL, "endstream", an
        // EOL and "endobj" (7.3.8.1, 7.3.8.2); anything else is in Problems.
        Assert.NotEmpty(streams);
        Assert.Empty(p.Problems);
        foreach (var s in streams)
        {
            Assert.NotNull(s.Dict!.Int("Length"));
            Assert.Equal(s.Dict.Int("Length"), s.StreamData!.Length);
        }

        // 6.1.7.1: no external stream keys, in any dictionary of the file.
        foreach (var d in AllDictionaries(p))
        {
            Assert.False(d.Has("FFilter"));
            Assert.False(d.Has("FDecodeParms"));
        }
        foreach (var s in streams)
            Assert.False(s.Dict!.Has("F"), $"object {s.Number} is a stream with /F");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_7_2_OnlyFlateDecodeIsUsed(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var filters = new HashSet<string>();
        foreach (var d in p.Objects.Where(o => o.StreamData is not null).Select(o => o.Dict!))
        {
            if (d["Filter"] is Scan.PName n)
                filters.Add(n.Text);
            else if (d["Filter"] is List<object?> l)
                foreach (var v in l.OfType<Scan.PName>())
                    filters.Add(v.Text);
            Assert.False(d.Has("DecodeParms"));
        }

        // The construct: at least one filtered stream. Table 6 of ISO 32000-1 minus LZWDecode, and
        // Crypt only as Identity: the writer uses neither.
        Assert.Contains("FlateDecode", filters);
        Assert.Subset(new HashSet<string> { "FlateDecode" }, filters);
    }

    // ── ISO 19005-2, 6.1.9; ISO 32000-1, 7.3.10 ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_9_ObjectKeywordsAreSeparatedByOneSpaceAndSurroundedByEols(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var bodies = p.Objects.Where(o => !o.InObjectStream).ToList();

        // The construct: indirect objects. Each begins at the start of a line with "N G obj" and
        // one EOL, and ends with an EOL, "endobj" and an EOL (the scanner puts the closing EOLs in Problems).
        Assert.NotEmpty(bodies);
        Assert.Empty(p.Problems);
        foreach (var o in bodies)
        {
            Assert.Matches(@"\A\d+ \d+ obj\n", o.Raw);
            Assert.Equal((byte)'\n', p.Bytes[(int)o.Offset - 1]);
        }

        // 7.3.10: a new file's generation numbers are all 0, and no object number repeats.
        Assert.All(bodies, o => Assert.Equal(0, o.Generation));
        Assert.Equal(bodies.Count, bodies.Select(o => o.Number).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso32000_1_7_3_7_NoDictionaryRepeatsAKey(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var dictionaries = AllDictionaries(p).ToList();
        Assert.True(dictionaries.Count > 10);
        Assert.All(dictionaries, d => Assert.Empty(d.DuplicateKeys));

        // 7.3.7 and 7.3.9: a null value is the same as no entry, and the writer leaves the entry out. (Arrays do
        // hold null, as the unused destination parameter in /XYZ.)
        Assert.All(dictionaries, d => Assert.DoesNotContain(d.Entries, e => e.Value is null));
    }

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect(PdfConformance claim)
    {
        var p = Parsed(claim, FixturePath.ObjectStreams);

        // The construct: an object stream and a cross-reference stream.
        Assert.Equal(1, p.ObjStmCount);
        var xref = p.Objects.Single(o => o.Dict?.Name("Type") == "XRef");
        var objStm = p.Objects.Single(o => o.Dict?.Name("Type") == "ObjStm");
        Assert.NotNull(objStm.Dict!.Int("Length"));
        Assert.Equal("FlateDecode", objStm.Dict.Name("Filter"));
        Assert.Equal((long)p.Objects.Count(o => o.InObjectStream), objStm.Dict.Int("N"));

        // 7.5.7: no stream, no encryption dictionary, no generation other than 0 inside it.
        var inside = p.Objects.Where(o => o.InObjectStream).Select(o => o.Number).ToHashSet();
        var streams = p.Objects.Where(o => !o.InObjectStream && o.StreamData is not null).Select(o => o.Number).ToHashSet();
        Assert.Empty(inside.Intersect(streams));
        Assert.DoesNotContain(xref.Number, inside);
        Assert.DoesNotContain(objStm.Number, inside);
        Assert.False(xref.Dict!.Has("Encrypt"));

        // 7.5.7, the four layout rules: the N offsets increase, the first object starts right after the
        // offset header, no object is only a reference, and no obj or endobj keyword is inside.
        var body = Scan.Inflate(objStm.StreamData!);
        var n = (int)objStm.Dict.Int("N")!.Value;
        var first = (int)objStm.Dict.Int("First")!.Value;
        var head = new Scan.Lexer(body, 0);
        var offsets = new List<long>();
        for (var i = 0; i < n; i++)
        {
            Assert.IsType<Scan.PNum>(head.ReadToken());
            offsets.Add(long.Parse(((Scan.PNum)head.ReadToken()).Raw, CultureInfo.InvariantCulture));
        }
        Assert.Equal(0, offsets[0]);
        for (var i = 1; i < n; i++)
            Assert.True(offsets[i] > offsets[i - 1], $"offset {i} does not increase");
        Assert.All(body.AsSpan(head.Pos, first - head.Pos).ToArray(), b => Assert.Contains(b, new byte[] { 0, 9, 10, 12, 13, 32 }));
        Assert.All(p.Objects.Where(o => o.InObjectStream), o => Assert.IsNotType<Scan.PRef>(o.Value));
        Assert.DoesNotMatch(@"\b(end)?obj\b", Encoding.Latin1.GetString(body));

        // 7.5.8.2: the cross-reference stream dictionary's /W, /Size and /Root are direct, with /Root a reference.
        Assert.IsType<List<object?>>(xref.Dict["W"]);
        Assert.IsType<Scan.PRef>(xref.Dict["Root"]);
        Assert.Equal("FlateDecode", xref.Dict.Name("Filter"));
    }

    // ── ISO 19005-2, 6.1.11 and 6.1.12; ISO 32000-1, 7.5; ISO 19005-2, 6.11 ─────────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Iso19005_2_6_1_12_And_6_11_CatalogHasNoPermsAndNoRequirements(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var catalog = p.Catalog;
        Assert.Equal("Catalog", catalog.Name("Type"));
        Assert.False(catalog.Has("Perms"));
        Assert.False(catalog.Has("Requirements"));
        Assert.False(catalog.Has("NeedsRendering"));
        Assert.False(catalog.Has("Extensions"));
    }

    [Theory]
    [MemberData(nameof(PdfAClaimData))]
    public void Iso19005_2_6_1_11_LinearizedFileIsAllowedAndItsParameterDictionaryIsFirst(PdfConformance claim)
    {
        var p = Parsed(claim, FixturePath.Linearized);

        // The construct: a linearization parameter dictionary as the first object, a first-page and
        // a main cross-reference section.
        var first = p.Objects[0].Dict!;
        Assert.NotNull(first.Int("Linearized"));
        Assert.Equal(p.Bytes.Length, first.Int("L"));
        Assert.Equal(2, p.Xrefs.Count);
        Assert.Equal(1, p.Objects.Count(o => o.Dict?.Has("Linearized") == true));

        // Verdict by oracle in Oracles_AcceptEveryClaimedFixture; here the header version and /ID
        // stay what the other paths write.
        Assert.StartsWith("%PDF-1.7\n", p.Text);
        Assert.IsType<List<object?>>(p.Trailer["ID"]);
    }

    // ── ISO 19005-2, 6.1.13; ISO 32000-1, 7.3.3, Annex C ────────────────────────────────────────

    [Theory]
    [MemberData(nameof(DeliveredFixtures))]
    public void Iso19005_2_6_1_13_IntegerTokensStayWithinInt32AndRealsCarryNoExponent(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var integers = p.Tokens.Where(t => t.Kind == 'I').ToList();
        var reals = p.Tokens.Where(t => t.Kind == 'R').ToList();
        Assert.True(integers.Count > 10);

        // 6.1.13 (veraPDF 6.1.13-1): -2147483648 to 2147483647. The scanner took each token as one
        // integer, so a token that is not an integer never reaches this list.
        foreach (var t in integers)
        {
            var v = long.Parse(t.Raw, CultureInfo.InvariantCulture);
            Assert.InRange(v, int.MinValue, int.MaxValue);
        }

        // ISO 32000-1, 7.3.3, NOTE 1: no exponent and no radix. (An exponent would not scan as a real.)
        Assert.All(reals, t => Assert.Matches(@"^[+-]?(\d+\.\d*|\.\d+)$", t.Raw));
        Assert.DoesNotContain(p.Tokens, t => t.Kind == 'K' && Regex.IsMatch(t.Raw, @"^[+-]?[\d.]+[eE]"));
    }

    [Fact]
    public void Iso19005_2_6_1_13_PrepareForSigningWritesTenNinesInByteRangeAndSigningReplacesThem()
    {
        // Placeholder: four integers above 2147483647 until the signature patches them in. It is the
        // reason no validator is run on the unsigned file.
        var prepared = Parsed(PdfConformance.PdfA2b, FixturePath.Prepared);
        var sig = prepared.Objects.Single(o => o.Dict?.Name("Type") == "Sig").Dict!;
        var placeholder = sig.Array("ByteRange")!.Cast<Scan.PNum>().Select(n => n.Raw).ToArray();
        Assert.Equal(["9999999999", "9999999999", "9999999999", "9999999999"], placeholder);

        // The delivered file: four integers that fit, the first 0, and the last range ends at the end of the file.
        var signed = Parsed(PdfConformance.PdfA2b, FixturePath.Signed);
        var range = signed.Objects.Single(o => o.Dict?.Name("Type") == "Sig").Dict!.Array("ByteRange")!
            .Cast<Scan.PNum>().Select(n => long.Parse(n.Raw.Trim(), CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(0, range[0]);
        Assert.All(range, v => Assert.InRange(v, 0, int.MaxValue));
        Assert.Equal(signed.Bytes.Length, range[2] + range[3]);
        Assert.DoesNotContain("9999999999", signed.Text);
    }

    [Fact]
    public void Iso19005_2_6_1_13_RealsAtTheBoundariesAreWrittenAsTheClauseAllows()
    {
        static string Token(double v)
        {
            using var ms = new MemoryStream();
            new PdfReal(v).WriteTo(new PdfWriter(ms));
            return Encoding.ASCII.GetString(ms.ToArray());
        }

        static string Operand(double v)
        {
            using var doc = new PdfDocument();
            var page = doc.AddPage();
            new PdfCanvas(page).MoveTo(v, 5).Finish();
            return Encoding.Latin1.GetString(page.ContentBytes!).Split('\n')[0].Split(' ')[0];
        }

        // The integer range: 2147483647 and -2147483648 are integer tokens; one past either is a
        // real token (#646), so the file holds no integer outside the range.
        Assert.Equal("2147483647", Token(2147483647d));
        Assert.Equal("-2147483648", Token(-2147483648d));
        Assert.Equal("2147483648.0", Token(2147483648d));
        Assert.Equal("-2147483649.0", Token(-2147483649d));

        // The largest real the clause allows, 3.403e38, is written in full, with no exponent.
        Assert.Equal("340300000000000000000000000000000000000.0", Token(3.403e38));

        // A value closer to zero than 1.175e-38 is written as 0, which the clause allows; at five
        // decimals the writer reaches 0 far above that floor, and a value that rounds to 0.00001 is not below it.
        Assert.Equal("0", Token(1e-39));
        Assert.Equal("-0", Token(-1e-39));
        Assert.Equal("0", Token(1.174e-38));
        Assert.Equal("0", Token(4e-6));
        Assert.Equal("0.00001", Token(5e-6));
        Assert.Equal("-0.00001", Token(-5e-6));

        // The content stream formats through the same method.
        Assert.Equal("0", Operand(1e-39));
        Assert.Equal("2147483648.0", Operand(2147483648d));
        Assert.Equal("340300000000000000000000000000000000000.0", Operand(3.403e38));
    }

    [Fact(Skip = "Gap #666: a real above 3.403e38 is written as given under a PDF/A claim and fails 6.1.13 (veraPDF 6.1.13-2).")]
    public void Iso19005_2_6_1_13_RealsAbove3_403e38AreNotWrittenUnderAPdfAClaim()
    {
        foreach (var claim in PdfAClaims)
        {
            using var doc = new PdfDocument { Conformance = claim, Timestamp = Pinned, DocumentId = PinnedId };
            var page = doc.AddPage();
            Exception? refusal = null;
            try
            {
                new PdfCanvas(page).MoveTo(3.5e38, 5).LineTo(10, 10).Stroke().Finish();
                using var ms = new MemoryStream();
                doc.Save(ms);
                var tokens = Scan.TokenizeContent(page.ContentBytes!);
                Assert.All(tokens.Where(t => t.Kind is 'R' or 'I'), t => Assert.True(Math.Abs(double.Parse(t.Raw, CultureInfo.InvariantCulture)) <= 3.403e38));
            }
            catch (ArgumentException e)
            {
                refusal = e;
            }
            catch (InvalidOperationException e)
            {
                refusal = e;
            }
            _ = refusal;
        }
    }

    [Fact]
    public void Iso19005_2_6_1_13_StringsOfExactly32767BytesAndTitlesOfExactly32766AreWrittenInFull()
    {
        // A 16382-character title is 32766 bytes in UTF-16BE with its two-byte mark (2 + 2 x 16382 = 32766);
        // the output intent identifier is Latin-1, one byte a character, so 32767 characters are 32767 bytes.
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        doc.Info.Title = new string('a', 16382);
        doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, new string('o', 32767));
        doc.AddPage();
        var bytes = Save(doc);
        var p = Scan.Parse(bytes);

        // The construct: both strings, at their sizes, as tokens.
        var title = (Scan.PStr)p.Info!["Title"]!;
        Assert.Equal(2 + 2 * 16382, title.Bytes.Length);
        var intent = (Scan.PDict)((List<object?>)p.Catalog["OutputIntents"]!).Select(p.Resolve).Single()!;
        Assert.Equal(32767, ((Scan.PStr)intent["OutputConditionIdentifier"]!).Bytes.Length);
        Assert.Contains(p.Tokens, t => t.Kind == 'S' && t.Length == 32767);

        // 6.1.13 (veraPDF 6.1.13-3): at most 32767 bytes. Neither string is over it.
        Assert.All(p.Tokens.Where(t => t.Kind == 'S'), t => Assert.True(t.Length <= 32767));
        var (compliant, failed) = VeraPdf("2b", bytes);
        Assert.True(compliant, string.Join(",", failed));
    }

    [Fact(Skip = "Gap #666: a string of 32768 bytes or more (Info text, OutputConditionIdentifier, Lang) is written under a PDF/A claim and fails 6.1.13 (veraPDF 6.1.13-3); the class is #666.")]
    public void Iso19005_2_6_1_13_StringsOver32767BytesAreNotWrittenUnderAPdfAClaim()
    {
        // Each source named in the skip reason: an Info text key, the output intent identifier, and the catalog /Lang.
        // 16383 UTF-16 characters are 32768 bytes with the mark; 32768 Latin-1 characters are 32768 bytes.
        var builds = new Action<PdfDocument>[]
        {
            d => d.Info.Title = new string('a', 16383),
            d => d.Info.Author = new string('a', 16383),
            d => d.SetPdfAOutputIntent(IccProfiles.Srgb, 3, new string('o', 32768)),
            d => d.Language = new string('a', 16383),
        };
        var failures = new List<string>();
        for (var i = 0; i < builds.Length; i++)
        {
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
            builds[i](doc);
            doc.AddPage();
            try
            {
                var p = Scan.Parse(Save(doc));
                if (p.Tokens.Any(t => t.Kind == 'S' && t.Length > 32767))
                    failures.Add($"source {i} wrote a string over 32767 bytes");
            }
            catch (ArgumentException)
            {
                // A refusal is the other way to meet the clause.
            }
        }
        Assert.Empty(failures);
    }

    [Fact(Skip = "Gap #740: a font name or resource name over 127 bytes, with a byte above 127 (invalid UTF-8), with a character above U+00FF (written as '?') or with the null character (written /..#00..) is written; veraPDF 6.1.13-4 and 6.1.8-1 see the first two.")]
    public void Iso19005_2_6_1_8_And_6_1_13_NamesAreUtf8AndAtMost127BytesUnderAPdfAClaim()
    {
        var failures = new List<string>();

        // A font's PostScript name reaches /BaseFont as it is: too long, not UTF-8, not Latin-1.
        foreach (var (label, psName) in new[] { ("128 bytes", new string('P', 128 - 7)), ("U+00E9", "Café"), ("CJK", "日本") })
        {
            var font = EmbeddedFontFixtures.WithTables(DejaVu, add: new Dictionary<string, byte[]> { ["name"] = PostScriptNameTable(psName) });
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
            DrawHelloIn(doc, font);
            try
            {
                var p = Scan.Parse(Save(doc));
                var baseFont = AllDictionaries(p).Select(d => d["BaseFont"]).OfType<Scan.PName>().First();
                if (baseFont.Bytes.Length > 127)
                    failures.Add($"{label}: /BaseFont is {baseFont.Bytes.Length} bytes");
                if (baseFont.Bytes.Any(b => b > 127))
                    failures.Add($"{label}: /BaseFont holds a byte above 127, which is not UTF-8");
                if (psName.Any(c => c > 0xFF) && baseFont.Text.Contains('?'))
                    failures.Add($"{label}: /BaseFont has '?' where the name had a character above U+00FF");
            }
            catch (ArgumentException)
            {
                // A refusal meets the clause too.
            }
        }

        // A caller's resource name: too long, or holding the null character 7.3.5 excludes.
        foreach (var (label, resource) in new[] { ("128 bytes", new string('I', 128)), ("null", "Im\0x") })
        {
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
            var page = doc.AddPage();
            doc.RegisterImageXObject(page, Images.PngImageLoader.Load(TinyPng()), resource);
            new PdfCanvas(page).SaveState().Concat(10, 0, 0, 10, 0, 0).DoXObject(resource).RestoreState().Finish();
            try
            {
                var p = Scan.Parse(Save(doc));
                if (p.Tokens.Any(t => t.Kind == 'N' && t.Length > 127))
                    failures.Add($"{label}: a name over 127 bytes");
                if (p.Tokens.Any(t => t.Kind == 'N' && t.Raw.Contains("#00", StringComparison.Ordinal)))
                    failures.Add($"{label}: a name holds the null character");
            }
            catch (ArgumentException)
            {
                // A refusal meets the clause too.
            }
        }
        Assert.Empty(failures);
    }

    [Fact(Skip = "Gap #740: PdfName accepts the null character, which ISO 32000-1, 7.3.5 excludes from a name, and writes it as #00; qpdf 12.4.2 warns 'null character not allowed in name token' and damages the resource dictionary; under no claim too.")]
    public void Iso32000_1_7_3_5_ANameHoldsNoNullCharacter()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterImageXObject(page, Images.PngImageLoader.Load(TinyPng()), "Im\0x");
        new PdfCanvas(page).SaveState().Concat(10, 0, 0, 10, 0, 0).DoXObject("Im\0x").RestoreState().Finish();
        try
        {
            var p = Scan.Parse(Save(doc));
            Assert.DoesNotContain(p.Tokens, t => t.Kind == 'N' && t.Raw.Contains("#00", StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            // A refusal meets the clause too.
        }
    }

    [Fact]
    public void Iso19005_2_6_1_13_NamesOfExactly127BytesAreWrittenInFullAndPassVeraPdf()
    {
        var name = new string('I', 127);
        var font = EmbeddedFontFixtures.WithTables(DejaVu, add: new Dictionary<string, byte[]> { ["name"] = PostScriptNameTable(new string('P', 127 - 7)) });
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        DrawHelloIn(doc, font);
        var page = doc.Pages[0];
        var png = TinyPng();
        doc.RegisterImageXObject(page, Images.PngImageLoader.Load(png), name);
        new PdfCanvas(page).SaveState().Concat(10, 0, 0, 10, 0, 0).DoXObject(name).RestoreState().Finish();
        var bytes = Save(doc);
        var p = Scan.Parse(bytes);

        // The construct: a resource name of 127 bytes and a base font name of 127 bytes (subset
        // tag, plus sign, 120 characters).
        Assert.Contains(p.Tokens, t => t.Kind == 'N' && t.Length == 127 && t.Raw == name);
        var fontName = AllDictionaries(p).Select(d => d["BaseFont"]).OfType<Scan.PName>().First();
        Assert.Equal(127, fontName.Bytes.Length);

        // 6.1.13 (veraPDF 6.1.13-4): at most 127 bytes, after # expansion. None is over it.
        Assert.All(p.Tokens.Where(t => t.Kind == 'N'), t => Assert.True(t.Length <= 127));
        var (compliant, failed) = VeraPdf("2b", bytes);
        Assert.True(compliant, string.Join(",", failed));
    }

    [Fact]
    public void Iso19005_2_6_1_13_PageBoundariesOfThreeAndFourteenThousandFourHundredAreWritten()
    {
        // The construct: MediaBox arrays at both ends of the allowed size, from Kernel and Layout.
        foreach (var (w, h) in new[] { (3d, 3d), (14400d, 14400d), (3d, 14400d) })
        {
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
            doc.AddPage(new PdfRectangle(0, 0, w, h));
            var p = Scan.Parse(Save(doc));
            var box = p.Objects.Single(o => o.Dict?.Name("Type") == "Page").Dict!.Array("MediaBox")!
                .Cast<Scan.PNum>().Select(n => double.Parse(n.Raw, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal([0, 0, w, h], box);
        }

        var layout = new LayoutDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId, PageSize = new PdfRectangle(0, 0, 14400, 14400) };
        layout.Add(new Paragraph("x", new TextStyle { FontSize = 11 }));
        var lp = Scan.Parse(Save(layout));
        Assert.Equal(14400, double.Parse(((Scan.PNum)lp.Objects.Single(o => o.Dict?.Name("Type") == "Page").Dict!.Array("MediaBox")![2]!).Raw, CultureInfo.InvariantCulture));
    }

    [Fact(Skip = "Gap #666: a page size outside 3 to 14400 units is written under a PDF/A claim and fails 6.1.13 (veraPDF 6.1.13-11), from Kernel AddPage and from Layout PageSize.")]
    public void Iso19005_2_6_1_13_PageBoundariesOutsideThreeToFourteenThousandFourHundredAreNotWrittenUnderAPdfAClaim()
    {
        var failures = new List<string>();
        foreach (var (w, h) in new[] { (14401d, 100d), (2.9, 100d), (100d, 14400.5) })
        {
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
            doc.AddPage(new PdfRectangle(0, 0, w, h));
            var layout = new LayoutDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId, PageSize = new PdfRectangle(0, 0, w, h), Margins = new EdgeInsets(1) };
            layout.Add(new Paragraph("x", new TextStyle { FontSize = 1 }));
            foreach (var (label, save) in new (string, Func<byte[]>)[] { ("Kernel", () => Save(doc)), ("Layout", () => Save(layout)) })
            {
                try
                {
                    var p = Scan.Parse(save());
                    var box = p.Objects.First(o => o.Dict?.Name("Type") == "Page").Dict!.Array("MediaBox")!
                        .Cast<Scan.PNum>().Select(n => double.Parse(n.Raw, CultureInfo.InvariantCulture)).ToArray();
                    if (box[2] - box[0] is < 3 or > 14400 || box[3] - box[1] is < 3 or > 14400)
                        failures.Add($"{label} wrote a {w} x {h} page");
                }
                catch (ArgumentException)
                {
                    // A refusal meets the clause too.
                }
                catch (InvalidOperationException)
                {
                    // So does a refusal as too tall.
                }
            }
        }
        Assert.Empty(failures);
    }

    [Fact(Skip = "Gap #746: the incremental update Sign writes at PadesLevel B_LT or B_LTA drops /Info from the trailer and keeps the second /ID string equal to the first, against ISO 32000-1, 7.5.6 and 14.4; veraPDF 2b accepts the file.")]
    public void Iso32000_1_7_5_6_IncrementalTrailersKeepEveryEntryAndTheSecondIdChanges()
    {
        using var doc = NewKernel(PdfConformance.PdfA2b);
        using var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings
        {
            Certificate = Certificate.Value,
            SigningTime = Pinned,
            Level = PadesLevel.B_LT,
            TimestampClient = new TestTimestampClient(Pinned),
            RevocationClient = new CannedRevocation(),
        });
        var p = Scan.Parse(ms.ToArray());

        // The construct: the file holds the signing revision and at least one appended revision, each with a trailer.
        var trailers = p.Xrefs.Select(x => x.Trailer!).ToList();
        Assert.True(trailers.Count >= 2, $"{trailers.Count} trailers");
        Assert.Contains(p.Xrefs.Skip(1), x => x.Trailer!.Has("Prev"));

        // 7.5.6: an added trailer holds every entry of the previous one except /Prev; 14.4: the second /ID
        // string is the changing one, based on the file as last updated.
        for (var i = 1; i < trailers.Count; i++)
        {
            foreach (var (key, _) in trailers[i - 1].Entries.Where(e => e.Key != "Prev"))
                Assert.True(trailers[i].Has(key), $"trailer {i} lacks /{key}");
            var previous = (List<object?>)trailers[i - 1]["ID"]!;
            var current = (List<object?>)trailers[i]["ID"]!;
            Assert.Equal(((Scan.PStr)previous[0]!).Bytes, ((Scan.PStr)current[0]!).Bytes);
            Assert.NotEqual(((Scan.PStr)current[0]!).Bytes, ((Scan.PStr)current[1]!).Bytes);
        }
    }

    private sealed class CannedRevocation : IRevocationClient
    {
        public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer)
            => new()
            {
                Ocsp = new ReadOnlyMemory<byte>([0x30, 0x03, 0x0A, 0x01, 0x00]),
                Crl = new ReadOnlyMemory<byte>([0x30, 0x04, 0x02, 0x01, 0x2A]),
            };
    }

    [Fact]
    public void Iso19005_2_6_1_13_LayoutOutputNestsQAtMostOneDeepWhereKernelCallerCanNestPastTwentyEight()
    {
        // Every Layout renderer that saves state restores it in the same method. A document with all
        // five (image, pie chart, preformatted text, note block, a tab leader) shows the depth.
        var doc = new LayoutDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, "sRGB");
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVu), FontSize = 11 };
        var mono = new TextStyle { FontRef = doc.UseTrueTypeFont(EmbeddedFontFixtures.Load("LiberationMono-Regular.ttf")), FontSize = 10 };
        doc.Add(new LayoutImage(Images.PngImageLoader.Load(TinyPng())) { Width = 20, Height = 20 });
        doc.Add(new PieChart { Slices = [new PieSlice(1, ColorRgb.Black)], Diameter = 20 });
        doc.Add(new Preformatted("fixed", mono));
        doc.Add(new NoteBlock([new Paragraph("note", style)]) { Background = new ColorRgb(0.9, 0.9, 0.9), BorderWidth = 1 });
        doc.Add(new Paragraph("a\tb", style) { TabStops = [new TabStop(200, TabAlignment.Left, TabLeader.Dots)] });
        var p = Scan.Parse(Save(doc));

        var content = string.Join('\n', p.PageContents().Select(c => Encoding.Latin1.GetString(c)));
        var depth = 0;
        var max = 0;
        var saves = 0;
        foreach (var token in content.Split((char)10).Select(l => l.Trim()))
        {
            if (token == "q")
            {
                max = Math.Max(max, ++depth);
                saves++;
            }
            else if (token == "Q")
                depth--;
        }

        // The construct: at least four saves in the content (one per renderer that has one).
        Assert.True(saves >= 4, $"{saves} q operators");
        Assert.Equal(0, depth);
        Assert.InRange(max, 1, 28);
    }

    [Fact(Skip = "Gap #666: PdfCanvas.SaveState nests without a bound, so a caller's 29 nested q operators are written under a PDF/A claim and fail 6.1.13 (veraPDF 6.1.13-8).")]
    public void Iso19005_2_6_1_13_QqNestingOfTwentyNineIsNotWrittenUnderAPdfAClaim()
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        try
        {
            for (var i = 0; i < 29; i++)
                canvas.SaveState();
            canvas.Rectangle(1, 1, 5, 5).Fill();
            for (var i = 0; i < 29; i++)
                canvas.RestoreState();
            canvas.Finish();
            _ = Save(doc);
            Assert.Fail("29 nested q operators were saved under PDF/A.");
        }
        catch (InvalidOperationException)
        {
            // A refusal meets the clause.
        }
    }

    [Fact(Skip = "Gap #666: nothing bounds the number of indirect objects (8388607, veraPDF 6.1.13-7); the writer reserves object numbers without a check and two objects a page reach it at 4,194,304 pages; not measured at the boundary.")]
    public void Iso19005_2_6_1_13_AtMost8388607IndirectObjectsAreWrittenUnderAPdfAClaim()
    {
        // Left as a statement of the requirement: building the 4.2 million pages it takes needs several
        // gigabytes, which the shared test machines do not have.
        Assert.Fail("Not run.");
    }

    // ── ISO 19005-2, 6.6.2.1, 6.6.2.3.1, 6.6.3, 6.6.4; ISO 32000-1, 14.3 ──────────────────────

    [Theory]
    [MemberData(nameof(DeliveredFixtures))]
    public void Iso19005_2_6_6_2_1_MetadataStreamIsUncompressedXmlWithNoBytesOrEncodingAttribute(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var catalog = p.Catalog;
        var stream = p.Get(((Scan.PRef)catalog["Metadata"]!).Number);

        // The construct: a catalog /Metadata stream with /Type /Metadata and /Subtype /XML (14.3.2).
        Assert.Equal("Metadata", stream.Dict!.Name("Type"));
        Assert.Equal("XML", stream.Dict.Name("Subtype"));
        Assert.False(stream.Dict.Has("Filter"));

        var text = Packet(p);
        // The header processing instruction: no bytes and no encoding attribute (6.6.2.1); the id
        // is the XMP packet GUID (ISO 16684-1, 7.3.2).
        var header = Regex.Match(text, "^<\\?xpacket (?<attrs>[^?]*)\\?>");
        Assert.True(header.Success);
        Assert.DoesNotContain("bytes=", header.Groups["attrs"].Value);
        Assert.DoesNotContain("encoding=", header.Groups["attrs"].Value);
        Assert.Contains("id=\"W5M0MpCehiHzreSzNTczkc9d\"", header.Groups["attrs"].Value);
        Assert.EndsWith("<?xpacket end=\"r\"?>", text);

        // Well-formed XML 1.0 (CheckCharacters on), UTF-8 without a mark (veraPDF 6.6.2.1-4 and -5).
        var d = Description(p.MetadataXml);
        Assert.NotNull(d);
        Assert.NotEqual(0xEF, p.MetadataXml[0]);
    }

    [Fact(Skip = "Gap #742: the xpacket begin attribute holds the three characters U+00EF U+00BB U+00BF (bytes C3 AF C2 BB C2 BF) where ISO 16684-1, 7.3.2 allows U+FEFF or nothing.")]
    public void Iso16684_1_7_3_2_XpacketBeginHoldsTheByteOrderMarkOrNothing()
    {
        var p = Parsed(PdfConformance.PdfA2b, FixturePath.Kernel);
        var m = Regex.Match(Encoding.Latin1.GetString(p.MetadataXml), "^<\\?xpacket begin=\"(?<b>[^\"]*)\"");
        Assert.True(m.Success);
        Assert.Contains(m.Groups["b"].Value, new[] { string.Empty, "\u00EF\u00BB\u00BF" });
    }

    [Fact]
    public void Iso19005_2_6_6_2_1_XmpTextIsEscapedAndIllegalControlCharactersAreDropped()
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        doc.Info.Title = "a&b<c>\"d'e\u0001f\tg\U0001F600h";
        doc.AddPage();
        var p = Scan.Parse(Save(doc));

        // The construct: the title element, with entity references in the bytes.
        var raw = Packet(p);
        Assert.Contains("a&amp;b&lt;c&gt;&quot;d'ef\tg\U0001F600h", raw);

        // Parsed with CheckCharacters, the text is the title minus the one control character.
        var title = Description(p.MetadataXml).Element(Dc + "title")!.Descendants(Rdf + "li").Single().Value;
        Assert.Equal("a&b<c>\"d'ef\tg\U0001F600h", title);
    }

    [Fact(Skip = "Gap #741: U+FFFE and U+FFFF in an Info or Language string reach the XMP packet, which is then not well-formed XML 1.0 (veraPDF 6.6.2.1-4, -5 and 6.6.4-1).")]
    public void Iso19005_2_6_6_2_1_XmpStaysWellFormedForEveryStringTheWriterAccepts()
    {
        foreach (var title in new[] { "a\uFFFEb", "a\uFFFFb" })
        {
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
            doc.Info.Title = title;
            doc.AddPage();
            try
            {
                var p = Scan.Parse(Save(doc));
                Assert.NotNull(Description(p.MetadataXml));
            }
            catch (ArgumentException)
            {
                // A refusal meets the clause too.
            }
        }
    }

    [Theory]
    [MemberData(nameof(DeliveredFixtures))]
    public void Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel(PdfConformance claim, FixturePath path)
    {
        var d = Description(Parsed(claim, path).MetadataXml);
        var properties = d.Elements().Select(e => e.Name).ToList();

        // The construct: the properties the packet holds, all in schemas the XMP specification
        // predefines (dc, xmp, pdf) or in the identification schemas.
        Assert.Contains(Dc + "title", properties);
        Assert.Contains(Xmp + "CreateDate", properties);
        Assert.Contains(PdfNs + "Producer", properties);
        var allowed = new HashSet<XNamespace> { Dc, Xmp, PdfNs, PdfaId, PdfuaId };
        Assert.All(properties, n => Assert.Contains(n.Namespace, allowed));

        // 6.6.4 (veraPDF 6.6.4-1 to -5): part 2 and conformance A, B or U under the matching claim;
        // ISO 14289-1, 5: pdfuaid:part 1 under PDF/UA-1, with no pdfaid in that file.
        var expectedConformance = claim switch
        {
            PdfConformance.PdfA2b => "B",
            PdfConformance.PdfA2u => "U",
            PdfConformance.PdfA2a => "A",
            _ => null,
        };
        if (expectedConformance is not null)
        {
            Assert.Equal("2", d.Element(PdfaId + "part")!.Value);
            Assert.Equal(expectedConformance, d.Element(PdfaId + "conformance")!.Value);
            Assert.Null(d.Element(PdfaId + "amd"));
            Assert.Null(d.Element(PdfaId + "corr"));
            Assert.Null(d.Element(PdfuaId + "part"));
        }
        else if (claim == PdfConformance.PdfUA1)
        {
            Assert.Equal("1", d.Element(PdfuaId + "part")!.Value);
            Assert.Null(d.Element(PdfaId + "part"));
        }
        else
        {
            Assert.Null(d.Element(PdfaId + "part"));
            Assert.Null(d.Element(PdfuaId + "part"));
        }

        // No extension schema is written, so none is needed (6.6.2.3.2): every namespace declared on
        // the description is one of the above.
        Assert.DoesNotContain(d.Attributes(), a => a.Value.Contains("pdfa/ns/extension", StringComparison.Ordinal));
    }

    [Fact]
    public void Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText()
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2u, Timestamp = Pinned, DocumentId = PinnedId, Language = "de-CH" };
        doc.Info.Title = "T\u00e4st \u2013 \u2603";
        doc.Info.Author = "A. Author";
        doc.Info.Subject = "Subject \u00fc";
        doc.Info.Keywords = "one, two";
        doc.Info.Creator = "Creator";
        doc.Info.Producer = "Producer";
        doc.AddPage();
        var p = Scan.Parse(Save(doc));
        var info = p.Info!;
        var d = Description(p.MetadataXml);

        // The construct: all six Info entries, as UTF-16BE text strings with the mark (7.9.2.2).
        foreach (var key in new[] { "Title", "Author", "Subject", "Keywords", "Creator", "Producer" })
        {
            var s = Assert.IsType<Scan.PStr>(info[key]);
            Assert.Equal(0xFE, s.Bytes[0]);
            Assert.Equal(0xFF, s.Bytes[1]);
        }

        // Table 7 of ISO 19005-2, 6.6.3, read in the order the notes give.
        Assert.Equal(Decode((Scan.PStr)info["Title"]!), d.Element(Dc + "title")!.Descendants(Rdf + "li").Single().Value);
        Assert.Equal("x-default", (string?)d.Element(Dc + "title")!.Descendants(Rdf + "li").Single().Attribute(XNamespace.Xml + "lang"));
        Assert.Equal(Decode((Scan.PStr)info["Author"]!), d.Element(Dc + "creator")!.Descendants(Rdf + "li").Single().Value);
        Assert.NotNull(d.Element(Dc + "creator")!.Element(Rdf + "Seq"));
        Assert.Equal(Decode((Scan.PStr)info["Subject"]!), d.Element(Dc + "description")!.Descendants(Rdf + "li").Single().Value);
        Assert.Equal(Decode((Scan.PStr)info["Keywords"]!), d.Element(PdfNs + "Keywords")!.Value);
        Assert.Equal(Decode((Scan.PStr)info["Creator"]!), d.Element(Xmp + "CreatorTool")!.Value);
        Assert.Equal(Decode((Scan.PStr)info["Producer"]!), d.Element(PdfNs + "Producer")!.Value);

        // The Info dictionary holds no date, so CreationDate and ModDate need no counterpart; the
        // XMP dates are the pinned instant in the W3C form.
        Assert.False(info.Has("CreationDate"));
        Assert.False(info.Has("ModDate"));
        Assert.Equal("2026-11-20T12:00:00+00:00", d.Element(Xmp + "CreateDate")!.Value);
        Assert.Equal("2026-11-20T12:00:00+00:00", d.Element(Xmp + "ModifyDate")!.Value);
    }

    [Fact]
    public void Iso32000_1_14_3_TimestampSetsTheXmpDatesInThePinnedOffset()
    {
        using var doc = new LayoutDocument
        {
            Conformance = PdfConformance.PdfA2b,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(5.5)),
            DocumentId = PinnedId,
        };
        doc.Add("x");
        var p = Scan.Parse(Save(doc));
        var d = Description(p.MetadataXml);
        Assert.Equal("2026-11-20T12:00:00+05:30", d.Element(Xmp + "CreateDate")!.Value);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", d.Element(Xmp + "ModifyDate")!.Value);
    }

    [Fact]
    public void Iso32000_1_14_3_PinnedTimestampAndDocumentIdGiveIdenticalBytesOnEverySavePath()
    {
        foreach (var path in new[] { FixturePath.Layout, FixturePath.Kernel, FixturePath.ObjectStreams, FixturePath.Linearized })
        {
            foreach (var claim in new[] { PdfConformance.None, PdfConformance.PdfA2b })
            {
                var first = Build(claim, path);
                var second = Build(claim, path);

                // The construct: both pinned values are in the file; then the two saves are the same bytes.
                var p = Scan.Parse(first);
                Assert.Contains("2026-11-20T12:00:00+00:00", Packet(p));
                Assert.Contains("000102030405060708090A0B0C0D0E0F", p.Text);
                Assert.Equal(first, second);
            }
        }
    }

    [Theory]
    [MemberData(nameof(DeliveredFixtures))]
    public void Iso19005_2_6_7_4_And_Iso14289_1_7_2_CatalogLangIsALanguageTagMatchedByXmpAndByItsTrim(PdfConformance claim, FixturePath path)
    {
        var p = Parsed(claim, path);
        var lang = Assert.IsType<Scan.PStr>(p.Catalog["Lang"]);

        // veraPDF 7.2-29 pattern, which RFC 3066 and ISO 32000-1, 14.9.2.1 imply: 1 to 8 letters,
        // then groups of 1 to 8 letters or digits. The XMP dc:language holds the same tag.
        var tag = Decode(lang);
        Assert.Matches(@"^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", tag);
        Assert.Equal("en-US", tag);
        Assert.Equal(tag, Description(p.MetadataXml).Element(Dc + "language")!.Descendants(Rdf + "li").Single().Value);
    }

    [Fact]
    public void Iso19005_2_6_7_4_LanguageIsTrimmedWhereWrittenAndOmittedWhenBlank()
    {
        using var trimmed = new PdfDocument { Language = "  fr  ", Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        trimmed.AddPage();
        var p = Scan.Parse(Save(trimmed));
        Assert.Equal("fr", Decode((Scan.PStr)p.Catalog["Lang"]!));
        Assert.Equal("fr", Description(p.MetadataXml).Element(Dc + "language")!.Descendants(Rdf + "li").Single().Value);

        using var blank = new PdfDocument { Language = "   ", Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        blank.AddPage();
        var q = Scan.Parse(Save(blank));
        Assert.False(q.Catalog.Has("Lang"));
        Assert.Null(Description(q.MetadataXml).Element(Dc + "language"));
    }

    [Fact(Skip = "Gap #749 (the 3.0 refusal is #614): Document.Language is not validated as BCP 47, so a tag such as 'not a tag' is written to /Lang and fails veraPDF 7.2-29 under PDF/UA-1; the member's documentation says so.")]
    public void Iso14289_1_7_2_LanguageThatIsNotATagIsNotWrittenUnderPdfUA1()
    {
        using var doc = new PdfDocument { Language = "not a tag", Conformance = PdfConformance.PdfUA1, Timestamp = Pinned, DocumentId = PinnedId };
        doc.AddPage();
        try
        {
            var p = Scan.Parse(Save(doc));
            Assert.Matches(@"^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", Decode((Scan.PStr)p.Catalog["Lang"]!));
        }
        catch (ArgumentException)
        {
            // A refusal meets the clause too.
        }
    }

    [Fact]
    public void Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects()
    {
        var p = Parsed(PdfConformance.PdfUA1, FixturePath.Layout);
        var prefs = Assert.IsType<Scan.PDict>(p.Catalog["ViewerPreferences"]);

        // The construct: the dictionary, with DisplayDocTitle true (ISO 14289-1, 7.1); dc:title in the
        // catalog's metadata stream; MarkInfo with Marked true and no Suspects key.
        Assert.Equal(true, prefs["DisplayDocTitle"]);
        var title = Description(p.MetadataXml).Element(Dc + "title")!.Descendants(Rdf + "li").Single().Value;
        Assert.Equal("G5 audit", title);
        var markInfo = Assert.IsType<Scan.PDict>(p.Catalog["MarkInfo"]);
        Assert.Equal(true, markInfo["Marked"]);
        Assert.False(markInfo.Has("Suspects"));

        // No other claim writes DisplayDocTitle.
        foreach (var claim in new[] { PdfConformance.None, PdfConformance.PdfA2b, PdfConformance.PdfA2u, PdfConformance.PdfA2a })
            Assert.False(Parsed(claim, FixturePath.Layout).Catalog.Has("ViewerPreferences"));
    }

    [Fact(Skip = "Gap #748 (the 3.0 refusal is #474): a PDF/UA-1 claim is stamped on a Layout document with no title or no language; the file has no dc:title (ISO 14289-1, 7.1) and no /Lang (7.2), and fails veraPDF 7.1-9 and 7.2-34; Document.Language says to set it explicitly.")]
    public void Iso14289_1_7_1_PdfUA1RefusesOrCarriesATitleAndALanguage()
    {
        var doc = new LayoutDocument { Conformance = PdfConformance.PdfUA1, Timestamp = Pinned, DocumentId = PinnedId };
        doc.Add(new Paragraph("x", new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVu), FontSize = 11 }));
        try
        {
            var p = Scan.Parse(Save(doc));
            Assert.NotNull(Description(p.MetadataXml).Element(Dc + "title"));
            Assert.True(p.Catalog.Has("Lang"));
        }
        catch (InvalidOperationException)
        {
            // A refusal meets the clause too.
        }
    }

    // ── The 19005-2 rows read in full on NEN (2026-11-27): 6.6.3, 6.6.5, 6.7.4 ─────────────────

    [Fact]
    public void Iso19005_2_6_6_3_ProducerIsInInfoOnlyWhenSetAndTheXmpAlwaysNamesOne()
    {
        // Table 7 pairs Info/Producer with pdf:Producer. A writer should keep a pair that exists equal; an
        // Info key that is absent has nothing to disagree with.
        using var unset = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        unset.AddPage();
        var a = Scan.Parse(Save(unset));
        Assert.False(a.Info!.Has("Producer"));
        Assert.Equal("VellumPdf", Description(a.MetadataXml).Element(PdfNs + "Producer")!.Value);

        using var set = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        set.Info.Producer = "Audit producer";
        set.AddPage();
        var b = Scan.Parse(Save(set));
        Assert.Equal("Audit producer", Decode((Scan.PStr)b.Info!["Producer"]!));
        Assert.Equal("Audit producer", Description(b.MetadataXml).Element(PdfNs + "Producer")!.Value);
    }

    [Fact(Skip = "Gap #747: an Info string and its XMP property differ for a carriage return (the XMP parser reads CR as LF) and for U+0001 (kept in Info, dropped from the XMP), against the should of ISO 19005-2, 6.6.3 and Table 7.")]
    public void Iso19005_2_6_6_3_InfoAndXmpCarryTheSameTextForEveryStringTheWriterAccepts()
    {
        foreach (var title in new[] { "a\r\nb\rc", "a\u0001b" })
        {
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
            doc.Info.Title = title;
            doc.AddPage();
            var p = Scan.Parse(Save(doc));
            var xmp = Description(p.MetadataXml).Element(Dc + "title")!.Descendants(Rdf + "li").Single().Value;
            Assert.Equal(Decode((Scan.PStr)p.Info!["Title"]!), xmp);
        }
    }

    [Theory]
    [MemberData(nameof(DeliveredFixtures))]
    public void Iso19005_2_6_6_5_NoSavePathWritesXmpHistoryOrIdentifiers(PdfConformance claim, FixturePath path)
    {
        // 6.6.5 says the changing half of /ID shall change if an xmpMM:History entry is added. No path adds one:
        // the packet has no xmpMM namespace, no History, no InstanceID and no DocumentID, and the two /ID
        // strings are equal (a file written once).
        var p = Parsed(claim, path);
        var text = Packet(p);
        Assert.DoesNotContain("xmpMM", text);
        Assert.DoesNotContain("History", text);
        Assert.DoesNotContain("InstanceID", text);
        Assert.DoesNotContain("DocumentID", text);
        var id = (List<object?>)p.Trailer["ID"]!;
        Assert.Equal(((Scan.PStr)id[0]!).Bytes, ((Scan.PStr)id[1]!).Bytes);
    }

    [Fact]
    public void Iso19005_2_6_7_4_StructureElementLangIsALanguageIdentifierUnderPdfA2a()
    {
        var doc = new LayoutDocument { Conformance = PdfConformance.PdfA2a, Timestamp = Pinned, DocumentId = PinnedId, Language = "en-US" };
        doc.Info.Title = "G5 audit";
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVu), FontSize = 11 };
        doc.Add(new Paragraph("Bonjour", style) { Language = "fr-CA" });
        var p = Scan.Parse(Save(doc));

        // The construct: a structure element with its own /Lang, besides the catalog's.
        var tags = p.Objects.Where(o => o.Dict?.Name("Type") == "StructElem" && o.Dict.Has("Lang"))
            .Select(o => Decode((Scan.PStr)o.Dict!["Lang"]!)).ToList();
        Assert.Contains("fr-CA", tags);

        // 6.7.4: every /Lang, in the catalog and on elements, is a language identifier (the pattern of
        // ISO 32000-1, 14.9.2.1, as veraPDF 7.2-29 reads it).
        tags.Add(Decode((Scan.PStr)p.Catalog["Lang"]!));
        Assert.All(tags, t => Assert.Matches(@"^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", t));
    }

    [Fact(Skip = "Gap #749 (the 3.0 refusal is #614): Document.Language is not validated as BCP 47, so a tag such as 'not a tag' is written to the catalog /Lang under PDF/A-2a, where 6.7.4 makes a malformed value a shall; the member's documentation says so.")]
    public void Iso19005_2_6_7_4_LanguageThatIsNotATagIsNotWrittenUnderPdfA2a()
    {
        using var doc = new PdfDocument { Language = "not a tag", Conformance = PdfConformance.PdfA2a, Timestamp = Pinned, DocumentId = PinnedId };
        doc.AddPage();
        try
        {
            var p = Scan.Parse(Save(doc));
            Assert.Matches(@"^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", Decode((Scan.PStr)p.Catalog["Lang"]!));
        }
        catch (ArgumentException)
        {
            // A refusal meets the clause too.
        }
    }

    // ── The catalog the Save and the signing path share (#635, #626) ───────────────────────────

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Iso32000_1_7_7_2_SaveAndSigningPathWriteTheSameCatalogEntriesBesidesAcroForm(PdfConformance claim)
    {
        var saved = Parsed(claim, FixturePath.Kernel).Catalog;
        var prepared = Parsed(claim, FixturePath.Prepared).Catalog;
        var signed = Parsed(claim, FixturePath.Signed).Catalog;

        // The construct: the signing path's catalog has /AcroForm and Save's has none (no form field).
        Assert.True(prepared.Has("AcroForm"));
        Assert.True(signed.Has("AcroForm"));
        Assert.False(saved.Has("AcroForm"));

        static List<string> Keys(Scan.PDict d) => [.. d.Entries.Select(e => e.Key).Where(k => k != "AcroForm").OrderBy(k => k, StringComparer.Ordinal)];
        Assert.Equal(Keys(saved), Keys(prepared));
        Assert.Equal(Keys(saved), Keys(signed));

        // Value level, on the Save path: Type, Pages, Metadata, and what each claim adds.
        Assert.Equal("Catalog", saved.Name("Type"));
        Assert.IsType<Scan.PRef>(saved["Pages"]);
        Assert.IsType<Scan.PRef>(saved["Metadata"]);
        Assert.Equal(claim != PdfConformance.None, saved.Has("OutputIntents"));
        Assert.Equal(claim != PdfConformance.None, saved.Has("MarkInfo"));
        Assert.Equal(claim == PdfConformance.PdfUA1, saved.Has("ViewerPreferences"));
        Assert.Equal(claim is PdfConformance.PdfA2a or PdfConformance.PdfUA1, saved.Has("StructTreeRoot"));
        Assert.Equal(claim != PdfConformance.None ? 1 : 0, claim == PdfConformance.None ? 0 : ((List<object?>)saved["OutputIntents"]!).Count);
    }

    [Theory]
    [MemberData(nameof(PdfAClaimData))]
    public void Iso19005_2_6_2_3_OutputIntentIsGtsPdfa1WithAnIccProfileOnEverySavePath(PdfConformance claim)
    {
        foreach (var path in new[] { FixturePath.Kernel, FixturePath.Prepared, FixturePath.Layout, FixturePath.ObjectStreams })
        {
            var p = Parsed(claim, path);
            var intents = (List<object?>)p.Catalog["OutputIntents"]!;
            var intent = (Scan.PDict)p.Resolve(intents.Single())!;
            Assert.Equal("OutputIntent", intent.Name("Type"));
            Assert.Equal("GTS_PDFA1", intent.Name("S"));
            var profile = p.Get(((Scan.PRef)intent["DestOutputProfile"]!).Number);
            Assert.Equal(3, profile.Dict!.Int("N"));
            Assert.Equal("acsp", Encoding.ASCII.GetString(Scan.Inflate(profile.StreamData!), 36, 4));
        }
    }

    // ── Strings: ISO 32000-1, 7.3.4.2 and 7.9.2; names: 7.3.5 ─────────────────────────────────

    [Fact]
    public void Iso32000_1_7_3_4_2_LiteralStringsEscapeParenthesesBackslashAndLineEnds()
    {
        const string title = "line1\nline2\rline3 (par) \\back\r\nend";
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        doc.Info.Title = title;
        doc.AddPage();
        var p = Scan.Parse(Save(doc));
        var infoObject = p.Get(((Scan.PRef)p.Trailer["Info"]!).Number);

        // The construct: the title string in the bytes. A line feed or carriage return is written as an
        // escape, and a parenthesis or backslash with a backslash before it, so the string holds no
        // raw end-of-line marker for a reader to turn into a line feed (#625, #511).
        var raw = Regex.Match(infoObject.Raw, @"/Title \((?<v>(?:[^\\()]|\\.)*)\)", RegexOptions.Singleline).Groups["v"].Value;
        Assert.DoesNotContain('\n', raw);
        Assert.DoesNotContain('\r', raw);
        Assert.Contains("\\n", raw);
        Assert.Contains("\\r", raw);
        Assert.Contains("\\(", raw);
        Assert.Contains("\\)", raw);
        Assert.Contains("\\\\", raw);
        Assert.Equal(title, Decode((Scan.PStr)p.Info!["Title"]!));
    }

    [Fact]
    public void Iso32000_1_7_9_2_TextStringsAreUtf16BeWithTheMarkAndSignatureTextIsPdfDocEncodedWhenItCan()
    {
        using var doc = NewKernel(PdfConformance.PdfA2b);
        var bytes = doc.PrepareForSigning(new SignaturePlaceholderOptions
        {
            SigningTime = Pinned,
            Reason = "Caf\u00e9",
            Location = "\u65e5\u672c",
            SignerName = "A\uD800B",
            ContactInfo = "A\u00A0B",
        });
        var p = Scan.Parse(bytes);
        var sig = p.Objects.Single(o => o.Dict?.Name("Type") == "Sig").Dict!;

        // 7.9.2.2: a text string is PDFDocEncoding or UTF-16BE with a mark. Characters that are the
        // same in both are one byte each (Latin-1 range here); the rest take the mark (#625).
        Assert.Equal([0x43, 0x61, 0x66, 0xE9], ((Scan.PStr)sig["Reason"]!).Bytes);
        var location = ((Scan.PStr)sig["Location"]!).Bytes;
        Assert.Equal([0xFE, 0xFF, 0x65, 0xE5, 0x67, 0x2C], location);
        // U+00A0 is a euro sign in PDFDocEncoding, so it takes the UTF-16BE form.
        Assert.Equal([0xFE, 0xFF, 0x00, 0x41, 0x00, 0xA0, 0x00, 0x42], ((Scan.PStr)sig["ContactInfo"]!).Bytes);
        // A lone surrogate becomes U+FFFD, not an ill-formed UTF-16BE pair.
        Assert.Equal("A\uFFFDB", Decode((Scan.PStr)sig["Name"]!));
    }

    [Fact(Skip = "Gap #670: PdfLiteralString.FromUnicode copies a lone surrogate as it stands, so an Info, Lang, outline or page-label string with one is ill-formed UTF-16BE (ISO 32000-1, 7.9.2.2).")]
    public void Iso32000_1_7_9_2_2_InfoStringsWithALoneSurrogateAreWellFormedUtf16()
    {
        using var doc = new PdfDocument { Timestamp = Pinned, DocumentId = PinnedId };
        doc.Info.Title = "a\uD800b";
        doc.AddPage();
        var p = Scan.Parse(Save(doc));
        var title = (Scan.PStr)p.Info!["Title"]!;
        _ = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true).GetString(title.Bytes, 2, title.Bytes.Length - 2);
    }

    [Fact]
    public void Iso32000_1_7_3_5_NamesEscapeNumberSignSpaceAndDelimitersAndReadBackTheSame()
    {
        const string name = "A#B C/D(E)%F[G]<H>{I}\u00e9";
        using var ms = new MemoryStream();
        new PdfName(name).WriteTo(new PdfWriter(ms));
        var written = Encoding.ASCII.GetString(ms.ToArray());

        // 7.3.5 a to c: a number sign, white space, a delimiter and anything outside ! to ~ are written
        // as # and two hex digits after the solidus.
        Assert.Equal("/A#23B#20C#2FD#28E#29#25F#5BG#5D#3CH#3E#7BI#7D#E9", written);
        var lexer = new Scan.Lexer(ms.ToArray(), 0);
        var read = Assert.IsType<Scan.PName>(lexer.ReadToken());
        Assert.Equal(name, read.Text);

        // The content stream writes names through its own method; it makes the same bytes.
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        new PdfCanvas(page).DoXObject(name).Finish();
        Assert.Equal("/A#23B#20C#2FD#28E#29#25F#5BG#5D#3CH#3E#7BI#7D#E9 Do\n", Encoding.ASCII.GetString(page.ContentBytes!));
    }

    // ── Streams: ISO 32000-1, 7.3.8.2; the empty stream (#643) ─────────────────────────────────

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Iso19005_2_6_1_7_1_AnEmptyPageContentIsTheEmptyZlibStreamWithItsLengthAndPassesTheTools(PdfConformance claim)
    {
        using var doc = new PdfDocument { Conformance = claim, Timestamp = Pinned, DocumentId = PinnedId, Language = "en" };
        doc.Info.Title = "Empty";
        doc.AddPage();
        var bytes = Save(doc);
        var p = Scan.Parse(bytes);
        var page = p.Objects.Single(o => o.Dict?.Name("Type") == "Page").Dict!;
        var content = p.Get(((Scan.PRef)page["Contents"]!).Number);

        // The construct: an 8-byte body, which is a complete RFC 1950 stream (header 78 9C, one final
        // fixed-Huffman block with only end-of-block, Adler-32 of no data), and a /Length of 8.
        Assert.Equal([0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01], content.StreamData);
        Assert.Equal(8, content.Dict!.Int("Length"));
        Assert.Empty(Scan.Inflate(content.StreamData!));
        Assert.Empty(p.Problems);

        Assert.Contains("No syntax or stream encoding errors found", QpdfCheck(bytes), StringComparison.Ordinal);
        using var dir = new TempDir();
        var emptyPath = dir.Path("empty.pdf");
        File.WriteAllBytes(emptyPath, bytes);
        ExternalTool.TryRun("pdftotext", [emptyPath, "-"], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        Assert.Equal(string.Empty, stdout.Trim());
        if (claim is PdfConformance.PdfA2b or PdfConformance.PdfA2u)
        {
            var (compliant, failed) = VeraPdf(Flavour(claim), bytes);
            Assert.True(compliant, string.Join(",", failed));
        }
    }

    // ── Dates: ISO 32000-2, 7.9.4 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Iso32000_2_7_9_4_SignatureDateHasEveryFieldInRangeAndNamesUtc()
    {
        var p = Parsed(PdfConformance.None, FixturePath.Signed);
        var m = Decode((Scan.PStr)p.Objects.Single(o => o.Dict?.Name("Type") == "Sig").Dict!["M"]!);

        // The construct: a date string with the "D:" prefix and every field present.
        var match = Regex.Match(m, @"^D:(\d{4})(\d\d)(\d\d)(\d\d)(\d\d)(\d\d)([+\-Z])(\d\d)'(\d\d)'?$");
        Assert.True(match.Success, m);
        Assert.Equal("2026", match.Groups[1].Value);
        Assert.Equal("11", match.Groups[2].Value);
        Assert.Equal("20", match.Groups[3].Value);
        Assert.Equal("12", match.Groups[4].Value);
        Assert.Equal("00", match.Groups[5].Value);
        Assert.Equal("00", match.Groups[6].Value);
        Assert.Equal("+", match.Groups[7].Value);
        Assert.Equal("00", match.Groups[8].Value);
        Assert.Equal("00", match.Groups[9].Value);
    }

    [Fact(Skip = "Gap #743: the signature /M date ends in an apostrophe (D:20261120120000+00'00'), where the grammar of ISO 32000-2, 7.9.4 ends at the minutes; NOTE 2 calls the apostrophe the form up to PDF 1.7, which a reader should accept, not the form a writer produces.")]
    public void Iso32000_2_7_9_4_SignatureDateEndsAtTheMinutesInAPdf20File()
    {
        var p = Parsed(PdfConformance.None, FixturePath.Signed);
        var m = Decode((Scan.PStr)p.Objects.Single(o => o.Dict?.Name("Type") == "Sig").Dict!["M"]!);
        Assert.Matches(@"^D:\d{14}[+\-Z]\d\d'\d\d$", m);
    }

    [Fact(Skip = "Gap #743: the signature /M date of a signed PDF/A-2b file ends in an apostrophe; the date grammar of ISO 32000-1, 7.9.4 (page 87 of the held copy), which ISO 19005-2 clause 5 and ISO 14289-1 6.2 bind, ends at the minutes, so the file is invalid under its claim.")]
    public void Iso32000_1_7_9_4_SignatureDateEndsAtTheMinutesInAPdfAFile()
    {
        var p = Parsed(PdfConformance.PdfA2b, FixturePath.Signed);
        Assert.StartsWith("%PDF-1.7", p.Text);
        var m = Decode((Scan.PStr)p.Objects.Single(o => o.Dict?.Name("Type") == "Sig").Dict!["M"]!);
        Assert.Matches(@"^D:\d{14}[+\-Z]\d\d'\d\d$", m);
    }

    // ── Info in a PDF 2.0 file: ISO 32000-2, 14.3.3 ─────────────────────────────────────────────

    [Fact(Skip = "Gap #325: a file that names PDF 2.0 writes Title, Author, Subject, Keywords, Creator and Producer in /Info, which ISO 32000-2, 14.3.3 deprecates in favour of the metadata stream; issue #325 (v2.6).")]
    public void Iso32000_2_14_3_3_InfoOfAPdf20FileHoldsOnlyTheTwoDates()
    {
        using var doc = new PdfDocument { Timestamp = Pinned, DocumentId = PinnedId };
        doc.Info.Title = "t";
        doc.Info.Producer = "p";
        doc.AddPage();
        var p = Scan.Parse(Save(doc));
        Assert.StartsWith("%PDF-2.0", p.Text);
        Assert.All(p.Info!.Entries.Select(e => e.Key), k => Assert.Contains(k, new[] { "CreationDate", "ModDate" }));
    }

    [Fact]
    public void Iso32000_2_14_3_3_InfoValuesAreTextStringsAndNoDeprecatedKeyIsWrittenUnset()
    {
        using var doc = new PdfDocument { Timestamp = Pinned, DocumentId = PinnedId };
        doc.AddPage();
        var p = Scan.Parse(Save(doc));

        // With nothing set the Info dictionary is empty (and still referenced from the trailer).
        Assert.NotNull(p.Info);
        Assert.Empty(p.Info!.Entries);
        Assert.IsType<Scan.PRef>(p.Trailer["Info"]);
    }

    // ── Trees: ISO 32000-1, 7.9.6 and 7.9.7 (#622, #627) ───────────────────────────────────────

    [Fact]
    public void Iso32000_1_7_9_6_NameTreeKeysAreSortedEveryNodeHasItsLimitsAndTheRootHasNone()
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        var page = doc.AddPage();
        var names = Enumerable.Range(0, 400).Select(i => $"dest-{(i * 7919) % 400:D4}").ToList();
        foreach (var n in names)
            doc.AddNamedDestination(n, page, 0, 100);
        var p = Scan.Parse(Save(doc));

        var root = (Scan.PDict)((Scan.PDict)p.Catalog["Names"]!)["Dests"]!;

        // The construct: a root with /Kids, so the tree has more than one level.
        Assert.True(root.Has("Kids"));
        Assert.False(root.Has("Limits"));
        Assert.False(root.Has("Names"));

        var leafKeys = new List<string>();
        void Walk(Scan.PDict node, bool isRoot)
        {
            if (node.Has("Kids"))
            {
                var lows = new List<string>();
                var highs = new List<string>();
                foreach (var kid in node.Array("Kids")!.Select(p.Resolve).Cast<Scan.PDict>())
                {
                    Walk(kid, false);
                    var limits = kid.Array("Limits")!;
                    lows.Add(Decode((Scan.PStr)limits[0]!));
                    highs.Add(Decode((Scan.PStr)limits[1]!));
                }
                Assert.Equal(lows.OrderBy(x => x, StringComparer.Ordinal), lows);
                if (!isRoot)
                {
                    var limits = node.Array("Limits")!;
                    Assert.Equal(lows[0], Decode((Scan.PStr)limits[0]!));
                    Assert.Equal(highs[^1], Decode((Scan.PStr)limits[1]!));
                }
            }
            else
            {
                var pairs = node.Array("Names")!;
                Assert.Equal(0, pairs.Count % 2);
                var keys = Enumerable.Range(0, pairs.Count / 2).Select(i => Decode((Scan.PStr)pairs[2 * i]!)).ToList();
                var limits = node.Array("Limits")!;
                Assert.Equal(keys[0], Decode((Scan.PStr)limits[0]!));
                Assert.Equal(keys[^1], Decode((Scan.PStr)limits[1]!));
                leafKeys.AddRange(keys);
            }
        }
        Walk(root, true);

        Assert.Equal(400, leafKeys.Count);
        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal), leafKeys);
        Assert.Equal(400, leafKeys.Distinct().Count());
    }

    [Fact]
    public void Iso32000_1_7_9_7_NumberTreeKeysAreAscendingIntegersFromTheFirstPage()
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b, Timestamp = Pinned, DocumentId = PinnedId };
        for (var i = 0; i < 12; i++)
            doc.AddPage();
        doc.AddPageLabelRange(8, PdfPageLabelStyle.UpperRoman);
        doc.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman, prefix: "front-");
        doc.AddPageLabelRange(4, PdfPageLabelStyle.Decimal, firstNumber: 1);
        var p = Scan.Parse(Save(doc));

        var labels = (Scan.PDict)p.Catalog["PageLabels"]!;
        var nums = labels.Array("Nums")!;

        // The construct: three key/value pairs; 12.4.2 asks for a range at page 0, and 7.9.7 for ascending keys.
        Assert.Equal(6, nums.Count);
        var keys = Enumerable.Range(0, 3).Select(i => int.Parse(((Scan.PNum)nums[2 * i]!).Raw, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal([0, 4, 8], keys);
        Assert.All(Enumerable.Range(0, 3), i => Assert.IsType<Scan.PDict>(nums[2 * i + 1]));
        Assert.Equal("r", ((Scan.PDict)nums[1]!).Name("S"));
        Assert.Equal("front-", Decode((Scan.PStr)((Scan.PDict)nums[1]!)["P"]!));
    }

    // ── Oracles over every claimed fixture ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(OracleFixtures))]
    public void Oracles_AcceptEveryClaimedFixture(PdfConformance claim, FixturePath path)
    {
        var bytes = Fixture(claim, path);
        var p = Parsed(claim, path);

        // Each path's construct is in the bytes before the verdict is read.
        switch (path)
        {
            case FixturePath.ObjectStreams:
                Assert.Equal(1, p.ObjStmCount);
                Assert.Empty(p.Xrefs);
                break;
            case FixturePath.Linearized:
                Assert.Contains("/Linearized 1", p.Text);
                break;
            case FixturePath.Signed:
                Assert.Contains("/Type /Sig", p.Text);
                Assert.DoesNotContain("9999999999", p.Text);
                break;
            case FixturePath.Layout:
                Assert.Equal(claim is PdfConformance.PdfA2a or PdfConformance.PdfUA1, p.Catalog.Has("StructTreeRoot"));
                break;
            default:
                Assert.Single(p.Xrefs);
                break;
        }

        var (compliant, failed) = VeraPdf(Flavour(claim), bytes);
        Assert.True(compliant, $"veraPDF {Flavour(claim)} failed {string.Join(",", failed)}");
        Assert.Empty(failed);
        var report = QpdfCheck(bytes);
        Assert.Contains("No syntax or stream encoding errors found", report, StringComparison.Ordinal);
        if (path == FixturePath.Linearized)
            Assert.Contains("File is linearized", report, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Oracles_QpdfAcceptsEveryFixtureUnderEveryClaim(PdfConformance claim, FixturePath path)
    {
        var report = QpdfCheck(Fixture(claim, path));
        Assert.Contains("No syntax or stream encoding errors found", report, StringComparison.Ordinal);
        Assert.Contains($"PDF Version: {(claim == PdfConformance.None ? "2.0" : "1.7")}", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Iso32000_1_C_2_ClassicXrefRefusesAnOffsetOfElevenDigitsAndAcceptsTenNines()
    {
        // Annex C.2: a classic table has ten digits for an offset, so a file is limited to 10^10 bytes.
        // The writer's own refusal is the boundary, measured here without writing ten gigabytes.
        var accepted = new CrossReferenceBuilder();
        accepted.ReserveObjectNumber(9_999_999_999L);
        using var ok = new MemoryStream();
        accepted.WriteXrefAndTrailer(new PdfWriter(ok), new PdfIndirectReference(1), null);
        Assert.Contains("9999999999 00000 n\r\n", Encoding.ASCII.GetString(ok.ToArray()), StringComparison.Ordinal);

        var refused = new CrossReferenceBuilder();
        refused.ReserveObjectNumber(10_000_000_000L);
        Assert.Throws<NotSupportedException>(() => refused.WriteXrefAndTrailer(new PdfWriter(new MemoryStream()), new PdfIndirectReference(1), null));
    }

    // ── helpers for the name and image fixtures ────────────────────────────────────────────────

    private static void DrawHelloIn(PdfDocument doc, byte[] font)
    {
        var handle = doc.UseTrueTypeFont(font);
        var page = doc.AddPage();
        doc.RegisterEmbeddedFontUsage(page, handle);
        var glyphs = new ushort[5];
        var count = handle.GetGlyphIds("Hello", glyphs);
        new PdfCanvas(page)
            .BeginArtifactMarkedContent()
            .BeginText()
            .SetFontByName(handle.ResourceName, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowGlyphs(glyphs.AsSpan(0, count))
            .EndText()
            .EndMarkedContent()
            .Finish();
    }

    // A name table of one record: the PostScript name (name ID 6), platform 3, encoding 1. The
    // font program is the in-tree one with its name table replaced, so no font file is added.
    private static byte[] PostScriptNameTable(string postScriptName)
    {
        var s = Encoding.BigEndianUnicode.GetBytes(postScriptName);
        using var ms = new MemoryStream();
        void U16(int v)
        {
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }
        U16(0);
        U16(1);
        U16(18);
        U16(3);
        U16(1);
        U16(0x409);
        U16(6);
        U16(s.Length);
        U16(0);
        ms.Write(s);
        return ms.ToArray();
    }

    // A 1x1 RGB PNG; the loader does not check chunk checksums, so they are zero.
    private static byte[] TinyPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            ms.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            ms.Write(Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]);
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(new byte[] { 0, 1, 2, 3 });
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
        return ms.ToArray();
    }
}
