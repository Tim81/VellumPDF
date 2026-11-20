// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <see cref="Document.Timestamp"/> and <see cref="Document.DocumentId"/>: what pinning them
/// makes reproducible, and the limits the XML documentation states. Nothing here reads a wall
/// clock: the pin replaces the clock, so equality of two builds proves the pin and a one-second
/// step proves it is the pin that decides.
/// </summary>
public sealed class ReproducibleOutputTests
{
    private static readonly DateTimeOffset Pinned = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    private static readonly string Regular = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");

    private static readonly Regex TrailerId = new(
        @"/ID\s*\[\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*\]", RegexOptions.CultureInvariant);

    // The normalised hash of StandardDocument(), recorded from the first run and checked by every
    // later one, so a second process, and CI on Linux, must give the same bytes. It is normalised
    // (see Normalise) because the compressed bytes depend on the runtime's compressor, which the
    // property's documentation says separately. One value serves net10.0 and net11.0.
    private const string StandardDocumentNormalisedSha256 = "8BCDADDFA5FE56302A8CE0161E467A9663E71F58E9B15FE8BB8722D709DDB507";

    // ── Documents under test ──────────────────────────────────────────────────

    private static Document StandardDocument(DateTimeOffset? timestamp, byte[]? id = null)
    {
        var doc = new Document { Timestamp = timestamp, DocumentId = id };
        doc.Info.Title = "Reproducible";
        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        for (var i = 0; i < 3; i++)
            doc.Add(new Paragraph($"Paragraph {i + 1}: the quick brown fox jumps over the lazy dog.", style));
        return doc;
    }

    /// <summary>
    /// PDF/A-2u, tagged, with a language, an embedded font, three headings (outline entries) and a
    /// footer with a page count, which makes the layout run twice.
    /// </summary>
    private static Document RichDocument(DateTimeOffset? timestamp, byte[]? id, bool objectStreams)
    {
        var doc = new Document
        {
            Timestamp = timestamp,
            DocumentId = id,
            Conformance = PdfConformance.PdfA2u,
            Tagged = true,
            Language = "nl-NL",
            UseObjectStreams = objectStreams,
        };
        doc.Info.Title = "Rich reproducible";
        var handle = doc.LoadTrueTypeFont(Regular);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.SetFooter("Pagina {page} van {pages}", style);
        for (var i = 1; i <= 3; i++)
        {
            doc.Add(new Heading($"Hoofdstuk {i}", style));
            doc.Add(new Paragraph($"Tekst van hoofdstuk {i}: de snelle bruine vos springt over de luie hond.", style));
        }

        return doc;
    }

