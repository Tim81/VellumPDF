// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// The per-page and call-wide ceilings one <c>ExtractText</c> call enforces (#98), mirroring <see
/// cref="ImageCallBudget"/>'s shape but, unlike that type, taking every ceiling as a constructor
/// parameter rather than a hardcoded constant: each of the four conditions below needs its own
/// bounds test constructing this type with a cap of 3 on ONE ceiling while the others stay at their
/// generous default, so mutating one and leaving the rest alone must fail exactly one test, not
/// entangle two.
/// </summary>
/// <remarks>
/// Glyphs, characters, and runs are three separate counters, not one, because none of them bounds
/// the other two: a ligature glyph (one AGL name mapping to several Unicode code points, e.g.
/// "ffi") can push the character count past the glyph count with no extra glyph decoded at all, and
/// the worst case for the run count is one run per glyph (a document alternating baselines every
/// glyph), which the glyph cap alone would not catch until it was already far larger than a
/// reasonable run cap. The call-wide character total is separate again, and is the only one of the
/// four that survives <see cref="BeginPage"/>: it exists because none of the three per-page ceilings
/// bounds the SUM across every page one <c>PdfDocumentReader.ExtractText()</c> call visits, so
/// without it that call's own output is unbounded in the number of pages alone.
/// </remarks>
internal sealed class TextCallBudget
{
    /// <summary>The processor's own choice of default per-page glyph ceiling: generous enough that
    /// no ordinary page's own glyph count comes close, tight enough that a hostile page's
    /// astronomical show-operator count still terminates in bounded time.</summary>
    internal const int DefaultMaxGlyphsPerPage = 2_000_000;

    /// <summary>The processor's own choice of default per-page character ceiling. Larger than <see
    /// cref="DefaultMaxGlyphsPerPage"/>: a ligature glyph can map to more than one character, so a
    /// character ceiling equal to the glyph ceiling could be reached by ligatures alone on a page
    /// nowhere near its own glyph ceiling.</summary>
    internal const int DefaultMaxCharactersPerPage = 4_000_000;

    /// <summary>The processor's own choice of default per-page run ceiling: one run per glyph is
    /// this budget's own worst case (see this type's own remarks), so this matches <see
    /// cref="DefaultMaxGlyphsPerPage"/> rather than sitting below it.</summary>
    internal const int DefaultMaxRunsPerPage = 2_000_000;

    private readonly int _maxGlyphsPerPage;
    private readonly int _maxCharactersPerPage;
    private readonly int _maxRunsPerPage;
    private readonly long _maxCharactersPerCall;
    private readonly DiagnosticSink _diagnostics;

    private int _glyphsThisPage;
    private int _charactersThisPage;
    private int _runsThisPage;
    private long _charactersThisCall;

    /// <summary>Creates a budget with every ceiling given explicitly, so a test can drive one of
    /// them down to a small value (3, by this PR's own convention) while leaving the rest at a
    /// value no ordinary test fixture reaches.</summary>
    internal TextCallBudget(
        int maxGlyphsPerPage, int maxCharactersPerPage, int maxRunsPerPage, long maxCharactersPerCall,
        DiagnosticSink diagnostics)
    {
        _maxGlyphsPerPage = maxGlyphsPerPage;
        _maxCharactersPerPage = maxCharactersPerPage;
        _maxRunsPerPage = maxRunsPerPage;
        _maxCharactersPerCall = maxCharactersPerCall;
        _diagnostics = diagnostics;
    }

    /// <summary>Whether any ceiling has already been reported exhausted, this call or an earlier
    /// page of it. Once true, the rest of the CURRENT page is skipped (the caller stops decoding
    /// further glyphs on it); <see cref="BeginPage"/> below explains why a later page still starts
    /// clean for the three per-page ceilings even so.</summary>
    internal bool IsPageExhausted { get; private set; }

