// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// The JBIG2 parameters an extracted image's <c>/DecodeParms</c> carries (ISO 32000-2 §7.4.7). This
/// reader never decodes JBIG2 segment data itself; <see cref="PdfExtractedImage.Data"/> is the
/// embedded-organisation payload verbatim, and <see cref="Globals"/> is the shared segment stream
/// that payload depends on, when one is named.
/// </summary>
public sealed class PdfJbig2Parameters
{
    /// <summary>
    /// The decoded <c>/JBIG2Globals</c> stream (ISO 32000-2 §7.4.7): globally-referenced segments
    /// this image's own embedded segments depend on. Empty when the image names no globals stream,
    /// or when the one it names does not resolve to a stream, fails to decode, or is refused by the
    /// decode budget (see <see cref="PdfReaderDiagnosticCode.ImageDictionaryInvalid"/>).
    /// </summary>
    public ReadOnlyMemory<byte> Globals { get; }

    internal PdfJbig2Parameters(ReadOnlyMemory<byte> globals) => Globals = globals;
}
