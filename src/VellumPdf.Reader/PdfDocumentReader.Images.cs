// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Content;

namespace VellumPdf.Reader;

public sealed partial class PdfDocumentReader
{
    /// <summary>
    /// Returns every image XObject and inline image this document's pages draw (ISO 32000-2 §8.9),
    /// including those inside Form XObjects and, by default, inside annotation appearance streams
    /// (§12.5.5), in page-tree order (#98). Equivalent to
    /// <see cref="ExtractImages(PdfImageExtractionOptions)"/> with the default options.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This reader has been disposed.</exception>
    public PdfImageExtractionResult ExtractImages() => ExtractImages(new PdfImageExtractionOptions());

    /// <summary> Returns every image XObject and inline image this document's pages draw, per
    /// <paramref name="options"/>. Each distinct image (by object number and generation) is
    /// returned once, the first time the page-tree walk reaches it; an inline image has no object
    /// number and is never deduped, so every inline image occurrence is returned. A <c>/SMask</c>
    /// or <c>/Mask</c> reached through another image is itself an entry in <see
    /// cref="PdfImageExtractionResult.Images"/>, immediately after the image that reached it,
    /// subject to the same dedupe.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see
    /// langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This reader has been disposed.</exception>
    public PdfImageExtractionResult ExtractImages(PdfImageExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        var scope = CreateContentDiagnosticScope();
        var decoder = new ImageDecoder(this, _limits, scope);
        var images = new List<PdfExtractedImage>();
        var seen = new HashSet<(int ObjectNumber, int Generation)>();

        foreach (var page in Pages)
        {
            var interpreter = new ContentInterpreter(this);
            var walker = new ImageReachabilityWalker(this, decoder, interpreter, page.Index, options.IncludeInlineImages, scope);

            interpreter.Run(page, walker, scope);
            if (options.IncludeAnnotationAppearances)
                walker.WalkAnnotationAppearances(page);

            foreach (var image in walker.Images)
                AppendDeduped(image, images, seen);
        }

        return new PdfImageExtractionResult(images, scope.Diagnostics);
    }

    /// <summary> The shared implementation behind <see
    /// cref="PdfReadPage.ExtractImages(PdfImageExtractionOptions)"/>: walks one page's content
    /// (and, per <paramref name="options"/>, its annotation appearances) through a fresh <see
    /// cref="ImageDecoder"/> and returns every occurrence in draw order, with no dedupe (unlike the
    /// document-level overloads above, the same XObject drawn twice on this page is returned twice,
    /// sharing one decoded <see cref="PdfExtractedImage.Data"/> instance through the decoder's own
    /// per-call cache).
    /// </summary>
    internal PdfImageExtractionResult ExtractImagesFromPage(PdfReadPage page, PdfImageExtractionOptions options)
    {
        ThrowIfDisposed();

        var scope = CreateContentDiagnosticScope();
        var decoder = new ImageDecoder(this, _limits, scope);
        var interpreter = new ContentInterpreter(this);
        var walker = new ImageReachabilityWalker(this, decoder, interpreter, page.Index, options.IncludeInlineImages, scope);

        interpreter.Run(page, walker, scope);
        if (options.IncludeAnnotationAppearances)
            walker.WalkAnnotationAppearances(page);

        var images = new List<PdfExtractedImage>();
        foreach (var image in walker.Images)
            AppendWithMasks(image, images);

        return new PdfImageExtractionResult(images, scope.Diagnostics);
    }

    // Document-level accumulation: an image (or mask) already seen by object number/generation is
    // not added again, so a shared image or mask is returned once, at the first page (and, within a
    // page, the first draw) the walk reaches it. An inline image (ObjectNumber null) is never
    // deduped, since it has no identity to dedupe by.
    private static void AppendDeduped(
        PdfExtractedImage image, List<PdfExtractedImage> images, HashSet<(int ObjectNumber, int Generation)> seen)
    {
        if (image.ObjectNumber is int objectNumber && !seen.Add((objectNumber, image.Generation ?? 0)))
            return;

        images.Add(image);
        if (image.SoftMask is not null)
            AppendDeduped(image.SoftMask, images, seen);
        if (image.ExplicitMask is not null)
            AppendDeduped(image.ExplicitMask, images, seen);
    }

    // Page-level accumulation: every occurrence is kept, masks included, immediately after the
    // image that reached them.
    private static void AppendWithMasks(PdfExtractedImage image, List<PdfExtractedImage> images)
    {
        images.Add(image);
        if (image.SoftMask is not null)
            AppendWithMasks(image.SoftMask, images);
        if (image.ExplicitMask is not null)
            AppendWithMasks(image.ExplicitMask, images);
    }
}
