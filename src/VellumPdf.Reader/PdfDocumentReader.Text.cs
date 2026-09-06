// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Content;

namespace VellumPdf.Reader;

public sealed partial class PdfDocumentReader
{
    /// <summary>
    /// Returns every simple-font (Type1, MMType1, TrueType) glyph this document's pages draw
    /// through their own content — annotation appearance streams are excluded, a deliberate
    /// asymmetry with <see cref="ExtractImages()"/>: §12.5.5 makes an appearance a rendering
    /// convenience layered on top of the page, and this reader draws the line at the page's own
    /// content for text the way it does not for images (#98) — positioned by ISO 32000-2 §9.4.4 and
    /// assembled in content order, one page's text followed by the next's, joined by
    /// <see cref="PdfTextExtractionOptions.PageSeparator"/>. Equivalent to
    /// <see cref="ExtractText(PdfTextExtractionOptions)"/> with the default options.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This reader has been disposed.</exception>
    public PdfTextExtractionResult ExtractText() => ExtractText(new PdfTextExtractionOptions());

    /// <summary>
    /// Returns every simple-font glyph this document's pages draw through their own content, per
    /// <paramref name="options"/>. <c>/ToUnicode</c>, predefined CMaps, Type0 and Type3 fonts,
    /// <c>/ActualText</c>, <c>/ReversedChars</c>, and rotation-, word-, and paragraph-aware line
    /// grouping all land in a later change (#98); a code this reader cannot decode contributes no
    /// character but still advances the text matrix, so later glyphs on the same line stay
    /// correctly positioned.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see
    /// langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/>.<see
    /// cref="PdfTextExtractionOptions.PageSeparator"/> is <see langword="null"/>. Checked here
    /// rather than in the property's own <c>init</c>, the same way
    /// <c>ReaderLimits.Resolve</c> validates <see cref="PdfReaderOptions"/> at the point of use
    /// rather than at construction.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/>.<see
    /// cref="PdfTextExtractionOptions.Pages"/> resolves outside <c>0..</c><see
    /// cref="PageCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">This reader has been disposed.</exception>
    public PdfTextExtractionResult ExtractText(PdfTextExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        if (options.PageSeparator is null)
        {
            throw new ArgumentException(
                $"{nameof(PdfTextExtractionOptions.PageSeparator)} must not be null.", nameof(options));
        }

        // Range.GetOffsetAndLength itself throws ArgumentOutOfRangeException for a range outside
        // 0..PageCount, matching what GetPage(int) already documents for a single out-of-range
        // index.
        var (start, length) = options.Pages.GetOffsetAndLength(PageCount);

        var scope = CreateContentDiagnosticScope();
        var budget = new TextCallBudget(
            TextCallBudget.DefaultMaxGlyphsPerPage, TextCallBudget.DefaultMaxCharactersPerPage,
            TextCallBudget.DefaultMaxRunsPerPage, _limits.MaxDecodedBytes, scope);

        var pageTexts = new List<string>(length);
        for (var i = 0; i < length; i++)
        {
            var page = Pages[start + i];
            pageTexts.Add(ExtractTextFromPageCore(page, budget, scope));
        }

        return new PdfTextExtractionResult(string.Join(options.PageSeparator, pageTexts), scope.Diagnostics);
    }

    /// <summary> The shared implementation behind <see
    /// cref="PdfReadPage.ExtractText(PdfTextExtractionOptions)"/>: walks one page's own content
    /// (never its annotation appearances — see <see cref="ExtractText()"/>'s own remarks) through a
    /// fresh, call-scoped <see cref="TextCallBudget"/>.
    /// </summary>
    internal PdfTextExtractionResult ExtractTextFromPage(PdfReadPage page, PdfTextExtractionOptions options)
    {
        ThrowIfDisposed();

        // Pages is meaningless for a single already-identified page; silently ignoring it would be
        // the dishonest option the brief for this method explicitly rules out.
        if (!options.Pages.Equals(Range.All))
        {
            throw new ArgumentException(
                $"{nameof(PdfTextExtractionOptions.Pages)} is meaningless on a page-level "
                + $"{nameof(PdfReadPage.ExtractText)} call; it applies only to "
                + $"{nameof(PdfDocumentReader)}.{nameof(ExtractText)}.",
                nameof(options));
        }

        var scope = CreateContentDiagnosticScope();
        var budget = new TextCallBudget(
            TextCallBudget.DefaultMaxGlyphsPerPage, TextCallBudget.DefaultMaxCharactersPerPage,
            TextCallBudget.DefaultMaxRunsPerPage, _limits.MaxDecodedBytes, scope);

        var text = ExtractTextFromPageCore(page, budget, scope);
        return new PdfTextExtractionResult(text, scope.Diagnostics);
    }

    // Runs one page's own content (never its annotation appearances) through a fresh
    // ContentInterpreter and TextExtractionVisitor sharing budget and scope with the caller, so a
    // document-level ExtractText() call enforces one call-wide character ceiling across every page
    // it visits (TextCallBudget's own remarks explain why that ceiling does not reset per page) and
    // reports into one shared diagnostics scope.
    private string ExtractTextFromPageCore(PdfReadPage page, TextCallBudget budget, DiagnosticSink scope)
    {
        budget.BeginPage();
        var interpreter = new ContentInterpreter(this);
        var visitor = new TextExtractionVisitor(this, interpreter, budget, scope, page.Index);

        interpreter.Run(page, visitor, scope);
        visitor.Finish();

        // Every run boundary IS a baseline change (TextAssembler opens a new run only then), so
        // joining with '\n' here is what turns "new run" into "new line" for the page's own text.
        return string.Join('\n', visitor.Runs.Select(r => r.Text));
    }
}
