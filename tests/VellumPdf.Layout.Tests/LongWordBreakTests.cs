// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.Reader;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// With <see cref="Document.BreakLongWordsAtPlaces"/> set, a word wider than its line is cut after
/// the last solidus, reverse solidus or hyphen-minus that fits, and at the glyph only when it has
/// none (#594). The fixtures use Courier at 10 pt, where every glyph is 6 pt wide, and a 60 pt line,
/// so a line holds ten characters and the expected cut is an exact index. Every wrap of the library
/// is tested: the paragraph, the heading, the list item, the paragraphs of a note block, a table cell
/// given as a string, and one given as runs. A test that calls a wrap directly turns the setting on
/// for the call; a test that saves a document sets the property.
/// </summary>
public sealed class LongWordBreakTests
{
    private const double Line = 60;

    private static TextStyle Courier(string? uri = null) =>
        new() { Font = Standard14.Courier, FontSize = 10, LinkUri = uri };

    // The wraps below are static and read the setting from the thread, which a document sets while it
    // lays out. A direct call sets it for its own duration.
    private static LongWordBreaks.PlaceCutScope On() => LongWordBreaks.PlaceCuts(true);

    private static (List<LineFragment[]> Lines, List<LineEnd> Ends) Wrap(
        IReadOnlyList<TextRun> runs, double width, out int nullStyleRuns)
    {
        using var on = On();
        return ParagraphRenderer.WordWrap(runs, width, out nullStyleRuns);
    }

    private static List<string> Lines(string text, TextStyle style, double width, bool honourBreaks)
    {
        using var on = On();
        return Lines(text, style, width, honourBreaks);
    }

    private static string[] Paragraph(string text, double width = Line) =>
        [.. Wrap([new TextRun(text, Courier())], width, out _).Lines
            .Select(l => string.Concat(l.Select(f => f.Text)))];

    // ── The break point ──────────────────────────────────────────────────────

