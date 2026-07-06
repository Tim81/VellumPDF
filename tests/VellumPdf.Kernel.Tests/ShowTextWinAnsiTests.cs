// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for the WinAnsi fix: <see cref="PdfCanvas.ShowText"/> byte-level encoding,
/// <see cref="PdfCanvas.TextEncodingWarnings"/>, and the <c>/Encoding</c> entry
/// <see cref="PdfFontResource.BuildDictionary"/> adds for non-symbolic Standard-14 fonts.
/// </summary>
public sealed class ShowTextWinAnsiTests
{
    // ── ShowText: WinAnsi punctuation round-trips as its byte, not '?' ───────

    [Fact]
    public void ShowText_winAnsiPunctuation_emitsWinAnsiBytes()
    {
        var literal = BuildAndExtractTjLiteral(canvas =>
            canvas.ShowText("15°46' • en–dash em—dash"));

        Assert.Contains((byte)0xB0, literal); // ° degree
        Assert.Contains((byte)0x95, literal); // • bullet
        Assert.Contains((byte)0x96, literal); // – endash
        Assert.Contains((byte)0x97, literal); // — emdash
    }

    // ── ShowText: char outside WinAnsi becomes '?' and records a warning ────

    [Fact]
    public void ShowText_charOutsideWinAnsi_writesQuestionMarkAndRecordsWarning()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);

        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        canvas.ShowText("A★B"); // ★ (U+2605) is outside WinAnsiEncoding
        canvas.EndText();
        canvas.Finish();

        Assert.Single(canvas.TextEncodingWarnings);
        Assert.Equal('★', canvas.TextEncodingWarnings[0].Character);
        Assert.Equal(0x2605, canvas.TextEncodingWarnings[0].CodePoint);

        var ms = new MemoryStream();
        doc.Save(ms);
        var literal = ExtractTjLiteral(DecompressContentStream(ms.ToArray()));

        Assert.Equal((byte)'A', literal[0]);
        Assert.Equal((byte)'?', literal[1]);
        Assert.Equal((byte)'B', literal[2]);
    }

    // ── ShowText: pure WinAnsi content never warns ───────────────────────────

    [Fact]
    public void ShowText_winAnsiOnlyContent_recordsNoWarnings()
    {
        var canvas = BuildCanvas(c => c.ShowText("Café • 15°"));

        Assert.Empty(canvas.TextEncodingWarnings);
    }

    // ── ShowText: full byte-sequence pin (catches a neighbouring-byte mangle) ────

    [Fact]
    public void ShowText_winAnsiOnlyContent_pinsExactByteSequence()
    {
        // The apostrophe follows é and 'd' follows the em dash: a Contains-only check
        // would miss an off-by-one that corrupted a neighbour without ever emitting '?'.
        var literal = BuildAndExtractTjLiteral(canvas => canvas.ShowText("café's—done"));

        byte[] expected =
        [
            (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\'', (byte)'s',
            0x97, (byte)'d', (byte)'o', (byte)'n', (byte)'e',
        ];
        Assert.Equal(expected, literal);
        Assert.DoesNotContain((byte)'?', literal);
    }

    // ── ShowText: astral character (surrogate pair) ──────────────────────────

    [Fact]
    public void ShowText_astralSurrogatePair_emitsTwoQuestionMarksAndTwoWarnings()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);

        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        canvas.ShowText("😀"); // U+1F600: a UTF-16 surrogate pair, both halves outside WinAnsi
        canvas.EndText();
        canvas.Finish();

        Assert.Equal(2, canvas.TextEncodingWarnings.Count);
        foreach (var warning in canvas.TextEncodingWarnings)
            Assert.InRange(warning.CodePoint, 0xD800, 0xDFFF);

        var ms = new MemoryStream();
        doc.Save(ms);
        var literal = ExtractTjLiteral(DecompressContentStream(ms.ToArray()));

        byte[] expected = [0x3F, 0x3F];
        Assert.Equal(expected, literal);
    }

    // ── ShowText: a symbolic font is set, but ShowText still WinAnsi-encodes ─────

    [Fact]
    public void ShowText_symbolFontActive_stillWinAnsiEncodesAndDoesNotThrow()
    {
        // ShowText has no knowledge of the active font's encoding — it always
        // WinAnsi-encodes its argument. So with Symbol or ZapfDingbats set, ASCII input
        // round-trips as its own bytes (never '?'), but those bytes select the WRONG
        // glyphs under the font's built-in symbolic encoding. TextEncodingWarnings is
        // therefore only meaningful for the 12 non-symbolic Standard-14 faces.
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Symbol);

        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        canvas.ShowText("abc");
        canvas.EndText();
        canvas.Finish();

        Assert.Empty(canvas.TextEncodingWarnings);

        var ms = new MemoryStream();
        doc.Save(ms);
        var literal = ExtractTjLiteral(DecompressContentStream(ms.ToArray()));

        byte[] expected = [(byte)'a', (byte)'b', (byte)'c'];
        Assert.Equal(expected, literal);
    }

    // ── PdfFontResource.BuildDictionary: /Encoding on text fonts only ────────

    [Theory]
    [InlineData(Standard14.Helvetica)]
    [InlineData(Standard14.HelveticaBold)]
    [InlineData(Standard14.TimesRoman)]
    [InlineData(Standard14.Courier)]
    public void BuildDictionary_textFont_declaresWinAnsiEncoding(Standard14 font)
    {
        var dict = new PdfFontResource(font, "F1").BuildDictionary();

        Assert.True(dict.TryGet(PdfName.Encoding, out var encoding));
        var name = Assert.IsType<PdfName>(encoding);
        Assert.Equal("WinAnsiEncoding", name.Value);
    }

    [Theory]
    [InlineData(Standard14.Symbol)]
    [InlineData(Standard14.ZapfDingbats)]
    public void BuildDictionary_symbolicFont_omitsEncoding(Standard14 font)
    {
        var dict = new PdfFontResource(font, "F1").BuildDictionary();

        Assert.False(dict.TryGet(PdfName.Encoding, out _));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal one-page PDF, runs <paramref name="draw"/> against the canvas, and returns it (before Save/Finish).</summary>
    private static PdfCanvas BuildCanvas(Action<PdfCanvas> draw)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        draw(canvas);
        canvas.EndText();
        canvas.Finish();
        return canvas;
    }

    /// <summary>Builds a one-page PDF, runs <paramref name="draw"/>, saves it, and returns the raw bytes of the Tj literal in the decompressed content stream.</summary>
    private static byte[] BuildAndExtractTjLiteral(Action<PdfCanvas> draw)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        draw(canvas);
        canvas.EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        return ExtractTjLiteral(DecompressContentStream(ms.ToArray()));
    }

    /// <summary>Returns the bytes strictly between the first '(' and the following ") Tj" marker.</summary>
    private static byte[] ExtractTjLiteral(byte[] ops)
    {
        var start = Array.IndexOf(ops, (byte)'(');
        Assert.True(start >= 0, "No '(' found in content stream operators");

        var end = FindSequence(ops, ") Tj"u8, start);
        Assert.True(end >= 0, "No ') Tj' found in content stream operators");

        return ops[(start + 1)..end];
    }

    /// <summary>
    /// Finds the FlateDecode content stream in the PDF bytes and decompresses it, preserving
    /// every byte value 0x00–0xFF (unlike an ASCII decode, which would replace WinAnsi's
    /// 0x80–0xFF range with '?').
    /// </summary>
    private static byte[] DecompressContentStream(byte[] pdfBytes)
    {
        var streamStart = FindSequence(pdfBytes, "\nstream\n"u8);
        Assert.True(streamStart >= 0, "No stream found in PDF");

        var dataStart = streamStart + 8; // length of "\nstream\n"

        var streamEnd = FindSequence(pdfBytes, "\nendstream"u8, dataStart);
        Assert.True(streamEnd >= 0, "No endstream found in PDF");

        var compressed = pdfBytes[dataStart..streamEnd];

        using var zms = new MemoryStream(compressed);
        using var z = new ZLibStream(zms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        z.CopyTo(result);
        return result.ToArray();
    }

    private static int FindSequence(byte[] haystack, ReadOnlySpan<byte> needle, int startAt = 0)
    {
        for (var i = startAt; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }
}
