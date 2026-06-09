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
    /// <summary>The <c>/Type</c> name.</summary>
    public static readonly PdfName Type = new("Type");
    /// <summary>The <c>/Subtype</c> name.</summary>
    public static readonly PdfName Subtype = new("Subtype");
    /// <summary>The <c>/Length</c> name.</summary>
    public static readonly PdfName Length = new("Length");
    /// <summary>The <c>/Filter</c> name.</summary>
    public static readonly PdfName Filter = new("Filter");
    /// <summary>The <c>/FlateDecode</c> name.</summary>
    public static readonly PdfName FlateDecode = new("FlateDecode");
    /// <summary>The <c>/DCTDecode</c> name.</summary>
    public static readonly PdfName DCTDecode = new("DCTDecode");
    /// <summary>The <c>/Page</c> name.</summary>
    public static readonly PdfName Page = new("Page");
    /// <summary>The <c>/Pages</c> name.</summary>
    public static readonly PdfName Pages = new("Pages");
    /// <summary>The <c>/Catalog</c> name.</summary>
    public static readonly PdfName Catalog = new("Catalog");
    /// <summary>The <c>/Kids</c> name.</summary>
    public static readonly PdfName Kids = new("Kids");
    /// <summary>The <c>/Count</c> name.</summary>
    public static readonly PdfName Count = new("Count");
    /// <summary>The <c>/MediaBox</c> name.</summary>
    public static readonly PdfName MediaBox = new("MediaBox");
    /// <summary>The <c>/Contents</c> name.</summary>
    public static readonly PdfName Contents = new("Contents");
    /// <summary>The <c>/Resources</c> name.</summary>
    public static readonly PdfName Resources = new("Resources");
    /// <summary>The <c>/Font</c> name.</summary>
    public static readonly PdfName Font = new("Font");
    /// <summary>The <c>/XObject</c> name.</summary>
    public static readonly PdfName XObject = new("XObject");
    /// <summary>The <c>/ProcSet</c> name.</summary>
    public static readonly PdfName ProcSet = new("ProcSet");
    /// <summary>The <c>/PDF</c> name.</summary>
    public static readonly PdfName PDF = new("PDF");
    /// <summary>The <c>/Text</c> name.</summary>
    public static readonly PdfName Text = new("Text");
    /// <summary>The <c>/ImageB</c> name.</summary>
    public static readonly PdfName ImageB = new("ImageB");
    /// <summary>The <c>/ImageC</c> name.</summary>
    public static readonly PdfName ImageC = new("ImageC");
    /// <summary>The <c>/ImageI</c> name.</summary>
    public static readonly PdfName ImageI = new("ImageI");
    /// <summary>The <c>/Parent</c> name.</summary>
    public static readonly PdfName Parent = new("Parent");
    /// <summary>The <c>/Annots</c> name.</summary>
    public static readonly PdfName Annots = new("Annots");
    /// <summary>The <c>/Rotate</c> name.</summary>
    public static readonly PdfName Rotate = new("Rotate");
    /// <summary>The <c>/Info</c> name.</summary>
    public static readonly PdfName Info = new("Info");
    /// <summary>The <c>/Root</c> name.</summary>
    public static readonly PdfName Root = new("Root");
    /// <summary>The <c>/Size</c> name.</summary>
    public static readonly PdfName Size = new("Size");
    /// <summary>The <c>/Prev</c> name.</summary>
    public static readonly PdfName Prev = new("Prev");
    /// <summary>The <c>/ID</c> name.</summary>
    public static readonly PdfName ID = new("ID");
    /// <summary>The <c>/Encoding</c> name.</summary>
    public static readonly PdfName Encoding = new("Encoding");
    /// <summary>The <c>/BaseFont</c> name.</summary>
    public static readonly PdfName BaseFont = new("BaseFont");
    /// <summary>The <c>/Name</c> name.</summary>
    public static readonly PdfName Name = new("Name");
    /// <summary>The <c>/ExtGState</c> name.</summary>
    public static readonly PdfName ExtGState = new("ExtGState");
    /// <summary>The <c>/Shading</c> name.</summary>
    public static readonly PdfName Shading = new("Shading");
    /// <summary>The <c>/ShadingType</c> name.</summary>
    public static readonly PdfName ShadingType = new("ShadingType");
    /// <summary>The <c>/ColorSpace</c> name.</summary>
    public static readonly PdfName ColorSpace = new("ColorSpace");
    /// <summary>The <c>/Coords</c> name.</summary>
    public static readonly PdfName Coords = new("Coords");
    /// <summary>The <c>/Domain</c> name.</summary>
    public static readonly PdfName Domain = new("Domain");
    /// <summary>The <c>/Function</c> name.</summary>
    public static readonly PdfName Function = new("Function");
    /// <summary>The <c>/Extend</c> name.</summary>
    public static readonly PdfName Extend = new("Extend");
    /// <summary>The <c>/FunctionType</c> name.</summary>
    public static readonly PdfName FunctionType = new("FunctionType");
    /// <summary>The <c>/C0</c> name.</summary>
    public static readonly PdfName C0 = new("C0");
    /// <summary>The <c>/C1</c> name.</summary>
    public static readonly PdfName C1 = new("C1");
    /// <summary>The <c>/N</c> name.</summary>
    public static readonly PdfName N = new("N");
    /// <summary>The <c>/DeviceRGB</c> name.</summary>
    public static readonly PdfName DeviceRGB = new("DeviceRGB");

    /// <summary>The unescaped name value (without the leading <c>/</c>).</summary>
    public string Value { get; }

    /// <summary>Initialises a new <see cref="PdfName"/> with the given unescaped value.</summary>
    /// <param name="value">The name value; must be non-null and non-empty.</param>
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

    /// <inheritdoc />
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

    /// <summary>Determines whether this name equals <paramref name="other"/> by value.</summary>
    public bool Equals(PdfName? other) => other is not null && Value == other.Value;
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PdfName n && Equals(n);
    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    /// <summary>Returns the name in PDF syntax (<c>/Value</c>).</summary>
    public override string ToString() => $"/{Value}";
}
