// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader.Content;

/// <summary>
/// The abbreviations ISO 32000-2 §8.9.7 permits inside an inline image's <c>BI</c>…<c>ID</c>
/// key-value pairs: Table 91's key names, and Table 92's colour-space and filter-name values.
/// </summary>
/// <remarks>
/// <c>/L</c> (the inline-image length entry) belongs to Table 91, alongside every other
/// inline-image key abbreviation; ISO 32000-2 reserves Table 93 for a Form XObject dictionary's own
/// entries (§8.10.2, an unrelated table two clauses later), so this implementation and its
/// citations cite Table 91 for it.
/// </remarks>
internal static class InlineImageAbbreviations
{
    // Table 91: Entries in an inline image object.
    private static readonly Dictionary<string, PdfName> _keys = new(StringComparer.Ordinal)
    {
        ["BPC"] = new PdfName("BitsPerComponent"),
        ["CS"] = PdfName.ColorSpace,
        ["D"] = new PdfName("Decode"),
        ["DP"] = new PdfName("DecodeParms"),
        ["F"] = PdfName.Filter,
        ["H"] = new PdfName("Height"),
        ["IM"] = new PdfName("ImageMask"),
        ["I"] = new PdfName("Interpolate"), // ambiguous with Table 92's /Indexed "I"; see below
        ["L"] = PdfName.Length,
        ["W"] = new PdfName("Width"),
    };

    // Table 92: Additional abbreviations in an inline image object (colour spaces and filter names).
    // "I" is deliberately excluded from this table: the same one-letter abbreviation "I" stands for
    // /Interpolate as a Table 91 KEY abbreviation and for /Indexed as a Table 92 colour-space VALUE
    // abbreviation, so which it means depends on where it appears (a dictionary key vs. a /CS
    // value), not on the bytes alone. ExpandKey and ExpandColorSpaceOrFilterName each resolve it
    // from their own position in the caller's parse, not from this shared table.
    private static readonly Dictionary<string, PdfName> _colorSpacesAndFilters = new(StringComparer.Ordinal)
    {
        ["G"] = new PdfName("DeviceGray"),
        ["RGB"] = PdfName.DeviceRGB,
        ["CMYK"] = new PdfName("DeviceCMYK"),
        ["AHx"] = new PdfName("ASCIIHexDecode"),
        ["A85"] = new PdfName("ASCII85Decode"),
        ["LZW"] = new PdfName("LZWDecode"),
        ["Fl"] = PdfName.FlateDecode,
        ["RL"] = new PdfName("RunLengthDecode"),
        ["CCF"] = PdfName.CCITTFaxDecode,
        ["DCT"] = PdfName.DCTDecode,
    };

    private static readonly PdfName _indexed = new("Indexed");

    /// <summary>Expands a Table 91 key abbreviation to its full name, or returns
    /// <paramref name="key"/> unchanged when it is not one of Table 91's abbreviations (including
    /// when it is already a full name).</summary>
    internal static PdfName ExpandKey(PdfName key) =>
        _keys.TryGetValue(key.Value, out var full) ? full : key;

    /// <summary>
    /// Expands a Table 92 colour-space-or-filter-name abbreviation. Colour space and filter names
    /// share one lookup because Table 92 lists them together and neither can collide with the
    /// other's abbreviations (distinct strings), except for the "I" ambiguity documented on
    /// <see cref="_colorSpacesAndFilters"/>: <paramref name="isColorSpace"/> is what tells this
    /// method the caller's "I" is a colour-space value (→ /Indexed) rather than the Table 91 key
    /// abbreviation (→ /Interpolate) <see cref="ExpandKey"/> handles instead.
    /// </summary>
    internal static PdfName ExpandColorSpaceOrFilterName(PdfName name, bool isColorSpace)
    {
        if (isColorSpace && name.Value == "I") // Indexed (Table 92); ExpandKey handles Interpolate
            return _indexed;
        return _colorSpacesAndFilters.TryGetValue(name.Value, out var full) ? full : name;
    }
}
