// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Forms;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for radio button group and push button AcroForm fields.
/// </summary>
public sealed class RadioPushButtonTests
{
    // ── Radio button group ───────────────────────────────────────────────────

    [Fact]
    public void Save_radioGroup_containsFtBtn()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "OptionA"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "OptionB"),
            new RadioOption(page, new PdfRectangle(72, 660, 90, 678), "OptionC"),
        };
        doc.AddRadioButtonGroup("RadioGroup1", options, selectedExportValue: "OptionB");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/FT /Btn", content);
    }

    [Fact]
    public void Save_radioGroup_hasKidsEntry()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "OptionA"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "OptionB"),
            new RadioOption(page, new PdfRectangle(72, 660, 90, 678), "OptionC"),
        };
        doc.AddRadioButtonGroup("RadioGroup1", options, selectedExportValue: "OptionB");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Kids", content);
    }

    [Fact]
    public void Save_radioGroup_selectedValueInVEntry()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "OptionA"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "OptionB"),
            new RadioOption(page, new PdfRectangle(72, 660, 90, 678), "OptionC"),
        };
        doc.AddRadioButtonGroup("RadioGroup1", options, selectedExportValue: "OptionB");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // /V must be /OptionB (the selected export value)
        Assert.Contains("/V /OptionB", content);
    }

    [Fact]
    public void Save_radioGroup_kidWidgetsHaveAsEntry()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "OptionA"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "OptionB"),
            new RadioOption(page, new PdfRectangle(72, 660, 90, 678), "OptionC"),
        };
        doc.AddRadioButtonGroup("RadioGroup1", options, selectedExportValue: "OptionB");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AS", content);
    }

    [Fact]
    public void Save_radioGroup_selectedKidAsMatchesExportValue()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "OptionA"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "OptionB"),
            new RadioOption(page, new PdfRectangle(72, 660, 90, 678), "OptionC"),
        };
        doc.AddRadioButtonGroup("RadioGroup1", options, selectedExportValue: "OptionB");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // The selected kid must have /AS /OptionB
        Assert.Contains("/AS /OptionB", content);
        // Unselected kids must have /AS /Off
        Assert.Contains("/AS /Off", content);
    }

    [Fact]
    public void Save_radioGroup_ffHasRadioBit()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "Yes"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "No"),
        };
        doc.AddRadioButtonGroup("YesNo", options, selectedExportValue: "Yes");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Radio = 1<<15 = 32768; NoToggleToOff = 1<<14 = 16384; combined = 49152
        Assert.Contains("/Ff 49152", content);
    }

    [Fact]
    public void Save_radioGroup_kidWidgetsOnPageAnnots()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "OptionA"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "OptionB"),
            new RadioOption(page, new PdfRectangle(72, 660, 90, 678), "OptionC"),
        };
        doc.AddRadioButtonGroup("RadioGroup1", options, selectedExportValue: "OptionA");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Each kid widget annotation is added to its page, so /Annots must be present.
        Assert.Contains("/Annots", content);
    }

    [Fact]
    public void Save_radioGroup_kidWidgetsAcrossPages_bothPagesHaveAnnots()
    {
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        var page2 = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page1, new PdfRectangle(72, 700, 90, 718), "OptionA"),
            new RadioOption(page2, new PdfRectangle(72, 680, 90, 698), "OptionB"),
            new RadioOption(page1, new PdfRectangle(72, 660, 90, 678), "OptionC"),
        };
        doc.AddRadioButtonGroup("MultiPageRadio", options, selectedExportValue: "OptionA");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // /Annots must appear at least twice (once per page that has kids).
        var count = CountOccurrences(content, "/Annots");
        Assert.True(count >= 2, $"Expected /Annots on both pages, found {count} occurrence(s).");
    }

    [Fact]
    public void Save_radioGroup_isInAcroFormFields()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "A"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "B"),
        };
        doc.AddRadioButtonGroup("Group1", options, selectedExportValue: "A");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AcroForm", content);
        Assert.Contains("/Fields", content);
    }

    [Fact]
    public void Save_radioGroup_noSelectedValue_vIsOff()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "Alpha"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "Beta"),
        };
        doc.AddRadioButtonGroup("Unselected", options, selectedExportValue: null);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/V /Off", content);
    }

    [Fact]
    public void Save_radioGroup_apDictionaryPresent()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var options = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "On"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "Off"),
        };
        doc.AddRadioButtonGroup("Toggle", options, selectedExportValue: "On");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Each kid widget must have /AP.
        Assert.Contains("/AP", content);
        // ZapfDingbats used in on-state appearances.
        Assert.Contains("/ZaDb", content);
    }

    // ── Push button ──────────────────────────────────────────────────────────

    [Fact]
    public void Save_pushButton_containsFtBtn()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "Submit", new PdfRectangle(72, 600, 200, 625), "Submit Form");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/FT /Btn", content);
    }

    [Fact]
    public void Save_pushButton_ffHasPushButtonBit()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "ClickMe", new PdfRectangle(72, 600, 200, 625), "Click Me");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // PushButton bit = 1<<16 = 65536
        Assert.Contains("/Ff 65536", content);
    }

    [Fact]
    public void Save_pushButton_mkCaptionPresent()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "Btn1", new PdfRectangle(72, 600, 200, 625), "OracleCaption");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/MK", content);
        Assert.Contains("/CA (OracleCaption)", content);
    }

    [Fact]
    public void Save_pushButton_mkHasBgAndBcEntries()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "BtnBg", new PdfRectangle(72, 600, 200, 625), "Press");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/BG", content);
        Assert.Contains("/BC", content);
    }

    [Fact]
    public void Save_pushButton_hasApEntry()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "BtnAp", new PdfRectangle(72, 600, 200, 625), "OK");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AP", content);
    }

    [Fact]
    public void Save_pushButton_isInPageAnnots()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "BtnAnnot", new PdfRectangle(72, 600, 200, 625), "Go");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Annots", content);
    }

    [Fact]
    public void Save_pushButton_isInAcroFormFields()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "FieldBtn", new PdfRectangle(72, 600, 200, 625), "Activate");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/AcroForm", content);
        Assert.Contains("/Fields", content);
    }

    [Fact]
    public void Save_pushButton_appearanceContainsHelv()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "HelvBtn", new PdfRectangle(72, 600, 200, 625), "Hello");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Helvetica is used inside the push button appearance XObject.
        Assert.Contains("/Helv", content);
        Assert.Contains("/Subtype /Form", content);
    }

    // ── Radio group + push button in one document ────────────────────────────

    [Fact]
    public void Save_radioGroupAndPushButton_bothPresent()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var radioOptions = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 720, 90, 738), "Red"),
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "Green"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "Blue"),
        };
        doc.AddRadioButtonGroup("Color", radioOptions, selectedExportValue: "Green");
        doc.AddPushButton(page, "Submit", new PdfRectangle(72, 640, 200, 660), "Submit");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Both fields use /FT /Btn.
        var btnCount = CountOccurrences(content, "/FT /Btn");
        Assert.True(btnCount >= 2, $"Expected at least 2 /FT /Btn entries, found {btnCount}.");

        // Radio group
        Assert.Contains("/Kids", content);
        Assert.Contains("/V /Green", content);
        Assert.Contains("/Ff 49152", content); // Radio | NoToggleToOff

        // Push button
        Assert.Contains("/Ff 65536", content); // PushButton
        Assert.Contains("/CA (Submit)", content);

        // AcroForm wiring
        Assert.Contains("/AcroForm", content);
        Assert.Contains("/Fields", content);
        Assert.Contains("/Annots", content);
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
