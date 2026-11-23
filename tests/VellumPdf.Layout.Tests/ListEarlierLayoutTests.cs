// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A list builds its entries at its first layout and keeps them, as it did. When something other
/// than the document's own call in this save laid it out first, the document lays the list out as
/// it was before the fixes of #476 and #685, whatever comes later: the document placing it, a
/// renderer of the caller's holding it, a second document. The reference for every row is the
/// same document built over <see cref="ListRenderer.Unclamped"/>, the list as it was.
/// </summary>
public sealed class ListEarlierLayoutTests
{
    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

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

    private const double PageWidth = 110;
    private const double PageHeight = 100;

    private static Document Page(double width) =>
        new() { PageSize = new PdfRectangle(0, 0, width, PageHeight), Margins = new EdgeInsets(10) };

    private static ListElement Make(string kind) => kind switch
    {
        // Text that wraps at the width it is laid out at.
        "wrap" => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("WWW ab cd ef gh ij", H(10)))
            .Add(new ListItem("kl mn op qr st uv wx", H(10))),

        // An indent no page width leaves any text, so the new layout clamps both entries.
        "clamp" => new ListElement(ListStyle.OrderedDecimal) { Indent = 250 }
            .Add(new ListItem("clamped text", H(10)))
            .Add(new ListItem("second one", H(10))),

