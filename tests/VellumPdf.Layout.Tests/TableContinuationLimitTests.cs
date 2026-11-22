// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering.Table;
using static VellumPdf.Layout.Tests.ContinuationSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A table that keeps its cells' breaks is laid out the way the table was laid out before the breaks
/// were read where the document, run at a limit of 300 page continuations, would otherwise refuse
/// it (#679 through the shared mechanism of #686). A table that main refused stays refused, with
/// the same exception, and a table that a caller's renderer holds, or that is laid out under any
/// context the document did not address to it, is laid out as main does.
/// </summary>
public sealed class TableContinuationLimitTests
{
    private static TextStyle Helvetica() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    /// <summary>
    /// A first row of <c>mmmmmmmm</c> words joined by line feeds and a short cell, then rows of a
    /// letter and a two-word paragraph, on a page that holds two rows of the second kind when the
    /// first row's breaks are read and one when they are not: read unbroken the first row is one
    /// long word, so the first column is wide and the paragraphs wrap into two lines.
    /// </summary>
    private static TableElement TwoLineRowsWhenTheFirstRowIsUnbroken(int rows)
    {
        var t = new TableElement { DefaultCellStyle = Helvetica() };
        t.AddRow().AddCell(string.Join("\n", Enumerable.Repeat("mmmmmmmm", 6))).AddCell("s");
        for (var i = 0; i < rows; i++) t.AddRow().AddCell("a").AddCell("lorem lorem");
        return t;
    }

    private static Setup Sixty { get; } = new() { PageWidth = 400, PageHeight = 60 };

    /// <summary>One cell of <paramref name="lines"/> lines on a page one line of it tall.</summary>
    private static TableElement OneCellOfLines(int lines)
    {
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(1900);
        t.AddRow().AddCell(string.Join("\n", Enumerable.Repeat("ab", lines)));
        return t;
    }

    private static Setup Wide { get; } = new() { PageWidth = 2000, PageHeight = 40 };

    private static Outcome Run(Setup setup, IRenderer element, bool measuredAsOneLine = false)
    {
        TableMeasureProbe.Current = measuredAsOneLine ? new TableMeasureProbe { ReadEveryCellUnbroken = true } : null;
        try
        {
            return ContinuationSupport.Run(setup, new Log(), element);
        }
        finally
        {
            TableMeasureProbe.Current = null;
        }
    }

