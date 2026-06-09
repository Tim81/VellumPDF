// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
}
