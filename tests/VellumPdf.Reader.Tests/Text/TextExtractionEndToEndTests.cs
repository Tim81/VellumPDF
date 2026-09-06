// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Content;

namespace VellumPdf.Reader.Tests.Text;

/// <summary>
/// Known-answer, contract, and honesty tests for <see cref="PdfDocumentReader.ExtractText()"/> and
/// <see cref="PdfReadPage.ExtractText()"/> (#98), against hand-built PDFs in the
/// <see cref="TextTestSupport"/> style.
/// </summary>
public sealed class TextExtractionEndToEndTests
{
    // ── End-to-end known-answer tests ───────────────────────────────────────────────────────────

    [Fact]
    public void TwoLines_joinedByNewline()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (Line one) Tj 0 -14 Td (Line two) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("Line one\nLine two", result.Text);
    }

    /// <summary>
    /// The discriminating fixture for the line-break rule: a mid-line <c>Ts</c> (rise) must NOT
    /// start a new run, since §9.3.7 scopes rise to superscripts and subscripts, which read as the
    /// same line as their surrounding text.
    /// </summary>
    [Fact]
    public void MidLineRise_producesNoNewline()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (AB) Tj 3 Ts (C) Tj 0 Ts (D) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("ABCD", result.Text);
    }

    [Fact]
    public void TwoPages_joinedByPageSeparator()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            ["BT /F1 12 Tf 100 700 Td (First) Tj ET", "BT /F1 12 Tf 100 700 Td (Second) Tj ET"],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).ExtractText();

        Assert.Equal("First\fSecond", result.Text);
    }

    [Fact]
    public void CustomPageSeparator_isHonoured()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            ["BT /F1 12 Tf 100 700 Td (First) Tj ET", "BT /F1 12 Tf 100 700 Td (Second) Tj ET"],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).ExtractText(new PdfTextExtractionOptions { PageSeparator = "|" });

        Assert.Equal("First|Second", result.Text);
    }

    [Fact]
    public void PageRange_extractsOnlyTheRequestedPages()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            [
                "BT /F1 12 Tf 100 700 Td (One) Tj ET",
                "BT /F1 12 Tf 100 700 Td (Two) Tj ET",
                "BT /F1 12 Tf 100 700 Td (Three) Tj ET",
            ],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).ExtractText(new PdfTextExtractionOptions { Pages = 1..3 });

        Assert.Equal("Two\fThree", result.Text);
    }

    /// <summary>
    /// A Form XObject's own text is included, positioned through its own <c>/Matrix</c> (§8.10.2)
    /// rather than the invoker's own text-matrix trajectory, and the invoker's own text matrix is
    /// unaffected once the form's content finishes: the page's own content keeps ONE text object
    /// open across the intervening <c>Do</c> (itself informational-only per §8.2 Figure 9, but still
    /// processed — see <c>ContentInterpreter.HandleDo</c>'s own remarks), so "Y" shown after the
    /// form returns lands on the exact same baseline as "X" shown before it, producing three
    /// distinct lines in content order rather than "X" and "Y" merging with "Inside" or with each
    /// other by coincidence.
    /// </summary>
    [Fact]
    public void FormXObjectText_included_matrixReflected_invokerTextMatrixUnchangedAfter()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (X) Tj\n/Fm0 Do\n(Y) Tj ET",
            "<< /Font << /F1 5 0 R >> /XObject << /Fm0 10 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()),
            new TextTestSupport.Obj(10,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Matrix [1 0 0 1 50 50] "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
                System.Text.Encoding.ASCII.GetBytes("BT /F1 24 Tf 0 0 Td (Inside) Tj ET")));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("X\nInside\nY", result.Text);
    }

    /// <summary>
    /// Annotation appearance streams are excluded from text extraction (a deliberate asymmetry with
    /// <see cref="PdfDocumentReader.ExtractImages()"/>, which includes them by default): only the
    /// page's own content contributes text.
    /// </summary>
    [Fact]
    public void AnnotationAppearanceText_isExcluded()
    {
        // BuildPageDoc's own page object has no /Annots slot, so this fixture is built directly
        // with TextTestSupport.BuildPdf instead, the same hand-built-byte-string idiom every other
        // fixture here uses.
        var pdfWithAnnots = TextTestSupport.BuildPdf(
            1,
            new TextTestSupport.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new TextTestSupport.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new TextTestSupport.Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [20 0 R] >>"),
            new TextTestSupport.Obj(4, "<< >>", System.Text.Encoding.ASCII.GetBytes(
                "BT /F1 12 Tf 100 700 Td (Visible) Tj ET")),
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()),
            new TextTestSupport.Obj(20, "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AP << /N 21 0 R >> >>"),
            new TextTestSupport.Obj(21,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
                System.Text.Encoding.ASCII.GetBytes("BT /F1 12 Tf 0 0 Td (Hidden) Tj ET")));

        var result = PdfReader.Open(pdfWithAnnots).GetPage(0).ExtractText();

        Assert.Equal("Visible", result.Text);
        Assert.DoesNotContain("Hidden", result.Text);
    }

    /// <summary> Extraction reports what the file contains, not what a renderer would show:
    /// invisible-render-mode text (<c>3 Tr</c>) is included, which is why a scanned page carrying an
    /// invisible OCR text layer over its image still works with this reader.</summary>
    [Fact]
    public void InvisibleRenderModeText_isIncluded()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 3 Tr 100 700 Td (Invisible) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("Invisible", result.Text);
    }

    /// <summary>
    /// A font set inside <c>q</c>/<c>Q</c> resolves to the OUTER font again once <c>Q</c> restores
    /// it: <c>/F1</c> maps code 0x41 to "A" (plain WinAnsiEncoding), <c>/F2</c> remaps the SAME code
    /// to "B" via <c>/Differences</c>, so the three shows in this fixture are only distinguishable
    /// by which font actually resolved for each: "A" (outer), "B" (inner, before Q), "A" again
    /// (outer, after Q) — a text-only assertion this time genuinely proves font resolution, since a
    /// resolution bug (stale memoisation keyed by operand alone, say) would leak "B" into the third
    /// show instead.
    /// </summary>
    [Fact]
    public void FontSetInsideQQ_resolvesToOuterFont_afterQ()
    {
        const string f2Dict =
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            + "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /B] >> "
            + "/FirstChar 65 /LastChar 66 /Widths [600 600] >>";

        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf <41> Tj ET\nq\nBT /F2 12 Tf <41> Tj ET\nQ\nBT <41> Tj ET",
            "<< /Font << /F1 5 0 R /F2 6 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict(65, 66, 600)),
            new TextTestSupport.Obj(6, f2Dict));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("ABA", result.Text);
    }

    /// <summary>An ExtGState's own <c>/Font</c> (Table 57) sets font and size without any <c>Tf</c>
    /// at all.</summary>
    [Fact]
    public void ExtGStateFont_setsFontAndSize()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "/GS0 gs\nBT (A) Tj ET",
            "<< /Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [5 0 R 12] >> >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("A", result.Text);
    }

    /// <summary>
    /// An extreme (but individually finite) <c>Tm</c>, compounded through an equally extreme CTM,
    /// overflows to a non-finite <c>Trm</c>: the line-grouping key treats that as "same line as
    /// before" (see <see cref="TextAssembler"/>'s own remarks), so extraction still terminates with
    /// one ordinary run rather than one run per glyph.
    /// </summary>
    [Fact]
    public void NonFiniteTm_terminatesWithoutANewlineStorm()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "1e200 0 0 1e200 0 0 cm\nBT /F1 12 Tf 1e200 0 0 1e200 0 0 Tm (AB) Tj (CD) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("ABCD", result.Text);
    }

    /// <summary>A numeric <c>Tj</c> operand (never permitted by §9.4.3, which types it a string) is
    /// skipped with exactly one <see cref="PdfReaderDiagnosticCode.OperandStackMalformed"/>
    /// (302).</summary>
    [Fact]
    public void NumericTjOperand_skippedWithOne302()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 5 Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        var d = Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    // ── Spec-stated identities (§9.4.3, Table 107) ──────────────────────────────────────────────

    [Theory]
    [InlineData("(ABC) Tj")]
    [InlineData("(A) Tj (B) Tj (C) Tj")]
    [InlineData("[(A)(B)(C)] TJ")]
    public void MultipleGlyphsInOneShow_equalsSeparateTjs_equalsTJArray(string showOps)
    {
        var pdf = TextTestSupport.BuildPageDoc(
            $"BT /F1 12 Tf 100 700 Td {showOps} ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("ABC", result.Text);
    }

    [Fact]
    public void QuoteOperator_equalsTStarThenTj()
    {
        var viaQuote = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td 14 TL (Line1) Tj (Line2) ' ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));
        var viaTStarTj = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td 14 TL (Line1) Tj T* (Line2) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var quoteResult = PdfReader.Open(viaQuote).GetPage(0).ExtractText();
        var tStarTjResult = PdfReader.Open(viaTStarTj).GetPage(0).ExtractText();

        Assert.Equal("Line1\nLine2", quoteResult.Text);
        Assert.Equal(quoteResult.Text, tStarTjResult.Text);
    }

    [Fact]
    public void DoubleQuoteOperator_equalsTwAndTcThenQuote()
    {
        var viaDoubleQuote = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td 14 TL 2 3 (Hi) \" ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));
        var viaTwTcQuote = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td 14 TL 2 Tw 3 Tc (Hi) ' ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var doubleQuoteResult = PdfReader.Open(viaDoubleQuote).GetPage(0).ExtractText();
        var twTcQuoteResult = PdfReader.Open(viaTwTcQuote).GetPage(0).ExtractText();

        Assert.Equal("Hi", doubleQuoteResult.Text);
        Assert.Equal(doubleQuoteResult.Text, twTcQuoteResult.Text);
    }

    // ── Honesty tests ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Type0Font_producesNoText_reports405()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (AB) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, "<< /Type /Font /Subtype /Type0 /BaseFont /Foo >>"));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontTypeUnsupported);
    }

    [Fact]
    public void Type3Font_producesNoText_reports405()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (AB) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, "<< /Type /Font /Subtype /Type3 /BaseFont /Foo >>"));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontTypeUnsupported);
    }

    /// <summary>A font whose <c>/Subtype</c> this reader knows nothing about (not Type0, not
    /// Type3) reports <see cref="PdfReaderDiagnosticCode.FontUnreadable"/> (400), NOT <see
    /// cref="PdfReaderDiagnosticCode.FontTypeUnsupported"/> (405): 400 means the font dictionary
    /// itself does not name a type this reader recognises at all, distinct from 405's "recognised,
    /// but not simple" (double-report avoided).</summary>
    [Fact]
    public void BogusSubtypeFont_reports400_not405()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (AB) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, "<< /Type /Font /Subtype /Bogus /BaseFont /Foo >>"));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontUnreadable);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontTypeUnsupported);
    }

    [Fact]
    public void ShowOperatorWithNoTf_producesNoText_reports601()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT (AB) Tj ET", "<< >>");

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        var d = Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextShownWithoutFont);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void TfNamingAbsentResource_reportsOnlyResourceMissing_noDoubleReport()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT /F1 12 Tf (AB) Tj ET", "<< >>");

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextShownWithoutFont);
    }

    /// <summary>
    /// A code with no Unicode route contributes no character but its own advance is still applied:
    /// code 65 is deliberately mapped to a glyph name absent from the Adobe Glyph List (so it never
    /// resolves to Unicode), width 1000 (so its advance at 12pt is exactly 12 units); code 66 maps
    /// normally to "B". A text-only assertion on the RESULT cannot see this at all — "B" alone says
    /// nothing about whether the reader skipped the unmapped glyph's position along with its
    /// character — so this reaches <see cref="TextExtractionVisitor"/> directly to read the "B"
    /// run's own <see cref="TextRun.StartX"/>: 100 (the <c>Td</c> origin) plus 12 (the unmapped
    /// glyph's own advance) is 112, not 100.
    /// </summary>
    [Fact]
    public void UnmappedGlyph_noCharacter_butStillAdvances()
    {
        const string fontDict =
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            + "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /zzznotarealglyphname99] >> "
            + "/FirstChar 65 /LastChar 66 /Widths [1000 600] >>";

        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td <4142> Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, fontDict));

        var reader = PdfReader.Open(pdf);
        var page = reader.GetPage(0);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            TextCallBudget.DefaultMaxGlyphsPerPage, TextCallBudget.DefaultMaxCharactersPerPage,
            TextCallBudget.DefaultMaxRunsPerPage, long.MaxValue, sink);
        var interpreter = new ContentInterpreter(reader);
        var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);

        interpreter.Run(page, visitor, sink);
        visitor.Finish();

        var run = Assert.Single(visitor.Runs);
        Assert.Equal("B", run.Text);
        Assert.Equal(112.0, run.StartX, 6);
        Assert.Contains(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnmappedGlyphs);
    }

    // ── Contracts ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractText_nullOptions_throwsArgumentNullException()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT ET", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Throws<ArgumentNullException>(() => reader.ExtractText(null!));
        Assert.Throws<ArgumentNullException>(() => reader.GetPage(0).ExtractText(null!));
    }

    [Fact]
    public void ExtractText_pagesOutOfRange_throwsArgumentOutOfRangeException()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT ET", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => reader.ExtractText(new PdfTextExtractionOptions { Pages = 5..6 }));
    }

    [Fact]
    public void PageLevelExtractText_nonAllPages_throwsArgumentException()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT ET", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Throws<ArgumentException>(
            () => reader.GetPage(0).ExtractText(new PdfTextExtractionOptions { Pages = 0..1 }));
    }

    [Fact]
    public void ExtractText_nullPageSeparator_throwsArgumentException()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT ET", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Throws<ArgumentException>(
            () => reader.ExtractText(new PdfTextExtractionOptions { PageSeparator = null! }));
    }

    [Fact]
    public void ExtractText_emptyPage_producesEmptyString_neverNull()
    {
        var pdf = TextTestSupport.BuildPageDoc("", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Equal(string.Empty, reader.ExtractText().Text);
        Assert.Equal(string.Empty, reader.GetPage(0).ExtractText().Text);
        Assert.NotNull(reader.ExtractText().Text);
    }
}
