// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;

namespace VellumPdf.Reader;

public sealed partial class PdfDocumentReader
{
    private IReadOnlyList<PdfReadPage>? _pages;

    /// <summary>
    /// The document's pages, in page-tree order (ISO 32000-2 §7.7.3) — found by walking
    /// <c>/Root</c> → <c>/Pages</c> → <c>/Kids</c> rather than trusting any node's own
    /// <c>/Count</c>, which §7.7.3.2's own NOTE calls redundant with the tree structure and which
    /// real producers disagree with their own <c>/Kids</c> often enough that trusting it would
    /// misreport this on ordinary files, not just adversarial ones. A page tree the walk cannot use
    /// at all — a missing or non-dictionary <c>/Pages</c>, most commonly — yields an empty list and
    /// a <see cref="PdfReaderDiagnosticCode.PageTreeMissing"/> report, not an exception; a
    /// structural problem found partway through (a cycle, a nesting depth or leaf-count past the
    /// walker's own caps) yields whatever pages were found before that point.
    /// </summary>
    /// <remarks>
    /// Computed on first access to this property, <see cref="PageCount"/>, or
    /// <see cref="GetPage(int)"/> — never in the constructor — and cached for the life of this
    /// reader. Not thread-safe, like every other cache this type keeps.
    /// </remarks>
    public IReadOnlyList<PdfReadPage> Pages =>
        _pages ??= new ReadOnlyCollection<PdfReadPage>(PageTreeWalker.Walk(this, _diagnostics));

    /// <summary>The number of pages the page-tree walk found — see <see cref="Pages"/> for what
    /// that means when the tree is malformed. Never <c>/Count</c>.</summary>
    public int PageCount => Pages.Count;

    /// <summary>Returns the page at <paramref name="index"/> (0-based, page-tree order).</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is outside <c>[0, PageCount)</c>.
    /// </exception>
    public PdfReadPage GetPage(int index)
    {
        var pages = Pages;
        if (index < 0 || index >= pages.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"Page index must be within [0, {pages.Count}).");
        }
        return pages[index];
    }
}
