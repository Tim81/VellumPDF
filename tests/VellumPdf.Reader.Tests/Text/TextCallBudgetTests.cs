// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Tests.Text;

/// <summary>
/// One bounds test per <see cref="TextCallBudget"/> guard (#98), each constructed with a cap of 3
/// on exactly ONE ceiling while the other three stay generous, per this PR's own convention:
/// mutating one and leaving the rest alone must fail exactly one test, never entangle two.
/// </summary>
public sealed class TextCallBudgetTests
{
    private const int Generous = 1_000_000;

    [Fact]
    public void GlyphsPerPage_capOf3_firesAloneWhileOthersStayGenerous()
    {
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 3, maxCharactersPerPage: Generous, maxRunsPerPage: Generous,
            maxCharactersPerCall: Generous, sink);
        budget.BeginPage();

        Assert.True(budget.TryConsumeGlyph(0));
        Assert.True(budget.TryConsumeGlyph(0));
        Assert.True(budget.TryConsumeGlyph(0));
        // Runs and characters, far below their own generous caps, are unaffected by the glyph
        // cap alone — proof the guards are independent, not that they always fire together.
        Assert.True(budget.TryConsumeRun(0));
        Assert.True(budget.TryConsumeCharacters(10, 0));
        Assert.False(budget.IsPageExhausted);

        Assert.False(budget.TryConsumeGlyph(0));
        Assert.True(budget.IsPageExhausted);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.TextExtractionLimitExceeded, d.Code);
        Assert.Contains("glyphs", d.Message);
    }

    [Fact]
    public void CharactersPerPage_capOf3_firesAloneWhileOthersStayGenerous()
    {
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: Generous, maxCharactersPerPage: 3, maxRunsPerPage: Generous,
            maxCharactersPerCall: Generous, sink);
        budget.BeginPage();

        Assert.True(budget.TryConsumeGlyph(0));
        Assert.True(budget.TryConsumeRun(0));
        Assert.True(budget.TryConsumeCharacters(3, 0));
        Assert.False(budget.IsPageExhausted);

        Assert.False(budget.TryConsumeCharacters(1, 0));
        Assert.True(budget.IsPageExhausted);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.TextExtractionLimitExceeded, d.Code);
        Assert.Contains("characters on page", d.Message);
    }

    [Fact]
    public void RunsPerPage_capOf3_firesAloneWhileOthersStayGenerous()
    {
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: Generous, maxCharactersPerPage: Generous, maxRunsPerPage: 3,
            maxCharactersPerCall: Generous, sink);
        budget.BeginPage();

        for (var i = 0; i < 100; i++)
            Assert.True(budget.TryConsumeGlyph(0));
        Assert.True(budget.TryConsumeCharacters(100, 0));

        Assert.True(budget.TryConsumeRun(0));
        Assert.True(budget.TryConsumeRun(0));
        Assert.True(budget.TryConsumeRun(0));
        Assert.False(budget.IsPageExhausted);

        Assert.False(budget.TryConsumeRun(0));
        Assert.True(budget.IsPageExhausted);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.TextExtractionLimitExceeded, d.Code);
        Assert.Contains("text runs", d.Message);
    }

    /// <summary>
    /// The call-wide character ceiling is the one guard <see cref="TextCallBudget.BeginPage"/>
    /// does NOT reset: a small per-call total, reached partway through page 1, still refuses page
    /// 2's own characters even though page 2's own per-page counters start clean. Both per-page
    /// character caps stay generous throughout, so this is the call-wide ceiling firing alone.
    /// </summary>
    [Fact]
    public void CharactersPerCall_capOf3_survivesBeginPage_firesAloneWhileOthersStayGenerous()
    {
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: Generous, maxCharactersPerPage: Generous, maxRunsPerPage: Generous,
            maxCharactersPerCall: 3, sink);

        budget.BeginPage();
        Assert.True(budget.TryConsumeCharacters(2, pageIndex: 0));
        Assert.False(budget.IsPageExhausted);

        // A new page resets the per-page counters, but not the call-wide total: 2 (page 0) + 2
        // (page 1) exceeds the call-wide cap of 3, even though page 1's own per-page count (2) is
        // nowhere near its own generous cap.
        budget.BeginPage();
        Assert.False(budget.TryConsumeCharacters(2, pageIndex: 1));
        Assert.True(budget.IsPageExhausted);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.TextExtractionLimitExceeded, d.Code);
        Assert.Contains("across the whole call", d.Message);
    }

    [Fact]
    public void BeginPage_resetsPerPageExhaustion_forTheThreePerPageGuards()
    {
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 1, maxCharactersPerPage: Generous, maxRunsPerPage: Generous,
            maxCharactersPerCall: Generous, sink);

        budget.BeginPage();
        Assert.True(budget.TryConsumeGlyph(0));
        Assert.False(budget.TryConsumeGlyph(0));
        Assert.True(budget.IsPageExhausted);

        // Page 2 is not refused outright merely because page 1 alone reached its own glyph
        // ceiling: BeginPage clears the per-page exhaustion flag along with the counters.
        budget.BeginPage();
        Assert.False(budget.IsPageExhausted);
        Assert.True(budget.TryConsumeGlyph(1));
    }
}
