// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader.Content;

namespace VellumPdf.Reader;

/// <summary>
/// Records every image XObject and inline image one <c>ContentInterpreter</c> walk draws, in draw
/// order, and drives every annotation appearance stream on the page afterward (#98). One instance
/// per page walked; <see cref="ImageDecoder"/> and its own <see cref="ImageCallBudget"/> are shared
/// across every page a document-level <c>ExtractImages</c> call visits, but this walker's own
/// <see cref="Images"/> list is not; a fresh instance starts a fresh list for each page.
/// </summary>
internal sealed class ImageReachabilityWalker : IContentVisitor
{
    private const int MaxAnnotsExaminedPerPage = 4096;
    private const int MaxAppearanceStatesPerAnnotation = 64;
    private const int MaxAnnotationAppearancesPerPage = 1024;

    private static readonly PdfName ApKey = new("AP");
    private static readonly PdfName ImageSubtypeValue = new("Image");

    private readonly PdfDocumentReader _reader;
    private readonly ImageDecoder _decoder;
    private readonly ContentInterpreter _interpreter;
    private readonly int _pageIndex;
    private readonly bool _includeInlineImages;
    private readonly DiagnosticSink _diagnostics;
    private readonly List<PdfExtractedImage> _images = [];

    /// <summary>Every image this walk found, in draw order.</summary>
    internal IReadOnlyList<PdfExtractedImage> Images => _images;

    internal ImageReachabilityWalker(
        PdfDocumentReader reader, ImageDecoder decoder, ContentInterpreter interpreter, int pageIndex,
        bool includeInlineImages, DiagnosticSink diagnostics)
    {
        _reader = reader;
        _decoder = decoder;
        _interpreter = interpreter;
        _pageIndex = pageIndex;
        _includeInlineImages = includeInlineImages;
        _diagnostics = diagnostics;
    }

    // Form XObjects are recursed by the interpreter itself; this walker sees the images they draw
    // through OnImageXObject, so none of these three needs to do anything.
    public void OnOperator(string operatorName, IReadOnlyList<PdfObject> operands, int offset)
    {
    }

    public void OnFormBegin(
        PdfDictionary formDictionary, Matrix formMatrix, PdfRectangle? boundingBox, int objectNumber,
        int offset)
    {
    }

    public void OnFormEnd(int objectNumber)
    {
    }

    public void OnImageXObject(ParsedStream stream, int offset)
    {
        if (!_decoder.Budget.TryConsumeOccurrence())
            return;

        var image = _decoder.Decode(stream, _interpreter.CurrentResources, _pageIndex, _diagnostics);
        if (image is not null)
            _images.Add(image);
    }

    public void OnInlineImage(PdfDictionary dictionary, ReadOnlyMemory<byte> data, int offset)
    {
        if (!_includeInlineImages)
            return;

        // Checked BEFORE data.ToArray(): the slice dies with this callback and must be copied to
        // survive past it, and a 64 MiB content stream of ten-byte inline images produces roughly
        // 6.7 million callbacks. Paying the copy past the occurrence cap is the cost the cap
        // exists to stop.
        if (!_decoder.Budget.TryConsumeOccurrence())
            return;

        var copy = data.ToArray();
        var image = _decoder.DecodeInline(dictionary, copy, _interpreter.CurrentResources, _pageIndex, _diagnostics);
        if (image is not null)
            _images.Add(image);
    }

