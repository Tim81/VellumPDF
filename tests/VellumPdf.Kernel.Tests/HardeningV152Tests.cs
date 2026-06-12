// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Forms;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Regression tests for the v1.5.2 hardening findings
/// (issues #74, #75, #80a-e, #83a-d).
/// All inputs are synthesised in-memory; no external resources are required.
/// </summary>
public sealed class HardeningV152Tests
{
    // ── #75: Content-stream name injection ──────────────────────────────────

    [Fact]
    public void BeginMarkedContent_tagWithInjectionChars_producesEscapedToken()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.BeginText();
        // Tag containing ") Tj /Bad Do" — injection attempt
        canvas.BeginMarkedContent(") Tj /Bad Do");
        canvas.EndMarkedContent();
        canvas.EndText();
        canvas.Finish();

        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        // Raw injection string must not appear in the content stream
        Assert.DoesNotContain(") Tj /Bad Do", content, StringComparison.Ordinal);
        // Space (0x20) = #20, ) (0x29) = #29, / (0x2F) = #2F
        Assert.Contains("/#29#20Tj#20#2FBad#20Do", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DoXObject_nameWithSpace_producesEscapedToken()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.DoXObject("Im 1"); // space in resource name
        canvas.Finish();

        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        Assert.DoesNotContain("/Im 1 Do", content, StringComparison.Ordinal);
        Assert.Contains("/Im#201 Do", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SetFontByName_nameWithSlash_producesEscapedToken()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.SetFontByName("F/1", 12.0);
        canvas.Finish();

        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        Assert.DoesNotContain("/F/1 ", content, StringComparison.Ordinal);
        Assert.Contains("/F#2F1 ", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SetFillColorSpace_nameWithParenthesis_producesEscapedToken()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.SetFillColorSpace("CS(0)");
        canvas.Finish();

        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        Assert.DoesNotContain("/CS(0) cs", content, StringComparison.Ordinal);
        Assert.Contains("/CS#280#29 cs", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SetStrokeColorSpace_nameWithParenthesis_producesEscapedToken()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.SetStrokeColorSpace("CS(0)");
        canvas.Finish();

        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        Assert.DoesNotContain("/CS(0) CS", content, StringComparison.Ordinal);
        Assert.Contains("/CS#280#29 CS", content, StringComparison.Ordinal);
    }

    // ── #80a: xref table offset >10 digits ──────────────────────────────────

    [Fact]
    public void CrossReferenceBuilder_Write10Digits_above9999999999_throwsNotSupportedException()
    {
        var type = typeof(CrossReferenceBuilder);
        var method = type.GetMethod("Write10Digits",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        var ex = Assert.Throws<TargetInvocationException>(() =>
            method!.Invoke(null, new object[] { writer, 10_000_000_001L }));
        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    [Fact]
    public void CrossReferenceBuilder_Write10Digits_maxValid_doesNotThrow()
    {
        var type = typeof(CrossReferenceBuilder);
        var method = type.GetMethod("Write10Digits",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        // 9_999_999_999 is the maximum representable value — must not throw
        method!.Invoke(null, new object[] { writer, 9_999_999_999L });
        Assert.Equal(10, ms.Length);
    }

    // ── #80c: PdfStream.WriteTo is idempotent (no Dictionary mutation) ───────

    [Fact]
    public void PdfStream_writtenTwice_yieldsIdenticalBytes()
    {
        var data = Encoding.ASCII.GetBytes("Hello PDF stream idempotency test");
        var stream = new PdfStream(data);

        using var ms1 = new MemoryStream();
        var w1 = new PdfWriter(ms1);
        stream.WriteTo(w1);

        using var ms2 = new MemoryStream();
        var w2 = new PdfWriter(ms2);
        stream.WriteTo(w2);

        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void PdfStream_WriteTo_doesNotMutateSharedDictionary()
    {
        var data = new byte[] { 1, 2, 3 };
        var stream = new PdfStream(data);

        // Nothing in Dictionary yet
        Assert.Null(stream.Dictionary.Get(PdfName.Filter));
        Assert.Null(stream.Dictionary.Get(PdfName.Length));

        using var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        stream.WriteTo(w);

        // Dictionary must remain unmodified after WriteTo
        Assert.Null(stream.Dictionary.Get(PdfName.Filter));
        Assert.Null(stream.Dictionary.Get(PdfName.Length));
    }

    // ── #80d: XMP control characters stripped ───────────────────────────────

    [Fact]
    public void PdfDocument_metadataWithControlChar_xmpDoesNotContainControlChar()
    {
        using var doc = new PdfDocument();
        // Title contains U+0001 (XML 1.0 illegal): "TestTitle"
        doc.Info.Title = "TestTitle";
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);

        // Locate the XMP packet in the raw PDF output and verify the control char was stripped.
        var pdfText = Encoding.UTF8.GetString(ms.ToArray());
        var xmpStart = pdfText.IndexOf("<?xpacket", StringComparison.Ordinal);
        var xmpEnd = pdfText.LastIndexOf("xpacket end", StringComparison.Ordinal);
        Assert.True(xmpStart >= 0, "XMP packet not found in output");
        var xmpPart = xmpStart < xmpEnd ? pdfText[xmpStart..xmpEnd] : pdfText[xmpStart..];
        Assert.DoesNotContain("", xmpPart, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfDocument_metadataWithControlChar_titleTextPreserved()
    {
        using var doc = new PdfDocument();
        // U+0001 should be stripped; the surrounding text must survive
        doc.Info.Title = "TestTitle";
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);

        var pdfText = Encoding.UTF8.GetString(ms.ToArray());
        var xmpStart = pdfText.IndexOf("<?xpacket", StringComparison.Ordinal);
        Assert.True(xmpStart >= 0, "XMP packet not found in output");
        var xmpPart = pdfText[xmpStart..];
        // "Test" and "Title" must survive; the control char between them should be gone
        Assert.Contains("TestTitle", xmpPart, StringComparison.Ordinal);
    }

    // ── #80e: N() precision alignment (5dp) ─────────────────────────────────

    [Fact]
    public void PdfCanvas_N_usesUpTo5DecimalPlaces()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        // Use a value that differs at the 5th decimal place
        canvas.SetLineWidth(1.12345);
        canvas.Finish();

        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        Assert.Contains("1.12345 w", content, StringComparison.Ordinal);
    }

    // ── #74: Save is not idempotent ──────────────────────────────────────────

    [Fact]
    public void PdfDocument_saveTwice_throwsOnSecondSave()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms1 = new MemoryStream();
        doc.Save(ms1);
        using var ms2 = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => doc.Save(ms2));
    }

    [Fact]
    public void PdfDocument_freshDocument_savesSuccessfully()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void PdfDocument_prepareForSigningTwice_throwsOnSecond()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var opts = new SignaturePlaceholderOptions();
        doc.PrepareForSigning(opts);
        Assert.Throws<InvalidOperationException>(() => doc.PrepareForSigning(opts));
    }

    // ── #83a: Zero-page guard ────────────────────────────────────────────────

    [Fact]
    public void PdfDocument_saveWithNoPages_throwsInvalidOperationException()
    {
        using var doc = new PdfDocument();
        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
        Assert.Contains("no pages", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfDocument_prepareForSigningWithNoPages_throwsInvalidOperationException()
    {
        using var doc = new PdfDocument();
        var opts = new SignaturePlaceholderOptions();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.PrepareForSigning(opts));
        Assert.Contains("no pages", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── #83b: Non-ASCII URI percent-encoding ─────────────────────────────────

    [Fact]
    public void PdfLinkAnnotation_uriWithNonAsciiChar_getsPercentEncoded()
    {
        var annot = new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(0, 0, 100, 20),
            Uri = "https://example.com/café" // é = U+00E9
        };

        var dict = annot.BuildDictionary(destPageRef: null);

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        dict.WriteTo(writer);
        var content = Encoding.ASCII.GetString(ms.ToArray());

        // Latin-1 fallback '?' must not appear
        Assert.DoesNotContain("?", content, StringComparison.Ordinal);
        // é = U+00E9, UTF-8 = 0xC3 0xA9 → %C3%A9
        Assert.Contains("%C3%A9", content, StringComparison.Ordinal);
    }

    // ── #83c: AcroForm duplicate field names ────────────────────────────────

    [Fact]
    public void PdfDocument_duplicateTextFieldName_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(0, 0, 100, 20);
        doc.AddTextField(page, "myField", rect);
        var ex = Assert.Throws<ArgumentException>(() => doc.AddTextField(page, "myField", rect));
        Assert.Contains("myField", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfDocument_duplicateCheckBoxName_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(0, 0, 20, 20);
        doc.AddCheckBox(page, "cb1", rect);
        Assert.Throws<ArgumentException>(() => doc.AddCheckBox(page, "cb1", rect));
    }

    [Fact]
    public void PdfDocument_duplicateRadioGroupName_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var opts = new[]
        {
            new RadioOption(page, new PdfRectangle(0, 0, 20, 20), "ValA"),
            new RadioOption(page, new PdfRectangle(0, 30, 20, 20), "ValB"),
        };
        doc.AddRadioButtonGroup("group1", opts);
        var opts2 = new[] { new RadioOption(page, new PdfRectangle(0, 60, 20, 20), "ValC") };
        Assert.Throws<ArgumentException>(() => doc.AddRadioButtonGroup("group1", opts2));
    }

    [Fact]
    public void PdfDocument_radioGroupWithOffExportValue_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var opts = new[] { new RadioOption(page, new PdfRectangle(0, 0, 20, 20), "Off") };
        var ex = Assert.Throws<ArgumentException>(() => doc.AddRadioButtonGroup("group1", opts));
        Assert.Contains("Off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfDocument_radioGroupWithDuplicateExportValues_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var opts = new[]
        {
            new RadioOption(page, new PdfRectangle(0, 0, 20, 20), "Val1"),
            new RadioOption(page, new PdfRectangle(0, 30, 20, 20), "Val1"),
        };
        var ex = Assert.Throws<ArgumentException>(() => doc.AddRadioButtonGroup("group1", opts));
        Assert.Contains("Val1", ex.Message, StringComparison.Ordinal);
    }
}