        // A 30 pt marker beside 10 pt text: the gutter is the marker's width where the area leaves
        // the widest word room, and the indent where it does not.
        _ => new ListElement(ListStyle.OrderedDecimal) { Indent = 10 }
            .Add(new ListItem([new TextRun("WWWWW ab", H(10))]) { Style = H(30) })
            .Add(new ListItem([new TextRun("WWWWW cd", H(10))]) { Style = H(30) }),
    };

    /// <summary>
    /// A renderer of the caller's. <c>how</c> 0 lays the inner renderer out through the context
    /// it was given, 1 through <see cref="LayoutContext.WithArea"/> at the width factor, 2 through
    /// the public constructor at the factor, and 3 through <see cref="LayoutContext.WithArea"/> at
    /// three quarters of the width first and then through the context.
    /// </summary>
    private sealed class Caller(IRenderer inner, int how, double factor) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var a = context.Area;
            var box = new LayoutBox(a.X, a.Y, a.Width * factor, a.Height);
            LayoutResult result;
            switch (how)
            {
                case 1: result = inner.Layout(context.WithArea(box)); break;
                case 2: result = inner.Layout(new LayoutContext(box)); break;
                case 3:
                    inner.Layout(context.WithArea(new LayoutBox(a.X, a.Y, a.Width * 0.75, a.Height)));
                    result = inner.Layout(context);
                    break;
                default: result = inner.Layout(context); break;
            }

            return result.Status switch
            {
                LayoutResult.Outcome.Full => LayoutResult.Full(
                    new LayoutBox(a.X, a.Y, a.Width, result.OccupiedArea!.Value.Height)),
                LayoutResult.Outcome.Partial => LayoutResult.Partial(
                    new LayoutBox(a.X, a.Y, a.Width, result.OccupiedArea!.Value.Height),
                    new Caller(result.SplitRenderer!, 0, 1),
                    new Caller(result.OverflowRenderer!, how, factor)),
                _ => LayoutResult.Nothing(),
            };
        }

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }

    private static IRenderer Held(string how, IRenderer renderer) => how switch
    {
        "ctx" => new Caller(renderer, 0, 1),
        "wa75" => new Caller(renderer, 1, 0.75),
        "wa150" => new Caller(renderer, 1, 1.5),
        "ctor75" => new Caller(renderer, 2, 0.75),
        _ => new Caller(renderer, 3, 1),
    };

    // The renderer a scenario places: the list's own renderer, or the list as it was before the
    // fixes, which is what 2.3.3 did with every list.
    private static ListRenderer Renderer(string kind, bool twin, out ListElement list)
    {
        list = Make(kind);
        var renderer = new ListRenderer(list);
        return twin ? renderer.Unclamped() : renderer;
    }

    /// <summary>
    /// Runs one scenario and returns the last document's bytes. <paramref name="earlier"/> is the
    /// layout that comes first and is not the document's own call: a renderer of the caller's that
    /// holds the list in the same document, a layout before the save, or another document's save.
    /// <paramref name="current"/> is the placement that follows, in the document that is saved last.
    /// </summary>
    private static byte[] Scenario(string earlier, string current, string kind, bool twin)
    {
        var renderer = Renderer(kind, twin, out _);
        var width = PageWidth;
        var doc = Page(width);

        if (earlier.StartsWith("pre"))
        {
            var factor = earlier == "pre75" ? 0.75 : 1.5;
            renderer.Layout(new LayoutContext(new LayoutBox(10, 10, width * factor - 20, PageHeight - 20)));
        }
        else if (earlier.StartsWith("doc"))
        {
            var factor = earlier.EndsWith("75") ? 0.75 : 1.5;
            var first = Page(width * factor);
            first.Add(earlier.StartsWith("doc-held") ? Held("ctx", renderer) : renderer);
            Save(first);
        }
        else
        {
            doc.Add(Held(earlier, renderer));
            doc.Add(new Paragraph("between", H(10)) { Margins = EdgeInsets.Zero });
        }

        doc.Add(current == "direct" ? renderer : Held(current, renderer));
        return Save(doc);
    }

    /// <summary>
    /// Whichever shape of layout built the entries first, and whichever shape comes after it, the
    /// document lays the list out from those entries as it was before the fixes: the bytes are the
    /// list's own as it was, for a list that wraps, one that clamps and one whose gutter depends on
    /// the width, at an equal, a narrower and a wider first width.
    /// </summary>
    [Theory]
    [InlineData("ctx", "direct")]
    [InlineData("wa75", "direct")]
    [InlineData("wa150", "direct")]
    [InlineData("ctor75", "direct")]
    [InlineData("measure", "direct")]
    [InlineData("pre75", "direct")]
    [InlineData("pre150", "direct")]
    [InlineData("doc-held75", "direct")]
    [InlineData("doc-direct75", "direct")]
    [InlineData("doc-direct150", "direct")]
    [InlineData("ctx", "wa150")]
    [InlineData("wa75", "wa150")]
    [InlineData("measure", "wa150")]
    [InlineData("pre75", "wa150")]
    [InlineData("doc-direct75", "wa150")]
    [InlineData("doc-direct150", "ctx")]
    public void WhateverLaidTheListOutFirst_theDocumentKeepsTheEntriesThatLayoutBuilt(string earlier, string current)
    {
        foreach (var kind in new[] { "wrap", "clamp", "gutter" })
        {
            var reference = Sha(Scenario(earlier, current, kind, twin: true));
            Assert.True(reference == Sha(Scenario(earlier, current, kind, twin: false)), $"{kind}: {earlier} then {current}");
        }
    }

    /// <summary>
    /// A renderer of the caller's lays the list out at three quarters of the width, and the document
    /// then places the same renderer itself: the document draws that placement from the entries the
    /// first layout built, at the width that layout had, so the text wraps where it did there. Built
    /// at the page's own width the gutter would be the marker's, and the text would wrap once.
    /// </summary>
    [Fact]
    public void AListHeldByACallersRendererAndThenAddedToTheDocument_isDrawnFromTheEntriesTheHeldLayoutBuilt()
    {
        var shown = Shows(Scenario("measure", "direct", "gutter", twin: false));
        var asBefore = Shows(Scenario("measure", "direct", "gutter", twin: true));

        Assert.Equal(asBefore, shown);

        // The first placement, the paragraph and the second placement: the second wraps "WWWWW"
        // and "ab" apart, as the entries built at three quarters of the width do.
        var second = shown.Skip(shown.ToList().IndexOf("between")).ToArray();
        Assert.Equal(["between", "1.", "WWWWW", "ab", "2.", "WWWWW", "cd"], second);
    }

    /// <summary>
    /// A list the document placed first and a renderer of the caller's holds afterwards keeps the
    /// entries the document built: the held placement is laid out from them as the list was, and
    /// the document's own placement after it is not turned into a placement of the list as it was.
    /// </summary>
    [Fact]
    public void AListThePlacementBuiltAndACallersRendererThenHolds_isStillPlacedTheNewWayByTheDocument()
    {
        var renderer = new ListRenderer(Make("clamp"));
        var doc = Page(PageWidth);
        doc.Add(renderer);
        doc.Add(Held("ctx", renderer));
        doc.Add(renderer);
        Save(doc);

        Assert.Contains(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
    }

    private static ListElement OneEntry(string text) =>
        new ListElement(ListStyle.OrderedDecimal).Add(new ListItem(text, H(10)));

    /// <summary>
    /// A reused list in a document that lays every list out as it was reports what any list does
    /// there: the fallback, once, whichever of the two rules made the document do so. A reused list
    /// in a document that does not reports nothing.
    /// </summary>
    [Theory]
    [InlineData("font", true)]
    [InlineData("limit", true)]
    [InlineData("none", false)]
    public void AReusedList_reportsTheFallbackOnlyWhereTheDocumentLaysEveryListOutAsItWas(string rule, bool reports)
    {
        var renderer = new ListRenderer(Make("clamp"));
        var first = Page(PageWidth);
        first.Add(renderer);
        Save(first);

        var second = Page(PageWidth);
        second.Add(renderer);
        if (rule == "font")
        {
            // Its text has no width at this indent, so it is dropped and the save does not draw it.
            second.Add(new ListElement(ListStyle.Unordered) { Indent = 1000 }
                .Add(new ListItem([new TextRun("bad", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 })])
                { Style = H(10) }));
        }
        else if (rule == "limit")
        {
            second.Add(new Preformatted(new string('\t', 1_700), H(1)) { TabWidth = 32 });
        }

        Save(second);

        var skipped = second.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();
        Assert.Equal(reports, skipped.Length > 0);
        Assert.DoesNotContain(second.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
        if (reports) Assert.Contains(skipped, d => d.ElementIndex == 0);
    }

    /// <summary>
    /// The answer for a Standard 14 value is its own, whichever value was asked about before it:
    /// the memo packs the value and its answer in one word, and no two values can share it.
    /// </summary>
    [Fact]
    public void TheMemoOfTheLastStandard14Value_neverAnswersOneValueForAnother()
    {
        var run = ListLayoutRun.Begin(new PdfDocument(), out var previous);
        try
        {
            var values = Enumerable.Range(-40, 100).Append(int.MinValue).Append(int.MaxValue).ToArray();
            foreach (var first in values)
            {
                ListLayoutRun.CanDraw(run, new TextStyle { FontRef = (Standard14)first });
                foreach (var second in values)
                {
                    var style = new TextStyle { FontRef = (Standard14)second };
                    Assert.True(
                        Enum.IsDefined((Standard14)second) == ListLayoutRun.CanDraw(run, style),
                        $"{second} asked after {first}");
                }
            }
        }
        finally
        {
            ListLayoutRun.End(previous);
        }
    }
}
