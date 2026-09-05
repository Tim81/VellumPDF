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

        if (ResolveValue(rawFontEntry) is not PdfDictionary fontDict)
        {
            sink.Report(
                PdfReaderDiagnosticCode.FontUnreadable, "the font resource is not a dictionary.",
                objectNumber, generation, pageIndex);
            return null;
        }

        var subtypeRaw = fontDict.Get(PdfName.Subtype);
        var subtype = (subtypeRaw is null ? null : ResolveValue(subtypeRaw)) as PdfName;
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
