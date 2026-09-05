// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// #385's remaining routing sites — the ones not already pinned alongside an existing test for the
/// underlying condition (<see cref="XrefReconstructionTests"/>, <see cref="XrefStreamTests"/>,
/// <see cref="GenerationNumberTests"/> each gained one assertion instead of a parallel fixture).
/// Every test here asserts the routing is OBSERVE-ONLY: a decode that used to succeed still
/// succeeds with the same bytes, and a decode that used to throw still throws — #385 adds an
/// observation, never a behaviour change.
/// </summary>
public sealed class DiagnosticRoutingTests
{
    // These streams are decoded directly, never through a reader's decrypt path, so the identity is
    // arbitrary but spelled out, matching ReaderLimitsTests' MakeParsedStream.
    private static ParsedStream MakeParsedStream(PdfDictionary dict, byte[] rawBody, int objectNumber = 1) =>
        new(dict, new ReadOnlyMemory<byte>(rawBody), bodyOffset: 0, objectNumber: objectNumber, generation: 0);

    private static byte[] CompressZlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    // ── Filters.cs: /Filter shapes ───────────────────────────────────────────────────────────────

    [Fact]
    public void FilterNull_reportsInfo_andDecodesAsThoughAbsent()
    {
        var dict = new PdfDictionary().Set(PdfName.Filter, PdfNull.Instance);
        var raw = "hello"u8.ToArray();
        var stream = MakeParsedStream(dict, raw);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(raw, decoded); // unfiltered — same outcome as /Filter being absent entirely.
        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.FilterNull);
        Assert.Equal(PdfReaderDiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public void FilterArrayElementNotName_dropsElement_reportsWarning()
    {
        var body = "hello"u8.ToArray();
        var compressed = CompressZlib(body);
        var filterArray = new PdfArray().Add(new PdfInteger(5)).Add(PdfName.FlateDecode);
        var dict = new PdfDictionary().Set(PdfName.Filter, filterArray);
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        // The malformed element is dropped, not applied — FlateDecode alone still decodes the body.
        Assert.Equal(body, decoded);
        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.FilterArrayElementNotName);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void FilterValueMalformed_treatsAsAbsent_reportsWarning()
    {
        var raw = "hello"u8.ToArray();
        var dict = new PdfDictionary().Set(PdfName.Filter, new PdfInteger(42));
        var stream = MakeParsedStream(dict, raw);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(raw, decoded);
        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.FilterValueMalformed);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    // ── Filters.cs: /DecodeParms shapes ──────────────────────────────────────────────────────────

    [Fact]
    public void DecodeParmsMalformed_wholeEntryNotADictionaryOrArray_reportsWarning_decodesWithNoPredictor()
    {
        var body = "hello"u8.ToArray();
        var compressed = CompressZlib(body);
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(new PdfName("DecodeParms"), new PdfInteger(1));
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(body, decoded);
        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.DecodeParmsMalformed);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void DecodeParmsMalformed_arrayElementNotADictionary_reportsWarning_decodesWithNoPredictor()
    {
        var body = "hello"u8.ToArray();
        var compressed = CompressZlib(body);
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, new PdfArray().Add(PdfName.FlateDecode))
            .Set(new PdfName("DecodeParms"), new PdfArray().Add(new PdfInteger(1)));
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(body, decoded);
        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.DecodeParmsMalformed);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    /// <summary>
    /// An explicit <c>/DecodeParms null</c> — as opposed to the key being absent — is equivalent
    /// to absent per ISO 32000-2 §7.3.9, so it must not fall into the catch-all that reports
    /// <see cref="PdfReaderDiagnosticCode.DecodeParmsMalformed"/> with a "neither a dictionary, an
    /// array, nor null" message that would then be false of its own input.
    /// </summary>
    [Fact]
    public void DecodeParmsExplicitNull_wholeEntry_treatedAsAbsent_reportsNothing()
    {
        var body = "hello"u8.ToArray();
        var compressed = CompressZlib(body);
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(new PdfName("DecodeParms"), PdfNull.Instance);
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(body, decoded);
        Assert.Empty(sink.Diagnostics);
    }

