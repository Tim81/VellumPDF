// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// One test per resource ceiling <c>ExtractImages</c> enforces (#98), named after the condition it
/// pins. Where a test bounds memory, the call is bracketed with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> under a stated bound, following the
/// <c>ContentInterpreterTests</c> precedent; that counter is thread-local, never process-wide, and
/// no assertion here depends on wall-clock time (#400).
/// </summary>
public sealed class ImageExtractionBoundsTests
{
    private sealed record Obj(int Num, string Dict, byte[]? Stream = null);

    private static byte[] BuildPdf(int rootObjectNumber, params Obj[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");

        var maxNum = objects.Max(o => o.Num);
        var offsets = new int?[maxNum + 1];
        foreach (var obj in objects.OrderBy(o => o.Num))
        {
            offsets[obj.Num] = (int)ms.Position;
            if (obj.Stream is null)
            {
                W($"{obj.Num} 0 obj\n{obj.Dict}\nendobj\n");
            }
            else
            {
                var trimmed = obj.Dict.TrimEnd();
                var withLength = trimmed[..^2].TrimEnd() + $" /Length {obj.Stream.Length} >>";
                W($"{obj.Num} 0 obj\n{withLength}\nstream\n");
                ms.Write(obj.Stream);
                W("\nendstream\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {maxNum + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= maxNum; i++)
        {
            W(offsets[i] is { } offset
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        }
        W($"trailer\n<< /Size {maxNum + 1} /Root {rootObjectNumber} 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Flate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    // ── /Width and /Height integers 1..int.MaxValue ─────────────────────────────────────────────

    [Fact]
    public void WidthOutsideIntRange_reports500_skipped()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 99999999999 /Height 1 "
                + "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    // ── rowBytes * Height, no overflow, no allocation past the bound ───────────────────────────

    [Fact]
    public void RowBytesTimesHeightExceedsLimit_reports507_noException_boundedAllocation()
    {
        // /Width /Height 2147483647 at DeviceRGB 8: rowBytes = 6,442,450,941 (fits a long; would
        // overflow a checked int product before ever reaching Height, so the multiplication
        // computing it must be done in long arithmetic throughout).
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 2147483647 /Height 2147483647 "
                + "/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /FlateDecode >>", Flate([1])));

        using var reader = PdfReader.Open(pdf, new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = reader.ExtractImages();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageLimitExceeded);
        // No sample buffer (would need billions of bytes) was ever allocated; a generous ceiling
        // for bookkeeping (dictionaries, diagnostics, the result list) still catches a regression
        // that allocates anywhere near the image's own claimed size.
        Assert.True(allocated < 4 * 1024 * 1024, $"expected a bounded allocation, measured {allocated} bytes");
    }

    // ── passthrough payload size, checked before DecryptedStreamView's own copy ────────────────

    [Fact]
    public void PassthroughPayloadExceedsLimit_reports507_skipped()
    {
        var oversized = new byte[ReaderLimits.MinMaxDecodedBytes + 1024];
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /DCTDecode >>", oversized));

        using var reader = PdfReader.Open(pdf, new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });
        var result = reader.ExtractImages();

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageLimitExceeded);
    }

    // ── aggregate byte budget across one call ──────────────────────────────────────────────────

    [Fact]
    public void AggregateBudgetAcrossCall_reports510Once_remainderSkipped()
    {
        // Three images, each just under half the (tightened) per-call budget: the first two fit,
        // the third pushes the running total over, and a fourth must never even attempt decoding.
        var limit = ReaderLimits.MinMaxDecodedBytes; // 1 MiB
        var chunk = (int)(limit / 3) + 1024;
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R /Im1 11 0 R /Im2 12 0 R /Im3 13 0 R >> >> "
                + "/Contents 4 0 R >>"),
            new(4, "<< >>", "/Im0 Do\n/Im1 Do\n/Im2 Do\n/Im3 Do"u8.ToArray()),
        };
        for (var i = 0; i < 4; i++)
        {
            objs.Add(new Obj(10 + i,
                $"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + $"/ColorSpace /DeviceGray /Filter /DCTDecode >>", new byte[chunk]));
        }
        var pdf = BuildPdf(1, [.. objs]);

        using var reader = PdfReader.Open(pdf, new PdfReaderOptions { MaxDecodedStreamBytes = limit });
        var result = reader.ExtractImages();

        Assert.True(result.Images.Count < 4, "expected at least one image to be skipped once the budget ran out");
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageExtractionBudgetExhausted);
    }

    /// <summary>
    /// Charges the budget against what a Raw image's decode retains, not the declared
    /// <c>rowBytes * Height</c>: each image here declares a 1x1 sample buffer (1 byte) but its
    /// Flate body inflates to 700,000 bytes. Charging the declared size would let arbitrarily many
    /// of these fit inside a 1 MiB budget while retaining hundreds of times that; charging the
    /// retained size means the second one already exceeds it.
    /// </summary>
    [Fact]
    public void AggregateBudgetAcrossCall_chargesDecodedSize_notDeclaredSize_forCompressedRawImages()
    {
        var limit = ReaderLimits.MinMaxDecodedBytes; // 1 MiB
        var inflated = new byte[700_000];
        var compressed = Flate(inflated);
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R /Im1 11 0 R >> >> /Contents 4 0 R >>"),
            new(4, "<< >>", "/Im0 Do\n/Im1 Do"u8.ToArray()),
        };
        for (var i = 0; i < 2; i++)
        {
            objs.Add(new Obj(10 + i,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", compressed));
        }
        var pdf = BuildPdf(1, [.. objs]);

        using var reader = PdfReader.Open(pdf, new PdfReaderOptions { MaxDecodedStreamBytes = limit });
        var result = reader.ExtractImages();

        var extracted = Assert.Single(result.Images);
        Assert.Equal(inflated.Length, extracted.Data.Length);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageExtractionBudgetExhausted);

        long totalBytes = 0;
        foreach (var image in result.Images)
            totalBytes += image.Data.Length;
        Assert.True(totalBytes <= limit, $"sum of Data.Length ({totalBytes}) exceeds the limit ({limit})");
    }

    // ── occurrence cap (100,000), shared by drawn/inline images and derived masks ──────────────

    [Fact]
    public void OccurrenceLimit_reports511Once_remainderSkipped()
    {
        // 100,001 inline images on one page: the cheapest way to reach the occurrence cap without
        // registering that many indirect objects. Each is a 1-byte, undeclared-length image so the
        // tier-c EI scan delimits it; content is otherwise minimal.
        const int Count = ImageCallBudget.MaxImageOccurrencesPerCall + 1;
        var content = new StringBuilder();
        for (var i = 0; i < Count; i++)
            content.Append("BI /W 1 /H 1 /CS /G /BPC 8 ID \x01 EI\n");
        var bytes = Encoding.Latin1.GetBytes(content.ToString());

        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R >>"),
            new Obj(4, "<< >>", bytes));

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();

        Assert.Equal(ImageCallBudget.MaxImageOccurrencesPerCall, result.Images.Count);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageOccurrenceLimitExceeded);
    }

