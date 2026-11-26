// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A document that holds a renderer that is not one of the library's own lays every list out as it
/// was before the fixes of #476 and #685, decided once from the elements added to it, wherever the
/// renderer sits and however it lays its own list out. The reference for each row is the same
/// document built over <see cref="ListRenderer.Unclamped"/>, the list as it was.
/// </summary>
public sealed class ListCallerRendererRuleTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static byte[] Save(Document doc)
    {
        doc.Timestamp = Stamp;
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    private static string[] Shows(byte[] bytes) =>
        Regex.Matches(PdfTestUtil.DecompressAllFlatStreams(bytes), @"\(((?:\\.|[^\\)])*)\)\s*Tj")
            .Select(m => m.Groups[1].Value).ToArray();

    // An indent no page width leaves any text, so the new layout clamps the entry.
    private static ListElement Clamp() =>
        new ListElement(ListStyle.OrderedDecimal) { Indent = 300 }.Add(new ListItem("clamped text", H(10)));

    // Many entries of many words: one word to a line when clamped, only the markers as it was.
    private static ListElement Tall(int items)
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 300 };
        for (var i = 0; i < items; i++)
            list.Add(new ListItem(string.Join(' ', Enumerable.Repeat("word", 12)), H(10)));
        return list;
    }

    private static ListElement Undrawable() =>
        new ListElement(ListStyle.Unordered) { Indent = 1000 }
            .Add(new ListItem([new TextRun("bad", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 })])
            { Style = H(10) });

    private static TableElement Table()
    {
        var table = new TableElement();
        table.AddRow().AddCell("x");
        return table;
    }

    private static Preformatted NearTheLimit() => new(new string('\t', 1_700), H(1)) { TabWidth = 32 };

    // A renderer of the caller's that takes one point and draws nothing.
    private sealed class Opaque : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 1, 1));

        public void Draw(DrawContext ctx)
        {
        }
    }

    // A renderer of the caller's that lays a list out only from its Draw, at the area Layout kept.
    private sealed class LaysOutFromDraw(IRenderer inner) : IRenderer
    {
        private LayoutBox _box;

        public LayoutResult Layout(LayoutContext context)
        {
            _box = context.Area.WithHeight(Math.Min(20, context.Area.Height));
            return LayoutResult.Full(_box);
        }

        public void Draw(DrawContext ctx)
        {
            inner.Layout(new LayoutContext(_box));
            inner.Draw(ctx);
        }
    }

    private static Document Page(bool footer)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 300), Margins = new EdgeInsets(10, 10, 30, 10) };
        if (footer) doc.SetFooter("pg{page}of{pages}");
        return doc;
    }

    private static string[] Skipped(Document doc) =>
        doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped)
            .Select(d => d.Message).ToArray();

    private const string RendererCause = "a renderer of yours";

    /// <summary>
    /// The renderer before the lists, between them, after them, or the only other element: each is
    /// the same document as the one built over the twins, and the lists the document places say
    /// why. The control without the renderer is clamped, so the rows compare two layouts that
    /// differ.
    /// </summary>
    [Theory]
    [InlineData("before", false)]
    [InlineData("between", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public void ARendererOfTheCallers_anywhere_laysEveryListOutAsItWas(string where, bool footer)
    {
        Document Build(bool twin, bool withCaller)
        {
            var doc = Page(footer);
            if (withCaller && where == "before") doc.Add(new Opaque());
            doc.Add(twin ? new ListRenderer(Tall(12)).Unclamped() : new ListRenderer(Tall(12)));
            if (withCaller && where == "between") doc.Add(new Opaque());
            doc.Add(twin ? new ListRenderer(Clamp()).Unclamped() : new ListRenderer(Clamp()));
            if (withCaller && where == "after") doc.Add(new Opaque());
            return doc;
        }

        var reference = Save(Build(twin: true, withCaller: true));
        var doc = Build(twin: false, withCaller: true);
        var bytes = Save(doc);

        Assert.Equal(Sha(reference), Sha(bytes));
        Assert.NotEqual(Sha(reference), Sha(Save(Build(twin: false, withCaller: false))));
        Assert.All(Skipped(doc), m => Assert.Contains(RendererCause, m, StringComparison.Ordinal));
        Assert.Equal(2, Skipped(doc).Length);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
    }

    /// <summary>
    /// The two public doors a renderer enters a document by, <see cref="Document.Add(IRenderer)"/>
    /// and <see cref="DocumentRenderer.Add(IRenderer)"/>, are both read.
    /// </summary>
    [Fact]
    public void TheDocumentRendererDoor_isReadAsWellAsTheDocumentOne()
    {
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 300), new EdgeInsets(10));
        renderer.Add(new ListRenderer(Clamp()));
        renderer.Add(new Opaque());
        using var ms = new MemoryStream();

        renderer.Render(ms);

        var one = Assert.Single(renderer.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.ListIndentClampSkipped, one.Code);
        Assert.Contains(RendererCause, one.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A renderer this library ships, added through <c>Add(IRenderer)</c>, is the library's own:
    /// the lists are laid out the new way and nothing is reported as skipped.
    /// </summary>
    [Theory]
    [InlineData("paragraph")]
    [InlineData("list")]
    [InlineData("table")]
    public void ARendererTheLibraryShips_isNotARendererOfTheCallers(string kind)
    {
        var doc = Page(footer: false);
        doc.Add(new ListRenderer(Clamp()));
        doc.Add(kind switch
        {
            "paragraph" => new ParagraphRenderer(new Paragraph("text", H(10))),
            "list" => new ListRenderer(Clamp()),
            _ => (IRenderer)new TableRenderer(Table()),
        });

        Save(doc);

        Assert.Empty(Skipped(doc));
        Assert.Contains(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
    }

    /// <summary>
    /// A footer counts pages in a pass of its own, and the pass that draws must agree with it. The
    /// renderer lays its list out only from its <c>Draw</c>, so only the drawing pass reaches that
    /// list: the total in the footer is the number of pages in the file whatever the list holds.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(30)]
    public void AFooterTotal_isThePageCountOfTheFile_inADocumentThatHoldsARenderer(int items)
    {
        Document Build(bool twin)
        {
            var doc = Page(footer: true);
            doc.Add(twin ? new ListRenderer(Clamp()).Unclamped() : new ListRenderer(Clamp()));
            doc.Add(new LaysOutFromDraw(new ListRenderer(Undrawable())));
            doc.Add(twin ? new ListRenderer(Tall(items)).Unclamped() : new ListRenderer(Tall(items)));
            return doc;
        }

        var bytes = Save(Build(twin: false));

        var pages = PlacementLoopSupport.PageCount(bytes);
        var footers = Shows(bytes).Where(s => s.StartsWith("pg", StringComparison.Ordinal)).ToArray();
        Assert.Equal(pages, footers.Length);
        Assert.All(footers, f => Assert.EndsWith("of" + pages, f, StringComparison.Ordinal));
        Assert.Equal(Sha(Save(Build(twin: true))), Sha(bytes));
    }

    /// <summary>
    /// The 207 of a list the rule turned into its twin names every cause the document read, and
    /// only those: the renderer, alone or with the font, the element near the limit, or both. The
    /// rows without a renderer of the caller's expect its cause to be absent.
    /// </summary>
    [Theory]
    [InlineData("font", false, true, false)]
    [InlineData("near", false, false, true)]
    [InlineData("font, near", false, true, true)]
    [InlineData("renderer", true, false, false)]
    [InlineData("renderer, font", true, true, false)]
    [InlineData("renderer, near", true, false, true)]
    [InlineData("near, renderer, font", true, true, true)]
    public void TheFallbackOfARenderer_namesEveryCauseTheDocumentRead(
        string order, bool namesRenderer, bool namesFont, bool namesNear)
    {
        var doc = Page(footer: false);
        doc.Add(new ListRenderer(Clamp()));
        foreach (var cause in order.Split(", "))
        {
            if (cause == "renderer") doc.Add(new Opaque());
            else if (cause == "font") doc.Add(Undrawable());
            else doc.Add(NearTheLimit());
        }

        doc.Add(new ListRenderer(Clamp()));
        Save(doc);

        var first = Skipped(doc)[0];
        Assert.Equal(namesRenderer, first.Contains(RendererCause, StringComparison.Ordinal));
        Assert.Equal(namesFont, first.Contains("font it cannot draw", StringComparison.Ordinal));
        Assert.Equal(namesNear, first.Contains("element after a list", StringComparison.Ordinal));
    }

    /// <summary>
    /// The causes named are the ones the scan of the elements read, so a font in a list that a
    /// renderer of the caller's lays out is not named by the lists placed after it and not by the
    /// ones before it: every list of the document says the same.
    /// </summary>
    [Fact]
    public void AFontInAListARendererLaysOut_isNotNamedByAnyList()
    {
        var doc = Page(footer: false);
        doc.Add(new ListRenderer(Clamp()));
        doc.Add(new LaysOutFromDraw(new ListRenderer(Undrawable())));
        doc.Add(new ListRenderer(Clamp()));
        Save(doc);

        var messages = Skipped(doc);
        Assert.Equal(2, messages.Length);
        Assert.Equal(messages[0], messages[1]);
        Assert.Contains(RendererCause, messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("font it cannot draw", messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageOfAllThreeCauses_readsAsOneSentence()
    {
        var doc = Page(footer: false);
        doc.Add(new ListRenderer(Clamp()));
        doc.Add(Undrawable());
        doc.Add(NearTheLimit());
        doc.Add(new Opaque());
        Save(doc);

        Assert.StartsWith(
            "The document holds a list entry in a font it cannot draw, an element after a list "
            + "that can need about as many page continuations as one element may use and a renderer of "
            + "yours, so this list was laid out as it was before",
            Skipped(doc)[0],
            StringComparison.Ordinal);
    }

    /// <summary>A document with no list has nothing to report, whatever renderer it holds.</summary>
    [Fact]
    public void ADocumentWithoutAList_reportsNothingForARenderer()
    {
        var doc = Page(footer: false);
        doc.Add(new Paragraph("text", H(10)));
        doc.Add(new Opaque());

        Save(doc);

        Assert.Empty(doc.Diagnostics);
    }
}
