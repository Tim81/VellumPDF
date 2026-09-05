// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary> The CCITT fax parameters an extracted image's <c>/DecodeParms</c> carries (ISO 32000-2
/// §7.4.6, Table 11), with Table 11's own defaults applied where an entry is absent. Every member
/// reports the dictionary's own value or default; none is derived from the image's other
/// properties. In particular, <see cref="Rows"/> is the <c>/DecodeParms /Rows</c> value as written,
/// including 0. Table 11 gives 0 (or an absent entry) its own meaning,
/// "the image's height is not predetermined", which is a decoding mode, not a synonym for <see
/// cref="PdfExtractedImage.Height"/>. Read that property for the height the image dictionary
/// declares.
/// </summary>
public sealed class PdfCcittFaxParameters
{
    /// <summary>The encoding scheme (ISO 32000-2 Table 11): negative for pure two-dimensional
    /// (Group 4), 0 for pure one-dimensional (Group 3, one-dimensional), positive for mixed
    /// one- and two-dimensional (Group 3, two-dimensional). Default 0.</summary>
    public int K { get; }

    /// <summary>The number of pixels per row. Default 1728.</summary>
    public int Columns { get; }

    /// <summary>The number of rows, or 0 when not predetermined (see this type's own remarks).
    /// Default 0.</summary>
    public int Rows { get; }

    /// <summary>Whether 1 bits are black pixels and 0 bits white, the reverse of the normal PDF
    /// syntactic convention for image data. Default <see langword="false"/>.</summary>
    public bool BlackIs1 { get; }

    /// <summary>Whether encoded data is aligned to a byte boundary at the end of each row.
    /// Default <see langword="false"/>.</summary>
    public bool EncodedByteAlign { get; }

    /// <summary>Whether end-of-line bit patterns are present in the encoded data. Default
    /// <see langword="false"/>.</summary>
    public bool EndOfLine { get; }

    /// <summary>Whether the encoded data is terminated by an end-of-block bit pattern. Default
    /// <see langword="true"/>.</summary>
    public bool EndOfBlock { get; }

    /// <summary>The number of damaged rows tolerated before an error is signalled. Default
    /// 0.</summary>
    public int DamagedRowsBeforeError { get; }

    internal PdfCcittFaxParameters(
        int k, int columns, int rows, bool blackIs1, bool encodedByteAlign, bool endOfLine,
        bool endOfBlock, int damagedRowsBeforeError)
    {
        K = k;
        Columns = columns;
        Rows = rows;
        BlackIs1 = blackIs1;
        EncodedByteAlign = encodedByteAlign;
        EndOfLine = endOfLine;
        EndOfBlock = endOfBlock;
        DamagedRowsBeforeError = damagedRowsBeforeError;
    }
}
