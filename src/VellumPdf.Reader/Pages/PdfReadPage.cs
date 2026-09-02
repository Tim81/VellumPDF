// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;

namespace VellumPdf.Reader;

/// <summary>
/// One page found by <see cref="PdfDocumentReader.Pages"/>, with its inheritable attributes
/// (ISO 32000-2 §7.7.3.4) already resolved from the page's own dictionary or its page-tree
/// ancestors, and normalised. Has no public constructor: an instance only ever comes from a
/// <see cref="PdfDocumentReader"/>'s own page-tree walk.
/// </summary>
public sealed class PdfReadPage
{
    /// <summary>Zero-based position in page-tree order, as the walk found it — see
    /// <see cref="PdfDocumentReader.Pages"/> for why this does not come from any node's
    /// <c>/Count</c>.</summary>
    public int Index { get; }

    /// <summary>
    /// The indirect object number of this page's own dictionary, or <c>0</c> when the leaf was a
    /// direct dictionary embedded inside a <c>/Kids</c> array rather than referenced indirectly.
    /// Table 30 requires <c>/Kids</c> entries to be indirect references; a direct dictionary there is
    /// tolerated by this reader rather than rejected, and reported as object number <c>0</c>, which
    /// is otherwise unused (permanently reserved as the head of the free-object list, ISO 32000-2
    /// §7.5.4), so it is safe to use as the "no object number" sentinel here.
    /// </summary>
    public int ObjectNumber { get; }

    /// <summary>
    /// The page's own dictionary (ISO 32000-2 §7.7.3.3) from the reader's cached object graph, not a
    /// copy. Inherited attributes are exposed through this type's other properties, not merged into
    /// it. Treat it as read-only: mutating it mutates the reader's own cache, and does not update the
    /// already-resolved <see cref="MediaBox"/>, <see cref="CropBox"/>, or <see cref="Rotate"/> on this
    /// or any other <see cref="PdfReadPage"/>.
    /// </summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// The page's <c>/MediaBox</c> (ISO 32000-2 §7.7.3.3), own or inherited (§7.7.3.4), with its
    /// corners normalised so <see cref="PdfRectangle.LlX"/> ≤ <see cref="PdfRectangle.UrX"/> and
    /// <see cref="PdfRectangle.LlY"/> ≤ <see cref="PdfRectangle.UrY"/>. <c>/MediaBox</c> is Required
    /// by Table 31: a malformed value anywhere in the chain is skipped in favour of the nearest
    /// ancestor that supplies a valid one, each skip reported as
    /// <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/>; only when nothing in the chain
    /// ever resolves to a valid rectangle does this fall back to US Letter (612 × 792 points), this
    /// reader's own convention: the specification names no default.
    /// </summary>
    public PdfRectangle MediaBox { get; }

    /// <summary>
    /// The page's <c>/CropBox</c> (ISO 32000-2 §7.7.3.3), own or inherited, resolved the same
    /// ancestor-walking way as <see cref="MediaBox"/> and then intersected with
    /// <see cref="MediaBox"/> per §14.11.2.1: a crop region extending past the media box is clipped
    /// to it, not exposed as written. Optional: absent anywhere in the chain defaults to
    /// <see cref="MediaBox"/> with no diagnostic, exactly as §7.7.3.3 specifies; a value found but
    /// disjoint from <see cref="MediaBox"/>, or otherwise malformed throughout the chain, falls back
    /// to <see cref="MediaBox"/> with a <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/>
    /// report.
    /// </summary>
    public PdfRectangle CropBox { get; }

    /// <summary>
    /// Degrees clockwise the page shall be rotated when displayed or printed (ISO 32000-2 §7.7.3.3),
    /// own or inherited, normalised to one of 0, 90, 180, or 270. An integral real such as
    /// <c>90.0</c> is accepted, as is a negative value or one past 360 when it is still a multiple of
    /// 90 (e.g. <c>-90</c> becomes 270, <c>450</c> becomes 90); a value that is not a multiple of 90
    /// at all (which §7.7.3.3 forbids outright) is skipped in favour of the nearest ancestor that
    /// supplies a valid one, reported as <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/>.
    /// Default 0 when nothing in the chain ever resolves to a valid value.
    /// </summary>
    public int Rotate { get; }

    /// <summary>
    /// The page's <c>/Resources</c> dictionary (ISO 32000-2 §7.7.3.3), own or inherited, or
    /// <see langword="null"/> when nothing in the page's ancestor chain supplies one. Internal for
    /// v2.4 by design (the Reader package does not expose resource/content access yet).
    /// </summary>
    internal PdfDictionary? Resources { get; }

    internal PdfReadPage(
        int index, int objectNumber, PdfDictionary dictionary,
        PdfRectangle mediaBox, PdfRectangle cropBox, int rotate, PdfDictionary? resources)
    {
        Index = index;
        ObjectNumber = objectNumber;
        Dictionary = dictionary;
        MediaBox = mediaBox;
        CropBox = cropBox;
        Rotate = rotate;
        Resources = resources;
    }
}
