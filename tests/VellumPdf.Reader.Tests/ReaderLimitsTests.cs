// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Core;

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

        var decodedUnderDefault = PdfFilters.Decode(stream);
        Assert.Equal(DecodedSize, decodedUnderDefault!.Length);

        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });
        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(stream, limits: tightened));
    }

    // ── (b) tightened ReconstructionBudgetMultiplier exhausts the budget where the default succeeds ─

    /// <summary>
    /// Many tiny stream objects, each declaring a <c>/Length</c> one byte short of its real body —
    /// every one misses <c>XrefReconstructor</c>'s exact-position check and pays the ±64-byte
    /// near-miss window (row 4), which roughly doubles the walk's charged cost relative to the raw
    /// file length (measured ratio ≈ 2.06, stable across file sizes). At <see cref="Count"/> the
    /// file is comfortably over the reconstruction budget's 1 MiB floor, so the multiplier — not the
    /// floor — decides whether the walk fits: <c>max(1 MiB, 8 × length)</c> (the default) has ample
    /// headroom, while <c>max(1 MiB, 1 × length)</c> (tightened) does not.
    /// </summary>
    private const int Count = 20000;

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

        using (var reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }))
        {
            Assert.True(reader.WasReconstructed);
        }

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
