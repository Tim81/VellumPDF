// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A paragraph takes its line height once, where it wraps, and every page of a split reads that
/// value. The output tests pin bytes that a build without the cache wrote; the cost tests count
/// the fragments visited, which does not depend on how loaded the machine is.
/// </summary>
public sealed class ParagraphLineHeightTests
{
    private static readonly TextStyle Plain = new() { Font = Standard14.Helvetica, FontSize = 12 };

    private static readonly TextStyle Tall = new() { Font = Standard14.Helvetica, FontSize = 12, Leading = 30 };

    private static readonly DateTimeOffset Stamp = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Save(Action<Document> fill)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 120),
            Margins = new EdgeInsets(10),
            Timestamp = Stamp,
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        fill(doc);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Words(int count)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
            sb.Append("word").Append(i).Append(' ');
        return sb.ToString();
    }

    // ── Output ───────────────────────────────────────────────────────────────

    // The hashes below were taken from a build whose Layout walked the whole wrap on every call.
    [Fact]
    public void AParagraphSplitOverManyPages_writesTheBytesTheUncachedWalkWrote()
    {
        var pdf = Save(doc => doc.Add(new Paragraph(Words(1500), Plain)));

        Assert.Equal("2D2733554F5BF899E9D66C124669B9A5D4D3D8C142C1871A30A05F0D8C0621C7", Sha256(pdf));
    }

    [Fact]
    public void MixedLeadings_writeTheBytesTheUncachedWalkWrote()
    {
        var pdf = Save(doc => doc.Add(new Paragraph(
        [
            new TextRun(Words(300), Plain),
            new TextRun(Words(300), Tall),
            new TextRun(Words(300), Plain),
        ])));

        Assert.Equal("8B8647B8951130F931B89DBBE6D7B013D484466E120F96137EEE7269F76C884E", Sha256(pdf));
    }

    // ── What the cached value is ─────────────────────────────────────────────

    private static LayoutContext Context(double height) => new(new LayoutBox(0, 0, 180, height));

    // The fragments in a wrap of this text at 180 pt, from one layout on a page that holds it all.
    private static int FragmentsOf(string text)
    {
        var renderer = new ParagraphRenderer(new Paragraph(text, Plain));
        Assert.Equal(LayoutResult.Outcome.Full, renderer.Layout(Context(1_000_000)).Status);
        Assert.True(renderer.LeadingVisits > 0);
        return renderer.LeadingVisits;
    }

    // The tallest run sits on the last line, so a height taken from the lines laid out so far, or
    // from the first line, would be 14.4 rather than 30.
    [Fact]
    public void TheHeightIsTheMaximumOverTheWholeWrap_whenTheTallestRunIsOnTheLastLine()
    {
        var para = new Paragraph([new TextRun(Words(200), Plain), new TextRun("end", Tall)]);
        var renderer = new ParagraphRenderer(para);

        var result = renderer.Layout(Context(100));

        Assert.Equal(LayoutResult.Outcome.Partial, result.Status);
        // 100 / 30 = 3 lines fit; with the first line's 14.4 pt it would be 6.
        Assert.Equal(3 * 30, result.OccupiedArea!.Value.Height, 6);
        var overflow = (ParagraphRenderer)result.OverflowRenderer!;
        var next = overflow.Layout(Context(100));
        Assert.Equal(3 * 30, next.OccupiedArea!.Value.Height, 6);
    }

    [Fact]
    public void TheChainOfPieces_visitsEveryFragmentOnce()
    {
        var renderer = new ParagraphRenderer(new Paragraph(Words(1500), Plain));
        var pieces = new List<ParagraphRenderer> { renderer };
        var result = renderer.Layout(Context(100));
        var pages = 1;
        while (result.Status == LayoutResult.Outcome.Partial)
        {
            var overflow = (ParagraphRenderer)result.OverflowRenderer!;
            pieces.Add(overflow);
            pages++;
            result = overflow.Layout(Context(100));
        }

        Assert.True(pages > 50);
        // The walk is the only thing that counts fragments, and a wrap seen whole on one tall page
        // says how many there are.
        var visits = pieces.Sum(p => p.LeadingVisits);
        Assert.Equal(FragmentsOf(Words(1500)), visits);
        Assert.Equal(visits, renderer.LeadingVisits);
    }

    [Fact]
    public void LayingTheSameInstanceOutAgain_doesNotWalkTheWrapAgain()
    {
        var renderer = new ParagraphRenderer(new Paragraph(Words(400), Plain));

        for (var i = 0; i < 20; i++)
            renderer.Layout(Context(100));

        Assert.Equal(1, renderer.WrapCalls);
        Assert.Equal(FragmentsOf(Words(400)), renderer.LeadingVisits);
    }

    // The wrap is fixed at the width of the first call, so a later call at another width reads the
    // same lines and the same height. The height must follow the wrap, not the area.
    [Fact]
    public void ALaterLayoutAtAnotherWidth_keepsTheHeightOfTheWrapItReads()
    {
        var para = new Paragraph([new TextRun(Words(60), Plain), new TextRun("end", Tall)]);
        var renderer = new ParagraphRenderer(para);

        var first = renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 180, 100)));
        var second = renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 90, 100)));

        Assert.Equal(first.OccupiedArea!.Value.Height, second.OccupiedArea!.Value.Height, 6);
        Assert.Equal(1, renderer.WrapCalls);
        Assert.Equal(FragmentsOf(Words(60)) + 1, renderer.LeadingVisits);
    }

    [Fact]
    public void ANonFiniteStyle_isRefusedOnEveryCall_notOnlyTheFirst()
    {
        var bad = new TextStyle { Font = Standard14.Helvetica, FontSize = double.NaN };
        var renderer = new ParagraphRenderer(new Paragraph("x", bad));

        Assert.Throws<InvalidOperationException>(() => renderer.Layout(Context(100)));
        Assert.Throws<InvalidOperationException>(() => renderer.Layout(Context(100)));
    }
}
