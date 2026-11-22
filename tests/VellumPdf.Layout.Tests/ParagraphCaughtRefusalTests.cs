// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A renderer of the caller's can wrap a <see cref="ParagraphRenderer"/>, catch the refusal its
/// layout throws for a run style, report the paragraph as placed, and still draw it. The wrap the
/// refused layout made is then what the draw reads, so the bytes depend on what a refused layout
/// leaves behind. The hashes were taken from a build that walked the wrap on every layout.
/// </summary>
public sealed class ParagraphCaughtRefusalTests
{
    private sealed class Catching(ParagraphRenderer inner) : IRenderer
    {
        public bool Caught { get; private set; }

        public LayoutResult Layout(LayoutContext context)
        {
            try
            {
                return inner.Layout(context);
            }
            catch (InvalidOperationException)
            {
                Caught = true;
                return LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, context.Area.Width, 10));
            }
        }

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    private static (string Sha, string Diagnostics) Run(bool tagged, bool nullStyleRun)
    {
        using var doc = new Document
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"),
        };
        if (tagged)
        {
            doc.Tagged = true;
            doc.Language = "en";
        }

        var runs = new List<TextRun>
        {
            new("ok words here", new TextStyle()),
            new("bad", new TextStyle { FontSize = double.NaN }),
        };
        if (nullStyleRun)
            runs.Insert(0, new TextRun("", null!));
        var catching = new Catching(new ParagraphRenderer(new Paragraph(runs)));
        doc.Add(new Paragraph("before"));
        doc.Add(catching);
        doc.Add(new Paragraph("after"));

        using var ms = new MemoryStream();
        doc.Save(ms);

        Assert.True(catching.Caught);
        return (Convert.ToHexString(SHA256.HashData(ms.ToArray())), string.Join(",", doc.Diagnostics.Select(d => d.Code)));
    }

    [Fact]
    public void ACaughtRefusal_leavesTheWrapForDraw_untagged()
    {
        var (sha, diagnostics) = Run(tagged: false, nullStyleRun: false);

        Assert.Equal("9C3386E0327025836FBA27192111E6AE729AA6D4F10449EFE114CB79C4961F37", sha);
        Assert.Equal("", diagnostics);
    }

    [Fact]
    public void ACaughtRefusal_leavesTheWrapForDraw_tagged()
    {
        var (sha, diagnostics) = Run(tagged: true, nullStyleRun: false);

        Assert.Equal("972CE8C71EC454E2E7B6D3BF629546490307AD5A8FDB5F969CBA1C8792458E19", sha);
        Assert.Equal("", diagnostics);
    }

    [Fact]
    public void ACaughtRefusal_leavesTheWrapForDraw_andDrawReportsANullRunStyle()
    {
        var (sha, diagnostics) = Run(tagged: false, nullStyleRun: true);

        Assert.Equal("9C3386E0327025836FBA27192111E6AE729AA6D4F10449EFE114CB79C4961F37", sha);
        Assert.Equal("NullTextRunStyle", diagnostics);
    }
}
