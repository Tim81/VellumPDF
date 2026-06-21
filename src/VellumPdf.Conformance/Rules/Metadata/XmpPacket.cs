// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml.Linq;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// Parses an XMP metadata packet into the facts the PDF/A metadata rules need beyond the property
/// values that <see cref="XmpReader"/> exposes: whether the packet is well-formed XML, the
/// pseudo-attributes of its <c>&lt;?xpacket?&gt;</c> processing-instruction header, and the byte
/// encoding it is actually serialised in.
/// </summary>
/// <remarks>
/// AOT- and trim-safe: uses only <see cref="System.Text.Encoding"/> byte inspection and the
/// XXE-hardened <c>XmlReader</c> path in <see cref="XmpReader.Parse"/> — no reflection, no
/// XML serialisation.
/// </remarks>
internal sealed class XmpPacket
{
    /// <summary>The parsed document, or <see langword="null"/> when the packet is not well-formed XML.</summary>
    public XDocument? Document { get; private init; }

    /// <summary>True when the packet parsed as well-formed XML (ISO 19005-2 §6.6.2.1).</summary>
    public bool IsWellFormed => Document is not null;

    /// <summary>True when the <c>&lt;?xpacket?&gt;</c> header carries a <c>bytes</c> pseudo-attribute (forbidden, §6.6.2.1).</summary>
    public bool HasBytesAttribute { get; private init; }

    /// <summary>True when the <c>&lt;?xpacket?&gt;</c> header carries an <c>encoding</c> pseudo-attribute (forbidden, §6.6.2.1).</summary>
    public bool HasEncodingAttribute { get; private init; }

    /// <summary>True when the packet is serialised as UTF-8 (required by §6.6.2.1); false for UTF-16/UTF-32.</summary>
    public bool IsUtf8 { get; private init; }

    public static XmpPacket Parse(byte[] bytes)
    {
        var (encoding, isUtf8) = DetectEncoding(bytes);
        var (hasBytes, hasEncoding) = ParseXpacketHeader(DecodeHeader(bytes, encoding));
        return new XmpPacket
        {
            Document = XmpReader.Parse(bytes),
            IsUtf8 = isUtf8,
            HasBytesAttribute = hasBytes,
            HasEncodingAttribute = hasEncoding,
        };
    }

    // Determine the serialisation encoding from the leading byte-order mark, or — for a BOM-less
    // packet — from the byte pattern of the opening '<' (ISO 16684-1 permits UTF-8/16/32). UTF-32
    // marks are tested before the UTF-16 marks they share a prefix with.
    private static (Encoding Encoding, bool IsUtf8) DetectEncoding(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return (Encoding.UTF8, true);                                   // UTF-8 BOM
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), false);   // UTF-32 BE
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), false);  // UTF-32 LE
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return (Encoding.BigEndianUnicode, false);                      // UTF-16 BE BOM
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return (Encoding.Unicode, false);                              // UTF-16 LE BOM
        if (b.Length >= 2 && b[0] == (byte)'<' && b[1] == 0x00)
            return (Encoding.Unicode, false);                              // BOM-less UTF-16 LE
        if (b.Length >= 2 && b[0] == 0x00 && b[1] == (byte)'<')
            return (Encoding.BigEndianUnicode, false);                      // BOM-less UTF-16 BE
        return (Encoding.UTF8, true);                                       // default per ISO 16684-1
    }

    // Decode just enough of the packet to inspect the <?xpacket?> header.
    private static string DecodeHeader(byte[] bytes, Encoding encoding)
    {
        try
        {
            var take = Math.Min(bytes.Length, 512);
            return encoding.GetString(bytes, 0, take);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static (bool HasBytes, bool HasEncoding) ParseXpacketHeader(string text)
    {
        var start = text.IndexOf("<?xpacket", StringComparison.Ordinal);
        if (start < 0)
            return (false, false);
        var end = text.IndexOf("?>", start, StringComparison.Ordinal);
        var header = end < 0 ? text[start..] : text[start..end];
        return (HasPseudoAttribute(header, "bytes"), HasPseudoAttribute(header, "encoding"));
    }

    // True when <paramref name="header"/> contains a pseudo-attribute named exactly
    // <paramref name="name"/> (whitespace-delimited, followed by '='), not merely the substring.
    private static bool HasPseudoAttribute(string header, string name)
    {
        var from = 0;
        while ((from = header.IndexOf(name, from, StringComparison.Ordinal)) >= 0)
        {
            var boundedLeft = from == 0 || char.IsWhiteSpace(header[from - 1]);
            var after = from + name.Length;
            var j = after;
            while (j < header.Length && char.IsWhiteSpace(header[j]))
                j++;
            if (boundedLeft && j < header.Length && header[j] == '=')
                return true;
            from = after;
        }
        return false;
    }
}