    [Theory]
    // The last solidus that fits: "aa/bb/" is 6 characters, the next solidus is past the line.
    [InlineData("aa/bb/cccccccccc", new[] { "aa/bb/", "cccccccccc" })]
    // A cut after the first solidus alone would leave "aa/" and push the rest down a line.
    [InlineData("aaaa/bbbbbbbbbbbb", new[] { "aaaa/", "bbbbbbbbbb", "bb" })]
    // The cut is after a solidus even when the solidus is the tenth character.
    [InlineData("aaaaaaaaa/bbbbbbbbbb", new[] { "aaaaaaaaa/", "bbbbbbbbbb" })]
    // A hyphen-minus.
    [InlineData("abcd-efghijklmnop", new[] { "abcd-", "efghijklmn", "op" })]
    [InlineData("well-known-wording-here", new[] { "well-", "known-", "wording-", "here" })]
    // A path with reverse solidus: the cut is after the one that fits and no glyph is added.
    [InlineData(@"C:\Users\Name\Documents", new[] { @"C:\Users\", @"Name\", "Documents" })]
    // The two solidi of a scheme stay together, so the cut is after the second.
    [InlineData("https://aaaaaaaaaaaa", new[] { "https://", "aaaaaaaaaa", "aa" })]
    // Nothing fits before the glyph: the first place is past the line, so the word is cut at the
    // glyph as it always was, and the rest is then read again.
    [InlineData("aaaaaaaaaaaaaaaa/bbb", new[] { "aaaaaaaaaa", "aaaaaa/bbb" })]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaa-bbb", new[] { "aaaaaaaaaa", "aaaaaaaaaa", "aaa-bbb" })]
    // Characters that UAX #14 keeps joined to what follows are not places.
    [InlineData("aaaaa=bbbbbbbbbbbb", new[] { "aaaaa=bbbb", "bbbbbbbb" })]
    [InlineData("aaaaa&bbbbbbbbbbbb", new[] { "aaaaa&bbbb", "bbbbbbbb" })]
    [InlineData("aaaaa_bbbbbbbbbbbb", new[] { "aaaaa_bbbb", "bbbbbbbb" })]
    [InlineData("aaaaa#bbbbbbbbbbbb", new[] { "aaaaa#bbbb", "bbbbbbbb" })]
    [InlineData("aaaaa.bbbbbbbbbbbb", new[] { "aaaaa.bbbb", "bbbbbbbb" })]
    [InlineData("aaaaa?bbbbbbbbbbbb", new[] { "aaaaa?bbbb", "bbbbbbbb" })]
    // A hyphen that starts the word, or follows another, is not a place (LB20a, LB21).
    [InlineData("-aaaaaaaaaaaaaaa", new[] { "-aaaaaaaaa", "aaaaaa" })]
    [InlineData("a--bbbbbbbbbbbbbb", new[] { "a--bbbbbbb", "bbbbbbb" })]
    public void AWordWiderThanTheLineIsCutAfterTheLastPlaceThatFits(string word, string[] expected) =>
        Assert.Equal(expected, Paragraph(word));

    [Fact]
    public void AWordThatFitsIsNeverCut()
    {
        Assert.Equal(["aa/bb/cc/dd"], Paragraph("aa/bb/cc/dd", 66));
        Assert.Equal(["aa/bb/cc-dd"], Paragraph("aa/bb/cc-dd", 66));

        // A word that fits on a fresh line is kept whole, though the line it follows is too full.
        Assert.Equal(["xxxxxxxx", "aa/bb/cc/d"], Paragraph("xxxxxxxx aa/bb/cc/d"));
    }

    [Fact]
    public void ARemainderThatFitsIsNotCutAgain()
    {
        // "bbbb/cc" is seven characters and fits, so the solidus inside it is not used.
        Assert.Equal(["aaaaaa/", "bbbb/cc"], Paragraph("aaaaaa/bbbb/cc"));
    }

    [Fact]
    public void EveryLineOfALongAddressIsCutAtItsOwnLastPlace()
    {
        const string url = "https://example.org/aaa/bbbb/cc/ddddddd/eee";

        Assert.Equal(
            ["https://", "example.", "org/aaa/", "bbbb/cc/", "ddddddd/", "eee"],
            Paragraph(url, 48));
    }

    // ── The digit rule ───────────────────────────────────────────────────────

    // LB25 forbids a break before a digit after a hyphen-minus (HY x NU) and after a solidus that
    // follows a digit (NU (SY | IS)* x NU). The tailoring that UAX #14 suggests for web addresses
    // suppresses the second, and a place of either kind is used only when no other place fits.
    [Theory]
    // After a digit, a solidus is not a place before a digit unless nothing else fits.
    [InlineData("ab12/34cdefghijkl", new[] { "ab12/", "34cdefghij", "kl" })]
    // A hyphen-minus before a digit likewise.
    [InlineData("abcd-12345678901", new[] { "abcd-", "1234567890", "1" })]
    // A solidus that follows a letter is a place before a digit as before any other character.
    [InlineData("ab/12345678901234", new[] { "ab/", "1234567890", "1234" })]
    // A place LB25 allows wins over a later one it forbids, though the later one fits.
    [InlineData("ab-cd12/34eeeeeeee", new[] { "ab-", "cd12/", "34eeeeeeee" })]
    // A place LB25 forbids wins over the glyph.
    [InlineData("abc-1234567890123", new[] { "abc-", "1234567890", "123" })]
    // The preferred place is the last one that fits, not the first.
    [InlineData("ab12/34cd-efghijklm", new[] { "ab12/34cd-", "efghijklm" })]
    // The characters of IS between the digit and the solidus do not change the reading.
    [InlineData("1.5/2cdefghijklmnop", new[] { "1.5/", "2cdefghijk", "lmnop" })]
    // Two places LB25 forbids on one line: the last that fits is used, not the first.
    [InlineData("ab-1-2345678901", new[] { "ab-1-", "2345678901" })]
    // A place right after the first character of a line is a place.
    [InlineData("/uuuuuuuuuuuuuu", new[] { "/", "uuuuuuuuuu", "uuuu" })]
    // The characters of SY and IS between the digit and the solidus are skipped, whichever they
    // are and however many: the place before the 2 is LB25's, so the earlier one is used, and
    // the line then ends at the forbidden place because nothing else fits it.
    [InlineData("ab/1,/2cccccccccccc", new[] { "ab/", "1,/", "2ccccccccc", "ccc" })]
    [InlineData("ab/1:/2cccccccccccc", new[] { "ab/", "1:/", "2ccccccccc", "ccc" })]
    [InlineData("ab/1;/2cccccccccccc", new[] { "ab/", "1;/", "2ccccccccc", "ccc" })]
    [InlineData("ab/1./2cccccccccccc", new[] { "ab/", "1./", "2ccccccccc", "ccc" })]
    [InlineData("ab/1//2cccccccccccc", new[] { "ab/", "1//", "2ccccccccc", "ccc" })]
    [InlineData("ab/1\\/2cccccccccccc", new[] { "ab/", "1\\/", "2ccccccccc", "ccc" })]
    public void ABreakBeforeADigitThatLb25ForbidsIsTheFallback(string word, string[] expected) =>
        Assert.Equal(expected, Paragraph(word));

    [Fact]
    public void TheNumericClassOfUax14ReachesTheArabicDecimalSeparator()
    {
        // U+066B is in NU, so a solidus after it and before a digit is a place LB25 forbids.
        var word = "a-b\u066B/2cdefghijkl";
        var tiers = LongWordBreaks.Tiers([.. word.EnumerateRunes()]);

        Assert.NotNull(tiers);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, tiers);
    }

    [Fact]
    public void ThePlacesOfAWordAreReadFromItsCharacters()
    {
        byte[]? Tiers(string word) => LongWordBreaks.Tiers([.. word.EnumerateRunes()]);

        Assert.Null(Tiers("plain"));
        Assert.Null(Tiers("trailing/"));
        Assert.Null(Tiers("-leading"));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0, 0 }, Tiers("abc-def"));
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, Tiers("a/bc"));
        // Two solidi: the break between them is held by LB13, the one after them is free.
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0 }, Tiers("a//bc"));
        // A closing bracket, a full stop, a question mark and another solidus after the solidus.
        Assert.Null(Tiers("a/)bc"));
        Assert.Null(Tiers("a/.bc"));
        Assert.Null(Tiers("a/?bc"));
        // Hebrew on either side (LB21a, LB21b), and kana and CJK symbols (LB21).
        Assert.Null(Tiers("a/\u05D0b"));
        Assert.Null(Tiers("\u05D0-b"));
        // A Hebrew letter before a solidus is a place: LB21b holds the break after it only.
        Assert.Equal(new byte[] { 0, 0, 1 }, Tiers("\u05D0/b"));
        // Halfwidth and small kana (CJ in UAX #14) are no place either.
        Assert.Null(Tiers("a/\uFF67b"));
        Assert.Null(Tiers("a/\u31F0b"));
        Assert.Null(Tiers("a-\u30A1b"));
        Assert.Null(Tiers("a/\u3001b"));
        // A letter of another script, and an ideograph, are free after a solidus.
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, Tiers("a/\u0436x"));
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, Tiers("a/\u4E2Dx"));
        // A hyphen after a digit, before a letter and before a digit.
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, Tiers("1-ab"));
        Assert.Equal(new byte[] { 0, 0, 2, 0 }, Tiers("1-2b"));
        // A word of one character has no place.
        Assert.Null(Tiers("-"));
    }

    // ── Pieces in more than one style ────────────────────────────────────────

    [Fact]
    public void AWordInTwoStylesIsCutAtItsPlaceAndKeepsItsFragments()
    {
        var a = Courier();
        var b = Courier();
        var (lines, ends) = Wrap(
            [new TextRun("aaaa/bb", a), new TextRun("bbbbbbbbbbb", b)], Line, out _);

        Assert.Equal(3, lines.Count);
        Assert.Equal(["aaaa/"], lines[0].Select(f => f.Text));
        Assert.Equal(["bb", "bbbbbbbb"], lines[1].Select(f => f.Text));
        Assert.Equal(["bbb"], lines[2].Select(f => f.Text));
        Assert.Same(a, lines[1][0].Style);
        Assert.Same(b, lines[1][1].Style);
        Assert.Equal([LineEnd.Forced, LineEnd.Forced, LineEnd.Last], ends);
        Assert.Equal(30, lines[0][0].Width);
        Assert.Equal(12, lines[1][0].Width);
        Assert.Equal(48, lines[1][1].Width);
    }

    [Fact]
    public void ALineEndsWhereTheCutIs_andTheWordThatFollowsStartsAFreshLine()
    {
        var (_, ends) = Wrap(
            [new TextRun("aaaa/bbbbbbbbbbbb next", Courier())], Line, out _);

        Assert.Equal([LineEnd.Forced, LineEnd.Forced, LineEnd.Soft, LineEnd.Last], ends);
    }

    // ── Every wrap path ──────────────────────────────────────────────────────

    private const string Path = "aa/bb/cccccccccc";

    private static Document Page() => new()
    {
        BreakLongWordsAtPlaces = true,
        PageSize = new PdfRectangle(0, 0, 100, 300),
        Margins = new EdgeInsets(20),
    };

    // A page whose content area is as wide as the 72 pt column: the 6 pt padding leaves a 60 pt line.
    private static Document TablePage() => new()
    {
        BreakLongWordsAtPlaces = true,
        PageSize = new PdfRectangle(0, 0, 112, 300),
        Margins = new EdgeInsets(20),
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string[] Shown(byte[] pdf) =>
        [.. ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(pdf))
            .Where(p => p.Text.Length > 0).Select(p => p.Text)];

    [Fact]
    public void AParagraphIsCutAtThePlace()
    {
        using var doc = Page();
        doc.Add(new Paragraph(Path, Courier()));

        Assert.Equal(["aa/bb/", "cccccccccc"], Shown(Save(doc)));
    }

    [Fact]
    public void AHeadingIsCutAtThePlace()
    {
        using var doc = Page();
        doc.Add(new Heading(Path, Courier()));

        Assert.Equal(["aa/bb/", "cccccccccc"], Shown(Save(doc)));
    }

    [Fact]
    public void AListItemIsCutAtThePlace()
    {
        using var doc = new Document { BreakLongWordsAtPlaces = true, PageSize = new PdfRectangle(0, 0, 120, 300), Margins = new EdgeInsets(20) };
        doc.Add(new ListElement { DefaultStyle = Courier(), Indent = 20 }.Add(Path));

        var shown = Shown(Save(doc));

        // The marker is drawn first; the item's text is what follows it.
        Assert.Equal(["aa/bb/", "cccccccccc"], shown.Skip(shown.Length - 2));
    }

    [Fact]
    public void ATableCellStringIsCutAtThePlace()
    {
        using var doc = TablePage();
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
        table.AddRow().AddCell(Path);
        doc.Add(table);

        Assert.Equal(["aa/bb/", "cccccccccc"], Shown(Save(doc)));
    }

    [Fact]
    public void ATableCellOfRunsIsCutAtThePlace()
    {
        using var doc = TablePage();
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
        table.AddRow().AddCell(new Cell([new TextRun("aa/bb/", Courier()), new TextRun("cccccccccc", Courier())]));
        doc.Add(table);

        Assert.Equal(["aa/bb/", "cccccccccc"], Shown(Save(doc)));
    }

    [Fact]
    public void ATableCellOfRunsInTwoStylesKeepsTheStylesOfItsPieces()
    {
        var a = Courier();
        var b = Courier();
        var cell = new Cell([new TextRun("aaaa/bb", a), new TextRun("bbbbbbbbbbb", b)]);

        var lines = cell.RunText!.Lines(Line, honourBreaks: true);

        Assert.Equal(["aaaa/", "bbbbbbbbbb", "bbb"], lines);
        Assert.Equal(["bb", "bbbbbbbb"], lines.Fragments[1].Select(f => f.Text));
        Assert.Same(a, lines.Fragments[1][0].Style);
        Assert.Same(b, lines.Fragments[1][1].Style);
    }

    [Theory]
    [InlineData(Path, new[] { "aa/bb/", "cccccccccc" })]
    [InlineData("aaaa/1111bbbbbbbb", new[] { "aaaa/", "1111bbbbbb", "bb" })]
    [InlineData("ab12/34cdefghijkl", new[] { "ab12/", "34cdefghij", "kl" })]
    [InlineData(@"C:\Users\Name\Documents", new[] { @"C:\Users\", @"Name\", "Documents" })]
    [InlineData("aaaaaaaaaaaaaaaa/bbb", new[] { "aaaaaaaaaa", "aaaaaa/bbb" })]
    // A word that fits its line is not cut, alone or after another word.
    [InlineData("aa/bb/cc/d", new[] { "aa/bb/cc/d" })]
    [InlineData("xx aa/bb/cc/d", new[] { "xx", "aa/bb/cc/d" })]
    public void ACellStringAndACellOfRunsWrapTheSameAsTheParagraph(string word, string[] expected)
    {
        var style = Courier();

        Assert.Equal(expected, Paragraph(word));
        Assert.Equal(expected, Lines(word, style, Line, honourBreaks: true));
        Assert.Equal(expected, new Cell([new TextRun(word, style)]).RunText!.Lines(Line, honourBreaks: true));
    }

    [Fact]
    public void ACellStringAndACellOfRunsAgreeOnLinesAndOnTheWidthsTheCountHoldsOver()
    {
        using var on = On();
        var style = Courier();
        string[] words =
        [
            Path, "https://example.org/aaa/bbbb/cc/ddddddd/eee", @"C:\Users\Name\Documents\report-2024-final.txt",
            "ab12/34cdefghijkl", "x-1-2-3-4-5-6-7-8-9-0-1-2-3", "a //  b/c-d/e xxxxxxxxxxxxxxxxx/y",
        ];
        foreach (var text in words)
        {
            var cell = new Cell([new TextRun(text, style)]);
            for (var width = 1.0; width < 130; width += 0.7)
            {
                var expected = Lines(text, style, width, honourBreaks: true);
                Assert.Equal(expected, cell.RunText!.Lines(width, honourBreaks: true));

                var stringCount = TableRenderer.CountLines(text, style, width, true, out var from, out var below);
                var runCount = cell.RunText.CountLines(width, true, out var runFrom, out var runBelow);
                Assert.Equal(expected.Count, stringCount);
                Assert.Equal((stringCount, from, below), (runCount, runFrom, runBelow));

                // The interval is where the lines do not change.
                Assert.True(from <= width && width < below, $"{text} at {width}: [{from}, {below})");
                if (double.IsFinite(from))
                    Assert.Equal(expected, Lines(text, style, from, honourBreaks: true));
                if (double.IsFinite(below))
                    Assert.Equal(expected, Lines(text, style, Math.BitDecrement(below), true));
            }
        }
    }

    [Fact]
    public void TheWidthsACountHoldsOverAreSoundForACutWord()
    {
        using var on = On();
        // A cached count answers every width inside its interval, so a width inside it must wrap
        // into the same lines as the width it was found at, whichever comparison narrowed it.
        var style = Courier();
        string[] words =
        [
            Path, "https://example.org/aaa/bbbb/cc/ddddddd/eee", @"C:\Users\Name\Documents\report-2024-final.txt",
            "ab12/34cdefghijkl", "x-1-2-3-4-5-6-7-8-9-0-1-2-3",
        ];
        foreach (var text in words)
        {
            var widths = Enumerable.Range(0, 200).Select(i => 3.1 + i * 0.6).ToArray();
            var found = widths.Select(w =>
            {
                var count = TableRenderer.CountLines(text, style, w, true, out var from, out var below);
                return (Width: w, Count: count, From: from, Below: below);
            }).ToArray();
            foreach (var a in found)
            {
                foreach (var b in found)
                {
                    if (a.From <= b.Width && b.Width < a.Below)
                    {
                        Assert.True(
                            Lines(text, style, a.Width, true)
                                .SequenceEqual(Lines(text, style, b.Width, true)),
                            $"{text}: width {b.Width} is inside [{a.From}, {a.Below}) found at {a.Width}");
                    }
                }
            }
        }
    }

    // ── Links ────────────────────────────────────────────────────────────────

    [Fact]
    public void AWrappedLinkGetsOneRectanglePerPieceThatCoversIt()
    {
        using var doc = Page();
        doc.Add(new Paragraph("aaaa/bbbbbbbbbbbb", Courier("https://example.org/a")));

        var links = LinkSupport.Links(LinkSupport.Save(doc));

        Assert.Equal(3, links.Count);
        Assert.All(links, l => Assert.Equal("https://example.org/a", l.Uri));
        // Each rectangle starts at the left margin and is as wide as its piece: 5, 10 and 2 glyphs.
        Assert.Equal([20.0, 20.0, 20.0], links.Select(l => l.X0));
        Assert.Equal([50.0, 80.0, 32.0], links.Select(l => l.X1));
        // The three lines are one leading apart, and the rectangles do not overlap.
        var tops = links.Select(l => l.Y1).ToArray();
        Assert.True(tops[0] > tops[1] && tops[1] > tops[2]);
        Assert.True(links[0].Y0 >= links[1].Y1 - 1e-9 && links[1].Y0 >= links[2].Y1 - 1e-9);
    }

    [Fact]
    public void AWrappedAnchorLinkGetsOneRectanglePerPiece()
    {
        using var doc = Page();
        var style = new TextStyle { Font = Standard14.Courier, FontSize = 10, LinkAnchor = "top" };
        doc.Add(new Paragraph("aa/bb/cccccccccc", style));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("target") { Anchor = "top" });

        var links = LinkSupport.Links(LinkSupport.Save(doc));

        Assert.Equal(2, links.Count);
        Assert.Equal([20.0, 20.0], links.Select(l => l.X0));
        Assert.Equal([56.0, 80.0], links.Select(l => l.X1));
    }

    // ── The continuation bound ───────────────────────────────────────────────

    [Fact]
    public void AWrapHoldsAtLeastOneCharacterPerLine_soTheBoundOfTheContinuationLimitStillHolds()
    {
        // ContinuationBoundOf counts a paragraph's characters plus 2, which holds while no line
        // is empty. A cut after a place is after at least one character of the line it ends.
        string[] words =
        [
            Path, "https://example.org/aaa/bbbb/cc/ddddddd/eee", @"C:\Users\Name\Documents", "a-b-c-d-e-f-g-h",
            "1/2/3/4/5/6/7/8", "a/-/-/-/-/-/-/b", "ab12/34cdefghijkl",
        ];
        foreach (var text in words)
        {
            for (var width = 0.5; width < 130; width += 0.5)
            {
                var (lines, _) = Wrap([new TextRun(text, Courier())], width, out _);

                Assert.True(lines.Count <= text.Length, $"{text} at {width}: {lines.Count} lines");
                Assert.All(lines, l => Assert.NotEmpty(string.Concat(l.Select(f => f.Text))));
                Assert.Equal(text, string.Concat(lines.SelectMany(l => l.Select(f => f.Text))));
            }
        }
    }

    // ── Extraction ───────────────────────────────────────────────────────────

    [Fact]
    public void NoGlyphIsAddedAtTheCut()
    {
        using var doc = Page();
        doc.Add(new Paragraph("aaaa/bbbbbbbbbbbb", Courier()));

        var shown = Shown(Save(doc));

        Assert.Equal(["aaaa/", "bbbbbbbbbb", "bb"], shown);
        Assert.Equal("aaaa/bbbbbbbbbbbb", string.Concat(shown));
    }

    [Fact]
    public void TheReaderReturnsTheCharactersAsWrittenAcrossTheCut()
    {
        using var doc = Page();
        doc.Add(new Paragraph("aaaa/bbbbbbbbbbbb", Courier()));

        using var reader = PdfReader.Open(Save(doc));
        var text = reader.GetPage(0).ExtractText().Text;

        Assert.Equal("aaaa/bbbbbbbbbbbb", string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
        Assert.Equal(["aaaa/", "bbbbbbbbbb", "bb"], text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    [Fact]
    public void PdftotextReturnsTheCharactersAsWrittenAcrossTheCutInAnEmbeddedFont()
    {
        using var doc = new Document { BreakLongWordsAtPlaces = true, PageSize = new PdfRectangle(0, 0, 200, 300), Margins = new EdgeInsets(20) };
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 11 };
        const string url = "https://example.org/aaa/bbbb/cc/dddddddd/eeeeeeeeee/ffffffffff";
        doc.Add(new Paragraph(url, style));

        var pdf = Save(doc);
        using var dir = new FontReportSupport.TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        ExternalTool.TryRun("pdftotext", ["-raw", path, "-"], out var exit, out var text, out var stderr, out var timedOut);

        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.True(lines.Length >= 3, text);
        Assert.Equal(url, string.Concat(lines));
        Assert.All(lines.Take(lines.Length - 1), l => Assert.EndsWith("/", l, StringComparison.Ordinal));
    }

    // ── PDF/A-2u ─────────────────────────────────────────────────────────────

    [Fact]
    public void PdfA2u_wrappedAddressesAndPaths_passVeraPdf()
    {
        using var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        doc.BreakLongWordsAtPlaces = true;
        var font = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        var body = new TextStyle { FontRef = font, FontSize = 11 };
        var link = new TextStyle { FontRef = font, FontSize = 11, LinkUri = "https://example.org/a" };
        doc.PageSize = new PdfRectangle(0, 0, 200, 500);
        doc.Margins = new EdgeInsets(20);
        const string url = "https://example.org/aaa/bbbb/cc/dddddddd/eeeeeeeeee/ffffffffff";
        const string path = @"C:\Users\Name\Documents\2024\quarterly-report-final-version.txt";
        doc.Add(new Paragraph(url, body));
        doc.Add(new Paragraph(path, body));
        doc.Add(new Paragraph([new TextRun("see ", body), new TextRun(url, link)]));
        doc.Add(new Heading(url, body));
        doc.Add(new ListElement { DefaultStyle = body }.Add(url).Add(path));
        var table = new TableElement { DefaultCellStyle = body }.SetColumnWidths(160);
        table.AddRow().AddCell(url);
        table.AddRow().AddCell(new Cell([new TextRun("see ", body), new TextRun(path, link)]));
        doc.Add(table);

        var pdf = FontReportSupport.Save(doc);

        // The fixture holds the change: the lines the extractor reads end after a slash or a
        // backslash, and none holds a glyph that the cut added.
        using var dir = new FontReportSupport.TempDir();
        var file = dir.Path("subject.pdf");
        File.WriteAllBytes(file, pdf);
        ExternalTool.TryRun("pdftotext", ["-raw", file, "-"], out var exit, out var text, out _, out _);
        Assert.Equal(0, exit);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Contains(lines, l => l.EndsWith("/", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("\\", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("-", StringComparison.Ordinal));
        Assert.True(LinkSupport.Links(pdf).Count > 2);
        var report = FontReportSupport.VeraPdf("2u", pdf);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
        Assert.Empty(FontReportSupport.FailedRules(report));
    }
}
