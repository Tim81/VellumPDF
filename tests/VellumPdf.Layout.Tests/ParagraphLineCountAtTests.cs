// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <see cref="ParagraphRenderer.LineCountAt"/> says how many lines
/// <see cref="ParagraphRenderer.Layout"/> wraps a paragraph into for a box of a given width, so it
/// must take off the margins on both sides. The list's own content paragraphs have a left margin
/// only, which leaves the right one unread by every list row.
/// </summary>
public sealed class ParagraphLineCountAtTests
{
    private static readonly TextStyle Style = new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private const string Text = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima";

    private static int LinesLaidOut(Paragraph paragraph, double boxWidth)
    {
        var result = new ParagraphRenderer(paragraph).Layout(new LayoutContext(new LayoutBox(0, 0, boxWidth, 10_000)));
        Assert.Equal(LayoutResult.Outcome.Full, result.Status);
        return (int)Math.Round(result.OccupiedArea!.Value.Height / Style.EffectiveLeading);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(40.0, 0.0)]
    [InlineData(0.0, 60.0)]
    [InlineData(25.0, 70.0)]
    public void TheCountIsTheLinesLayoutWraps_atEveryMargin(double left, double right)
    {
        Paragraph Make() => new(Text, Style) { Margins = new EdgeInsets(0, right, 0, left) };

        foreach (var box in new[] { 120.0, 200.0, 300.0 })
        {
            var expected = LinesLaidOut(Make(), box);
            Assert.Equal(expected, new ParagraphRenderer(Make()).LineCountAt(box));
        }
    }

    /// <summary>
    /// The fixture tells the margins apart: the same text wraps into more lines when a right margin
    /// narrows it, so a count that read only the left margin would not equal the one above.
    /// </summary>
    [Fact]
    public void ARightMargin_changesTheCount()
    {
        var without = new ParagraphRenderer(new Paragraph(Text, Style) { Margins = EdgeInsets.Zero }).LineCountAt(200);
        var with = new ParagraphRenderer(new Paragraph(Text, Style) { Margins = new EdgeInsets(0, 70, 0, 0) }).LineCountAt(200);

        Assert.True(with > without, $"{with} lines with a right margin, {without} without");
    }

    [Theory]
    [InlineData(50.0, 0.0, 50.0)]
    [InlineData(0.0, 50.0, 50.0)]
    [InlineData(20.0, 40.0, 60.0)]
    public void ABoxTheMarginsFill_hasNoLines(double left, double right, double box)
    {
        var renderer = new ParagraphRenderer(new Paragraph(Text, Style) { Margins = new EdgeInsets(0, right, 0, left) });

        Assert.Equal(0, renderer.LineCountAt(box));
    }
}