    private static byte[] Save(Document doc)
    {
        using (doc)
        {
            var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Ascii(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static (string First, string Second) TrailerIds(byte[] pdf)
    {
        var matches = TrailerId.Matches(Ascii(pdf));
        Assert.NotEmpty(matches);
        var last = matches[^1];
        return (last.Groups[1].Value, last.Groups[2].Value);
    }

    // ── Same calls, same pin, same bytes ──────────────────────────────────────

    [Fact]
    public void Standard14_pinnedTimestamp_twoDocumentsGiveSameBytes()
    {
        var first = Save(StandardDocument(Pinned));
        var second = Save(StandardDocument(Pinned));

        Assert.Equal(Sha256(first), Sha256(second));
    }

    [Fact]
    public void Standard14_pinnedTimestampAndId_twoDocumentsGiveSameBytes()
    {
        var first = Save(StandardDocument(Pinned, PinnedId));
        var second = Save(StandardDocument(Pinned, PinnedId));

        Assert.Equal(Sha256(first), Sha256(second));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Rich_pinnedTimestamp_twoDocumentsGiveSameBytes(bool objectStreams, bool pinId)
    {
        var id = pinId ? PinnedId : null;
        var first = Save(RichDocument(Pinned, id, objectStreams));
        var second = Save(RichDocument(Pinned, id, objectStreams));

        Assert.Equal(Sha256(first), Sha256(second));
        // The artefact must contain the change before an oracle's verdict means anything.
        var (a, b) = TrailerIds(first);
        Assert.Equal(a, b);
        if (pinId)
            Assert.Equal(Convert.ToHexString(PinnedId), a, ignoreCase: true);
    }

    [Fact]
    public void Standard14_pinnedTimestamp_normalisedHashMatchesTheRecordedValue()
    {
        var actual = Sha256(Encoding.Latin1.GetBytes(Normalise(Save(StandardDocument(Pinned)))));

        Assert.True(StandardDocumentNormalisedSha256 == actual, $"computed {actual}");
    }

    // ── The pin is what decides ───────────────────────────────────────────────

    [Fact]
    public void Timestamp_aSecondLater_changesTheBytesAndTheXmpDates()
    {
        var a = Save(StandardDocument(Pinned));
        var b = Save(StandardDocument(Pinned.AddSeconds(1)));

        Assert.NotEqual(Sha256(a), Sha256(b));
        Assert.Contains("<xmp:CreateDate>2026-11-20T12:00:00+00:00</xmp:CreateDate>", Ascii(a), StringComparison.Ordinal);
        Assert.Contains("<xmp:CreateDate>2026-11-20T12:00:01+00:00</xmp:CreateDate>", Ascii(b), StringComparison.Ordinal);
        Assert.NotEqual(TrailerIds(a).First, TrailerIds(b).First);
    }

    [Fact]
    public void Timestamp_null_writesTheSaveTimeNotThePin()
    {
        var text = Ascii(Save(StandardDocument(null)));

        Assert.DoesNotContain("2026-11-20T12:00:00", text, StringComparison.Ordinal);
        Assert.Matches(@"<xmp:CreateDate>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d</xmp:CreateDate>", text);
    }

    [Fact]
    public void Timestamp_xmpDatesAreWrittenExactly()
    {
        var text = Ascii(Save(StandardDocument(Pinned)));

        Assert.Contains("<xmp:CreateDate>2026-11-20T12:00:00+00:00</xmp:CreateDate>", text, StringComparison.Ordinal);
        Assert.Contains("<xmp:ModifyDate>2026-11-20T12:00:00+00:00</xmp:ModifyDate>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamp_fractionIsDroppedFromXmpAndOffsetIsKept()
    {
        var instant = new DateTimeOffset(2026, 11, 20, 17, 30, 15, 987, new TimeSpan(5, 30, 0));
        var text = Ascii(Save(StandardDocument(instant)));

        Assert.Contains("<xmp:CreateDate>2026-11-20T17:30:15+05:30</xmp:CreateDate>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamp_sameInstantInTwoOffsets_givesOneIdAndTwoXmpDates()
    {
        var utc = Save(StandardDocument(Pinned));
        var offset = Save(StandardDocument(Pinned.ToOffset(new TimeSpan(2, 0, 0))));

        Assert.Equal(TrailerIds(utc).First, TrailerIds(offset).First);
        Assert.Contains("2026-11-20T14:00:00+02:00", Ascii(offset), StringComparison.Ordinal);
        Assert.DoesNotContain("2026-11-20T14:00:00+02:00", Ascii(utc), StringComparison.Ordinal);
    }

    // ── The file identifier ───────────────────────────────────────────────────

    [Fact]
    public void DocumentId_pinned_isWrittenAsBothTrailerStrings()
    {
        var (first, second) = TrailerIds(Save(StandardDocument(Pinned, PinnedId)));

        Assert.Equal("000102030405060708090A0B0C0D0E0F", first, ignoreCase: true);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(null, "VellumPdf")]
    [InlineData("P", "P")]
    public void DocumentId_derived_isTheMd5OfTitleProducerPageCountAndMilliseconds(string? producer, string hashedProducer)
    {
        var doc = StandardDocument(Pinned);
        doc.Info.Producer = producer;
        var bytes = Save(doc);

        var expected = MD5.HashData(Encoding.UTF8.GetBytes(
            $"Reproducible|{hashedProducer}|1|{Pinned.ToUnixTimeMilliseconds()}"));
        Assert.Equal(Convert.ToHexString(expected), TrailerIds(bytes).First, ignoreCase: true);
    }

    [Fact]
    public void DocumentId_setToNullAfterAPin_derivesTheIdAgain()
    {
        var derived = TrailerIds(Save(StandardDocument(Pinned))).First;
        var doc = StandardDocument(Pinned, PinnedId);
        doc.DocumentId = null;

        Assert.Equal(derived, TrailerIds(Save(doc)).First);
    }

    [Fact]
    public void DocumentId_isCopiedOnGetAndOnSet()
    {
        var supplied = (byte[])PinnedId.Clone();
        using var doc = new Document { DocumentId = supplied };
        supplied[0] = 0xFF;
        var read = doc.DocumentId!;
        read[1] = 0xFF;

        Assert.Equal(PinnedId, doc.DocumentId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void DocumentId_notSixteenBytes_isRefusedBySetterNamingTheLength(int length)
    {
        using var doc = new Document();

        var ex = Assert.Throws<ArgumentException>(() => doc.DocumentId = new byte[length]);

        Assert.Equal("value", ex.ParamName);
        Assert.Contains($"got {length}", ex.Message, StringComparison.Ordinal);
        Assert.Null(doc.DocumentId);
    }

    [Fact]
    public void DocumentId_allZeroSixteenBytes_isAccepted()
    {
        var (first, _) = TrailerIds(Save(StandardDocument(Pinned, new byte[16])));

        Assert.Equal(new string('0', 32), first);
    }

    // ── Save paths, retry, and the documented limits ──────────────────────────

    [Fact]
    public async Task SavePaths_stream_asyncStream_asyncPath_giveSameBytes()
    {
        var sync = Save(StandardDocument(Pinned, PinnedId));

        var ms = new MemoryStream();
        using (var doc = StandardDocument(Pinned, PinnedId))
            await doc.SaveAsync(ms, TestContext.Current.CancellationToken);

        var path = Path.GetTempFileName();
        try
        {
            using (var doc = StandardDocument(Pinned, PinnedId))
                await doc.SaveAsync(path, TestContext.Current.CancellationToken);
            var viaPath = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(Sha256(sync), Sha256(ms.ToArray()));
            Assert.Equal(Sha256(sync), Sha256(viaPath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Retry_afterAPreconditionRefusal_writesTheValueSetBeforeTheRetry()
    {
        using var doc = StandardDocument(null);
        Assert.Throws<ArgumentNullException>(() => doc.Save((Stream)null!));

        doc.Timestamp = Pinned;
        doc.DocumentId = PinnedId;
        var ms = new MemoryStream();
        doc.Save(ms);

        var text = Ascii(ms.ToArray());
        Assert.Contains("<xmp:CreateDate>2026-11-20T12:00:00+00:00</xmp:CreateDate>", text, StringComparison.Ordinal);
        Assert.Equal("000102030405060708090A0B0C0D0E0F", TrailerIds(ms.ToArray()).First, ignoreCase: true);
    }

    [Fact]
    public void Encrypted_withPins_stillDiffersBetweenSaves()
    {
        byte[] Build()
        {
            var doc = StandardDocument(Pinned, PinnedId);
            doc.Encrypt(new PdfEncryptionSettings { UserPassword = "user", OwnerPassword = "owner" });
            return Save(doc);
        }

        Assert.NotEqual(Sha256(Build()), Sha256(Build()));
    }

    [Fact]
    public void PrepareForSigning_withPinsAndSigningTime_givesSamePlaceholderBytes()
    {
        byte[] Prepare()
        {
            using var doc = StandardDocument(Pinned, PinnedId);
            return doc.PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = Pinned });
        }

        Assert.Equal(Sha256(Prepare()), Sha256(Prepare()));
    }

    [Fact]
    public void Timestamp_extremeValues_saveWithWellFormedXmpDates()
    {
        Assert.Contains("<xmp:CreateDate>0001-01-01T00:00:00+00:00</xmp:CreateDate>",
            Ascii(Save(StandardDocument(DateTimeOffset.MinValue))), StringComparison.Ordinal);
        Assert.Contains("<xmp:CreateDate>9999-12-31T23:59:59+00:00</xmp:CreateDate>",
            Ascii(Save(StandardDocument(DateTimeOffset.MaxValue))), StringComparison.Ordinal);
    }

    // ── Oracles ───────────────────────────────────────────────────────────────

    [Fact]
    public void Rich_pinned_qpdfCheckPasses()
    {
        var path = WriteTemp(Save(RichDocument(Pinned, PinnedId, objectStreams: false)));
        try
        {
            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);

            Assert.False(timedOut, "qpdf --check timed out, or its output could not be fully captured.");
            Assert.True(exit == 0, $"qpdf --check failed (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");
            Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rich_pinned_veraPdf2uReportsCompliant(bool objectStreams)
    {
        var bytes = Save(RichDocument(Pinned, PinnedId, objectStreams));
        // The file under test must carry the pinned identifier, or the verdict says nothing
        // about it.
        Assert.Equal("000102030405060708090A0B0C0D0E0F", TrailerIds(bytes).First, ignoreCase: true);
        AssertVeraPdfCompliant(bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Timestamp_extremeValues_veraPdf2uVerdict(bool max)
    {
        var instant = max ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
        AssertVeraPdfCompliant(Save(RichDocument(instant, null, objectStreams: false)));
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vellum-repro-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertVeraPdfCompliant(byte[] bytes)
    {
        var path = WriteTemp(bytes);
        try
        {
            ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
                out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);

            Assert.False(timedOut, $"veraPDF timed out.\nstderr:\n{stderr}");
            Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
            Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal),
                $"veraPDF 2u validation failed (exit {exit}).\n{report}\n{stderr}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Normalisation ─────────────────────────────────────────────────────────

    // An object whose dictionary is followed by a stream. The same rule as
    // CatalogGoldenTests.Normalise in VellumPdf.Kernel.Tests (#626): inflate every Flate stream and
    // blank the xref offsets, so the compressor of the runtime drops out of the comparison.
    private static readonly Regex StreamObject = new(
        @"(?<head>\d+ 0 obj\n)(?<dict><<(?:(?!\nstream\n|endobj).)*?>>)(?<mid>\nstream\n)(?<data>.*?)(?<tail>\nendstream)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string Normalise(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        text = StreamObject.Replace(text, m =>
        {
            var dictionary = m.Groups["dict"].Value;
            if (!dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
                return m.Value;
            var inflated = Inflate(Encoding.Latin1.GetBytes(m.Groups["data"].Value));
            dictionary = Regex.Replace(dictionary, @"/Length \d+", "/Length " + inflated.Length);
            dictionary = dictionary.Replace("/Filter /FlateDecode", "/Filter /FlateDecode (inflated)", StringComparison.Ordinal);
            return m.Groups["head"].Value + dictionary + m.Groups["mid"].Value + inflated + m.Groups["tail"].Value;
        });
        text = Regex.Replace(text, @"\d{10} (\d{5}) ([nf])", "# $1 $2");
        text = Regex.Replace(text, @"startxref\n\d+", "startxref\n#");
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
}
