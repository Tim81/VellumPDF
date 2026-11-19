// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// PDF literal string object written as (…) with balanced-parenthesis and
/// backslash escaping (ISO 32000-2 §7.3.4.2).
/// </summary>
public sealed class PdfLiteralString : PdfObject
{
    /// <summary>The raw (unescaped) string bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Creates a literal string from the given raw bytes.</summary>
    public PdfLiteralString(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    /// <summary>Encodes a UTF-16BE string with BOM (used for metadata fields).</summary>
    public static PdfLiteralString FromUnicode(string value)
    {
        var bytes = new byte[2 + value.Length * 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        for (var i = 0; i < value.Length; i++)
        {
            var c = (ushort)value[i];
            bytes[2 + i * 2] = (byte)(c >> 8);
            bytes[2 + i * 2 + 1] = (byte)(c & 0xFF);
        }
        return new PdfLiteralString(bytes);
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a PDF text string (ISO 32000-2, 7.9.2.2): the compact
    /// PDFDocEncoding form when every character means the same byte there as in Latin-1, otherwise
    /// UTF-16BE with a byte order mark.
    /// </summary>
    /// <remarks>
    /// No well-formed character is substituted, which is what separates this from
    /// <c>Encoding.Latin1.GetBytes</c>: that encoder's fallback rewrites a character it cannot
    /// encode into a look-alike (U+0141 becomes <c>L</c>, U+2014 becomes <c>-</c>) and reports
    /// nothing. A lone surrogate, which is not a character, is written as U+FFFD.
    ///
    /// Latin-1 and PDFDocEncoding (Annex D.2) give the same byte for 0x20 to 0x7E and for 0xA1 to
    /// 0xFF except 0xAD, so ASCII and Western European text keeps the bytes it always had. The
    /// codes 0x00 to 0x17 are undefined in PDFDocEncoding and keep their Latin-1 byte, as before.
    /// The two differ on 0x18 to 0x1F, 0x7F, 0x80 to 0xA0 and 0xAD; a value holding one of those
    /// takes the UTF-16BE form rather than a byte a reader would show as another character. So
    /// does a value that starts with the bytes of a byte order mark (U+00FE U+00FF, or U+00EF
    /// U+00BB U+00BF), since PDFDocEncoding cannot begin that way (7.9.2.2.1, NOTE 3 and 4).
    /// </remarks>
    internal static PdfLiteralString FromTextString(string value)
    {
        foreach (var c in value)
        {
            var sameInBoth = c is < (char)0x18 or (>= (char)0x20 and <= (char)0x7E)
                or (>= (char)0xA1 and <= (char)0xFF and not (char)0xAD);
            if (!sameInBoth)
                return Utf16WithMark(value);
        }

        // 0xFE 0xFF (thorn, ydieresis) is the UTF-16BE mark and 0xEF 0xBB 0xBF (idieresis,
        // guillemotright, questiondown) the UTF-8 one: a reader would take either for the mark.
        if (value.Length >= 2 && value[0] == (char)0xFE && value[1] == (char)0xFF)
            return Utf16WithMark(value);
        if (value.Length >= 3 && value[0] == (char)0xEF && value[1] == (char)0xBB && value[2] == (char)0xBF)
            return Utf16WithMark(value);

        return new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes(value));
    }

    // Not FromUnicode, which copies UTF-16 code units as they stand: a lone surrogate would reach
    // the file as ill-formed UTF-16BE. The encoder below writes U+FFFD for one instead, which a
    // reader shows as a replacement character, where Latin-1 wrote a question mark.
    private static PdfLiteralString Utf16WithMark(string value)
    {
        var body = System.Text.Encoding.BigEndianUnicode.GetBytes(value);
        var bytes = new byte[2 + body.Length];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        body.CopyTo(bytes, 2);
        return new PdfLiteralString(bytes);
    }

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer)
    {
        // When an encryptor is active, encrypt the raw bytes first and then
        // write the escaped result. The (…) delimiters wrap the encrypted payload.
        var payload = writer.Encryptor is { } enc
            ? enc.Encrypt(Bytes.Span)
            : Bytes.Span.ToArray();

        writer.WriteByte((byte)'(');
        foreach (var b in payload.AsSpan())
        {
            switch (b)
            {
                case (byte)'(': writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'('); break;
                case (byte)')': writer.WriteByte((byte)'\\'); writer.WriteByte((byte)')'); break;
                case (byte)'\\': writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'\\'); break;
                case 0x0A: writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'n'); break;
                case 0x0D: writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'r'); break;
                default: writer.WriteByte(b); break;
            }
        }
        writer.WriteByte((byte)')');
    }
}
