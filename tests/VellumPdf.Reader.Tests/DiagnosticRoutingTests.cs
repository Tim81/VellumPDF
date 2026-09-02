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

    [Fact]
    public void UnsupportedPredictor_bpcNotEight_reportsWarning_stillReturnsBytes()
    {
        // Columns 8, Colors 1, BitsPerComponent 4 -> rowBytes = 4; two rows of arbitrary content.
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

        Assert.NotNull(decoded);
        Assert.Equal(raw.Length, decoded!.Length); // copied through, not thrown away.
        var d = Assert.Single(sink.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.UnsupportedPredictor);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
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
        Assert.Contains("3", sentinel.Message); // 5 attempts - 2 recorded = 3 suppressed.
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
