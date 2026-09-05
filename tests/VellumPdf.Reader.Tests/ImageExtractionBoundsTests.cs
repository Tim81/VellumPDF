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