    // ── /Annots work per page ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AnnotsIsNotAnArray_reports509()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R /Annots 5 0 R >>"),
            new Obj(4, "<< >>", []),
            new Obj(5, "<< /NotAnArray true >>"));

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.AnnotationAppearanceUnusable);
    }

    [Fact]
    public void AnnotsExceedsElementCap_reports509_furtherElementsNotExamined()
    {
        const int Count = 4096 + 1;
        var annotsArray = new StringBuilder("[");
        for (var i = 0; i < Count; i++)
            annotsArray.Append("5 0 R ");
        annotsArray.Append(']');

        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + $"/Contents 4 0 R /Annots {annotsArray} >>"),
            new Obj(4, "<< >>", []),
            new Obj(5, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] >>"));

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.AnnotationAppearanceUnusable);
    }

    [Fact]
    public void DistinctAppearanceStreamsExceedsCap_reports509_furtherOnesNotRun()
    {
        const int Count = 1024 + 1;
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };
        var annotsArray = new StringBuilder("[");
        for (var i = 0; i < Count; i++)
        {
            var annotNum = 100 + i * 2;
            var apNum = annotNum + 1;
            annotsArray.Append($"{annotNum} 0 R ");
            objs.Add(new Obj(annotNum,
                $"<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /AP << /N {apNum} 0 R >> >>"));
            objs.Add(new Obj(apNum, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] >>", []));
        }
        annotsArray.Append(']');

        objs.Add(new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
            + $"/Contents 4 0 R /Annots {annotsArray} >>"));
        objs.Add(new Obj(4, "<< >>", []));
        var pdf = BuildPdf(1, [.. objs]);

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();

        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.AnnotationAppearanceUnusable);
    }

    // ── decoded chain / underlying decode failure: 508 plus the underlying 1xx code ─────────────

    [Fact]
    public void DecodedChainFails_reports508AndUnderlying1xxCode_imageSkipped()
    {
        // Declares 1x1 (rowBytes * Height = 1 byte), well under the pre-decode 507 check, but the
        // Flate body inflates to 2,000,000 bytes: past the tightened 1 MiB MaxDecodedStreamBytes,
        // so the decompression-bomb guard inside FlateDecode itself throws, reporting 111
        // (DecodedStreamLimitExceeded) before BuildImage reports 508 (ImageDataUnreadable) over the
        // resulting decode failure.
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate(new byte[2_000_000])));

        using var reader = PdfReader.Open(
            pdf, new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });
        var result = reader.ExtractImages();

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDataUnreadable);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
    }

    // ── colour space: Indexed lookup decode bounded to a small multiple of what it needs ────────

    [Fact]
    public void IndexedLookupStreamInflatesFarPastWhatItNeeds_reports501_boundedAllocation()
    {
        // hival 3 over DeviceRGB needs (3 + 1) * 3 = 12 bytes; the lookup stream here inflates to
        // 4,000,000, which this reader refuses to decode in full just to keep those 12.
        var oversizedLookup = Flate(new byte[4_000_000]);
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace [/Indexed /DeviceRGB 3 11 0 R] /Filter /FlateDecode >>", Flate([0])),
            new Obj(11, "<< /Filter /FlateDecode >>", oversizedLookup));

        using var reader = PdfReader.Open(pdf);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = reader.ExtractImages();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
        // The cap this decode is refused against is this reader's own multiple of the lookup's
        // expected length, not the caller's MaxDecodedStreamBytes (512 MiB by default here), so the
        // 501 above is the only diagnostic this refusal raises: DecodedStreamLimitExceeded names a
        // limit the caller never configured, and would mislead one reading it alone.
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
        // Well under the 4,000,000-byte inflated lookup: a generous ceiling that still catches a
        // regression that decodes the lookup stream in full before refusing it.
        Assert.True(allocated < 1024 * 1024, $"expected a bounded allocation, measured {allocated} bytes");
    }

    // A lookup stream's decode raises its diagnostics against a cap this reader chose, so the one
    // that names that cap is dropped. Every other code it can raise belongs to the caller: an
    // unknown filter says nothing about the cap and is the caller's own file to fix.
    [Fact]
    public void IndexedLookupStreamWithAnUnknownFilter_reports110AndTheColorSpaceCode()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace [/Indexed /DeviceRGB 3 11 0 R] /Filter /FlateDecode >>", Flate([0])),
            new Obj(11, "<< /Filter /NoSuchFilter >>", [1, 2, 3, 4]));

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();

        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnknownFilter);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DecodedStreamLimitExceeded);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnknownFilter);
    }

    // ── colour space unknown: /Decode array length has its own cap, not just a known one ────────

    [Fact]
    public void DecodeArrayUnboundedWhenColorSpaceUnknown_reports502_imageStillReturned()
    {
        var decodeArray = new StringBuilder("[");
        for (var i = 0; i < 10_000; i++)
            decodeArray.Append("0 1 ");
        decodeArray.Append(']');

        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + $"/Filter /DCTDecode /Decode {decodeArray} >>", "JPEG"u8.ToArray()));

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Null(extracted.Decode);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDecodeArrayInvalid);
    }

    // ── appearance content/invocations share the page's own Run budgets ────────────────────────

    [Fact]
    public void AnnotationAppearance_sharesThePagesOwnBudgets_bySharingOneInterpreterInstance()
    {
        // Covered directly at the interpreter level by ContentInterpreterTests' own
        // RunFormXObject_doesNotResetTheContentBudget_reportsContentStreamTooLarge and
        // RunFormXObject_afterFormInvocationBudgetSpent_reportsFormXObjectBudgetExceeded,
        // which assert the exact mechanism (RunFormXObject resets neither budget). This test pins
        // the SAME contract from ExtractImages' own public surface: a page whose content already
        // exhausts the 4096-invocation budget still runs its annotation's own appearance stream
        // (RunFormXObject is always called, regardless of the page's own remaining budget; the
        // budget is enforced by RunFormXObject/InvokeForm's own internal checks when THAT stream
        // tries to invoke a further nested form, not by skipping the appearance stream's own single
        // entry), and finds no image drawn only inside a nested form past that cap.
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };
        var drawOps = new StringBuilder();
        for (var i = 100; i < 100 + 4096; i++)
        {
            objs.Add(new Obj(i, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] >>", []));
            drawOps.Append($"/F{i} Do\n");
        }
        var xobjectDict = new StringBuilder("<< ");
        for (var i = 100; i < 100 + 4096; i++)
            xobjectDict.Append($"/F{i} {i} 0 R ");
        xobjectDict.Append(">>");

        objs.Add(new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
            + $"/Resources << /XObject {xobjectDict} >> /Contents 4 0 R /Annots [5 0 R] >>"));
        objs.Add(new Obj(4, "<< >>", Encoding.Latin1.GetBytes(drawOps.ToString())));
        objs.Add(new Obj(5, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /AP << /N 20 0 R >> >>"));
        // The appearance's own form invokes ONE further nested form (21), which the page's own
        // 4096-invocation budget (already spent inside Run) must still refuse.
        objs.Add(new Obj(20, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
            + "/Resources << /XObject << /Nested 21 0 R >> >> >>", "/Nested Do"u8.ToArray()));
        objs.Add(new Obj(21, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
            + "/Resources << /XObject << /Im0 10 0 R >> >> >>", "/Im0 Do"u8.ToArray()));
        objs.Add(new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        var pdf = BuildPdf(1, [.. objs]);

        using var reader = PdfReader.Open(pdf);
        var result = reader.ExtractImages();

        Assert.Empty(result.Images); // The nested form inside the appearance never ran.
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FormXObjectBudgetExceeded);
    }
}
