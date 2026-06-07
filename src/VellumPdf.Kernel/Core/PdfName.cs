// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// PDF name object (ISO 32000-2 §7.3.5).
/// The value is stored unescaped; serialisation applies #XX escaping for
/// bytes outside 0x21–0x7E and for '#' itself.
/// </summary>
public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    // Well-known names used throughout the kernel — avoids string allocation.
    public static readonly PdfName Type       = new("Type");
    public static readonly PdfName Subtype    = new("Subtype");
    public static readonly PdfName Length     = new("Length");
    public static readonly PdfName Filter     = new("Filter");
    public static readonly PdfName FlateDecode = new("FlateDecode");
    public static readonly PdfName DCTDecode  = new("DCTDecode");
    public static readonly PdfName Page       = new("Page");
    public static readonly PdfName Pages      = new("Pages");
    public static readonly PdfName Catalog    = new("Catalog");
    public static readonly PdfName Kids       = new("Kids");
    public static readonly PdfName Count      = new("Count");
    public static readonly PdfName MediaBox   = new("MediaBox");
    public static readonly PdfName Contents   = new("Contents");
    public static readonly PdfName Resources  = new("Resources");
    public static readonly PdfName Font       = new("Font");
    public static readonly PdfName XObject    = new("XObject");
    public static readonly PdfName ProcSet    = new("ProcSet");
    public static readonly PdfName PDF        = new("PDF");
    public static readonly PdfName Text       = new("Text");
    public static readonly PdfName ImageB     = new("ImageB");
    public static readonly PdfName ImageC     = new("ImageC");
    public static readonly PdfName ImageI     = new("ImageI");
    public static readonly PdfName Parent     = new("Parent");
    public static readonly PdfName Annots     = new("Annots");
    public static readonly PdfName Rotate     = new("Rotate");
    public static readonly PdfName Info       = new("Info");
    public static readonly PdfName Root       = new("Root");
    public static readonly PdfName Size       = new("Size");
    public static readonly PdfName Prev       = new("Prev");
    public static readonly PdfName ID         = new("ID");
    public static readonly PdfName Encoding   = new("Encoding");
    public static readonly PdfName BaseFont   = new("BaseFont");
    public static readonly PdfName Name       = new("Name");

    public string Value { get; }

    public PdfName(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    // PDF delimiters that must be escaped in name objects (ISO 32000-2 §7.3.5).
    private static readonly System.Collections.Generic.HashSet<byte> _delimiters = new()
    {
        (byte)'(', (byte)')', (byte)'<', (byte)'>', (byte)'[', (byte)']',
        (byte)'{', (byte)'}', (byte)'/', (byte)'%'
    };

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteByte((byte)'/');
        foreach (var ch in System.Text.Encoding.Latin1.GetBytes(Value))
        {
            if (ch == '#' || ch < 0x21 || ch > 0x7E || _delimiters.Contains(ch))
            {
                writer.WriteByte((byte)'#');
                writer.WriteByte(ToHexNibble(ch >> 4));
                writer.WriteByte(ToHexNibble(ch & 0xF));
            }
            else
            {
                writer.WriteByte(ch);
            }
        }
    }

    private static byte ToHexNibble(int n) => (byte)(n < 10 ? '0' + n : 'A' + n - 10);

    public bool Equals(PdfName? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is PdfName n && Equals(n);
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => $"/{Value}";
}
