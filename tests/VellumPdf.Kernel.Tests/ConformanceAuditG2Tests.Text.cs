// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Kernel.Tests.ConformanceAuditG1Support;
using static VellumPdf.Kernel.Tests.ConformanceAuditG2Support;
using LayoutDocument = VellumPdf.Layout.Document;
using TextRun = VellumPdf.Layout.Elements.TextRun;

namespace VellumPdf.Kernel.Tests;

// Tabs and leaders (#653), Preformatted (#662), joined runs (#650), the space at a soft line end,
// and the Layout reports of glyph 0 and unembedded fonts (#657): what the content stream shows for
// them, read back through each font's own ToUnicode map, against ISO 32000-1 9.10 and 14.8.2.5 and the product
// rules of #647, #653 and #662,
// ISO 19005-2 6.2.11 and 6.7.3, and ISO 14289-1 7.21.
public sealed partial class ConformanceAuditG2Tests
{
    private static readonly byte[] Mono = Standard14Substitutes.GetFontProgram(Standard14.Courier);

    private static (byte[] Bytes, LayoutDocument Doc) Build(PdfConformance claim, Action<LayoutDocument> fill)
    {
        var doc = LayoutDocument(claim);
        fill(doc);
        var bytes = Save(doc);
        return (bytes, doc);
    }

    /// <summary>The text of every string shown, in order, with its font size, position and the tags open around
    /// it.</summary>
    private static List<(string Text, ConformanceAuditG2Support.Shown Shown)> Strings(Pdf pdf, int page = 0)
    {
        var fonts = EmbeddedFonts(pdf).ToDictionary(f => f.ResourceName);
        return [.. ShownStrings(Content(pdf, page)).Select(s => (Decode(s, fonts[s.Resource]), s))];
    }

    // ── Tab stops and leaders (#653); product rules, and ISO 32000-1 9.10.2 and 14.8.2.5 ──────────────────

