// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// A character the embedded font has no glyph for is written as glyph 0. The canvas lists each
/// such write, the document gathers the lists of its finished pages, and the font's ToUnicode CMap
/// carries one entry, U+FFFD, for glyph 0 (#592). The fonts are in-tree: DejaVu Sans lacks U+26B9
/// and U+1F31F, and Liberation Mono lacks U+2609, which DejaVu Sans has.
/// </summary>
public sealed class NotdefGlyphTests
{
    private static readonly string FontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");

    private static byte[] Font(string name) => File.ReadAllBytes(Path.Combine(FontsDir, name));

    private static PdfDocument NewDocument() => new()
    {
        Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        DocumentId = new byte[16],
    };

    // (character, resource, font name, count) for every entry, so an assertion names all four.
    private static List<(string? Character, string? Resource, string? Name, int Count)> Project(
        IReadOnlyList<NotdefGlyphWarning> list) =>
        [.. list.Select(e => (e.Character?.ToString(), e.FontResourceName, e.FontName, e.Count))];


    private static List<string> BfcharLines(string map) =>
        [.. map.Split('\n').Where(l => l.StartsWith("<", StringComparison.Ordinal) && l != "<0000> <FFFF>")];

    private static string Header(int entries) =>
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\n" +
        "begincmap\n" +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
        "/CMapName /Adobe-Identity-UCS def\n" +
        "/CMapType 2 def\n" +
        "1 begincodespacerange\n" +
        "<0000> <FFFF>\n" +
        "endcodespacerange\n" +
        $"{entries} beginbfchar\n";

    private const string Trailer =
        "endbfchar\n" +
        "endcmap\n" +
        "CMapName currentdict /CMap defineresource pop\n" +
        "end end\n";

    [Fact]
    public void ToUnicode_glyphZero_isOneEntryForReplacementCharacter()
    {
        var map = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(
            new Dictionary<int, ushort> { [0x26B9] = 0, [0x1F31F] = 0, [0x41] = 36 }, new HashSet<int>());

        Assert.Equal(Header(2) + "<0000> <FFFD>\n<0024> <0041>\n" + Trailer, map);
    }

    [Fact]
    public void ToUnicode_noGlyphZero_hasNoGlyphZeroEntry()
    {
        var map = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(
            new Dictionary<int, ushort> { [0x41] = 36 }, new HashSet<int>());

        Assert.Equal(Header(1) + "<0024> <0041>\n" + Trailer, map);
        Assert.DoesNotContain(BfcharLines(map), l => l.StartsWith("<0000>", StringComparison.Ordinal));
    }

    [Fact]
    public void ToUnicode_glyphZeroEntryIsFirst_andCountsTowardTheHundredLineSet()
    {
        var assignments = new Dictionary<int, ushort> { [0x26B9] = 0, [0x2609] = 0 };
        for (var i = 0; i < 101; i++) assignments[0x100 + i] = (ushort)(i + 1);

        var map = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(assignments, new HashSet<int>());

        var lines = map.Split('\n');
        Assert.Equal("100 beginbfchar", lines[9]);
        Assert.Equal("<0000> <FFFD>", lines[10]);
        Assert.Equal("2 beginbfchar", lines[9 + 1 + 100 + 1]);
        Assert.Single(BfcharLines(map), l => l.StartsWith("<0000>", StringComparison.Ordinal));
        Assert.Equal(1 + 101, BfcharLines(map).Count);
    }