    /// <summary>
    /// The array-element twin of the test above: a <c>/DecodeParms</c> array whose element is
    /// explicitly <c>null</c> rather than a dictionary is already silent (the array-element branch
    /// excludes <c>null</c>/<see cref="PdfNull"/> from its own report) — pinned here so a future
    /// change to that branch cannot regress it without a test noticing.
    /// </summary>
    [Fact]
    public void DecodeParmsExplicitNull_arrayElement_treatedAsAbsent_reportsNothing()
    {
        var body = "hello"u8.ToArray();
        var compressed = CompressZlib(body);
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, new PdfArray().Add(PdfName.FlateDecode))
            .Set(new PdfName("DecodeParms"), new PdfArray().Add(PdfNull.Instance));
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(body, decoded);
        Assert.Empty(sink.Diagnostics);
    }

    // ── Filters.cs: predictor ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The TIFF predictor (#98) un-differences <c>/BitsPerComponent</c> 4 the same way it already
    /// does 8, so the two rows this test feeds it decode rather than copying through still
    /// differenced. Known-answer bytes, derived by hand from ISO 32000-2 §7.4.4.4's own
    /// cumulative-sum rule: row one, nibbles 1 2 3 4 5 6 7 8, accumulates modulo 16 to 1 3 6 A F 5
    /// C 4 (<c>13 6A F5 C4</c>); row two, nibbles 9 A B C D E F 0, accumulates to 9 3 E A 7 5 4 4
    /// (<c>93 EA 75 44</c>).
    /// </summary>
    [Fact]
    public void TiffPredictor2_bpcFour_twoRows_decodesCorrectly_reportsNothing()
    {
        // Columns 8, Colors 1, BitsPerComponent 4 -> rowBytes = 4; two rows.
        var raw = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
        var compressed = CompressZlib(raw);
        var parms = new PdfDictionary()
            .Set(new PdfName("Predictor"), new PdfInteger(2))
            .Set(new PdfName("Columns"), new PdfInteger(8))
            .Set(new PdfName("Colors"), new PdfInteger(1))
            .Set(new PdfName("BitsPerComponent"), new PdfInteger(4));
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(new PdfName("DecodeParms"), parms);
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(new byte[] { 0x13, 0x6A, 0xF5, 0xC4, 0x93, 0xEA, 0x75, 0x44 }, decoded);
        Assert.Empty(sink.Diagnostics);
    }

    /// <summary>
    /// The one-row twin of the test above, pinning the same known-answer arithmetic against a
    /// body exactly one row long.
    /// </summary>
    [Fact]
    public void TiffPredictor2_bpcFour_oneRow_decodesCorrectly_reportsNothing()
    {
        // Columns 8, Colors 1, BitsPerComponent 4 -> rowBytes = 4; a 4-byte body is exactly one row.
        var raw = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var compressed = CompressZlib(raw);
        var parms = new PdfDictionary()
            .Set(new PdfName("Predictor"), new PdfInteger(2))
            .Set(new PdfName("Columns"), new PdfInteger(8))
            .Set(new PdfName("Colors"), new PdfInteger(1))
            .Set(new PdfName("BitsPerComponent"), new PdfInteger(4));
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(new PdfName("DecodeParms"), parms);
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Equal(new byte[] { 0x13, 0x6A, 0xF5, 0xC4 }, decoded);
        Assert.Empty(sink.Diagnostics);
    }

    /// <summary>
    /// A body shorter than one row (<c>rowBytes</c>) at a non-8-bit depth has zero full rows, so
    /// the decoder copies nothing and returns an empty array — the same outcome
    /// <c>data.Length == 0</c> already gets without a diagnostic. Reporting here would flag a
    /// condition that affected zero samples.
    /// </summary>
    [Fact]
    public void UnsupportedPredictor_bodyShorterThanOneRow_reportsNothing()
    {
        // Columns 8, Colors 1, BitsPerComponent 4 -> rowBytes = 4; a 2-byte body has zero full rows.
        var raw = new byte[] { 0x12, 0x34 };
        var compressed = CompressZlib(raw);
        var parms = new PdfDictionary()
            .Set(new PdfName("Predictor"), new PdfInteger(2))
            .Set(new PdfName("Columns"), new PdfInteger(8))
            .Set(new PdfName("Colors"), new PdfInteger(1))
            .Set(new PdfName("BitsPerComponent"), new PdfInteger(4));
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(new PdfName("DecodeParms"), parms);
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.NotNull(decoded);
        Assert.Empty(decoded!);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void SupportedPredictor_bpcEight_reportsNothing()
    {
        var raw = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var compressed = CompressZlib(raw);
        var parms = new PdfDictionary()
            .Set(new PdfName("Predictor"), new PdfInteger(2))
            .Set(new PdfName("Columns"), new PdfInteger(8))
            .Set(new PdfName("Colors"), new PdfInteger(1))
            .Set(new PdfName("BitsPerComponent"), new PdfInteger(8));
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(new PdfName("DecodeParms"), parms);
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);

        Assert.Empty(sink.Diagnostics);
    }

    // ── Filters.cs: unknown filter and the decode-size cap ───────────────────────────────────────

    [Fact]
    public void UnknownFilter_stillThrows_butReportsErrorFirst()
    {
        var dict = new PdfDictionary().Set(PdfName.Filter, new PdfName("Bogus"));
        var stream = MakeParsedStream(dict, "hello"u8.ToArray());
        var sink = new DiagnosticSink(cap: 10);

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.UnknownFilter);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void UnknownFilter_withAnOversizedName_reportsOnlyAFixedExcerpt()
    {
        // A /Filter name has no length bound (Annex C.1), and UnknownFilter is retained for
        // the reader's lifetime, so before this fix the whole name was interpolated via
        // filter.Value: the same class of defect DiagnosticExcerpt exists to bound elsewhere in
        // this reader (#402 round 8).
        var hugeFilter = new string('A', 1 << 20);
        var dict = new PdfDictionary().Set(PdfName.Filter, new PdfName(hugeFilter));
        var stream = MakeParsedStream(dict, "hello"u8.ToArray());
        var sink = new DiagnosticSink(cap: 10);

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.UnknownFilter);
        Assert.Equal(
            "Unknown PDF filter: /" + new string('A', 32) + "... (1048576 bytes).",
            d.Message);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void UnknownFilter_atTheExcerptBoundary_quotesThirtyTwoWhole_andExcerptsThirtyThree(
        int nameLength, bool expectExcerpt)
    {
        var name = new string('A', nameLength);
        var dict = new PdfDictionary().Set(PdfName.Filter, new PdfName(name));
        var stream = MakeParsedStream(dict, "hello"u8.ToArray());
        var sink = new DiagnosticSink(cap: 10);

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.UnknownFilter);
        var expected = expectExcerpt
            ? "Unknown PDF filter: /" + new string('A', 32) + $"... ({nameLength} bytes)."
            : $"Unknown PDF filter: /{name}.";
        Assert.Equal(expected, d.Message);
    }

    [Fact]
    public void DecodedStreamLimitExceeded_stillThrows_butReportsErrorFirst()
    {
        var decodedSize = (int)(ReaderLimits.MinMaxDecodedBytes * 2); // exceeds a tightened cap.
        var compressed = CompressZlib(new byte[decodedSize]);
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(PdfName.Length, compressed.Length);
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);
        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, tightened, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>
    /// The FlateDecode primary path above is one of three decoders that report
    /// <see cref="PdfReaderDiagnosticCode.DecodedStreamLimitExceeded"/> before re-throwing; this
    /// pins the LZWDecode guard. The input is not a real LZW encoding of a large payload (that
    /// would need a matching encoder to build), but a hand-packed code sequence exploiting the
    /// decoder's own KwKwK growth rule (ISO 32000-2 does not name it, but it is the classic LZW
    /// "code not yet in the table" case): after one literal code primes the table, sending each
    /// freshly created code back — 258, then 259, then 260, … — makes every entry one byte longer
    /// than the last, so N such codes emit roughly N²/2 bytes from an input just tens of bits
    /// long, well past the tightened cap after ~1,449 codes.
    /// </summary>
    [Fact]
    public void DecodedStreamLimitExceeded_lzwDecode_stillThrows_butReportsErrorFirst()
    {
        var codes = new List<int> { 0 }; // one literal byte to prime prevEntry.
        var table = 258;
        for (var i = 0; i < 1500; i++)
            codes.Add(table++); // always the code the decoder has not defined yet.

        var packed = PackLzwCodes(codes);
        var dict = new PdfDictionary().Set(PdfName.Filter, new PdfName("LZWDecode"));
        var stream = MakeParsedStream(dict, packed);
        var sink = new DiagnosticSink(cap: 10);
        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, tightened, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>
    /// Bit-packs <paramref name="codes"/> MSB-first within each byte — matching
    /// <c>PdfFilters.DecodeLzw</c>'s own <c>ReadCode</c> — at a code width that starts at 9 bits
    /// and grows exactly the way <c>DecodeLzw.MaybeGrow</c> does for the default
    /// <c>/EarlyChange 1</c>: every code after the first primes one new table entry (mirroring
    /// that every code but the first runs through the decoder's own
    /// <c>if (prevEntry is not null) table.Add(...)</c> branch), and the code width steps up the
    /// moment the simulated table count reaches <c>(1 &lt;&lt; codeSize) - 1</c>.
    /// </summary>
    private static byte[] PackLzwCodes(IReadOnlyList<int> codes)
    {
        var bits = new List<bool>();
        var tableCount = 258; // 256 literals + Clear(256) + EOI(257).
        var codeSize = 9;
        var first = true;

        foreach (var code in codes)
        {
            if (tableCount >= (1 << codeSize) - 1 && codeSize < 12)
                codeSize++;

            for (var bit = codeSize - 1; bit >= 0; bit--)
                bits.Add(((code >> bit) & 1) != 0);

            if (!first)
                tableCount++;
            first = false;
        }

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }
        return bytes;
    }

    /// <summary>
    /// The RunLengthDecode twin, literal-run guard: pads to just under the tightened cap with
    /// cheap repeat runs (2 input bytes each, 128 output bytes) and finishes with one 100-byte
    /// literal run that pushes the total over.
    /// </summary>
    [Fact]
    public void DecodedStreamLimitExceeded_runLengthDecode_literalRun_stillThrows_butReportsErrorFirst()
    {
        var input = BuildRunLengthOverflow(ReaderLimits.MinMaxDecodedBytes, finalRunIsLiteral: true);
        var dict = new PdfDictionary().Set(PdfName.Filter, new PdfName("RunLengthDecode"));
        var stream = MakeParsedStream(dict, input);
        var sink = new DiagnosticSink(cap: 10);
        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, tightened, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>The RunLengthDecode repeat-run guard twin of the test above.</summary>
    [Fact]
    public void DecodedStreamLimitExceeded_runLengthDecode_repeatRun_stillThrows_butReportsErrorFirst()
    {
        var input = BuildRunLengthOverflow(ReaderLimits.MinMaxDecodedBytes, finalRunIsLiteral: false);
        var dict = new PdfDictionary().Set(PdfName.Filter, new PdfName("RunLengthDecode"));
        var stream = MakeParsedStream(dict, input);
        var sink = new DiagnosticSink(cap: 10);
        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, tightened, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>
    /// Builds a RunLengthDecode body that pads to <paramref name="cap"/> minus 50 bytes using
    /// maximal repeat runs (a run count of 128 for 2 input bytes), then finishes with one 100-byte
    /// run of the requested kind, pushing the decoded total to <paramref name="cap"/> plus 50 —
    /// past the guard regardless of which run type does the pushing.
    /// </summary>
    private static byte[] BuildRunLengthOverflow(long cap, bool finalRunIsLiteral)
    {
        var ms = new MemoryStream();
        var remaining = cap - 50;

        while (remaining >= 128)
        {
            ms.WriteByte(129); // length 129 -> repeat count 257-129 = 128.
            ms.WriteByte(0x00);
            remaining -= 128;
        }
        if (remaining > 0)
        {
            // A short literal run for the remainder — literal supports any count from 1 to 128,
            // avoiding the repeat run's own two-byte-minimum count.
            var count = (int)remaining;
            ms.WriteByte((byte)(count - 1));
            ms.Write(new byte[count]);
        }

        if (finalRunIsLiteral)
        {
            ms.WriteByte(99); // length 99 -> literal count 100.
            ms.Write(new byte[100]);
        }
        else
        {
            ms.WriteByte((byte)(257 - 100)); // length 157 -> repeat count 100.
            ms.WriteByte(0x00);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// The fourth <see cref="PdfReaderDiagnosticCode.DecodedStreamLimitExceeded"/> site: the RETRY
    /// decoder InflateFlate falls back to when the primary guess at zlib-vs-raw-deflate framing
    /// turns out wrong. Reaching it needs input that (a) LooksLikeZlib misreads as zlib-framed, so
    /// the primary attempt fails on an ordinary format error rather than the size cap, and (b) is
    /// still genuinely valid, oversized raw deflate data when the retry reads the SAME bytes from
    /// byte 0 instead of skipping a would-be two-byte header. See
    /// <see cref="BuildRawDeflateMisreadAsZlib"/> for how those two bytes are made to do both jobs
    /// at once.
    /// </summary>
    [Fact]
    public void DecodedStreamLimitExceeded_flateRetryDecoder_stillThrows_butReportsErrorFirst()
    {
        var input = BuildRawDeflateMisreadAsZlib();
        var dict = new PdfDictionary().Set(PdfName.Filter, PdfName.FlateDecode);
        var stream = MakeParsedStream(dict, input);
        var sink = new DiagnosticSink(cap: 10);
        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });

        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, tightened, diagnostics: sink));

        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>
    /// Hand-builds raw DEFLATE data (RFC 1951 STORED blocks only — no Huffman coding, so every
    /// byte is exact and controllable) whose first two bytes are ALSO a valid zlib CMF/FLG pair,
    /// 0x78 0x9C, purely by how a stored block's own header happens to be laid out:
    /// <list type="bullet">
    /// <item><description>Byte 0 (0x78 = 0b0111_1000) is a stored-block header read LSB-first —
    /// bit 0 (BFINAL) = 0, bits 1-2 (BTYPE) = 00 (stored); the remaining bits are padding to the
    /// next byte boundary and RFC 1951 §3.2.4 says a decoder ignores them, so their actual value
    /// (imposed here by needing byte 0 to equal 0x78) is irrelevant to a raw-deflate reading.
    /// </description></item>
    /// <item><description>Bytes 1-2 are that stored block's own 2-byte little-endian LEN field.
    /// Its low byte has to be 0x9C for byte 1 to complete the zlib header, so LEN is fixed at
    /// 0xFF9C (65436, the free high byte chosen near the 65535 per-block maximum); bytes 3-4 are
    /// its one's-complement NLEN, and the 65436 zero bytes after that are the block's literal
    /// payload.</description></item>
    /// </list>
    /// Interpreted as zlib (the primary attempt, since <c>LooksLikeZlib</c> reads exactly those
    /// two bytes and sees 0x78 0x9C), the decoder treats byte 2 onward as the deflate payload —
    /// which is actually the MIDDLE of the stored block above, not a fresh block header — and
    /// fails with an ordinary format error. Interpreted as raw deflate from byte 0 (the retry),
    /// the same bytes are exactly what they are: a genuine, valid stored-block stream. Sixteen
    /// more full non-final stored blocks (65535 bytes each) and one empty final block bring the
    /// total decoded size to 1,113,996 bytes, comfortably past the 1 MiB tightened cap this test
    /// uses, so the retry's own bomb guard is what actually fires.
    /// </summary>
    private static byte[] BuildRawDeflateMisreadAsZlib()
    {
        var ms = new MemoryStream();

        void WriteStoredBlock(byte headerByte, int len)
        {
            ms.WriteByte(headerByte);
            ms.WriteByte((byte)(len & 0xFF));
            ms.WriteByte((byte)((len >> 8) & 0xFF));
            var nlen = ~len & 0xFFFF;
            ms.WriteByte((byte)(nlen & 0xFF));
            ms.WriteByte((byte)((nlen >> 8) & 0xFF));
            ms.Write(new byte[len]);
        }

        // Block 1: header 0x78 (non-final, stored — see remarks); LEN = 0xFF9C so its low byte
        // (this block's byte index 1, the array's byte index 1) is the required 0x9C.
        WriteStoredBlock(0x78, 0xFF9C);

        // Sixteen more non-final stored blocks (0x00 = BFINAL 0, BTYPE 00, clean padding) at the
        // maximum stored-block length, pushing the running total well past 1 MiB.
        for (var i = 0; i < 16; i++)
            WriteStoredBlock(0x00, 65535);

        // A final, empty stored block (0x01 = BFINAL 1, BTYPE 00) to terminate the stream cleanly
        // — never actually reached, since the cap trips first, but keeps the fixture well-formed.
        WriteStoredBlock(0x01, 0);

        return ms.ToArray();
    }

    // ── PdfDocumentReader: ObjectHeaderMismatch ──────────────────────────────────────────────────

    /// <summary>
    /// Object 10's cross-reference entry points at an offset whose own <c>"N G obj"</c> header names
    /// object 11 instead — the shape <c>PdfDocumentReader.Resolve(int, int?)</c> resolves to
    /// <see langword="null"/> rather than the wrong object's content.
    /// </summary>
    [Fact]
    public void ObjectHeaderMismatch_resolvesToNull_andReportsWarning()
    {
        var bytes = BuildHeaderMismatchDocument();
        using var reader = PdfReader.Open(bytes);

        Assert.Null(reader.Resolve(10));

        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.ObjectHeaderMismatch);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(10, d.ObjectNumber);
    }

    // ── PdfDocumentReader: ResolveStream mirrors Resolve's own reports ───────────────────────────

    /// <summary>
    /// The stream twin of <see cref="ObjectHeaderMismatch_resolvesToNull_andReportsWarning"/>:
    /// <c>ResolveStream(int, int?)</c> is a second entry point into the same object graph and must
    /// report the identical condition, not stay silent because the caller happened to reach the
    /// object through it instead of <c>Resolve</c>.
    /// </summary>
    [Fact]
    public void ResolveStream_headerMismatch_returnsNull_andReportsWarning()
    {
        var bytes = BuildStreamHeaderMismatchDocument();
        using var reader = PdfReader.Open(bytes);

        Assert.Null(reader.ResolveStream(10));

        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.ObjectHeaderMismatch);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(10, d.ObjectNumber);
    }

    /// <summary>
    /// The stream twin of <c>ClassicXref_referenceGenerationMismatch_resolvesToNull</c>
    /// (<see cref="GenerationNumberTests"/>): the same ISO 32000-2 §7.3.10 divergence, reached
    /// through <c>ResolveStream</c> instead of <c>Resolve</c>.
    /// </summary>
    [Fact]
    public void ResolveStream_generationMismatch_returnsNull_andReportsWarning()
    {
        var bytes = BuildStreamGenerationMismatchDocument();
        using var reader = PdfReader.Open(bytes);

        Assert.Null(reader.ResolveStream(new PdfIndirectReference(10, 5)));

        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.ObjectGenerationMismatch);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(10, d.ObjectNumber);
        Assert.Equal(5, d.Generation);
    }

    /// <summary>
    /// The stream twin of <c>ClassicXref_referenceGenerationMismatch_afterAWarmCacheHit_stillReports</c>
    /// (<see cref="GenerationNumberTests"/>): <c>ResolveStream(int, int?)</c>'s own warm-cache
    /// branch must not depend on request order either — a correct resolve first (warming
    /// <c>_streamCache</c>), then a mismatched one, must still report.
    /// </summary>
    [Fact]
    public void ResolveStream_generationMismatch_afterAWarmCacheHit_stillReports()
    {
        var bytes = BuildStreamGenerationMismatchDocument();
        using var reader = PdfReader.Open(bytes);

        Assert.NotNull(reader.ResolveStream(new PdfIndirectReference(10, 0))); // warms _streamCache.
        Assert.Empty(reader.Diagnostics);

        Assert.Null(reader.ResolveStream(new PdfIndirectReference(10, 5)));

        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.ObjectGenerationMismatch);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(10, d.ObjectNumber);
        Assert.Equal(5, d.Generation);
    }

    private static byte[] BuildStreamHeaderMismatchDocument()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        // The xref below points object 10 at THIS offset, but the header here says "11 0 obj".
        var mismatchOffset = (int)ms.Position;
        W("11 0 obj\n<< /Length 5 >>\nstream\nhello\nendstream\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 2\n");
        W($"{0:D10} 65535 f \n");
        W($"{obj1Offset:D10} 00000 n \n");
        W("10 1\n");
        W($"{mismatchOffset:D10} 00000 n \n");
        W("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] BuildStreamGenerationMismatchDocument()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var obj10Offset = (int)ms.Position;
        W("10 0 obj\n<< /Length 5 >>\nstream\nhello\nendstream\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 2\n");
        W($"{0:D10} 65535 f \n");
        W($"{obj1Offset:D10} 00000 n \n");
        W("10 1\n");
        W($"{obj10Offset:D10} 00000 n \n");
        W("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── PdfReaderOptions.MaxDiagnostics wired end to end ─────────────────────────────────────────

    /// <summary>
    /// Five objects, each resolved with a mismatched generation to produce five distinct (by object
    /// number) <see cref="PdfReaderDiagnosticCode.ObjectGenerationMismatch"/> reports — proving
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> actually reaches the reader's own
    /// <see cref="PdfDocumentReader.Diagnostics"/>, not just <see cref="DiagnosticSink"/> in
    /// isolation (<see cref="DiagnosticSinkTests"/> covers the sink's own cap/sentinel mechanics).
    /// </summary>
    [Fact]
    public void MaxDiagnostics_boundsReaderDiagnostics_endToEnd()
    {
        var bytes = BuildFiveObjectDocument();
        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { MaxDiagnostics = 2 });

        for (var n = 2; n <= 6; n++)
            Assert.Null(reader.Resolve(new PdfIndirectReference(n, 99)));

        Assert.Equal(3, reader.Diagnostics.Count); // 2 ordinary + 1 sentinel.
        var sentinel = Assert.Single(
            reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DiagnosticsSuppressed);
        // 5 attempts - 2 recorded = 3 suppressed; StartsWith rather than Contains, because the
        // message names the cap too (2), and Contains("3") alone can't tell those two numbers apart.
        Assert.StartsWith("3 diagnostics suppressed", sentinel.Message, StringComparison.Ordinal);
    }

    private static byte[] BuildFiveObjectDocument()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        var offsets = new int[7];
        offsets[1] = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        for (var n = 2; n <= 6; n++)
        {
            offsets[n] = (int)ms.Position;
            W($"{n} 0 obj\n<< /Marker /Obj{n} >>\nendobj\n");
        }

        var xrefOffset = (int)ms.Position;
        W("xref\n0 7\n");
        W($"{0:D10} 65535 f \n");
        for (var n = 1; n <= 6; n++)
            W($"{offsets[n]:D10} 00000 n \n");
        W("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] BuildHeaderMismatchDocument()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        // The xref below points object 10 at THIS offset, but the header here says "11 0 obj".
        var mismatchOffset = (int)ms.Position;
        W("11 0 obj\n<< /Marker /Wrong >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n");
        W("0 2\n");
        W($"{0:D10} 65535 f \n");
        W($"{obj1Offset:D10} 00000 n \n");
        W("10 1\n");
        W($"{mismatchOffset:D10} 00000 n \n");
        W("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }
}