    /// <summary> Walks <paramref name="page"/>'s own <c>/Annots</c> (ISO 32000-2 §12.5.5, Table
    /// 170) for appearance streams and runs each one on the same <c>ContentInterpreter</c> instance
    /// that already ran the page's own content, so the two share one content budget and one
    /// form-invocation budget (see <c>ContentInterpreter.RunFormXObject</c>). Every appearance
    /// state a dictionary-valued <c>/AP /N</c> carries is run, not only the one <c>/AS</c> selects:
    /// §12.5.5 makes <c>/AS</c> a rendering-time choice, and extraction asks what the file
    /// contains. Call only when the page's own content has already been interpreted on <paramref
    /// name="page"/>'s own <c>ContentInterpreter</c> instance.
    /// </summary>
    internal void WalkAnnotationAppearances(PdfReadPage page)
    {
        var annotsRaw = page.Dictionary.Get(PdfName.Annots);
        if (annotsRaw is null)
            return;

        if (_reader.ResolveValue(annotsRaw) is not PdfArray annots)
        {
            Report("/Annots is not an array (ISO 32000-2 §7.7.3.3); annotation appearances were not walked.");
            return;
        }

        var examined = 0;
        var runAppearances = 0;
        var seenObjectNumbers = new HashSet<int>();

        for (var i = 0; i < annots.Count; i++)
        {
            if (examined >= MaxAnnotsExaminedPerPage)
            {
                Report($"/Annots has more than {MaxAnnotsExaminedPerPage} elements; further ones were not examined.");
                return;
            }
            examined++;

            if (_reader.ResolveValue(annots[i]) is not PdfDictionary annotDict)
            {
                Report("An /Annots element is not a dictionary; it was skipped.");
                continue;
            }

            var apRaw = annotDict.Get(ApKey);
            if (apRaw is null)
                continue;

            if (_reader.ResolveValue(apRaw) is not PdfDictionary apDict)
            {
                Report("/AP is present but not a dictionary; it was skipped.");
                continue;
            }

            var nRaw = apDict.Get(PdfName.N);
            if (nRaw is null)
                continue;

            var streamsToRun = new List<ParsedStream>();
            var singleStream = nRaw is PdfIndirectReference nRef ? _reader.ResolveStream(nRef) : null;
            if (singleStream is not null)
            {
                streamsToRun.Add(singleStream);
            }
            else if (_reader.ResolveValue(nRaw) is PdfDictionary stateDict)
            {
                var statesExamined = 0;
                foreach (var entry in stateDict.Entries)
                {
                    if (statesExamined >= MaxAppearanceStatesPerAnnotation)
                    {
                        Report(
                            $"/AP /N has more than {MaxAppearanceStatesPerAnnotation} appearance-state "
                            + "entries; further ones were not examined.");
                        break;
                    }
                    statesExamined++;

                    // A state entry that is not itself an indirect reference to a stream is not an
                    // appearance to run. Table 170 does not require every state to be one, so this
                    // is not itself reported.
                    if (entry.Value is PdfIndirectReference stateRef
                        && _reader.ResolveStream(stateRef) is { } stateStream)
                    {
                        streamsToRun.Add(stateStream);
                    }
                }
            }
            else
            {
                Report("/AP /N is neither a stream nor a dictionary (ISO 32000-2 Table 170); it was skipped.");
                continue;
            }

            foreach (var stream in streamsToRun)
            {
                if (stream.Dictionary.Get(PdfName.Subtype) is PdfName subtype && subtype.Equals(ImageSubtypeValue))
                {
                    Report("An /AP /N appearance stream's /Subtype is /Image, not /Form (ISO 32000-2 "
                        + "§12.5.5 describes an appearance stream as a form XObject); it was skipped.");
                    continue;
                }

                if (!seenObjectNumbers.Add(stream.ObjectNumber))
                    continue; // Already run for an earlier annotation on this page.

                if (runAppearances >= MaxAnnotationAppearancesPerPage)
                {
                    Report(
                        $"More than {MaxAnnotationAppearancesPerPage} distinct appearance streams were "
                        + "found on this page; further ones were not run.");
                    return;
                }
                runAppearances++;

                _interpreter.RunFormXObject(page, stream, this, _diagnostics);
            }
        }
    }

    private void Report(string message) =>
        _diagnostics.Report(PdfReaderDiagnosticCode.AnnotationAppearanceUnusable, message, pageIndex: _pageIndex);
}