    [Fact]
    public void KernelDocument_drawingAMissingCharacter_mapsGlyphZeroToReplacementCharacter()
    {
        using var doc = NewDocument();
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(Font("DejaVuSans.ttf"));
        var gids = new ushort[2];
        Assert.Equal(2, handle.GetGlyphIds("A\u26B9", gids));
        Assert.Equal(0, gids[1]);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).ShowGlyphs(gids).EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);

        var map = Encoding.ASCII.GetString(Assert.Single(ToUnicodeLineEndTests.ToUnicodeMaps(ms.ToArray())));

        Assert.Equal(["<0000> <FFFD>", "<0024> <0041>"], BfcharLines(map));
    }


    [Fact]
    public void ShowEmbeddedText_writesTheBytesGetGlyphIdsAndShowGlyphsWrite_andListsEachCharacter()
    {
        using var doc = NewDocument();
        var handle = doc.UseTrueTypeFont(Font("DejaVuSans.ttf"));
        const string text = "A\u26B9\u26B9B\U0001F31F";

        var viaText = new PdfCanvas(doc.AddPage());
        viaText.BeginText().SetFontByName(handle.ResourceName, 12).ShowEmbeddedText(handle, text);
        viaText.EndText().Finish();

        var viaIds = new PdfCanvas(doc.AddPage());
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        viaIds.BeginText().SetFontByName(handle.ResourceName, 12).ShowGlyphs(gids.AsSpan(0, count));
        viaIds.EndText().Finish();

        Assert.Equal(doc.Pages[1].ContentBytes, doc.Pages[0].ContentBytes);
        Assert.Equal(
            [("\u26B9", "TT1", "DejaVuSans", 2), ("\U0001F31F", "TT1", "DejaVuSans", 1)],
            Project(viaText.NotdefGlyphs));
        Assert.Equal([(null, "TT1", null, 3)], Project(viaIds.NotdefGlyphs));
    }

    [Fact]
    public void ShowGlyphs_underASelectedFontName_listsGlyphZeroWithNoCharacter()
    {
        var canvas = new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100)));
        canvas.BeginText().SetFontByName("TT1", 12).ShowGlyphs([0, 36, 0]).EndText();

        Assert.Equal([(null, "TT1", null, 2)], Project(canvas.NotdefGlyphs));
        Assert.Empty(canvas.TextEncodingWarnings);
    }

    [Fact]
    public void SetFont_namesTheResourceOfTheStandard14Font()
    {
        using var doc = NewDocument();
        var canvas = new PdfCanvas(doc.AddPage());
        canvas.BeginText().SetFont(doc.UseFont(Standard14.Helvetica), 12).ShowGlyphs([0]).EndText();

        Assert.Equal([(null, "F1", null, 1)], Project(canvas.NotdefGlyphs));
    }

    [Fact]
    public void ACanvasWithNoGlyphZero_listsNothingAndAllocatesNothing()
    {
        var canvas = new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100)));
        canvas.BeginText().SetFontByName("TT1", 12).ShowGlyphs([36, 37]).EndText();

        Assert.Empty(canvas.NotdefGlyphs);
        Assert.Equal(0, canvas.NotdefGlyphsDropped);
        Assert.Same(canvas.NotdefGlyphs, canvas.NotdefGlyphs);
        Assert.Same(canvas.NotdefGlyphs, new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100))).NotdefGlyphs);
    }

    [Fact]
    public void ShowGlyphs_beforeAnyFontIsSelected_listsGlyphZeroWithNoResourceName()
    {
        var canvas = new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100)));
        canvas.BeginText().ShowGlyphs([0]).EndText();

        Assert.Equal([(null, null, null, 1)], Project(canvas.NotdefGlyphs));
    }

    [Fact]
    public void ASizeThatIsRefused_leavesTheCurrentFontResourceUnchanged()
    {
        var canvas = new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100)));
        canvas.SetFontByName("TT1", 12);
        Assert.ThrowsAny<ArgumentException>(() => canvas.SetFontByName("TT2", double.NaN));
        canvas.BeginText().ShowGlyphs([0]).EndText();

        Assert.Equal([(null, "TT1", null, 1)], Project(canvas.NotdefGlyphs));
    }

    [Fact]
    public void Entries_areDeduplicatedByCharacterAndResource_inOrderOfFirstOccurrence()
    {
        var canvas = new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100)));
        canvas.BeginText();
        canvas.SetFontByName("TT1", 12).ShowGlyphs([0]);
        canvas.SetFontByName("TT2", 12).ShowGlyphs([0]);
        canvas.SetFontByName("TT1", 12).ShowGlyphs([0]);
        canvas.EndText();

        Assert.Equal([(null, "TT1", null, 2), (null, "TT2", null, 1)], Project(canvas.NotdefGlyphs));
    }

    [Fact]
    public void ShowEmbeddedText_listsCharactersInOrderOfFirstOccurrence()
    {
        using var doc = NewDocument();
        var handle = doc.UseTrueTypeFont(Font("LiberationMono-Regular.ttf"));
        var canvas = new PdfCanvas(doc.AddPage());
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).ShowEmbeddedText(handle, "\u26B9\u2609\u26B9");

        Assert.Equal(
            [("\u26B9", "TT1", "LiberationMono", 2), ("\u2609", "TT1", "LiberationMono", 1)],
            Project(canvas.NotdefGlyphs));
    }

    [Fact]
    public void AStandInGlyph_isNotGlyphZero()
    {
        using var doc = NewDocument();
        var handle = doc.UseTrueTypeFont(Font("LiberationMono-Regular.ttf"));
        var canvas = new PdfCanvas(doc.AddPage());
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).ShowEmbeddedText(handle, "1\u202F000");

        Assert.Empty(canvas.NotdefGlyphs);
    }

    [Fact]
    public void TheCanvasListHoldsAThousandEntries_andCountsTheRestAsDropped()
    {
        var canvas = new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100)));
        canvas.BeginText();
        for (var i = 1; i <= 1001; i++) canvas.SetFontByName($"X{i}", 12).ShowGlyphs([0]);
        for (var i = 0; i < 3; i++) canvas.SetFontByName("X1001", 12).ShowGlyphs([0]);

        Assert.Equal(1000, canvas.NotdefGlyphs.Count);
        Assert.DoesNotContain(canvas.NotdefGlyphs, e => e.FontResourceName == "X1001");
        Assert.Equal(4, canvas.NotdefGlyphsDropped);

        // An entry that is already held still counts after the cap.
        canvas.SetFontByName("X1", 12).ShowGlyphs([0, 0]);
        Assert.Equal(3, canvas.NotdefGlyphs[0].Count);
        Assert.Equal(4, canvas.NotdefGlyphsDropped);
    }

    [Fact]
    public void Counts_saturateAtIntMaxValue()
    {
        var tally = new NotdefGlyphTally();
        tally.Add(null, "TT1", null, int.MaxValue);
        tally.Add(null, "TT1", null, int.MaxValue);
        Assert.Equal(int.MaxValue, Assert.Single(tally.Entries).Count);
        Assert.Equal(int.MaxValue, tally.Total);

        for (var i = 0; i < NotdefGlyphTally.MaxEntries; i++) tally.Add(null, $"X{i}", null);
        tally.Add(null, "past", null, int.MaxValue);
        tally.Add(null, "past", null, int.MaxValue);
        Assert.Equal(int.MaxValue, tally.Dropped);
    }


    private static PdfPage FinishedPage(PdfDocument doc, params string[] resources)
    {
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.BeginText();
        foreach (var r in resources) canvas.SetFontByName(r, 12).ShowGlyphs([0]);
        canvas.EndText().Finish();
        return page;
    }

    [Fact]
    public void TheDocumentList_addsTheCountsOfEveryFinishedPage()
    {
        using var doc = NewDocument();
        FinishedPage(doc, "TT1");
        FinishedPage(doc, "TT1", "TT2");
        doc.AddPage(); // never given a canvas

        Assert.Equal([(null, "TT1", null, 2), (null, "TT2", null, 1)], Project(doc.NotdefGlyphs));
        Assert.Equal(0, doc.NotdefGlyphsDropped);
    }

    [Fact]
    public void ADocumentWithNoGlyphZero_returnsTheSameEmptyList()
    {
        using var doc = NewDocument();
        FinishedPage(doc);

        Assert.Empty(doc.NotdefGlyphs);
        Assert.Same(doc.NotdefGlyphs, doc.NotdefGlyphs);
    }

    [Fact]
    public void DrawingAfterFinish_isNotCountedUntilFinishRunsAgain()
    {
        using var doc = NewDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName("TT1", 12).ShowGlyphs([0]).EndText().Finish();

        canvas.BeginText().ShowGlyphs([0]).EndText();
        Assert.Equal(2, Assert.Single(canvas.NotdefGlyphs).Count);
        Assert.Equal(1, Assert.Single(doc.NotdefGlyphs).Count);

        canvas.Finish();
        Assert.Equal(2, Assert.Single(doc.NotdefGlyphs).Count);
    }

    [Fact]
    public void ASecondCanvasFinishedOnAPage_replacesTheFirstOnesEntries()
    {
        using var doc = NewDocument();
        var page = FinishedPage(doc, "TT1");
        Assert.Single(doc.NotdefGlyphs);

        new PdfCanvas(page).BeginText().EndText().Finish();

        Assert.Empty(doc.NotdefGlyphs);
    }

    [Fact]
    public void SettingContentBytesDirectly_clearsThePagesEntries()
    {
        using var doc = NewDocument();
        var page = FinishedPage(doc, "TT1");
        Assert.Single(doc.NotdefGlyphs);

        page.ContentBytes = "BT <0000> Tj ET"u8.ToArray();

        Assert.Empty(doc.NotdefGlyphs);
    }

    [Fact]
    public void TheDocumentList_isCappedAtAThousandEntries_andAddsEveryDropCount()
    {
        using var doc = NewDocument();
        FinishedPage(doc, [.. Enumerable.Range(0, 600).Select(i => $"A{i}")]);
        FinishedPage(doc, [.. Enumerable.Range(0, 600).Select(i => $"B{i}")]);

        Assert.Equal(1000, doc.NotdefGlyphs.Count);
        Assert.Equal(200, doc.NotdefGlyphsDropped);

        // A page whose own list already dropped adds that count to the document's.
        FinishedPage(doc, [.. Enumerable.Range(0, 1002).Select(i => $"C{i}")]);
        Assert.Equal(1000, doc.NotdefGlyphs.Count);
        Assert.Equal(200 + 2 + 1000, doc.NotdefGlyphsDropped);
    }

    private static (PdfDocument Doc, (string?, string?, string?, int)[] Expected) DocumentWithOneEntry()
    {
        var doc = NewDocument();
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(Font("DejaVuSans.ttf"));
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).ShowEmbeddedText(handle, "A\u26B9").EndText();
        canvas.Finish();
        return (doc, [("\u26B9", "TT1", "DejaVuSans", 1)]);
    }

    [Fact]
    public void ARefusedSaveAndTheSavesThatFollow_leaveTheDocumentListAsItWas()
    {
        var (doc, expected) = DocumentWithOneEntry();
        using var _ = doc;
        Assert.Equal(expected, Project(doc.NotdefGlyphs));

        using (var closed = new MemoryStream())
        {
            closed.Dispose();
            Assert.ThrowsAny<Exception>(() => doc.Save(closed));
        }
        Assert.Equal(expected, Project(doc.NotdefGlyphs));

        using var ms = new MemoryStream();
        doc.Save(ms);
        Assert.Equal(expected, Project(doc.NotdefGlyphs));
    }

    [Fact]
    public async Task SaveAsync_leavesTheDocumentListAsItWas()
    {
        var (doc, expected) = DocumentWithOneEntry();
        using var _ = doc;

        using var ms = new MemoryStream();
        await doc.SaveAsync(ms, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Project(doc.NotdefGlyphs));
    }

    [Fact]
    public void PrepareForSigning_leavesTheDocumentListAsItWas()
    {
        var (doc, expected) = DocumentWithOneEntry();
        using var _ = doc;

        doc.PrepareForSigning(new SignaturePlaceholderOptions
        {
            SigningTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        });

        Assert.Equal(expected, Project(doc.NotdefGlyphs));
    }


    [Fact]
    public void TheSameCharacterInTwoFonts_isTwoEntries()
    {
        using var doc = NewDocument();
        var dejaVu = doc.UseTrueTypeFont(Font("DejaVuSans.ttf"));
        var mono = doc.UseTrueTypeFont(Font("LiberationMono-Regular.ttf"));
        var canvas = new PdfCanvas(doc.AddPage());
        canvas.BeginText();
        canvas.SetFontByName(dejaVu.ResourceName, 12).ShowEmbeddedText(dejaVu, "\u26B9");
        canvas.SetFontByName(mono.ResourceName, 12).ShowEmbeddedText(mono, "\u26B9");
        canvas.EndText();

        Assert.Equal(
            [("\u26B9", "TT1", "DejaVuSans", 1), ("\u26B9", "TT2", "LiberationMono", 1)],
            Project(canvas.NotdefGlyphs));
    }

    [Fact]
    public void ACharacterThatWasOnlyMeasuredAsGlyphZero_getsTheToUnicodeEntry_andNoWrite()
    {
        using var doc = NewDocument();
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(Font("DejaVuSans.ttf"));
        doc.RegisterEmbeddedFontUsage(page, handle);
        handle.MeasureString("\u26B9", 12);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).ShowEmbeddedText(handle, "A").EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);

        var map = Encoding.ASCII.GetString(Assert.Single(ToUnicodeLineEndTests.ToUnicodeMaps(ms.ToArray())));

        Assert.Equal(["<0000> <FFFD>", "<0024> <0041>"], BfcharLines(map));
        Assert.Empty(canvas.NotdefGlyphs);
        Assert.Empty(doc.NotdefGlyphs);
    }

    [Fact]
    public void GlyphIdsPassedStraightToShowGlyphs_addNoToUnicodeEntryForGlyphZero()
    {
        using var doc = NewDocument();
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(Font("DejaVuSans.ttf"));
        doc.RegisterEmbeddedFontUsage(page, handle);
        var ids = new ushort[1];
        handle.GetGlyphIds("A", ids);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).ShowGlyphs([0, ids[0]]).EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);

        var map = Encoding.ASCII.GetString(Assert.Single(ToUnicodeLineEndTests.ToUnicodeMaps(ms.ToArray())));

        Assert.Equal(["<0024> <0041>"], BfcharLines(map));
        Assert.Equal([(null, "TT1", null, 1)], Project(canvas.NotdefGlyphs));
    }

    [Fact]
    public async Task ARefusedSaveAsync_leavesTheDocumentListAsItWas()
    {
        var (doc, expected) = DocumentWithOneEntry();
        using var _ = doc;
        var closed = new MemoryStream();
        closed.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(() => doc.SaveAsync(closed, TestContext.Current.CancellationToken));

        Assert.Equal(expected, Project(doc.NotdefGlyphs));
    }

    [Fact]
    public void ARefusedPrepareForSigning_leavesTheDocumentListAsItWas()
    {
        var (doc, expected) = DocumentWithOneEntry();
        using var _ = doc;

        Assert.ThrowsAny<Exception>(() => doc.PrepareForSigning(new SignaturePlaceholderOptions
        {
            SignaturePage = -1,
            SigningTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        }));

        Assert.Equal(expected, Project(doc.NotdefGlyphs));
    }
}
