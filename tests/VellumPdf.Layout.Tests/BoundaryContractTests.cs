// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using LayoutDocument = VellumPdf.Layout.Document;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The boundaries the Layout XML documentation states, pinned as known answers.
///
/// PR #542 documented the package's public surface by measuring it over 21 review rounds, and later
/// rounds re-derived partitions an earlier round had already measured, because nothing held the
/// figures. A sentence in a doc comment is only as good as the last run that checked it, so these tests
/// are that check: a future round reads a failing assertion instead of re-measuring from zero.
///
/// Every case here comes from a probe that produced the sentence it pins. A refusal is asserted by
/// exception type, <c>ParamName</c> or a fragment of the message, an acceptance by the save
/// completing, and a placement by the bytes written. Non-finite inputs are listed value by value:
/// NaN, positive infinity and negative infinity can take three different branches, and during #542 a
/// measurement of one of them was twice promoted to a claim about all three.
///
/// A failure here means the library moved, not that the test is stale. Fix the code or fix the
/// sentence the case names, and do both in the same commit.
/// </summary>
public sealed class BoundaryContractTests
{
    private static byte[] Render(LayoutDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Stream(LayoutDocument doc) =>
        PdfTestUtil.DecompressAllFlatStreams(Render(doc));

    // ── The default page, which several boundaries below are expressed against ──────────────────

    /// <summary>
    /// A4 with 72pt margins leaves a content area 451.28pt wide. The figures below (451.28, 451.2799,
    /// 297.64, 523.28) are what #542's probes measured for the rules
    /// <see cref="ListElement.Indent"/> and <see cref="TextStyle.FontRef"/> state, not numbers the
    /// documentation quotes. Pinned first, because every boundary below moves if the default page does.
    /// </summary>
    [Fact]
    public void DefaultPage_contentArea_is451_28Wide()
    {
        using var doc = new LayoutDocument();

        Assert.Equal(595.28, doc.PageSize.Width, 2);
        Assert.Equal(841.89, doc.PageSize.Height, 2);
        Assert.Equal(new EdgeInsets(72), doc.Margins);
        Assert.Equal(451.28, doc.PageSize.Width - doc.Margins.Horizontal, 2);
    }

    // ── DrawContext and RendererContext: which null decides, when two are null ──────────────────

    private static DrawContext Context(PdfDocument pdf, PdfPage? page, RendererContext rc) =>
        new(new PdfCanvas(pdf.AddPage()), new LayoutBox(0, 0, 595.28, 841.89), rc, pdf, page!);

    /// <summary>
    /// <see cref="DrawContext.UseEmbeddedFont"/> reads only its <see cref="RendererContext"/>. A
    /// null handle raises <see cref="NullReferenceException"/> — but not when that context was
    /// built without a page, because the kernel looks the page up before it reads the handle, and
    /// answers <see cref="ArgumentNullException"/> with <c>ParamName</c> <c>key</c> instead.
    ///
    /// This overlap is why the member's two tags carry their conditions: an earlier version claimed
    /// the NullReferenceException for a null handle unconditionally, which is false for one of the
    /// four rows below.
    /// </summary>
    [Theory]
    [InlineData(true, true, "ArgumentNullException")] // page null, handle null: the page wins
    [InlineData(true, false, "ArgumentNullException")] // page null, handle given
    [InlineData(false, true, "NullReferenceException")] // page given, handle null
    [InlineData(false, false, null)] // neither null
    public void UseEmbeddedFont_nullPageOutranksNullHandle(bool pageNull, bool handleNull, string? expected)
    {
        // Only the rows with a real handle need a font file; the null-handle rows run everywhere.
        var fontPath = handleNull ? null : PdfTestUtil.FindPlatformFont();
        if (!handleNull && fontPath is null) Assert.Skip("no platform TrueType font found");

        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        var rc = new RendererContext(pageNull ? null! : page, pdf);
        var ctx = Context(pdf, page, rc);
        var handle = handleNull ? null! : pdf.UseTrueTypeFont(File.ReadAllBytes(fontPath!));

        if (expected is null)
        {
            Assert.False(string.IsNullOrEmpty(ctx.UseEmbeddedFont(handle)));
            return;
        }

        var ex = Record.Exception(() => ctx.UseEmbeddedFont(handle));
        Assert.NotNull(ex);
        Assert.Equal(expected, ex.GetType().Name);
        if (ex is ArgumentNullException ane) Assert.Equal("key", ane.ParamName);
    }

    /// <summary>
    /// <see cref="RendererContext.RegisterImageXObject"/> reads its own argument first, the opposite
    /// order to <see cref="DrawContext.UseEmbeddedFont"/>, so the two cannot share a rule. A null
    /// image answers <see cref="ArgumentNullException"/> even when the context has no document,
    /// while a sound image on a document-less context raises <see cref="NullReferenceException"/>.
    /// </summary>
    [Theory]
    [InlineData(true, true, "ArgumentNullException")] // both null: the image is read first
    [InlineData(true, false, "ArgumentNullException")]
    [InlineData(false, true, "NullReferenceException")] // sound image, no document
    [InlineData(false, false, null)]
    public void RegisterImageXObject_nullImageOutranksNullDocument(
        bool imageNull, bool documentNull, string? expected)
    {
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        var rc = new RendererContext(page, documentNull ? null! : pdf);
        var image = imageNull ? null! : Images.BmpImageLoader.Load(OnePixelBmp());

        if (expected is null)
        {
            Assert.Equal("Im1", rc.RegisterImageXObject(image));
            return;
        }

        var ex = Record.Exception(() => rc.RegisterImageXObject(image));
        Assert.NotNull(ex);
        Assert.Equal(expected, ex.GetType().Name);
        if (ex is ArgumentNullException ane) Assert.Equal("key", ane.ParamName);
    }

    /// <summary>
    /// The constructor's remarks say a null page costs a structure element its <c>/Pg</c> entry.
    /// That holds for <see cref="DrawContext.RegisterStructElem"/>, which stamps the page onto the
    /// element, and not for <see cref="DrawContext.RegisterStructElemTree"/>, which never touches
    /// it — the root keeps the page its caller set, and the save writes it. Round 21 found the
    /// sentence claiming both.
    /// </summary>
    [Theory]
    [InlineData(false, false)] // RegisterStructElem: page erased, no /Pg
    [InlineData(true, true)] // RegisterStructElemTree: page kept, /Pg written
    public void NullContextPage_erasesThePage_forRegisterStructElem_notForTheTree(bool viaTree, bool expectPg)
    {
        using var pdf = new PdfDocument { Tagged = true };
        var page = pdf.AddPage();
        var ctx = Context(pdf, null, new RendererContext(page, pdf));
        var elem = new PdfStructElem("Sect") { Page = page };

        if (viaTree) ctx.RegisterStructElemTree(elem);
        else ctx.RegisterStructElem(elem);

        using var ms = new MemoryStream();
        pdf.Save(ms);
        var bytes = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        Assert.Equal(expectPg, elem.Page is not null);
        Assert.Equal(expectPg, bytes.Contains("/Pg", StringComparison.Ordinal));
    }

    /// <summary>
    /// What a save refuses is the value it writes, not the value passed. <c>AddOutlineEntry</c> flips
    /// the Y through <see cref="DrawContext.PageBounds"/>, so a finite argument is refused when that
    /// height is not finite or the subtraction overflows. The tags blamed the caller's argument
    /// until round 21.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, 10.0)]
    [InlineData(double.PositiveInfinity, 10.0)]
    [InlineData(double.NegativeInfinity, 10.0)]
    [InlineData(double.MaxValue, -double.MaxValue)]
    public void OutlineEntry_refusesTheFlippedValue_notTheArgument(double pageHeight, double layoutY)
    {
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        var ctx = new DrawContext(new PdfCanvas(page), new LayoutBox(0, 0, 595.28, pageHeight),
            new RendererContext(page, pdf), pdf, page);

        ctx.AddOutlineEntry("t", 0, layoutY);

        var ex = Assert.Throws<ArgumentException>(() => { using var ms = new MemoryStream(); pdf.Save(ms); });
        Assert.Contains("PDF does not support NaN or Infinity", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page from another document is accepted, and the two members answer it differently: the
    /// save visits its own pages only, so a link annotation registered on a foreign page is left
    /// out of the file — with its coordinates never checked — while an outline entry is still
    /// written, pointing at a null destination.
    /// </summary>
    [Fact]
    public void ForeignPage_dropsTheAnnotation_andKeepsTheOutlineEntry()
    {
        using var pdf = new PdfDocument();
        var own = pdf.AddPage();
        using var other = new PdfDocument();
        var foreign = other.AddPage();
        var ctx = new DrawContext(new PdfCanvas(own), new LayoutBox(0, 0, 595.28, 841.89),
            new RendererContext(foreign, pdf), pdf, foreign);

        ctx.AddUriLinkAnnotation(new LayoutBox(double.NaN, 0, 1, 1), "https://example.org");
        ctx.AddOutlineEntry("T", 0, 10);

        using var ms = new MemoryStream();
        pdf.Save(ms);
        var bytes = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        Assert.DoesNotContain("/Annots", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", bytes, StringComparison.Ordinal);
        Assert.Contains("/Dest [null", bytes, StringComparison.Ordinal);
    }

    // ── Document.Add: the overloads that carry a TextStyle defer its refusals to the save ───────

    /// <summary>
    /// None of the five overloads below checks the style it is given, so a style the layout refuses
    /// surfaces at the save. A null element is a different matter and is not covered here: some
    /// overloads refuse it from the call itself. Round 9 gave
    /// <see cref="LayoutDocument.Add(string, TextStyle)"/> these tags and stopped; round 21 found
    /// the siblings that reach the same code still tagging only the null case.
    /// </summary>
    [Theory]
    [MemberData(nameof(StyleBearingElementsAtEachNonFiniteSize))]
    public void Add_nonFiniteFontSize_throwsFromSaveNotFromAdd(
        string _, Action<LayoutDocument, TextStyle> add, double size, string fragment)
    {
        using var doc = new LayoutDocument();
        add(doc, new TextStyle { FontSize = size });

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, Action<LayoutDocument, TextStyle>, double, string>
        StyleBearingElementsAtEachNonFiniteSize()
    {
        var data = new TheoryData<string, Action<LayoutDocument, TextStyle>, double, string>();
        var sizes = new (double Size, string Fragment)[]
        {
            (double.NaN, "a font size of NaN"),
            (double.PositiveInfinity, "a font size of positive infinity"),
            (double.NegativeInfinity, "a font size of negative infinity"),
        };
        foreach (var (name, add) in StyleBearingOverloads)
        {
            foreach (var (size, fragment) in sizes) data.Add(name, add, size, fragment);
        }
        return data;
    }

    /// <summary>
    /// The same overloads answer an unnamed <see cref="Standard14"/> value with
    /// <see cref="IndexOutOfRangeException"/>, again from the save. Both types are on each
    /// overload's tags because both are reachable through it.
    /// </summary>
    [Theory]
    [MemberData(nameof(StyleBearingElements))]
    public void Add_unnamedStandard14_throwsIndexOutOfRangeFromSave(
        string _, Action<LayoutDocument, TextStyle> add)
    {
        using var doc = new LayoutDocument();
        add(doc, new TextStyle { Font = (Standard14)99 });

        Assert.Throws<IndexOutOfRangeException>(() => Render(doc));
    }

    private static readonly (string Name, Action<LayoutDocument, TextStyle> Add)[] StyleBearingOverloads =
    [
        ("Add(string, style)", (d, s) => d.Add("x", s)),
        ("Add(Paragraph)", (d, s) => d.Add(new Paragraph("x", s))),
        ("Add(Heading)", (d, s) => d.Add(new Heading("x", s))),
        ("Add(ListElement)", (d, s) => d.Add(new ListElement().Add("x", s))),
        ("Add(TableElement)", (d, s) =>
        {
            var t = new TableElement();
            t.AddRow().AddCell(new Cell("x") { Style = s });
            d.Add(t);
        }),
    ];

    public static TheoryData<string, Action<LayoutDocument, TextStyle>> StyleBearingElements()
    {
        var data = new TheoryData<string, Action<LayoutDocument, TextStyle>>();
        foreach (var (name, add) in StyleBearingOverloads) data.Add(name, add);
        return data;
    }

    // ── Heading.Level: what the outline nests on ────────────────────────────────────────────────

    /// <summary>
    /// The outline builder hangs an entry under the most recent earlier entry at exactly
    /// <c>Level - 1</c>, and puts it at the top level when there is none. So consecutive levels
    /// nest and a jump does not: after a level 0 heading, level 5 and level 500 both land beside it.
    /// <see cref="Heading.Level"/> claimed the outline "still nests on the number you gave" until
    /// round 21 measured it.
    /// </summary>
    [Theory]
    [InlineData(new[] { 0, 1, 2 }, 3)] // a chain: three distinct parents
    [InlineData(new[] { 0, 5, 500 }, 1)] // all at the root: one parent
    [InlineData(new[] { 0, 6 }, 1)]
    [InlineData(new[] { 0, 1, 500 }, 2)] // the jump falls back to the root the level 0 entry uses
    [InlineData(new[] { -4, -3 }, 2)] // negative levels nest on the same rule
    public void HeadingLevel_nestsOnTheLevelBelowIt_notOnTheNumber(int[] levels, int distinctParents)
    {
        using var doc = new LayoutDocument();
        foreach (var level in levels) doc.Add(new Heading($"L{level}") { Level = level });

        var bytes = System.Text.Encoding.Latin1.GetString(Render(doc));
        var parents = System.Text.RegularExpressions.Regex
            .Matches(bytes, @"\d+ 0 obj\s*<<[^>]*?/Title[^>]*?/Parent (\d+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Equal(levels.Length, System.Text.RegularExpressions.Regex
            .Matches(bytes, @"\d+ 0 obj\s*<<[^>]*?/Title").Count);
        Assert.Equal(distinctParents, parents.Count);
    }

    // ── ListElement.Indent ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A nested list is refused when its indent reaches the list's own area width, inclusively, and
    /// the boundary moves with the list's margins. The documentation quotes these figures, so they
    /// are pinned either side of the edge rather than sampled.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 451.2799, false)]
    [InlineData(0, 0, 451.28, true)]
    [InlineData(50, 30, 371.27, false)]
    [InlineData(50, 30, 371.28, true)]
    public void NestedListIndent_isRefusedAtTheAreaWidth_inclusive(
        double left, double right, double indent, bool refused)
    {
        using var doc = new LayoutDocument();
        var item = new ListItem("parent");
        item.AddChild("child");
        doc.Add(new ListElement { Indent = indent, Margins = new EdgeInsets(0, right, 0, left) }.Add(item));

        if (refused)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
            Assert.Contains("too tall to fit on a single page", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEmpty(Render(doc));
        }
    }

    /// <summary>
    /// A flat list with unlinked text is refused at no indent at all — the marker paragraph is laid
    /// out with zero margins, so the indent can never drive its area to nothing. Every
    /// value in the rows below, including the three non-finite ones separately. Linked text
    /// is a different case: a linked run at NaN throws <see cref="ArgumentException"/>, per
    /// <see cref="ListElement.Indent"/>.
    /// </summary>
    [Theory]
    [InlineData(-1e308)]
    [InlineData(0)]
    [InlineData(451.28)]
    [InlineData(1e308)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FlatListIndent_withUnlinkedText_isNeverRefused(double indent)
    {
        using var doc = new LayoutDocument();
        doc.Add(new ListElement { Indent = indent }.Add(new ListItem("flat")));

        Assert.NotEmpty(Render(doc));
    }

    /// <summary>
    /// At positive infinity a flat list draws its marker and discards the item text (a behaviour
    /// #476 intends to change): one show
    /// operator in the content stream, and the item's own text nowhere in it. A default indent
    /// draws both, which is what makes the first assertion discriminating rather than a count of
    /// whatever happened to be emitted.
    /// </summary>
    [Theory]
    [InlineData(double.PositiveInfinity, 1, false)]
    [InlineData(20, 2, true)]
    public void FlatListIndent_atPositiveInfinity_drawsTheMarkerAndDropsTheText(
        double indent, int showOperators, bool keepsText)
    {
        using var doc = new LayoutDocument();
        doc.Add(new ListElement { Indent = indent }.Add(new ListItem("ITEMTEXT")));

        var stream = Stream(doc);

        Assert.Equal(showOperators, CountShowOperators(stream));
        Assert.Equal(keepsText, stream.Contains("ITEMTEXT", StringComparison.Ordinal));
    }

    /// <summary>
    /// The finite-negative route the member documents: a nested list with linked item text, an
    /// indent of -1e308 and a left margin of -1e308, whose gutter overflows into a link rectangle
    /// the save cannot write. The same indent without the margin, and the same pair on a flat list,
    /// both save — which is why the sentence carries its example.
    /// </summary>
    [Theory]
    [InlineData(true, -1e308, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, -1e308, false)]
    public void ListIndent_finiteNegative_throwsOnlyWhenTheGutterOverflows(
        bool nested, double leftMargin, bool refused)
    {
        using var doc = new LayoutDocument();
        var style = new TextStyle { LinkUri = "https://example.org" };
        var list = new ListElement { Indent = -1e308, Margins = new EdgeInsets(0, 0, 0, leftMargin) };
        if (nested)
        {
            var item = new ListItem("parent", style);
            item.AddChild("child", style);
            list.Add(item);
        }
        else
        {
            list.Add(new ListItem("flat", style));
        }
        doc.Add(list);

        if (refused)
        {
            var ex = Assert.Throws<ArgumentException>(() => Render(doc));
            Assert.Contains("PDF does not support NaN or Infinity", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEmpty(Render(doc));
        }
    }

    // ── Symbol and ZapfDingbats measure zero (#470) ──────────────────────────────────────────────

    /// <summary>
    /// Both faces measure every character as zero width, at every size probed. Checked across every
    /// non-surrogate Basic Multilingual Plane code point rather than at a handful, because the
    /// documentation says "every character" and a quantifier needs its boundary.
    /// </summary>
    [Theory]
    [InlineData(Standard14.Symbol)]
    [InlineData(Standard14.ZapfDingbats)]
    public void SymbolFaces_measureEveryBmpCharacterAsZero(Standard14 font)
    {
        foreach (var size in new[] { 1e-300, 1.0, 12.0, 1e308 })
        {
            var style = new TextStyle { FontRef = font, FontSize = size };
            for (var cp = 0; cp <= 0xFFFF; cp++)
            {
                if (cp is >= 0xD800 and <= 0xDFFF) continue; // an unpaired surrogate is a separate input
                Assert.Equal(0, style.MeasureString(((char)cp).ToString()));
            }
        }
    }

    /// <summary>
    /// Because the width is zero, a centred Symbol line is placed at the exact centre of the content
    /// area and a right-aligned one at its exact right edge. 297.64 and 523.28 are the values
    /// #542's probes measured for that rule on the default A4 page, not figures the documentation
    /// quotes. A Helvetica control shows the same line placed on its measured width.
    /// </summary>
    [Theory]
    [InlineData(Standard14.Symbol, HorizontalAlignment.Center, "297.64")]
    [InlineData(Standard14.Symbol, HorizontalAlignment.Right, "523.28")]
    [InlineData(Standard14.Helvetica, HorizontalAlignment.Center, "257.296")]
    [InlineData(Standard14.Helvetica, HorizontalAlignment.Right, "442.592")]
    public void SymbolLine_isPlacedAsIfItHadNoWidth(
        Standard14 font, HorizontalAlignment alignment, string expectedX)
    {
        using var doc = new LayoutDocument();
        doc.Add(new Paragraph("SYMBOLTEXT", new TextStyle { FontRef = font, FontSize = 12 })
        {
            Alignment = alignment,
        });

        Assert.Contains($"1 0 0 1 {expectedX} ", Stream(doc), StringComparison.Ordinal);
    }

    /// <summary>
    /// The unit is the line, not the paragraph. A line holding a Symbol run followed by a Helvetica
    /// run is centred on the Helvetica run's width alone. The Helvetica run starts where the Symbol
    /// run does and is drawn over it, the Symbol run being drawn first. Run order and the font of
    /// the space between the runs change the result: with a Helvetica run followed by a Symbol
    /// run, the Symbol run starts after the Helvetica one (x 262.63 and 332.65 measured).
    /// Round 20 changed <see cref="TextStyle.FontRef"/> to say "set wholly in either font", which
    /// scopes the zero-width placement to lines set wholly in one of the two fonts.
    /// </summary>
    [Fact]
    public void MixedLine_isCentredOnTheMeasuredRunAlone()
    {
        using var doc = new LayoutDocument();
        var p = new Paragraph("SYM", new TextStyle { FontRef = Standard14.Symbol, FontSize = 12 })
        {
            Alignment = HorizontalAlignment.Center,
        };
        p.Add("HELVETICA", new TextStyle { FontRef = Standard14.Helvetica, FontSize = 12 });
        doc.Add(p);

        var stream = Stream(doc);
        var xs = System.Text.RegularExpressions.Regex
            .Matches(stream, @"1 0 0 1 (\d+\.?\d*) \d")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(["264.298", "264.298"], xs);
        Assert.Contains("(SYM", stream, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rule per line. In a paragraph that wraps, the first line holds only Helvetica text
    /// and is centred on its measured width. The second holds Helvetica text, a Symbol run and a
    /// Helvetica run, and is centred on the Helvetica text alone: the Symbol run and the Helvetica
    /// run after it start at the same x, so the Helvetica run is drawn over the Symbol run.
    /// </summary>
    [Fact]
    public void MixedLine_afterAWrappedLine_isCentredPerLine()
    {
        using var doc = new LayoutDocument();
        var p = new Paragraph(string.Concat(Enumerable.Repeat("abcdefghij ", 8)),
            new TextStyle { FontRef = Standard14.Helvetica, FontSize = 12 })
        {
            Alignment = HorizontalAlignment.Center,
        };
        p.Add("SYM", new TextStyle { FontRef = Standard14.Symbol, FontSize = 12 });
        p.Add("HELVETICA", new TextStyle { FontRef = Standard14.Helvetica, FontSize = 12 });
        doc.Add(p);

        var stream = Stream(doc);
        var xs = System.Text.RegularExpressions.Regex
            .Matches(stream, @"1 0 0 1 (\d+\.?\d*) \d+\.?\d* Tm")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(["96.196", "235.282", "293.314", "293.314"], xs);
        Assert.Contains("(SYM ) Tj", stream, StringComparison.Ordinal);
    }

    /// <summary>
    /// A paragraph set wholly in Symbol is never wrapped, however long it is: 660 characters
    /// draw as one line, where the same text in Helvetica wraps to nine.
    /// </summary>
    [Fact]
    public void SymbolParagraph_isNeverWrapped()
    {
        var text = string.Concat(Enumerable.Repeat("abcdefghij ", 60));

        using var symbol = new LayoutDocument();
        symbol.Add(new Paragraph(text, new TextStyle { FontRef = Standard14.Symbol, FontSize = 12 }));
        using var helvetica = new LayoutDocument();
        helvetica.Add(new Paragraph(text, new TextStyle { FontRef = Standard14.Helvetica, FontSize = 12 }));

        Assert.Equal(1, CountShowOperators(Stream(symbol)));
        Assert.Equal(9, CountShowOperators(Stream(helvetica)));
    }

    private static int CountShowOperators(string stream) =>
        System.Text.RegularExpressions.Regex.Matches(stream, @"\) Tj").Count;

    private static byte[] OnePixelBmp()
    {
        var bmp = new byte[58];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(58).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(1).CopyTo(bmp, 18);
        BitConverter.GetBytes(1).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(4).CopyTo(bmp, 34);
        bmp[56] = 255;
        return bmp;
    }
}
