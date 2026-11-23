// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A moved entry (#685) changes where the element after its list starts, and an element that needs
/// about as many continuations as the limit allows saves or is refused according to where it
/// starts. A document with such an element after a list keeps every list as its twin, which is how
/// the lists were laid out before the clamp and the moving of entries, so the element starts where
/// it started then. The limit is small here so a document reaches it in a blink.
/// </summary>
public sealed class ListDocumentLimitRuleTests
{
    private const int Limit = 60;

    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    // What a save gives, as one string: the SHA of the file, or the exception's type and message.
    private static string Save(Action<DocumentRenderer> add, double pageHeight = 80, int limit = Limit)
    {
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, pageHeight), new EdgeInsets(10))
        {
            ContinuationLimit = limit,
        };
        add(renderer);
        try
        {
            using var ms = new MemoryStream();
            renderer.Render(ms);
            return "OK " + Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16];
        }
        catch (Exception ex)
        {
            return "EX " + ex.GetType().Name + " " + ex.Message;
        }
    }

    private static IRenderer ListOf(ListElement list, bool twin) =>
        twin ? new ListRenderer(list).Unclamped() : new ListRenderer(list);

    // An entry of 40 points does not fit the 20 points left under the first entry on a page of 60
    // after the lead, so the entry moves whole to the next page, which the twin does not do.
    private static ListElement Mover(double size) => new ListElement(ListStyle.OrderedDecimal)
        .Add(new ListItem("a", H(10)))
        .Add(new ListItem([new TextRun("mover", H(size))]) { Style = H(10) });

    private static Paragraph Lead() => new("lead", H(10)) { Margins = EdgeInsets.Zero };

    // One word to a line at 20 points, so n words are n lines.
    private static Paragraph Words(int n) =>
        new(string.Join(" ", Enumerable.Repeat("WWWWW", n)), H(20)) { Margins = EdgeInsets.Zero };

    private static string Shifted(int words, int leads, double mover, bool twin) => Save(r =>
    {
        for (var i = 0; i < leads; i++) r.Add(new ParagraphRenderer(Lead()));
        r.Add(ListOf(Mover(mover), twin));
        r.Add(new ParagraphRenderer(Words(words)));
    });

    /// <summary>
    /// A paragraph that needs about as many continuations as the limit allows starts lower when a
    /// list before it moves an entry, and the limit's verdict on it flips: one lead line and the
    /// moved entry saves where the twin is refused, and two lead lines and a taller entry is
    /// refused where the twin saves. The document is the twin's whichever way it would flip.
    /// </summary>
    [Theory]
    [InlineData(1, 40.0)]
    [InlineData(2, 30.0)]
    public void ALaterElementNearTheLimit_getsTheVerdictItHadWhenAListMovesAnEntry(int leads, double mover)
    {
        // The first length the twin refuses, which is where the limit falls for the paragraph.
        var edge = 1;
        while (edge < 40 * Limit && Shifted(edge, leads, mover, twin: true).StartsWith("OK", StringComparison.Ordinal)) edge++;
        Assert.InRange(edge, Limit / 2, 39 * Limit);

        for (var words = edge - 12; words <= edge + 12; words++)
            Assert.Equal(Shifted(words, leads, mover, twin: true), Shifted(words, leads, mover, twin: false));
    }

    /// <summary>
    /// The fixture's list does move an entry: with a paragraph too short to come near the limit,
    /// the document is not the twin's.
    /// </summary>
    [Theory]
    [InlineData(1, 40.0)]
    [InlineData(2, 30.0)]
    public void AControlWithAShortParagraph_isLaidOutNewAndMovesTheEntry(int leads, double mover)
    {
        var twin = Shifted(2, leads, mover, twin: true);

        Assert.StartsWith("OK ", twin);
        Assert.NotEqual(twin, Shifted(2, leads, mover, twin: false));
    }

    // ── Which elements count, and where the line is ─────────────────────────

    // An element after the moving list whose bound is the count the caller names plus two. The
    // table is one row of one cell.
    private static IRenderer After(string kind, int characters)
    {
        var text = string.Join(" ", Enumerable.Repeat("ab", characters / 3 + 1))[..characters];
        switch (kind)
        {
            case "paragraph":
                return new ParagraphRenderer(new Paragraph(text, H(10)) { Margins = EdgeInsets.Zero });
            case "heading":
                return new HeadingRenderer(new Heading(text, H(10)));
            case "preformatted":
                return new PreformattedRenderer(new Preformatted(text, H(10)));
            case "table":
                var table = new TableElement();
                table.AddRow().AddCell(new Cell(text) { Style = H(10) });
                return new TableRenderer(table);
            case "listchild":
                var parent = new ListItem("p", H(10));
                parent.AddChild(new ListItem(text, H(10)));
                return new ListRenderer(new ListElement(ListStyle.Unordered).Add(parent));
            case "tablerows":
                // One row for each character asked for, each holding an empty cell.
                var rows = new TableElement().SetColumnWidths(100);
                for (var i = 0; i < characters; i++) rows.AddRow().AddCell(new Cell(""));
                return new TableRenderer(rows);
            case "listempty":
                // One entry for each character asked for, each holding no text.
                var empty = new ListElement(ListStyle.Unordered);
                for (var i = 0; i < characters; i++) empty.Add(new ListItem("", H(10)));
                return new ListRenderer(empty);
            default:
                var list = new ListElement(ListStyle.Unordered);
                list.Add(new ListItem(text, H(10)));
                return new ListRenderer(list);
        }
    }

    private static string AfterTheMover(string kind, int characters, bool twin) => Save(r =>
    {
        for (var i = 0; i < 4; i++) r.Add(new ParagraphRenderer(Lead()));
        r.Add(ListOf(Mover(30), twin));
        r.Add(After(kind, characters));
    }, pageHeight: 100);

    /// <summary>
    /// Every built-in element after a list counts, a paragraph, a heading, a preformatted text, a
    /// table and another list, by its characters, entries and rows. A child of an item counts, and
    /// an item with no text counts one. One whose bound is within the limit cannot reach it from
    /// any start, and the lists are laid out new.
    /// </summary>
    [Theory]
    [InlineData("paragraph")]
    [InlineData("heading")]
    [InlineData("preformatted")]
    [InlineData("table")]
    [InlineData("list")]
    [InlineData("listchild")]
    [InlineData("listempty")]
    [InlineData("tablerows")]
    public void AnElementWhoseBoundPassesTheLimit_keepsTheListsAsTheirTwins(string kind)
    {
        // The bound is two plus the characters, and the table adds a row.
        var within = Limit - 4;
        var past = Limit + 4;

        Assert.Equal(AfterTheMover(kind, past, twin: true), AfterTheMover(kind, past, twin: false));
        Assert.NotEqual(AfterTheMover(kind, within, twin: true), AfterTheMover(kind, within, twin: false));
    }

    /// <summary>
    /// The size is read before anything is laid out, so a cell with no text, which a table with
    /// column widths saves, is read as empty and does not raise an exception of its own.
    /// </summary>
    [Fact]
    public void ATableCellWithNoText_isReadAsEmpty()
    {
        string Build(bool twin) => Save(r =>
        {
            for (var i = 0; i < 4; i++) r.Add(new ParagraphRenderer(Lead()));
            r.Add(ListOf(Mover(30), twin));
            var table = new TableElement().SetColumnWidths(100);
            table.AddRow().AddCell(new Cell(null!));
            r.Add(new TableRenderer(table));
        }, pageHeight: 100);

        Assert.StartsWith("OK ", Build(twin: true));
        Assert.StartsWith("OK ", Build(twin: false));
    }

    /// <summary>
    /// The line is the limit: a bound equal to it needs no fallback, and one more does.
    /// </summary>
    [Fact]
    public void TheBoundOfTwoPlusTheCharacters_isComparedWithTheLimitItself()
    {
        Assert.NotEqual(AfterTheMover("paragraph", Limit - 2, twin: true), AfterTheMover("paragraph", Limit - 2, twin: false));
        Assert.Equal(AfterTheMover("paragraph", Limit - 1, twin: true), AfterTheMover("paragraph", Limit - 1, twin: false));
    }

    /// <summary>
    /// The element has to come after a list: before every list its start is nobody's doing, so a
    /// big element ahead of the lists leaves them new. Some number of lead lines puts the entry
    /// where it moves, and there the document is not the twin's.
    /// </summary>
    [Fact]
    public void AnElementAheadOfEveryList_doesNotTurnTheListsIntoTheirTwins()
    {
        string Build(int leads, bool twin) => Save(r =>
        {
            r.Add(After("paragraph", Limit + 40));
            for (var i = 0; i < leads; i++) r.Add(new ParagraphRenderer(Lead()));
            r.Add(ListOf(Mover(30), twin));
        }, pageHeight: 100);

        Assert.Contains(Enumerable.Range(0, 7), leads => Build(leads, twin: true) != Build(leads, twin: false));
    }

    /// <summary>Each list the document places reports the fallback, as with the font rule.</summary>
    [Fact]
    public void TheFallback_isReportedOncePerList()
    {
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 100), new EdgeInsets(10)) { ContinuationLimit = Limit };
        for (var i = 0; i < 4; i++) renderer.Add(new ParagraphRenderer(Lead()));
        renderer.Add(new ListRenderer(Mover(30)));
        renderer.Add(After("paragraph", Limit + 40));
        using var ms = new MemoryStream();

        renderer.Render(ms);

        Assert.Single(renderer.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);
    }

    // ── Renderers the library ships that hold no list ───────────────────────

    /// <summary>A renderer that lays no list out and carries no mark.</summary>
    private sealed class Unmarked : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 1, 1));

        public void Draw(DrawContext ctx)
        {
        }
    }

    private sealed class Marked : IRenderer, IListFreeRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 1, 1));

        public void Draw(DrawContext ctx)
        {
        }
    }

    /// <summary>
    /// A renderer that says it holds no list does not make the document lay out once more, and one
    /// that does not say so does. The mark is internal, so a caller's renderer cannot carry it.
    /// </summary>
    [Fact]
    public void AMarkedRendererAfterAList_isNotLaidOutOnceMore_andAnUnmarkedOneIs()
    {
        int Calls(IRenderer after)
        {
            var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
            var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 100), new EdgeInsets(10));
            renderer.Add(new ListRenderer(new ListElement().Add(new ListItem("a", H(10)))));
            renderer.Add(after);
            using var ms = new MemoryStream();
            renderer.Render(ms);
            return renderer.LayoutCalls;
        }

        Assert.Equal(2, Calls(new Marked()));
        Assert.Equal(4, Calls(new Unmarked()));
        Assert.False(typeof(IListFreeRenderer).IsPublic);
    }
}
