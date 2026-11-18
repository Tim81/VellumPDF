// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #544: an <see cref="VellumPdf.Fonts.EmbeddedFontHandle"/> is only meaningful to the document that
/// embedded it. Its resource name is a counter local to that document, so two documents can each
/// hold a different font called <c>TT1</c>, and usage is recorded by that name. A handle from another
/// document was recorded without question and the page saved with a font its resources do not
/// define, or, when the saving document had its own <c>TT1</c>, with that other font silently.
/// </summary>
public sealed class EmbeddedFontOwnershipTests
{
    // Copied beside the test assembly by the project file; the two faces differ, which is all this
    // needs. Their text is "Hello", which both have.
    private static readonly string Sans = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");
    private static readonly string Bold = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Bold.ttf");

    private static (byte[] First, byte[] Second) TwoDifferentFonts()
    {
        if (!File.Exists(Sans) || !File.Exists(Bold))
            Assert.Skip("Liberation Sans Regular and Bold are both needed to give two documents different fonts under one resource name.");
        return (File.ReadAllBytes(Sans), File.ReadAllBytes(Bold));
    }

    private static void SaveWith(Document doc, VellumPdf.Fonts.EmbeddedFontHandle handle)
    {
        doc.Add(new Paragraph("Hello", new TextStyle { FontRef = handle, FontSize = 12 }));
        using var ms = new MemoryStream();
        doc.Save(ms);
    }

    [Fact]
    public void ForeignHandle_onADocumentWithNoEmbeddedFont_isRefusedAtSave()
    {
        var (first, _) = TwoDifferentFonts();
        using var owner = new Document();
        var handle = owner.UseTrueTypeFont(first);
        using var other = new Document();

        var ex = Assert.Throws<ArgumentException>(() => SaveWith(other, handle));

        Assert.Equal("handle", ex.ParamName);
        Assert.Contains("belongs to a different document", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worse case: both documents call their font TT1, so the foreign handle used to be recorded
    /// under the saving document's own, different, font and the page quietly used the wrong one.
    /// </summary>
    [Fact]
    public void ForeignHandle_whoseResourceNameCollidesWithTheSavingDocumentsOwn_isRefused()
    {
        var (first, second) = TwoDifferentFonts();
        using var owner = new Document();
        var foreign = owner.UseTrueTypeFont(first);
        using var other = new Document();
        var own = other.UseTrueTypeFont(second);
        Assert.Equal(foreign.ResourceName, own.ResourceName);

        Assert.Throws<ArgumentException>(() => SaveWith(other, foreign));
    }

    [Fact]
    public void OwnHandle_stillSaves_andDefinesTheFontOnThePage()
    {
        var (first, _) = TwoDifferentFonts();
        using var doc = new Document();
        var handle = doc.UseTrueTypeFont(first);
        doc.Add(new Paragraph("Hello", new TextStyle { FontRef = handle, FontSize = 12 }));

        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/" + handle.ResourceName, bytes, StringComparison.Ordinal);
        Assert.Contains("/FontFile2", bytes, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingTheSameFontTwiceOnOneDocument_returnsAHandleItAccepts()
    {
        var (first, _) = TwoDifferentFonts();
        using var doc = new Document();
        var a = doc.UseTrueTypeFont(first);
        var b = doc.UseTrueTypeFont(first);

        Assert.Same(a, b);
        SaveWith(doc, b);
    }

    [Fact]
    public void RegisterEmbeddedFontUsage_nullArguments_areRefused()
    {
        var (first, _) = TwoDifferentFonts();
        var pdf = new PdfDocument();
        var handle = pdf.UseTrueTypeFont(first);
        var page = pdf.AddPage();

        Assert.Equal("handle", Assert.Throws<ArgumentNullException>(() => pdf.RegisterEmbeddedFontUsage(page, null!)).ParamName);
        Assert.Equal("page", Assert.Throws<ArgumentNullException>(() => pdf.RegisterEmbeddedFontUsage(null!, handle)).ParamName);
    }

    private static string SaveToUnicode(Document doc, VellumPdf.Fonts.EmbeddedFontHandle handle, string text)
    {
        doc.Add(new Paragraph(text, new TextStyle { FontRef = handle, FontSize = 12 }));
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    /// <summary>
    /// On 1c61fa6 this saved a correct file by name coincidence: both documents embedded the same font
    /// program as TT1, so the foreign handle's name resolved to the saving document's own embedding.
    /// It keeps saving, and the glyphs the foreign handle registered reach the subset, which the
    /// saving document never measured itself.
    /// </summary>
    [Fact]
    public void ForeignHandle_ofTheSameFontProgramUnderTheSameName_saves_withItsGlyphsInTheSubset()
    {
        var (first, _) = TwoDifferentFonts();
        using var owner = new Document();
        var foreign = owner.UseTrueTypeFont(first);
        using var other = new Document();
        var own = other.UseTrueTypeFont(first);
        Assert.Equal(foreign.ResourceName, own.ResourceName);

        var stream = SaveToUnicode(other, foreign, "Hello");

        // 0048 is H, 0065 e, 006C l, 006F o: none of them was measured through the saving
        // document's own handle, so they are in its ToUnicode CMap only if the merge ran.
        foreach (var cp in new[] { "0048", "0065", "006C", "006F" })
            Assert.Contains("<" + cp + ">", stream, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same font program under another resource name is not main's correct case: the content
    /// stream would name TT2 while this document's resources define it as TT1.
    /// </summary>
    [Fact]
    public void ForeignHandle_ofTheSameFontProgramUnderAnotherName_isRefused()
    {
        var (first, second) = TwoDifferentFonts();
        using var owner = new Document();
        _ = owner.UseTrueTypeFont(first);
        var foreign = owner.UseTrueTypeFont(second);
        using var other = new Document();
        var own = other.UseTrueTypeFont(second);
        Assert.NotEqual(foreign.ResourceName, own.ResourceName);

        Assert.Throws<ArgumentException>(() => SaveWith(other, foreign));
    }
}