    private static int Dropped(Outcome outcome) =>
        outcome.Renderer.Diagnostics.Count(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

    /// <summary>
    /// A table whose first row read unbroken takes more continuations than the limit is refused, as
    /// it was when the table was laid out unbroken, though it takes half as many with its breaks
    /// read. The same table with one row fewer is saved, with its breaks.
    /// </summary>
    [Fact]
    public void ATableWhoseUnbrokenLayoutReachesTheLimit_isRefusedWithTheMessageThatLayoutGives()
    {
        var oneLine = Run(Sixty, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit + 1)), measuredAsOneLine: true);
        var decided = Run(Sixty, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit + 1)));

        Assert.True(IsLimitRefusal(oneLine.Error), "the measurement must be refused for the test to mean anything");
        Assert.True(IsLimitRefusal(decided.Error), decided.Error?.Message ?? "saved");
        Assert.Equal(oneLine.Error!.GetType(), decided.Error!.GetType());
        Assert.Equal(oneLine.Error.Message, decided.Error.Message);
        Assert.True(decided.Probes > 0, "the document must have asked");
    }

    /// <summary>
    /// One row fewer: both layouts are within the limit, so the table keeps its breaks, in about
    /// half the pages the unbroken layout takes.
    /// </summary>
    [Fact]
    public void ATableWhoseUnbrokenLayoutIsWithinTheLimit_keepsItsBreaks()
    {
        var oneLine = Run(Sixty, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit)), measuredAsOneLine: true);
        var decided = Run(Sixty, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit)));

        Assert.Null(oneLine.Error);
        Assert.Null(decided.Error);
        Assert.True(decided.Pages < oneLine.Pages, $"{decided.Pages} pages against {oneLine.Pages}");
        Assert.Equal(0, Dropped(decided));
    }

    /// <summary>
    /// A cell whose breaks, read, take more continuations than the limit is laid out unbroken, in
    /// the bytes of the table as it was laid out unbroken, and the fallback is reported.
    /// </summary>
    [Fact]
    public void ACellThatWouldTakeMoreThanTheLimitIsLaidOutUnbroken_andReported()
    {
        var oneLine = Run(Wide, new TableRenderer(OneCellOfLines(Limit + 2)), measuredAsOneLine: true);
        var decided = Run(Wide, new TableRenderer(OneCellOfLines(Limit + 2)));

        Assert.Null(decided.Error);
        Assert.Equal(oneLine.Bytes, decided.Bytes);
        Assert.Equal(1, Dropped(decided));
        Assert.Contains(decided.Renderer.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped
            && d.Message.Contains("more page continuations than one element may take", StringComparison.Ordinal));
    }

    /// <summary>
    /// A caller's renderer that forwards to a public <c>TableRenderer</c> never asks the document
    /// to decide, so the table is laid out as it was before its breaks were read, however it
    /// forwards: a cell that the document would give the limit's fallback saves in the same bytes,
    /// and nothing is reported, because no decision chose it.
    /// </summary>
    [Theory]
    [InlineData((int)WrapKind.PassThrough)]
    [InlineData((int)WrapKind.WithArea)]
    [InlineData((int)WrapKind.CachedByArea)]
    public void ATableAWrapperHolds_isLaidOutAsBeforeItsBreaksWereRead(int wrapKind)
    {
        var oneLine = Run(Wide, new TableRenderer(OneCellOfLines(Limit + 2)), measuredAsOneLine: true);
        var wrapped = Run(Wide, new Wrapper(new TableRenderer(OneCellOfLines(Limit + 2)), (WrapKind)wrapKind));

        Assert.Null(wrapped.Error);
        Assert.Equal(oneLine.Bytes, wrapped.Bytes);
        Assert.Equal(0, Dropped(wrapped));
        Assert.Equal(0, wrapped.Probes);
    }

    /// <summary>
    /// A wrapper that lays the table out under a context it builds knows no fresh page, so the
    /// table cuts no row, as the table did before its breaks were read: the cell that does not fit
    /// a page is refused as too tall, not as a limit.
    /// </summary>
    [Fact]
    public void ATableAWrapperLaysOutUnderAContextItBuilds_cutsNoRow()
    {
        var wrapped = Run(Wide, new Wrapper(new TableRenderer(OneCellOfLines(Limit + 2)), WrapKind.PublicConstructor));

        Assert.IsType<InvalidOperationException>(wrapped.Error);
        Assert.Contains("too tall", wrapped.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, wrapped.Probes);
    }

    /// <summary>
    /// The same wrapper around a table that takes the page count as it was: a table of rows is
    /// laid out in the pages main laid it out in, not in the pages its breaks would take, since
    /// the breaks are not read where the document did not call the table.
    /// </summary>
    [Theory]
    [InlineData((int)WrapKind.PassThrough)]
    [InlineData((int)WrapKind.WithArea)]
    [InlineData((int)WrapKind.PublicConstructor)]
    public void ATableAWrapperHolds_takesTheUnbrokenLayoutsPages(int wrapKind)
    {
        var oneLine = Run(Sixty, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit)), measuredAsOneLine: true);
        var wrapped = Run(Sixty, new Wrapper(new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit)), (WrapKind)wrapKind));

        Assert.Null(wrapped.Error);
        Assert.Equal(oneLine.Bytes, wrapped.Bytes);
    }

    /// <summary>
    /// A wrapper around a table whose unbroken layout reaches the limit is refused as main refuses
    /// it, whether or not the document would have given the table's breaks up.
    /// </summary>
    [Fact]
    public void ATableAWrapperHolds_whoseUnbrokenLayoutReachesTheLimit_isRefusedAsBefore()
    {
        var oneLine = Run(Sixty, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit + 1)), measuredAsOneLine: true);
        var wrapped = Run(Sixty, new Wrapper(new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit + 1)), WrapKind.PassThrough));

        Assert.True(IsLimitRefusal(wrapped.Error), wrapped.Error?.Message ?? "saved");
        Assert.Equal(oneLine.Error!.Message, wrapped.Error!.Message);
    }

    /// <summary>
    /// A table the document lays out, with the same cell, keeps reading its breaks: the wrapper
    /// is the only difference, so the pages differ where the layouts do.
    /// </summary>
    [Fact]
    public void TheSameTableWithoutAWrapperKeepsItsBreaks()
    {
        var direct = Run(Sixty, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit)));
        var wrapped = Run(Sixty, new Wrapper(new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit)), WrapKind.PassThrough));

        Assert.NotEqual(direct.Pages, wrapped.Pages);
    }

    /// <summary>
    /// The document asks about the table in every pass: with a footer it lays the table out twice,
    /// and the second, drawing pass keeps the breaks the first measured, so a table within the
    /// limit takes the pages its breaks give and not the pages of the unbroken layout.
    /// </summary>
    [Fact]
    public void ATableThatKeepsItsBreaks_keepsThemInEveryPass()
    {
        var setup = Sixty with { Footer = true, PageHeight = 80 };
        var oneLine = Run(setup, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit / 2)), measuredAsOneLine: true);
        var decided = Run(setup, new TableRenderer(TwoLineRowsWhenTheFirstRowIsUnbroken(Limit / 2)));

        Assert.Null(decided.Error);
        Assert.True(decided.Pages < oneLine.Pages, $"{decided.Pages} pages against {oneLine.Pages}");
        Assert.Equal(0, Dropped(decided));
    }

    /// <summary>
    /// The twin is one object for the life of the renderer, so a caller's renderer that lays the
    /// table out under a context it built reaches the same one each time, and so is the twin of the
    /// twin.
    /// </summary>
    [Fact]
    public void TheTwinIsTheSameObjectOnEveryCall()
    {
        var renderer = new TableRenderer(OneCellOfLines(2));
        var twin = ((Rendering.IContinuationTwin)renderer).Twin();

        Assert.NotSame(renderer, twin);
        Assert.Same(twin, ((Rendering.IContinuationTwin)renderer).Twin());
        Assert.Same(twin, ((Rendering.IContinuationTwin)twin).Twin());
    }
}