    private static (byte[] Bytes, TextStyle Style) TabbedDocument(PdfConformance claim)
    {
        TextStyle? style = null;
        var (bytes, _) = Build(claim, doc =>
        {
            style = Style(doc, DejaVu);
            doc.Add(new Paragraph("Chapter one\t12\nChapter two\t134", style)
            {
                TabStops = [new TabStop(300, TabAlignment.Right, TabLeader.Dots)],
            });
            doc.Add(new Paragraph("Chapter one\t12\tx\ty", style)
            {
                TabStops = [new TabStop(150, TabAlignment.Left, TabLeader.Dashes), new TabStop(250,
                    TabAlignment.Left, TabLeader.Rule)],
            });
        });
        return (bytes, style!);
    }

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Issue653_ATabIsNeverShownAndALeaderIsAnArtifactPathNotText(PdfConformance claim)
    {
        var (bytes, style) = TabbedDocument(claim);
        using var pdf = new Pdf(bytes);
        var strings = Strings(pdf);

        // The construct: the right-aligned numbers are their own show strings, placed by Tm, and
        // no tab character or glyph 0 reaches the font.
        Assert.Equal(["Chapter one ", "12", "Chapter two ", "134", "Chapter one ", "12 ", "x y"], strings.Select(s
            => s.Text).ToArray());
        Assert.All(strings, s => Assert.DoesNotContain('\t', s.Text));
        Assert.DoesNotContain(0, strings.SelectMany(s => Codes(s.Shown.Codes)));
        var map = ParseToUnicode(Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes);
        Assert.DoesNotContain(map.Entries, e => e.Value.Contains("\t"));

        // A right stop at 300 puts the right edge of the text at the content left edge (72) plus 300.
        var number = strings[1].Shown;
        Assert.Equal(72 + 300 - style.MeasureString("12"), number.X, 2);
        var second = strings[3].Shown;
        Assert.Equal(72 + 300 - style.MeasureString("134"), second.X, 2);

        // A leader is drawn as a stroked path inside an Artifact sequence, so it is neither text nor
        // part of the structure (14.8.2.2) and no consumer reads it as characters.
        var ops = Operators(Content(pdf));
        var marked = new Stack<string>();
        var paths = new List<(string Tag, double X1, double X2)>();
        double? from = null;
        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "BMC" or "BDC":
                    marked.Push(op.Operands[0][1..]);
                    break;
                case "EMC":
                    marked.Pop();
                    break;
                case "m":
                    from = double.Parse(op.Operands[0], CultureInfo.InvariantCulture);
                    break;
                case "l" when from is { } x1:
                    paths.Add((marked.Count > 0 ? marked.Peek() : "", x1, double.Parse(op.Operands[0],
                        CultureInfo.InvariantCulture)));
                    break;
            }
        }

        // Two dotted leaders (one per line of the first paragraph), a dashed one and a rule.
        Assert.Equal(4, paths.Count);
        // Marked content is written only for a tagged document (PDF/A-2a and PDF/UA-1 here).
        Assert.All(paths, p => Assert.Equal(Tagged(claim) ? "Artifact" : "", p.Tag));
        // The dotted leader fills the gap between "Chapter one " and the number, without overlapping either.
        var textEnd = 72 + style.MeasureString("Chapter one ");
        Assert.True(paths[0].X1 >= textEnd - 0.01 && paths[0].X2 <= number.X + 0.01,
            $"leader {paths[0].X1}..{paths[0].X2}");
        // A leader stays out of the shown text: a poppler read of the page holds the words and no leader glyph.
        var text = PopplerText(bytes);
        Assert.Contains("Chapter one 12", text, StringComparison.Ordinal);
        Assert.Contains("Chapter two 134", text, StringComparison.Ordinal);
        Assert.DoesNotContain("..", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    public void Iso32000_1_14_8_2_5_ATabWritesTheSpaceThatSeparatesTheWordsBeforeIt(PdfConformance claim)
    {
        var (bytes, _) = TabbedDocument(claim);
        using var pdf = new Pdf(bytes);
        var strings = Strings(pdf).Select(s => s.Text).ToList();

        // 14.8.2.5 asks that the characters that separate words in plain text are in a Tagged PDF
        // file: the text before a tab ends with a space inside its show string, so the number after
        // it is a word of its own, and a tab with no stop to its right is a space of its own.
        Assert.EndsWith(" ", strings[0], StringComparison.Ordinal);
        Assert.EndsWith(" ", strings[2], StringComparison.Ordinal);
        Assert.Equal("x y", strings[^1]);
    }

    // ── Preformatted (#662) ──────────────────────────────────────────────────────────────────

    private const string Code = "  int  x;\t// a\n\n    return  x ;  ";

    private static (byte[] Bytes, TextStyle Style) PreformattedDocument(PdfConformance claim, string text)
    {
        TextStyle? style = null;
        var (bytes, _) = Build(claim, doc =>
        {
            style = Style(doc, Mono, 10);
            doc.Add(new Preformatted(text, style));
        });
        return (bytes, style!);
    }

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Issue662_APreformattedBlockExpandsTabsToSpacesAndKeepsEveryOtherSpace(PdfConformance claim)
    {
        var (bytes, style) = PreformattedDocument(claim, Code);
        using var pdf = new Pdf(bytes);
        var strings = Strings(pdf);

        // The construct: the block is shown in the Liberation Mono program, one show string per
        // non-empty line, and the empty line is not shown.
        Assert.Equal(2, strings.Count);
        // The tab after "  int  x;" (9 columns) reaches the next multiple of eight columns, 16, so it
        // is seven spaces. Leading, repeated and trailing spaces are written as given.
        Assert.Equal("  int  x;       // a", strings[0].Text);
        Assert.Equal("    return  x ;  ", strings[1].Text);
        Assert.All(strings, s => Assert.DoesNotContain('\t', s.Text));
        Assert.DoesNotContain(0, strings.SelectMany(s => Codes(s.Shown.Codes)));

        // Lines are one leading apart, so the empty line between them keeps its place.
        Assert.Equal(strings[0].Shown.Y - (2 * style.EffectiveLeading), strings[1].Shown.Y, 2);
        Assert.Equal(strings[0].Shown.X, strings[1].Shown.X, 3);
    }

    private static readonly string Lf = ((char)10).ToString();

    private static List<string> TaggedStrings(Action<LayoutDocument, TextStyle> add)
    {
        var (bytes, _) = Build(PdfConformance.PdfA2a, doc => add(doc, Style(doc, Mono, 10)));
        using var pdf = new Pdf(bytes);
        return [.. Strings(pdf).Select(s => s.Text)];
    }

    [Fact]
    public void Iso19005_2_6_7_3_2_APreformattedLineEndHasNoSpaceInItsShowStringAsIssue588Describes()
    {
        // The state #588's comment on Preformatted describes: one marked-content sequence holds the whole
        // block, each line is its own show string with its own text matrix, and nothing in the character
        // stream separates two lines, so a tagged reader reads "betagamma". poppler gets the break from
        // glyph positions, which 14.8.2.5 note 2 says a reader does not need to guess from.
        var strings = TaggedStrings((d, s) => d.Add(new Preformatted("alpha beta" + Lf + "gamma delta", s)));

        Assert.Equal(["alpha beta", "gamma delta"], strings);
        Assert.Equal("alpha betagamma delta", string.Concat(strings));
    }

    [Fact(Skip = "Gap #588: a Preformatted line ends without a space in its show string, so the character " +
        "stream of a tagged file reads 'betagamma' across the line; 14.8.2.5 asks for the separator " +
        "that plain text would have.")]
    public void Iso19005_2_6_7_3_2_APreformattedLineEndHasASpaceInItsShowString()
    {
        var strings = TaggedStrings((d, s) => d.Add(new Preformatted("alpha beta" + Lf + "gamma delta", s)));

        Assert.EndsWith(" ", strings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Iso19005_2_6_7_3_2_AParagraphLineBreakHasNoSpaceInItsShowStringAsIssue588Describes()
    {
        // A paragraph's explicit line break has the same shape: "alpha beta" and "gamma delta" are two show
        // strings, so the stream reads "betagamma". #588's Scope says a line ended by an explicit line
        // feed gets no space, which conflicts with its comment on Preformatted and with 14.8.2.5.
        var strings = TaggedStrings((d, s) => d.Add(new Paragraph("alpha beta" + Lf + "gamma delta", s)));

        Assert.Equal(["alpha beta", "gamma delta"], strings);
    }

    [Fact(Skip = "Gap #588: a paragraph's explicit line break ends a show string without a space, so a tagged " +
        "file's character stream reads 'betagamma' across it; 14.8.2.5 asks for the separator that " +
        "plain text would have. #588's Scope sentence about lines ended by an explicit line feed " +
        "conflicts with this.")]
    public void Iso19005_2_6_7_3_2_AParagraphLineBreakHasASpaceInItsShowString()
    {
        var strings = TaggedStrings((d, s) => d.Add(new Paragraph("alpha beta" + Lf + "gamma delta", s)));

        Assert.EndsWith(" ", strings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Issue662_AWrappedPreformattedLineKeepsEveryCharacterAndDrawsItsHookAsAnArtifact()
    {
        var line = string.Concat(Enumerable.Repeat("abcdefghij", 12));
        LayoutDocument? document = null;
        var (bytes, _) = Build(PdfConformance.PdfA2a, doc =>
        {
            document = doc;
            doc.Add(new Preformatted(line, Style(doc, Mono, 10)));
        });
        using var pdf = new Pdf(bytes);
        var strings = Strings(pdf);

        // 120 columns of Courier-metric text are wider than the 468-point content area at 10 pt, so
        // the line is split. No character is dropped and the hook is not text.
        Assert.True(strings.Count >= 2);
        Assert.Equal(line, string.Concat(strings.Select(s => s.Text)));
        Assert.All(strings, s => Assert.Matches("^[a-j]+$", s.Text));
        Assert.Contains(document!.Diagnostics, d => d.Code == LayoutDiagnosticCode.PreformattedLineWrapped);

        var marked = new Stack<string>();
        var artifactPaths = 0;
        foreach (var op in Operators(Content(pdf)))
        {
            if (op.Name is "BMC" or "BDC")
                marked.Push(op.Operands[0][1..]);
            else if (op.Name == "EMC")
                marked.Pop();
            else if (op.Name is "l" or "c" && marked.Count > 0 && marked.Peek() == "Artifact")
                artifactPaths++;
        }

        Assert.True(artifactPaths >= strings.Count - 1, "the hooks are path operators inside Artifact sequences");
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b, "6.2.11.4.1")]
    [InlineData(PdfConformance.PdfA2u, "6.2.11.4.1")]
    [InlineData(PdfConformance.PdfA2a, "6.2.11.4.1")]
    [InlineData(PdfConformance.PdfUA1, "7.21.4.1")]
    public void Iso19005_2_6_2_11_4_1_APreformattedBlockInAnUnembeddedFontIsRefusedUnderAClaimAndSavesWithout(
        PdfConformance claim, string clause)
    {
        // R4: Preformatted is new API, so it refuses a font that is not embedded at the save.
        var doc = LayoutDocument(claim);
        doc.Add(new Preformatted("a  b\tc"));
        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains(clause, ex.Message, StringComparison.Ordinal);

        var free = LayoutDocument(PdfConformance.None);
        free.Add(new Preformatted("a  b\tc"));
        Assert.NotEmpty(Save(free));
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u, "\uFEFF")]
    [InlineData(PdfConformance.PdfA2a, "\uFFFE")]
    [InlineData(PdfConformance.PdfUA1, "\uFEFF")]
    [InlineData(PdfConformance.PdfA2b, "\u0378")]
    [InlineData(PdfConformance.PdfA2u, "\u0378")]
    [InlineData(PdfConformance.PdfUA1, "\u0378")]
    public void Iso19005_2_6_2_11_8_APreformattedBlockRefusesACharacterToUnicodeCannotNameOrTheFontLacks(
        PdfConformance claim, string character)
    {
        var doc = LayoutDocument(claim);
        doc.Add(new Preformatted("A" + character + "B", Style(doc, DejaVu, 10)));
        Assert.Throws<InvalidOperationException>(() => Save(doc));

        // Under no claim the same text saves, and a character the font lacks is glyph 0 and reported.
        var free = LayoutDocument(PdfConformance.None);
        free.Add(new Preformatted("A" + character + "B", Style(free, DejaVu, 10)));
        Assert.NotEmpty(Save(free));
    }

    // ── Joined runs (#650), the space at a style change, and the soft line end ─────────────────

    private static (byte[] Bytes, List<string> Strings) Runs(PdfConformance claim, params (string Text,
        double Size)[] runs)
    {
        var (bytes, _) = Build(claim, doc =>
        {
            var styles = new Dictionary<double, TextStyle>();
            TextStyle S(double size) => styles.TryGetValue(size, out var s) ? s : styles[size] = Style(doc, DejaVu,
                size);
            doc.Add(new Paragraph(runs.Select(r => new TextRun(r.Text, S(r.Size)))));
        });
        using var pdf = new Pdf(bytes);
        return (bytes, [.. Strings(pdf).Select(s => s.Text)]);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2a, "Hel", 11.0, "lo", 11.0, "Hello")]
    [InlineData(PdfConformance.PdfA2a, "Hel", 11.0, "lo", 16.0, "Hello")]
    [InlineData(PdfConformance.PdfUA1, "Hello", 11.0, " world", 16.0, "Hello world")]
    [InlineData(PdfConformance.PdfUA1, "Hello ", 11.0, "world", 16.0, "Hello world")]
    [InlineData(PdfConformance.PdfA2a, "Hello ", 11.0, " world", 16.0, "Hello world")]
    public void Iso32000_1_14_8_2_5_ARunBoundaryInsideAWordWritesNoSpaceAndOneAtAWordBreakWritesIt(
        PdfConformance claim, string first, double size1, string second, double size2, string expected)
    {
        var (bytes, strings) = Runs(claim, (first, size1), (second, size2));

        // The characters of the show strings, in order, are the characters of the runs: a space is
        // written where the runs have one, in the string that holds it, and none is added where a
        // run ends inside a word, so a consumer that reads the Unicode stream finds the same words.
        Assert.Equal(expected, string.Concat(strings));
        if (first.EndsWith(' ') || second.StartsWith(' '))
            Assert.Contains(strings, s => s.EndsWith(' ') || s.StartsWith(' '));
        else
            Assert.DoesNotContain(strings, s => s.EndsWith(' ') && s != strings[^1]);
        Assert.Equal(expected, PopplerText(bytes).Trim());
    }

    [Fact]
    public void Iso19005_2_6_7_3_2_AWordEndingAtAStyleChangeKeepsItsSpaceInTheShowString()
    {
        // 6.7.3.2 (level A): a word that ends at the end of a show string has its space in that string.
        // Here the second run starts a new show string with its own font size, and the boundary is a word
        // break: the space is the last character of the first string, never lost between the two.
        var (_, strings) = Runs(PdfConformance.PdfA2a, ("one two ", 11), ("three", 16), (" four", 11));

        Assert.Equal("one two three four", string.Concat(strings));
        // The break after "two" and the break after "three" are each the last character of the string
        // before it (the writer moves a space at the start of a run to the end of the one before).
        Assert.EndsWith(" ", strings[0], StringComparison.Ordinal);
        Assert.EndsWith(" ", strings[1], StringComparison.Ordinal);
        Assert.All(strings, s => Assert.False(s.StartsWith(' ')));
    }

    private static readonly string Wrapped = string.Concat(Enumerable.Repeat("alpha beta gamma delta ", 12)).TrimEnd();

    private static (byte[] Bytes, List<string> Lines) WrappedParagraph(PdfConformance claim)
    {
        var (bytes, _) = Build(claim, doc => doc.Add(new Paragraph(Wrapped, Style(doc, DejaVu))));
        using var pdf = new Pdf(bytes);
        return (bytes, [.. Strings(pdf).Select(s => s.Text)]);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    public void Iso19005_2_6_7_3_2_TheLastShowStringOfAWrappedLineHasNoSpaceAndTheLinesJoinWithoutOne(
        PdfConformance claim)
    {
        // The state issue #588 describes, pinned: a line that wraps at a space ends in the word and the
        // next begins with the next word, so the Unicode stream across the break has no space. Inside
        // each line the spaces are written.
        var (bytes, lines) = WrappedParagraph(claim);

        Assert.True(lines.Count >= 3);
        Assert.All(lines, l => Assert.DoesNotMatch("\\s$", l));
        Assert.All(lines, l => Assert.Contains(' ', l));
        Assert.NotEqual(Wrapped, string.Concat(lines));
        Assert.Equal(Wrapped.Replace(" ", "", StringComparison.Ordinal), string.Concat(lines).Replace(" ", "",
            StringComparison.Ordinal));
        // poppler reads the break as a line end, so it gets the words right by the layout it measures,
        // not by a character in the stream.
        Assert.Equal(Wrapped, PopplerText(bytes).Trim());
    }

    [Theory(Skip = "Gap #588: the last show string of a line that wrapped at a space has no trailing space, so " +
        "the Unicode stream of a tagged file reads 'alphabeta' across the break; 14.8.2.5 and 19005-2" +
        " 6.7.3.2 need the space in that show string.")]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    public void Iso19005_2_6_7_3_2_AWordEndingAtASoftLineEndHasASpaceInItsShowString(PdfConformance claim)
    {
        var (_, lines) = WrappedParagraph(claim);

        Assert.Equal(Wrapped, string.Concat(lines).TrimEnd());
        Assert.All(lines.Take(lines.Count - 1), l => Assert.EndsWith(" ", l, StringComparison.Ordinal));
    }

    // ── Glyph 0 and unembedded fonts as Layout reports them (#657, #591) ─────────────────────

    private static LayoutDocument DocumentWith(PdfConformance claim, bool strict, Func<LayoutDocument,
        Paragraph> paragraph)
    {
        var doc = LayoutDocument(claim);
        doc.StrictDiagnostics = strict;
        doc.Add(paragraph(doc));
        return doc;
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b, "6.2.11.8")]
    [InlineData(PdfConformance.PdfA2u, "6.2.11.8")]
    [InlineData(PdfConformance.PdfA2a, "6.2.11.8")]
    [InlineData(PdfConformance.PdfUA1, "7.21.8")]
    public void Iso19005_2_6_2_11_8_LayoutReportsEachCharacterWrittenAsGlyphZeroWithTheClauseAndStrictModeRefusesIt(
        PdfConformance claim, string clause)
    {
        var doc = DocumentWith(claim, strict: false, d => new Paragraph("A\u0378B", Style(d, DejaVu)));
        var bytes = Save(doc);

        // The construct: the content shows code 0, and the report names the character, the font and the clause.
        using var pdf = new Pdf(bytes);
        Assert.Contains(0, Strings(pdf).SelectMany(s => Codes(s.Shown.Codes)));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.NotdefGlyph);
        Assert.Equal(LayoutDiagnosticSeverity.Error, report.Severity);
        Assert.Contains(clause, report.Clause, StringComparison.Ordinal);
        Assert.Contains("U+0378", report.Message, StringComparison.Ordinal);

        // StrictDiagnostics turns the report into a refusal at the save (R4 for the opt-in channel).
        var strict = DocumentWith(claim, strict: true, d => new Paragraph("A\u0378B", Style(d, DejaVu)));
        Assert.Throws<InvalidOperationException>(() => Save(strict));
    }

    [Fact]
    public void Iso19005_2_6_2_11_8_WithoutAClaimLayoutStillReportsGlyphZeroAndSavesTheFile()
    {
        var doc = DocumentWith(PdfConformance.None, strict: false, d => new Paragraph("A\u0378B", Style(d, DejaVu)));

        Assert.NotEmpty(Save(doc));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.NotdefGlyph);
        Assert.Contains("U+0378", report.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b, "6.2.11.4.1")]
    [InlineData(PdfConformance.PdfA2u, "6.2.11.4.1")]
    [InlineData(PdfConformance.PdfA2a, "6.2.11.4.1")]
    [InlineData(PdfConformance.PdfUA1, "7.21.4.1")]
    public void Iso19005_2_6_2_11_4_1_LayoutReportsAStandard14FontAsAnErrorAndSavesItUnlessStrict(
        PdfConformance claim, string clause)
    {
        var doc = DocumentWith(claim, strict: false, _ => new Paragraph("Hello",
            new TextStyle { FontRef = Standard14.Helvetica }));
        var bytes = Save(doc);

        using var pdf = new Pdf(bytes);
        Assert.Single(SimpleFonts(pdf));
        Assert.Empty(EmbeddedFonts(pdf));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.UnembeddedFont);
        Assert.Equal(LayoutDiagnosticSeverity.Error, report.Severity);
        Assert.Contains(clause, report.Clause, StringComparison.Ordinal);

        var strict = DocumentWith(claim, strict: true, _ => new Paragraph("Hello",
            new TextStyle { FontRef = Standard14.Helvetica }));
        Assert.Throws<InvalidOperationException>(() => Save(strict));
    }

    [Fact]
    public void Iso19005_2_6_2_11_4_1_WithoutAClaimAStandard14FontIsNotReported()
    {
        var doc = DocumentWith(PdfConformance.None, strict: true, _ => new Paragraph("Hello",
            new TextStyle { FontRef = Standard14.Helvetica }));

        Assert.NotEmpty(Save(doc));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.UnembeddedFont);
    }

    // ── The oracles for the Layout text fixture ──────────────────────────────────────────────

    private static byte[] TextFixture(PdfConformance claim)
    {
        var doc = LayoutDocument(claim);
        var style = Style(doc, DejaVu);
        doc.Add(new Paragraph("Chapter one\t12\nChapter two\t134", style)
        {
            TabStops = [new TabStop(300,
            TabAlignment.Right, TabLeader.Dots)]
        });
        doc.Add(new Preformatted(Code, Style(doc, Mono, 10)));
        doc.Add(new Paragraph([new TextRun("Hel", style), new TextRun("lo wor", Style(doc, DejaVu, 14)),
            new TextRun("ld", style)]));
        doc.Add(new Paragraph(Wrapped, style));
        doc.Add(new Paragraph("Astral \U0001F600 \U0001D538 and private \uF6C5", style));
        return Save(doc);
    }

    [Theory]
    [MemberData(nameof(OracleClaimData))]
    public void Iso19005_2_TheLayoutTextFixtureIsAcceptedByVeraPdfAndQpdfUnderEachClaim(PdfConformance claim)
    {
        var bytes = TextFixture(claim);

        // The only rule the fixture is expected to fail is the one for a private use character
        // without ActualText at level A (gap #602), which no test here hides.
        var failed = VeraPdfFailures(Flavour(claim), bytes);
        Assert.Equal(claim == PdfConformance.PdfA2a ? ["6.2.11.7.3-1"] : [], failed);
        QpdfChecks(bytes);
    }

    [Fact]
    public void Iso32000_2_9_TheLayoutTextFixtureOfAFileWithoutAClaimPassesQpdf()
    {
        var bytes = TextFixture(PdfConformance.None);

        Assert.StartsWith("%PDF-2.0", Encoding.ASCII.GetString(bytes, 0, 8), StringComparison.Ordinal);
        QpdfChecks(bytes);
    }
}
