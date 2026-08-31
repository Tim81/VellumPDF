// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// #376 follow-up: <c>EmbeddedFilePdfaRule</c>'s recursive validation of an embedded PDF/A
/// attachment used to open the nested document through <c>PdfReader.Open(byte[])</c> — the
/// untightened 512 MiB / 8× defaults — regardless of what the OUTER document was opened with. A
/// caller who tightened <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> to harden against a
/// decompression bomb got no protection at all against one hidden inside an attachment: the nested
/// open silently widened the cap straight back to 512 MiB.
/// </summary>
/// <remarks>
/// Driving this end to end through <c>EmbeddedFilePdfaRule.Evaluate</c> would need an embedded
/// document that is genuinely PDF/A-2b compliant AND whose <c>/Metadata</c> stream decodes past a
/// tightened cap while still parsing as valid <c>pdfaid</c> XMP — achievable, but the compliance
/// surface (output intent, tagged structure, embedded fonts, …) makes that fixture large and
/// fragile relative to what it would prove beyond what the three tests below already pin: that
/// <see cref="PdfDocumentReader.Limits"/> and <see cref="PreflightContext.Limits"/> both carry the
/// outer read's resolved ceiling, and that the internal <c>PdfReader.Open(byte[], ReaderLimits)</c>
/// overload — the exact call <c>EmbeddedFilePdfaRule</c> now opens the nested document through —
/// actually enforces whatever ceiling it is handed rather than falling back to the 512 MiB default.
/// </remarks>
public sealed class EmbeddedFileReaderLimitsTests
{
    // Distinct from both the 512 MiB default and the 1 MiB floor, so a test passing by coincidence
    // (a mutant that hardcodes either constant instead of forwarding the real value) is visible.
    private const long TightenedButNotFloor = 4L * 1024 * 1024;

    private static byte[] Save(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static ParsedStream GetPageContentStream(PdfDocumentReader reader)
    {
        var pages = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!));
        var kids = Assert.IsType<PdfArray>(pages.Get(PdfName.Kids));
        var page = Assert.IsType<PdfDictionary>(reader.ResolveValue(kids[0]));
        var contentsRef = Assert.IsType<PdfIndirectReference>(page.Get(PdfName.Contents));
        return reader.ResolveStream(contentsRef)
            ?? throw new InvalidOperationException("content stream did not resolve");
    }

    // ── PdfDocumentReader.Limits / PreflightContext.Limits carry the outer read's resolved ceiling ─

    [Fact]
    public void PdfDocumentReader_Limits_matchesTheResolvedOptions()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = Save(doc);

        using var reader = PdfReader.Open(
            bytes, new PdfReaderOptions { MaxDecodedStreamBytes = TightenedButNotFloor });

        Assert.Equal(TightenedButNotFloor, reader.Limits.MaxDecodedBytes);
        Assert.Equal(TightenedButNotFloor, reader.Limits.MaxAggregateReconstructionDecodeBytes);
    }

    [Fact]
    public void PreflightContext_Limits_forwardsReaderLimits()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = Save(doc);

        using var reader = PdfReader.Open(
            bytes, new PdfReaderOptions { MaxDecodedStreamBytes = TightenedButNotFloor });
        var context = new PreflightContext(reader, PdfConformance.PdfA2B, []);

        Assert.Equal(reader.Limits, context.Limits);
    }

    // ── The internal ReaderLimits overload EmbeddedFilePdfaRule now opens nested bytes through ─────

    /// <summary>
    /// The exact mechanism the fix put into <c>EmbeddedFilePdfaRule.ReadEmbeddedPdfAInfo</c> and its
    /// recursive-validation call: open attacker-supplied nested bytes with the OUTER read's resolved
    /// <see cref="ReaderLimits"/>, not the 512 MiB default <see cref="PdfReader.Open(byte[])"/> uses.
    /// A page content stream that decodes fine at the default fails once opened with a tightened
    /// ceiling instead.
    /// </summary>
    [Fact]
    public void PdfReaderOpenWithReaderLimits_enforcesTheCallersCeiling_notThe512MiBDefault()
    {
        const int DecodedSize = 2 * 1024 * 1024;
        Assert.True(DecodedSize > ReaderLimits.MinMaxDecodedBytes,
            "the fixture's content stream must actually exceed the floor it is tightened to");

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.ContentBytes = new byte[DecodedSize]; // highly compressible; content stays tiny on disk
        var bytes = Save(doc);

        using (var atDefault = PdfReader.Open(bytes, ReaderLimits.Defaults))
        {
            var stream = GetPageContentStream(atDefault);
            Assert.Equal(DecodedSize, atDefault.GetDecodedStreamData(stream)!.Length);
        }

        var tightened = ReaderLimits.Resolve(
            new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });
        using var atTightened = PdfReader.Open(bytes, tightened);
        var tightenedStream = GetPageContentStream(atTightened);
        Assert.Throws<InvalidDataException>(() => atTightened.GetDecodedStreamData(tightenedStream));
    }
}
