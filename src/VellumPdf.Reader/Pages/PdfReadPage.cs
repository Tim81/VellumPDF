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
    /// direct object embedded inside a <c>/Kids</c> array rather than referenced indirectly — ISO
    /// 32000-2 does not forbid that shape, and object number <c>0</c> is otherwise unused (it is
    /// permanently reserved as the head of the free-object list, ISO 32000-2 §7.5.4), so it is safe
    /// to use as the "no object number" sentinel here.
    /// </summary>
    public int ObjectNumber { get; }

    /// <summary>The page object's own dictionary (ISO 32000-2 §7.7.3.3), unmodified — inherited
    /// attributes are exposed through this type's other properties, not merged into this
    /// dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// The page's <c>/MediaBox</c> (ISO 32000-2 §7.7.3.3), own or inherited (§7.7.3.4), with its
    /// corners normalised so <see cref="PdfRectangle.LlX"/> ≤ <see cref="PdfRectangle.UrX"/> and
    /// <see cref="PdfRectangle.LlY"/> ≤ <see cref="PdfRectangle.UrY"/>. <c>/MediaBox</c> is Required
    /// by Table 31, so a page whose chain never supplies a valid one falls back to US Letter
    /// (612 × 792 points) and a <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/> report.
    /// </summary>
    public PdfRectangle MediaBox { get; }

    /// <summary>
    /// The page's <c>/CropBox</c> (ISO 32000-2 §7.7.3.3), own or inherited, normalised the same way
    /// as <see cref="MediaBox"/>. Optional: absent anywhere in the chain defaults to
    /// <see cref="MediaBox"/> with no diagnostic, exactly as §7.7.3.3 specifies; present but
    /// malformed falls back to <see cref="MediaBox"/> with a
    /// <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/> report. Exposed as written, not
    /// clipped to <see cref="MediaBox"/> — a producer that wrote a crop region extending past the
    /// media box gets that value back unchanged.
    /// </summary>
    public PdfRectangle CropBox { get; }

    /// <summary>
    /// Degrees clockwise the page shall be rotated when displayed or printed (ISO 32000-2 §7.7.3.3),
    /// own or inherited, normalised to one of 0, 90, 180, or 270. A negative value or one past 360
    /// is folded into that range when it is still a multiple of 90 (e.g. <c>-90</c> becomes 270,
    /// <c>450</c> becomes 90); a value that is not a multiple of 90 at all — which §7.7.3.3 forbids
    /// outright — normalises to 0 with a <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/>
    /// report instead. Default 0 when absent anywhere in the chain.
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
