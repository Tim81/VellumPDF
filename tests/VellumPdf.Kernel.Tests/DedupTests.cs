// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Verifies that an image or embedded font used in multiple places is written to
/// the PDF only once (shared indirect object) rather than duplicated.
/// </summary>
public sealed class DedupTests
{
    [Fact]
    public void SameImageInstance_onTwoPages_writtenOnce()
    {
        var img = new PdfImageXObject(2, 2, new byte[2 * 2 * 3], PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);

        using var doc = new PdfDocument();
        var p1 = doc.AddPage();
        var p2 = doc.AddPage();
        doc.RegisterImageXObject(p1, img, "Im1");
        doc.RegisterImageXObject(p2, img, "Im1");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Exactly one image XObject dictionary is written despite two-page usage.
        Assert.Equal(1, CountOccurrences(content, "/Subtype /Image"));
    }

    [Fact]
    public void DistinctImageInstances_writtenSeparately()
    {
        var imgA = new PdfImageXObject(2, 2, new byte[12], PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);
        var imgB = new PdfImageXObject(2, 2, new byte[12], PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterImageXObject(page, imgA, "Im1");
        doc.RegisterImageXObject(page, imgB, "Im2");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Equal(2, CountOccurrences(content, "/Subtype /Image"));
    }

    [Fact]
    public void SameFontContent_loadedTwice_sharesOneSubset()
    {
        const string fontPath = @"C:\Windows\Fonts\arial.ttf";
        if (!File.Exists(fontPath)) return; // guarded for CI/Linux

        using var doc = new PdfDocument();
        var h1 = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath));
        var h2 = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath)); // distinct array, same content
        Assert.Same(h1, h2);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
