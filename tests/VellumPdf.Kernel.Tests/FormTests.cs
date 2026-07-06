// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Forms;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for AcroForm field generation (text fields, checkboxes, choice/dropdown).
/// </summary>
public sealed class FormTests
{
    // ── No-form document must NOT include /AcroForm ─────────────────────────

    [Fact]
    public void Save_noFields_doesNotContainAcroForm()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.DoesNotContain("/AcroForm", content);
    }

    // ── Text field ────────────────────────────────────────────────────────────

    [Fact]
    public void Save_withTextField_containsAcroFormAndWidgetKeys()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 700, 300, 720);
        doc.AddTextField(page, "FullName", rect, value: "FieldValABC");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AcroForm", content);
        Assert.Contains("/Fields", content);
        Assert.Contains("/FT /Tx", content);
        Assert.Contains("/Subtype /Widget", content);
        Assert.Contains("(FieldValABC)", content);
        Assert.Contains("/AP", content);
    }

    [Fact]
    public void Save_withTextField_pageHasAnnots()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 700, 300, 720);
        doc.AddTextField(page, "MyField", rect, value: "hello");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Annots", content);
    }

    [Fact]
    public void Save_withTextField_readOnlyFlag_setsCorrectFf()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 700, 300, 720);
        doc.AddTextField(page, "ROField", rect, options: new FormFieldOptions { ReadOnly = true });

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // ReadOnly bit = 1, so /Ff 1 must appear
        Assert.Contains("/Ff 1", content);
    }

    [Fact]
    public void Save_withTextField_multilineFlag_setsBit13()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 600, 400, 700);
        doc.AddTextField(page, "Notes", rect, options: new FormFieldOptions { Multiline = true });

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Multiline = 1<<12 = 4096
        Assert.Contains("/Ff 4096", content);
    }

    // ── Checkbox field ────────────────────────────────────────────────────────

    [Fact]
    public void Save_withCheckBox_containsRequiredKeys()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 680, 90, 698);
        doc.AddCheckBox(page, "Agree", rect, checkedState: true);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AcroForm", content);
        Assert.Contains("/FT /Btn", content);
        Assert.Contains("/Subtype /Widget", content);
        Assert.Contains("/AP", content);
        Assert.Contains("/V /Yes", content);
        Assert.Contains("/AS /Yes", content);
    }

    [Fact]
    public void Save_withCheckBox_unchecked_hasOffState()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 660, 90, 678);
        doc.AddCheckBox(page, "Terms", rect, checkedState: false);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/V /Off", content);
        Assert.Contains("/AS /Off", content);
    }

    // ── Choice (dropdown) field ───────────────────────────────────────────────

    [Fact]
    public void Save_withChoiceField_containsRequiredKeys()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 650, 300, 670);
        var options = new[] { "Option A", "Option B", "Option C" };
        doc.AddChoiceField(page, "Color", rect, options, selected: "Option B");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AcroForm", content);
        Assert.Contains("/FT /Ch", content);
        Assert.Contains("/Subtype /Widget", content);
        Assert.Contains("/AP", content);
    }

    [Fact]
    public void Save_withChoiceField_comboFlag_setsBit18()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 640, 300, 660);
        var options = new[] { "Red", "Green", "Blue" };
        doc.AddChoiceField(page, "PickColor", rect, options, combo: true);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Combo bit = 1<<17 = 131072
        Assert.Contains("/Ff 131072", content);
    }

    // ── All three field types in one document ─────────────────────────────────

    [Fact]
    public void Save_allThreeFieldTypes_allPresentInOutput()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // Text field
        doc.AddTextField(page, "Name", new PdfRectangle(72, 720, 300, 740), value: "FieldValABC");

        // Checkbox
        doc.AddCheckBox(page, "Accept", new PdfRectangle(72, 700, 90, 718), checkedState: true);

        // Dropdown
        doc.AddChoiceField(page, "Choice", new PdfRectangle(72, 680, 300, 700),
            ["Alpha", "Beta", "Gamma"], selected: "Beta");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AcroForm", content);
        Assert.Contains("/Fields", content);
        Assert.Contains("/FT /Tx", content);
        Assert.Contains("/FT /Btn", content);
        Assert.Contains("/FT /Ch", content);
        Assert.Contains("/Subtype /Widget", content);
        Assert.Contains("(FieldValABC)", content);
        Assert.Contains("/AP", content);
        Assert.Contains("/Annots", content);
    }

    [Fact]
    public void Save_allThreeFieldTypes_needAppearancesIsFalse()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddTextField(page, "F1", new PdfRectangle(72, 720, 300, 740));
        doc.AddCheckBox(page, "F2", new PdfRectangle(72, 700, 90, 718));
        doc.AddChoiceField(page, "F3", new PdfRectangle(72, 680, 300, 700), ["X", "Y"]);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/NeedAppearances false", content);
    }

    // ── Appearance stream helpers ─────────────────────────────────────────────

    [Fact]
    public void Save_textField_appearanceStreamContainsHelvFont()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddTextField(page, "T1", new PdfRectangle(50, 700, 250, 720), value: "Hello");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // /Helv must appear as both a /DR font reference and inside the appearance XObject
        Assert.Contains("/Helv", content);
        Assert.Contains("/Subtype /Form", content);
    }

    [Fact]
    public void Save_checkBox_appearanceStreamContainsZaDb()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddCheckBox(page, "CB1", new PdfRectangle(50, 700, 70, 720), checkedState: true);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/ZaDb", content);
    }

    // ── DA (default appearance) ───────────────────────────────────────────────

    [Fact]
    public void Save_textField_daContainsFontSize()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddTextField(page, "DA_Test", new PdfRectangle(72, 700, 300, 720),
            options: new FormFieldOptions { FontSize = 14 });

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Helv 14 Tf", content);
    }

    // ── Multiple fields on multiple pages ─────────────────────────────────────

    [Fact]
    public void Save_fieldsOnTwoPages_bothPagesHaveAnnots()
    {
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        var page2 = doc.AddPage();

        doc.AddTextField(page1, "P1Field", new PdfRectangle(72, 720, 300, 740));
        doc.AddTextField(page2, "P2Field", new PdfRectangle(72, 720, 300, 740));

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // /Annots must appear for both pages. Count occurrences.
        var count = CountOccurrences(content, "/Annots");
        Assert.True(count >= 2, $"Expected /Annots on both pages, found {count} occurrence(s).");
    }

    // ── Signature1 field-name reservation (issue #83c) ───────────────────────

    /// <summary>
    /// "Signature1" is pre-seeded in <c>_fieldNames</c> so that a caller-added form
    /// field cannot collide with the invisible signature widget that the signing path
    /// emits under the name "Signature1" (ISO 32000-2 §12.7.4.2 prohibits duplicate
    /// fully-qualified field names).
    ///
    /// This test verifies that <c>AddTextField</c> with the reserved name throws
    /// <see cref="ArgumentException"/> containing the duplicate-field message.
    /// </summary>
    [Fact]
    public void AddTextField_nameIsSignature1_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(72, 700, 300, 720);

        var ex = Assert.Throws<ArgumentException>(() =>
            doc.AddTextField(page, "Signature1", rect));

        Assert.Contains("Signature1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A field with a name OTHER than "Signature1" can be added and saved without error.
    /// Positive counterpart to <see cref="AddTextField_nameIsSignature1_throwsArgumentException"/>.
    /// </summary>
    [Fact]
    public void AddTextField_nameNotSignature1_succeedsAndAppearsinOutput()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(72, 700, 300, 720);

        // Must not throw.
        doc.AddTextField(page, "ContactName", rect, value: "Alice");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AcroForm", content);
        Assert.Contains("/FT /Tx", content);
    }

    // ── WinAnsi punctuation in Helv appearance text ──────────────────────────

    /// <summary>
    /// A text field value containing an accented Latin-1 char (é) and a WinAnsi 0x80-0x9F
    /// punctuation char (•) must render as their WinAnsi bytes in the /Helv appearance stream.
    /// The /Helv and /ZaDb font dicts must still declare encoding independently — the fix must
    /// not leak into the ZapfDingbats checkbox/radio path.
    /// </summary>
    [Fact]
    public void Save_textFieldWithWinAnsiPunctuation_appearanceContainsWinAnsiBytes()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var rect = new PdfRectangle(72, 700, 300, 720);
        doc.AddTextField(page, "Note", rect, value: "café • rate"); // é (0xE9), bullet (0x95)

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        var literal = ExtractFirstTjLiteral(bytes);
        Assert.Contains((byte)0xE9, literal);
        Assert.Contains((byte)0x95, literal);

        var content = Encoding.Latin1.GetString(bytes);
        var helvObject = ExtractObjectContaining(content, "/BaseFont /Helvetica");
        Assert.Contains("/Encoding /WinAnsiEncoding", helvObject, StringComparison.Ordinal);

        var zadbObject = ExtractObjectContaining(content, "/BaseFont /ZapfDingbats");
        Assert.DoesNotContain("/Encoding", zadbObject, StringComparison.Ordinal);
    }

    /// <summary>
    /// A checkbox's /Yes appearance still draws ZapfDingbats char '4' (✔) untouched by the
    /// Helv/WinAnsi fix. <see cref="AcroFormBuilder"/> builds it directly, never through
    /// <c>EscapePdfString</c>, and its /ZaDb font dict still has no /Encoding entry.
    /// </summary>
    [Fact]
    public void Save_checkBox_yesAppearanceStillDrawsZaDbGlyphWithNoEncoding()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddCheckBox(page, "Agree", new PdfRectangle(72, 680, 90, 698), checkedState: true);

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        var literal = ExtractFirstTjLiteral(bytes);
        byte[] expected = [(byte)'4'];
        Assert.Equal(expected, literal);

        var content = Encoding.Latin1.GetString(bytes);
        var zadbObject = ExtractObjectContaining(content, "/BaseFont /ZapfDingbats");
        Assert.DoesNotContain("/Encoding", zadbObject, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    /// <summary>
    /// Returns the object body (from " obj" up to but excluding "endobj") of the first
    /// indirect object in <paramref name="content"/> whose dictionary contains <paramref name="marker"/>.
    /// </summary>
    private static string ExtractObjectContaining(string content, string marker)
    {
        var markerIdx = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIdx >= 0, $"Marker '{marker}' not found in the saved PDF.");

        var objStart = content.LastIndexOf(" obj\n", markerIdx, StringComparison.Ordinal);
        Assert.True(objStart >= 0, $"No enclosing ' obj' found before '{marker}'.");

        var objEnd = content.IndexOf("\nendobj", markerIdx, StringComparison.Ordinal);
        Assert.True(objEnd >= 0, $"No enclosing 'endobj' found after '{marker}'.");

        return content[objStart..objEnd];
    }

    /// <summary>
    /// Scans every FlateDecode stream in <paramref name="pdfBytes"/> in order and returns the
    /// bytes strictly between the first '(' and the following ") Tj" in the first stream that
    /// contains one (an appearance stream's Tj literal). A page's own content stream — empty
    /// for these form-only test documents — is skipped because it has no such literal.
    /// </summary>
    private static byte[] ExtractFirstTjLiteral(byte[] pdfBytes)
    {
        var searchFrom = 0;
        while (true)
        {
            var streamStart = FindSequence(pdfBytes, "\nstream\n"u8, searchFrom);
            Assert.True(streamStart >= 0, "No stream with a Tj literal found in the PDF.");

            var dataStart = streamStart + 8; // length of "\nstream\n"
            var streamEnd = FindSequence(pdfBytes, "\nendstream"u8, dataStart);
            Assert.True(streamEnd >= 0, "No matching endstream found in the PDF.");

            var decompressed = Decompress(pdfBytes[dataStart..streamEnd]);

            var litStart = Array.IndexOf(decompressed, (byte)'(');
            if (litStart >= 0)
            {
                var litEnd = FindSequence(decompressed, ") Tj"u8, litStart);
                if (litEnd >= 0)
                    return decompressed[(litStart + 1)..litEnd];
            }

            searchFrom = streamEnd;
        }
    }

    private static byte[] Decompress(byte[] compressed)
    {
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
