// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Golden / snapshot tests using Verify.XunitV3.
///
/// <para>
/// The kernel-API test mirrors <c>VellumPdf.Kernel.Tests.GoldenTests</c> exactly: pinned
/// Timestamp/DocumentId, a double-build <c>SequenceEqual</c> determinism assertion, then a
/// raw-byte PDF snapshot. The flow-layout (<c>VellumPdf.Layout.Document</c>) test uses a
/// structural projection instead — that type does not expose Timestamp/DocumentId pins
/// (confirmed by <c>VellumPdf.Layout.Tests.GoldenTests</c>' own comment), so raw output there
/// is not byte-reproducible across runs.
/// </para>
/// </summary>
public sealed class GoldenTests
{
    private static readonly DateTimeOffset PinnedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    // ── 1. AllSixSymbologies_kernelApi_rawBytes ──────────────────────────────

    [Fact]
    public async Task AllSixSymbologies_kernelApi_rawBytes()
    {
        var b1 = BuildAllSymbologiesDoc();
        var b2 = BuildAllSymbologiesDoc();
        Assert.True(b1.SequenceEqual(b2), "All-symbologies barcode page must be byte-identical across two builds");

        await Verify(new MemoryStream(b1), "pdf");
    }

    private static byte[] BuildAllSymbologiesDoc()
    {
        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
        };
        doc.Info.Title = "GoldenAllSymbologies";

        var page = doc.AddPage(PageSize.A4);
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);

        canvas.DrawBarcode(new QrCode("https://example.com/vellumpdf") { ModuleSize = 3 }, 50, 680);
        canvas.DrawBarcode(new MicroQrCode("12345") { ModuleSize = 4 }, 320, 680);
        canvas.DrawBarcode(new Pdf417Barcode("VellumPdf PDF417 golden test") { ModuleSize = 1.5 }, 50, 560);
        canvas.DrawBarcode(new Code128Barcode("CODE128-GOLDEN"), 50, 400, font);
        canvas.DrawBarcode(new EanBarcode(EanSymbology.Ean13, "400638133393"), 50, 260, font);
        canvas.DrawBarcode(new Itf14Barcode("1234567890123"), 320, 260, font);

        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── 2. DocumentFlow_allSymbologies_projection ────────────────────────────

    [Fact]
    public async Task DocumentFlow_allSymbologies_projection()
    {
        using var doc = new VellumPdf.Layout.Document();
        doc.Add(new QrCode("https://example.com/vellumpdf") { ModuleSize = 3 });
        doc.Add(new Code128Barcode("CODE128-FLOW"));
        doc.Add(new EanBarcode(EanSymbology.Ean13, "400638133393"));
        doc.Add(new Itf14Barcode("1234567890123"));

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var pdfText = Encoding.Latin1.GetString(bytes);

        var countMatch = Regex.Match(pdfText, @"/Count (\d+)");
        var pageCount = countMatch.Success ? int.Parse(countMatch.Groups[1].Value) : 0;
        var objectCount = Regex.Matches(pdfText, @"\d+ 0 obj").Count;
        var hasHelvetica = pdfText.Contains("/Helvetica");

        var decompressed = PdfTestUtil.DecompressAllFlatStreams(bytes);
        var hasBT = decompressed.Contains("BT");
        var hasET = decompressed.Contains("ET");
        var hasCode128Text = decompressed.Contains("CODE128-FLOW");
        // EAN-13 HRI splits into three groups (leading digit, then two sixes either side of the
        // centre guard) rather than one contiguous string — "006381" is the left six-digit group.
        var hasEanDigits = decompressed.Contains("006381") || pdfText.Contains("006381");
        var hasItfDigits = decompressed.Contains("1234567890123") || pdfText.Contains("1234567890123");

        var projection = $"""
            PageCount: {pageCount}
            IndirectObjectCount: {objectCount}
            HasHelvetica: {hasHelvetica}
            HasBT: {hasBT}
            HasET: {hasET}
            HasCode128Text: {hasCode128Text}
            HasEanDigits: {hasEanDigits}
            HasItfDigits: {hasItfDigits}
            """;

        await Verify(projection);
    }
}
