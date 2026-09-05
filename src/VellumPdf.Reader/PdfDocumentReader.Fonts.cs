// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader;

public sealed partial class PdfDocumentReader
{
    private readonly FontCache _fontCache = new();

    /// <summary>
    /// Builds (or returns the cached) <see cref="PdfFontReader"/> for a <c>/Font</c> resource
    /// entry. <paramref name="rawFontEntry"/> is the raw value from a resource dictionary's
    /// <c>/Font</c> subdictionary (an indirect reference, or, unusually, a direct dictionary),
    /// resolved here before its <c>/Subtype</c> is read.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> silently, with no diagnostic, for <c>/Subtype /Type0</c> and
    /// <c>/Subtype /Type3</c>: readers for those are not built yet (#98), and reporting
    /// <see cref="PdfReaderDiagnosticCode.FontUnreadable"/> here would fire on every CJK or Type 3
    /// document until they are. Not yet wired to <c>ContentInterpreter</c>, so the only callers
    /// today are tests.
    /// <para>
    /// Both resolves this method does of its own (the font entry itself, then its <c>/Subtype</c>)
    /// go through a single <see langword="try"/>/<see langword="catch"/> for
    /// <see cref="InvalidDataException"/>: <see cref="Resolve(int, int?)"/> throws past
    /// <c>MaxResolveDepth</c> when parsing a stream's own structure re-enters resolution (a
    /// font entry naming a stream whose <c>/Length</c> chains through indirect references deeply
    /// enough), and that can happen before <see cref="SimpleFontReader.Create"/> is ever reached
    /// to catch it with its own equivalent guard. Caught here, it reports the same
    /// <see cref="PdfReaderDiagnosticCode.FontUnreadable"/> and returns
    /// <see langword="null"/> instead of letting the exception escape.
    /// </para>
    /// </remarks>
    internal PdfFontReader? GetFontReader(PdfObject rawFontEntry, DiagnosticSink sink, int? pageIndex)
    {
        int? objectNumber = null;
        int? generation = null;
        if (rawFontEntry is PdfIndirectReference r)
        {
            objectNumber = r.ObjectNumber;
            generation = r.Generation;
        }

        PdfDictionary fontDict;
        PdfName? subtype;
        try
        {
            if (ResolveValue(rawFontEntry) is not PdfDictionary resolved)
            {
                sink.Report(
                    PdfReaderDiagnosticCode.FontUnreadable, "the font resource is not a dictionary.",
                    objectNumber, generation, pageIndex);
                return null;
            }
            fontDict = resolved;

            var subtypeRaw = fontDict.Get(PdfName.Subtype);
            subtype = (subtypeRaw is null ? null : ResolveValue(subtypeRaw)) as PdfName;
        }
        catch (InvalidDataException)
        {
            sink.Report(
                PdfReaderDiagnosticCode.FontUnreadable,
                "building this font hit the reader's own indirect-object resolution depth limit.",
                objectNumber, generation, pageIndex);
            return null;
        }

        switch (subtype?.Value)
        {
            case "Type1" or "MMType1" or "TrueType":
                return _fontCache.GetOrCreate(
                    objectNumber, generation,
                    () => SimpleFontReader.Create(this, fontDict, objectNumber, generation, sink, pageIndex));

            case "Type0" or "Type3":
                return null;

            default:
                sink.Report(
                    PdfReaderDiagnosticCode.FontUnreadable,
                    "/Subtype is missing or names a font type this reader does not know.",
                    objectNumber, generation, pageIndex);
                return null;
        }
    }
}
