// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

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
    private static readonly string Sans = Path.Combine(
        AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");
    private static readonly string Bold = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Bold.ttf");

    private static (byte[] First, byte[] Second) TwoDifferentFonts()
    {
        if (!File.Exists(Sans) || !File.Exists(Bold))
            OracleGate.Unavailable("LiberationSans fonts", "Liberation Sans Regular and Bold are both needed to give two documents different fonts under one resource name.");
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
    /// Both documents call their font TT1 but the fonts differ. 1c61fa6 recorded the foreign handle
    /// under the saving document's own TT1 and saved a valid file, so it still saves. The page uses
    /// the saving document's font of that name and the foreign glyphs are not merged into its subset,
    /// since the two subsets belong to different fonts.
    /// </summary>
    [Fact]
    public void ForeignHandle_whoseResourceNameCollidesWithTheSavingDocumentsOwn_saves_withoutTheMerge()
    {
        var (first, second) = TwoDifferentFonts();
        using var owner = new Document();
        var foreign = owner.UseTrueTypeFont(first);
        using var other = new Document();
        var own = other.UseTrueTypeFont(second);
        Assert.Equal(foreign.ResourceName, own.ResourceName);

        var stream = SaveToUnicode(other, foreign, "Hello");

        Assert.Contains("/" + own.ResourceName + " 12 Tf", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("<0048>", stream, StringComparison.Ordinal);
    }

    /// <summary>
    /// The foreign handle's program is embedded here, but under another name, and this document
    /// defines the handle's name for a different program. 1c61fa6 saved a valid file; it still does.
    /// </summary>
    [Fact]
    public void ForeignHandle_ofAProgramHeldUnderAnotherName_whenTheNameIsDefinedHere_saves()
    {
        var (first, second) = TwoDifferentFonts();
        using var owner = new Document();
        _ = owner.UseTrueTypeFont(first);
        var foreign = owner.UseTrueTypeFont(second);               // TT2
        using var other = new Document();
        _ = other.UseTrueTypeFont(second);                         // TT1: the foreign program, another name
        var own2 = other.UseTrueTypeFont(first);                   // TT2: a different program
        Assert.Equal(foreign.ResourceName, own2.ResourceName);

        var stream = SaveToUnicode(other, foreign, "Hello");

        Assert.Contains("/" + own2.ResourceName + " 12 Tf", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("<0048>", stream, StringComparison.Ordinal);
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
    /// The same font program under another resource name, with the handle's name defined nowhere in
    /// this document: the content stream would name TT2 while the resources hold only TT1.
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

    private static (string Streams, string Widths) SaveWithOwnOrForeign(byte[] font, bool drawWithForeign)
    {
        using var owner = new Document();
        var foreign = owner.UseTrueTypeFont(font);
        using var saving = new Document();
        var own = saving.UseTrueTypeFont(font);
        var drawn = drawWithForeign ? foreign : own;
        saving.Add(new Paragraph("Hello", new TextStyle { FontRef = drawn, FontSize = 12 }));
        using var ms = new MemoryStream();
        saving.Save(ms);
        var bytes = ms.ToArray();
        var raw = System.Text.Encoding.Latin1.GetString(bytes);
        var widths = System.Text.RegularExpressions.Regex.Match(
            raw, @"/W\s*(\[.*?\])\s*(?=/|>>)",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        return (PdfTestUtil.DecompressAllFlatStreams(bytes), widths);
    }

    /// <summary>
    /// The glyph ids the foreign handle registered must be in the saving document's subset, not just
    /// in its ToUnicode map. Drawing "Hello" through the foreign handle has to give the same /W
    /// widths array and the same font program as drawing it through the document's own handle, which
    /// registers the same glyphs itself. Dropping the merge of the glyph ids leaves both smaller.
    /// </summary>
    [Fact]
    public void ForeignHandle_drawnText_getsTheSameSubsetAsTheOwnHandleWould()
    {
        var (first, _) = TwoDifferentFonts();

        var own = SaveWithOwnOrForeign(first, drawWithForeign: false);
        var viaForeign = SaveWithOwnOrForeign(first, drawWithForeign: true);

        Assert.NotEqual(string.Empty, own.Widths);
        Assert.Equal(own.Widths, viaForeign.Widths);
        Assert.Equal(own.Streams.Length, viaForeign.Streams.Length);
        Assert.Equal(own.Streams, viaForeign.Streams);
    }
}