    /// <summary>
    /// Resets the three PER-PAGE counters (glyphs, characters, runs) for a new page, and the
    /// per-page exhaustion flag with them, so page 2 of a document is not refused outright merely
    /// because page 1 alone reached one of those three ceilings. The call-wide character total
    /// (<see cref="TryConsumeCharacters"/>'s own second check) is deliberately NOT reset here: it
    /// is the one ceiling this type enforces across the whole call, not per page, so resetting it
    /// per page would defeat the reason it exists (see this type's own remarks).
    /// </summary>
    internal void BeginPage()
    {
        _glyphsThisPage = 0;
        _charactersThisPage = 0;
        _runsThisPage = 0;
        IsPageExhausted = false;
    }

    /// <summary>Charges one glyph against the per-page glyph ceiling, for every glyph
    /// <c>PdfFontReader.TryDecodeNext</c> decodes, whether or not it maps to a character: an
    /// unmapped code still costs this reader the same decode work.</summary>
    internal bool TryConsumeGlyph(int pageIndex)
    {
        if (IsPageExhausted)
            return false;

        if (_glyphsThisPage >= _maxGlyphsPerPage)
        {
            ReportExhausted($"more than {_maxGlyphsPerPage:N0} glyphs on page {pageIndex}", pageIndex);
            return false;
        }

        _glyphsThisPage++;
        return true;
    }

    /// <summary>Charges <paramref name="count"/> characters against both the per-page character
    /// ceiling and the call-wide one, only when a glyph actually produced characters (an unmapped
    /// glyph never calls this: it has none to charge).</summary>
    internal bool TryConsumeCharacters(int count, int pageIndex)
    {
        if (IsPageExhausted)
            return false;

        if (_charactersThisPage + count > _maxCharactersPerPage)
        {
            ReportExhausted(
                $"more than {_maxCharactersPerPage:N0} characters on page {pageIndex}", pageIndex);
            return false;
        }

        if (_charactersThisCall + count > _maxCharactersPerCall)
        {
            ReportExhausted(
                $"more than {_maxCharactersPerCall:N0} characters across the whole call "
                + $"(first observed on page {pageIndex})", pageIndex);
            return false;
        }

        _charactersThisPage += count;
        _charactersThisCall += count;
        return true;
    }

    /// <summary>Charges one run against the per-page run ceiling, on every new run <see
    /// cref="TextAssembler"/> opens (a baseline change, or the page's first glyph).</summary>
    internal bool TryConsumeRun(int pageIndex)
    {
        if (IsPageExhausted)
            return false;

        if (_runsThisPage >= _maxRunsPerPage)
        {
            ReportExhausted($"more than {_maxRunsPerPage:N0} text runs on page {pageIndex}", pageIndex);
            return false;
        }

        _runsThisPage++;
        return true;
    }

    // Reported through ReportRetained, keyed (code, null, null) rather than against pageIndex: this
    // condition is meant to surface once for the WHOLE call regardless of which page or which of
    // the four ceilings tripped it first, the same reasoning ImageCallBudget's own two retained
    // codes already established for this reader (see DiagnosticSink's own remarks on why
    // ContentInterpreter and ImageDecoder each bound themselves to a fixed number of retained
    // entries). pageIndex is folded into the MESSAGE instead, since the structured field would
    // otherwise make two trips on two different pages look like two distinct conditions to a
    // caller filtering by it, when this type treats them as the same one. ReportRetained's own
    // (code, object, page) dedupe — here (code, null, null) — is what keeps a second, later trip on
    // a different page from adding a second entry; nothing further is needed on this side of it.
    private void ReportExhausted(string detail, int pageIndex)
    {
        IsPageExhausted = true;
        _diagnostics.ReportRetained(
            PdfReaderDiagnosticCode.TextExtractionLimitExceeded,
            $"Text extraction reached this reader's own ceiling: {detail}; the rest of that page "
            + "was skipped.");
    }
}
