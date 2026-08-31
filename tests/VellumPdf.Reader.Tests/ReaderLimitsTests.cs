// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> and
/// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/> (#376): a caller may tighten either
/// resource ceiling below the library's built-in default, never raise it past that default.
/// </summary>
public sealed class ReaderLimitsTests
{
    // These streams are decoded directly, never through a reader's decrypt path, so the identity is
    // arbitrary — but it is spelled out rather than defaulted, matching XrefStreamTests'
    // MakeParsedStream.
    private static ParsedStream MakeParsedStream(PdfDictionary dict, byte[] rawBody) =>
        new(dict, new ReadOnlyMemory<byte>(rawBody), bodyOffset: 0, objectNumber: 1, generation: 0);

    private static byte[] CompressZlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] SaveDocToBytes(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── (a) tightened MaxDecodedStreamBytes rejects a decode that succeeds under the default ──────

    [Fact]
    public void TightenedMaxDecodedStreamBytes_rejectsAStreamThatDecodesFineUnderTheDefault()
    {
        // 2 MiB of zero bytes is highly compressible — the FlateDecode body stays tiny, but the
        // decoded output exceeds a cap tightened down to the 1 MiB floor while staying far under
        // the 512 MiB default.
        const int DecodedSize = 2 * 1024 * 1024;
        var compressed = CompressZlib(new byte[DecodedSize]);
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(PdfName.Length, compressed.Length);
        var stream = MakeParsedStream(dict, compressed);

        var decodedUnderDefault = PdfFilters.Decode(stream, ReaderLimits.Defaults);
        Assert.Equal(DecodedSize, decodedUnderDefault!.Length);

        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });
        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, tightened));
    }

    // ── (a-wire) the same cap, threaded end to end through PdfDocumentReader ────────────────────────

    /// <summary>
    /// Follows the outer content-stream reference from a full, real document instead of calling
    /// <see cref="PdfFilters.Decode"/> directly — the previous test proves the cap itself works, this
    /// one proves <c>PdfReader.Open</c> actually hands <see cref="PdfDocumentReader"/> the resolved
    /// <see cref="ReaderLimits"/> it was given rather than a private default. A mutant that hardcodes
    /// <c>PdfDocumentReader</c>'s <c>_limits</c> field to <see cref="ReaderLimits.Defaults"/> passes
    /// every other test in this file but turns this one red.
    /// </summary>
    [Fact]
    public void TightenedMaxDecodedStreamBytes_wiredThroughPdfDocumentReader_rejectsAPageContentStreamThatDecodesFineUnderTheDefault()
    {
        const int DecodedSize = 2 * 1024 * 1024;
        Assert.True(DecodedSize > ReaderLimits.MinMaxDecodedBytes, "the fixture's content stream must actually exceed the floor it is tightened to");

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.ContentBytes = new byte[DecodedSize]; // highly compressible; content stays tiny on disk
        var bytes = SaveDocToBytes(doc);

        using (var defaultReader = PdfReader.Open(bytes))
        {
            var stream = GetPageContentStream(defaultReader);
            var decoded = defaultReader.GetDecodedStreamData(stream);
            Assert.Equal(DecodedSize, decoded!.Length);
        }

        using var tightenedReader = PdfReader.Open(
            bytes, new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });
        var tightenedStream = GetPageContentStream(tightenedReader);
        Assert.Throws<InvalidDataException>(() => tightenedReader.GetDecodedStreamData(tightenedStream));
    }

    /// <summary>Walks Catalog → Pages → Kids[0] → Contents to the page's content stream.</summary>
    private static ParsedStream GetPageContentStream(PdfDocumentReader reader)
    {
        var pages = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!));
        var kids = Assert.IsType<PdfArray>(pages.Get(PdfName.Kids));
        var page = Assert.IsType<PdfDictionary>(reader.ResolveValue(kids[0]));
        var contentsRef = Assert.IsType<PdfIndirectReference>(page.Get(PdfName.Contents));
        return reader.ResolveStream(contentsRef) ?? throw new InvalidOperationException("content stream did not resolve");
    }

    // ── (a-wire, xref stream) the same cap, threaded through XrefParser's own decode call ──────────

    /// <summary>
    /// A hand-built cross-reference STREAM (ISO 32000-2 §7.5.8) whose decoded table is padded with
    /// free (type 0) rows well past what <c>/Size</c> needs to describe object 1 — the padding is
    /// what pushes the decoded body over a tightened cap while the actual table stays trivially
    /// valid (every padding row decodes to an inert, spec-legal "free" entry). This exercises
    /// <c>XrefParser.ParseXrefStream</c>'s own <c>PdfFilters.Decode</c> call, the second of the two
    /// production call sites the coverage gap in <see cref="TightenedMaxDecodedStreamBytes_rejectsAStreamThatDecodesFineUnderTheDefault"/>
    /// missed — a mutant that drops <c>limits:</c> from that call passes every other test in this
    /// file but turns this one red.
    /// </summary>
    private const int XrefStreamRowCount = 300_000;
    private const int XrefStreamRowSize = 6; // /W [1 4 1]: 1-byte type, 4-byte offset, 1-byte generation

    private static byte[] BuildXrefStreamPdfWithOversizedDecodedTable(int rowCount)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        var catalogOffset = (int)ms.Length;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        // Every row defaults to all-zero bytes — type 0 (free), a legal no-op entry — except row 1
        // (object 1), which is the real, resolvable catalog entry (type 1: offset, generation 0).
        var table = new byte[(long)rowCount * XrefStreamRowSize];
        table[XrefStreamRowSize + 0] = 1; // type 1: in-use, direct offset
        table[XrefStreamRowSize + 1] = (byte)(catalogOffset >> 24);
        table[XrefStreamRowSize + 2] = (byte)(catalogOffset >> 16);
        table[XrefStreamRowSize + 3] = (byte)(catalogOffset >> 8);
        table[XrefStreamRowSize + 4] = (byte)catalogOffset;
        table[XrefStreamRowSize + 5] = 0; // generation 0

        var compressed = CompressZlib(table);

        var xrefOffset = (int)ms.Length;
        W("2 0 obj\n");
        W($"<< /Type /XRef /Filter /FlateDecode /Length {compressed.Length} /W [1 4 1] "
            + $"/Size {rowCount} /Root 1 0 R >>\n");
        W("stream\n");
        ms.Write(compressed);
        W("\nendstream\nendobj\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void TightenedMaxDecodedStreamBytes_wiredThroughXrefParser_rejectsAnXrefStreamThatDecodesFineUnderTheDefault()
    {
        var decodedSize = (long)XrefStreamRowCount * XrefStreamRowSize;
        Assert.True(decodedSize > ReaderLimits.MinMaxDecodedBytes,
            $"the fixture's xref stream table ({decodedSize} bytes) must actually exceed the 1 MiB floor it is tightened to");

        var bytes = BuildXrefStreamPdfWithOversizedDecodedTable(XrefStreamRowCount);

        using (var defaultReader = PdfReader.Open(bytes))
        {
            var typeObj = Assert.IsType<PdfName>(defaultReader.Catalog.Get(PdfName.Type));
            Assert.Equal("Catalog", typeObj.Value);
        }

        var ex = Assert.Throws<InvalidDataException>(() =>
            PdfReader.Open(bytes, new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes }));
        Assert.Contains("cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── (b) tightened ReconstructionBudgetMultiplier exhausts the budget where the default succeeds ─

    /// <summary>
    /// Many tiny stream objects, each declaring a <c>/Length</c> one byte short of its real body —
    /// every one misses <c>XrefReconstructor</c>'s exact-position check and pays the ±64-byte
    /// near-miss window (row 4), which roughly doubles the walk's charged cost relative to the raw
    /// file length (measured ratio ≈ 2.05–2.07, stable from a few hundred objects to tens of
    /// thousands — see <see cref="TightenedReconstructionBudgetMultiplier_exhaustsBudget_whereDefaultSucceeds"/>'s
    /// own ratio assertion). At <see cref="Count"/> the file is ~32 % over the reconstruction
    /// budget's 1 MiB floor (measured, asserted below — not merely assumed), enough margin that the
    /// floor, not the charging model's own precision, decides the file clears it; the multiplier is
    /// then what decides whether the walk fits: <c>max(1 MiB, 8 × length)</c> (the default) has ample
    /// headroom, while <c>max(1 MiB, 1 × length)</c> (tightened) does not.
    /// </summary>
    private const int Count = 24000;

    // The reconstruction budget's own floor (XrefReconstructor.MinReconstructionByteBudget) —
    // duplicated here, not referenced, since that constant is private; kept in sync by the
    // fixture-margin assertion below, which would start failing well before a drift big enough to
    // matter.
    private const long ReconstructionBudgetFloorBytes = 1024L * 1024;

    private static byte[] BuildManyNearMissLengthStreams(int count)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        for (var i = 0; i < count; i++)
            W($"{5000 + i} 0 obj\n<< /Length 4 >>\nstream\nHi!!!\nendstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void TightenedReconstructionBudgetMultiplier_exhaustsBudget_whereDefaultSucceeds()
    {
        var bytes = BuildManyNearMissLengthStreams(Count);
        Assert.True(bytes.Length > ReconstructionBudgetFloorBytes,
            $"the {Count}-object fixture ({bytes.Length} bytes) must actually clear the reconstruction "
            + $"budget's {ReconstructionBudgetFloorBytes}-byte floor with real margin, not merely assume it");

        long consumed;
        using (var reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }))
        {
            Assert.True(reader.WasReconstructed);
            consumed = reader.ReconstructionBytesConsumed;
        }

        // Pins the charged-cost/file-length ratio the fixture's own doc comment measures (~2.05–2.07)
        // into a band comfortably inside it: below 1× would mean the near-miss window charge stopped
        // firing at all, and above 3× would mean it grew well past what was actually observed — either
        // is the charging model drifting under this fixture, not a change worth silently tolerating.
        var ratio = consumed / (double)bytes.Length;
        Assert.True(ratio is > 1.5 and < 3.0,
            $"charged/length ratio {ratio:F3} (consumed={consumed}, length={bytes.Length}) fell outside "
            + "the calibrated 1.5-3.0 band for BuildManyNearMissLengthStreams — the near-miss-window "
            + "charging model this fixture relies on may have changed");

        var ex = Assert.Throws<InvalidDataException>(() =>
            PdfReader.Open(
                bytes,
                new PdfReaderOptions { AllowReconstruction = true, ReconstructionBudgetMultiplier = 1 }));
        Assert.Contains("cost budget", ex.Message, StringComparison.Ordinal);
    }

    // ── (c) loosening either knob past the default, or past the floor, throws ──────────────────────

    [Theory]
    [InlineData(ReaderLimits.DefaultMaxDecodedBytes + 1)] // above the default
    [InlineData(ReaderLimits.MinMaxDecodedBytes - 1)] // below the floor
    public void MaxDecodedStreamBytes_outsideTheAllowedRange_throwsArgumentOutOfRange(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfReader.Open([], new PdfReaderOptions { MaxDecodedStreamBytes = value }));
    }

    [Theory]
    [InlineData(ReaderLimits.DefaultReconstructionBudgetMultiplier + 1)] // above the default
    [InlineData(ReaderLimits.MinReconstructionBudgetMultiplier - 1)] // below the floor
    public void ReconstructionBudgetMultiplier_outsideTheAllowedRange_throwsArgumentOutOfRange(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfReader.Open([], new PdfReaderOptions { ReconstructionBudgetMultiplier = value }));
    }

    // ── (d) defaults are unchanged ──────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultOptions_resolveToTheDocumentedDefaultLimits()
    {
        var limits = ReaderLimits.Resolve(new PdfReaderOptions());

        Assert.Equal(512L * 1024 * 1024, limits.MaxDecodedBytes);
        Assert.Equal(512L * 1024 * 1024, limits.MaxAggregateReconstructionDecodeBytes);
        Assert.Equal(8, limits.ReconstructionBudgetMultiplier);
    }
}
