// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Annotations;

/// <summary>
/// Describes a /Link annotation to be attached to a page.
/// Use <see cref="Uri"/> for external hyperlinks, <see cref="DestPage"/> for an internal link to a
/// position on a page, or <see cref="DestName"/> for an internal link to a destination added with
/// <see cref="PdfDocument.AddNamedDestination"/>.
/// </summary>
public sealed class PdfLinkAnnotation
{
    /// <summary>Annotation bounding rectangle in PDF user-space (points, Y-up).</summary>
    public required PdfRectangle Rect { get; init; }

    /// <summary>External URI action target, written as <c>/A &lt;&lt; /S /URI &gt;&gt;</c>.</summary>
    /// <remarks>
    /// <b>A <see cref="Uri"/> together with <see cref="DestName"/> is refused.</b>
    /// <see cref="PdfDocument.RegisterLinkAnnotation"/> throws <see cref="ArgumentException"/> for
    /// it (<c>ParamName</c> <c>annotation</c>), because ISO 32000-2, Table 176 does not permit
    /// <c>Dest</c> beside an <c>A</c> entry. An empty string counts as set.
    /// <para>When <see cref="Uri"/> and <see cref="DestPage"/> are both set, the save writes the
    /// <c>/A</c> action and leaves <see cref="DestPage"/>, <see cref="DestLeft"/> and
    /// <see cref="DestTop"/> out, without a message. <b>Do not pass</b> both: a later major will
    /// refuse the pair.</para>
    /// </remarks>
    public string? Uri { get; init; }

    /// <summary>
    /// Target page for an internal /XYZ destination, written as
    /// <c>/Dest [page /XYZ left top null]</c>. Resolved to a page indirect-reference during
    /// <see cref="PdfDocument.Save"/>.
    /// </summary>
    /// <remarks>
    /// <b>A <see cref="DestPage"/> together with <see cref="DestName"/> is refused.</b>
    /// <see cref="PdfDocument.RegisterLinkAnnotation"/> throws <see cref="ArgumentException"/> for
    /// it (<c>ParamName</c> <c>annotation</c>), because both would fill the one <c>Dest</c> entry.
    /// <para><b>Silently ignored:</b> a page that is not one of the saving document's pages. The
    /// link is then written with neither <c>/A</c> nor <c>/Dest</c>. When <see cref="Uri"/> is also
    /// set, this property is ignored; see <see cref="Uri"/>.</para>
    /// </remarks>
    public PdfPage? DestPage { get; init; }

    private string? _destName;

    /// <summary>
    /// The name of a destination added with <see cref="PdfDocument.AddNamedDestination"/>. When set,
    /// the link is written as <c>/Dest (name)</c>, a byte string of the name's ASCII characters
    /// (ISO 32000-2, 12.3.2.4 and Table 176), and a reader looks the name up in the document's
    /// <c>/Dests</c> name tree.
    /// </summary>
    /// <remarks>
    /// <b>A name outside the rule is refused.</b> The <see langword="init"/> accessor throws
    /// <see cref="ArgumentException"/> (<c>ParamName</c> <c>value</c>) for a name that is empty,
    /// has more than 127 characters, or holds a character other than <c>A-Z a-z 0-9 . _ - :</c>.
    /// <see langword="null"/> means no name and is accepted. The message gives the index and code
    /// point of a bad character and never quotes the name.
    /// <para><b>Combinations are refused by <see cref="PdfDocument.RegisterLinkAnnotation"/>,</b>
    /// not here, because an initializer sees one property at a time: <see cref="Uri"/> or
    /// <see cref="DestPage"/> set beside this name throws <see cref="ArgumentException"/> there
    /// (<c>ParamName</c> <c>annotation</c>). A name that no destination carries is refused from
    /// <see cref="PdfDocument.Save(System.IO.Stream)"/>, <see cref="PdfDocument.SaveAsync"/> and
    /// <see cref="PdfDocument.PrepareForSigning"/> with <see cref="InvalidOperationException"/>
    /// naming the destination, before anything is written; the document stays usable.</para>
    /// <para><b>Silently ignored:</b> <see cref="DestLeft"/> and <see cref="DestTop"/>. The
    /// destination's own coordinates apply, and zero cannot be told from "not set", so no value of
    /// them is refused.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty, longer than 127 characters or holds a
    /// character outside <c>A-Z a-z 0-9 . _ - :</c>.</exception>
    public string? DestName
    {
        get => _destName;
        init
        {
            if (value is not null)
                DestinationName.Validate(value, nameof(value));
            _destName = value;
        }
    }

    /// <summary>X coordinate of the destination viewport origin (PDF user-space).</summary>
    /// <remarks>A magnitude above 3.403 × 10^38 is written but fails PDF/A validation; see <see cref="PdfReal"/>.
    /// Not used when <see cref="DestName"/> is set.</remarks>
    public double DestLeft { get; init; }

    /// <summary>Y coordinate of the destination viewport origin (PDF user-space).</summary>
    /// <remarks>A magnitude above 3.403 × 10^38 is written but fails PDF/A validation; see <see cref="PdfReal"/>.
    /// Not used when <see cref="DestName"/> is set.</remarks>
    public double DestTop { get; init; }

    /// <summary>
    /// Annotation flags (ISO 32000-1 Table 165). Defaults to <see cref="PdfAnnotationFlags.Print"/>
    /// alone, which ISO 19005-2 §6.3.2 requires of every non-Popup annotation for PDF/A
    /// conformance. Override only for non-PDF/A output (e.g. a non-printing link).
    /// </summary>
    public PdfAnnotationFlags Flags { get; init; } = PdfAnnotationFlags.Print;

    /// <summary>Builds the annotation dictionary given the resolved destination page reference.</summary>
    internal PdfDictionary BuildDictionary(PdfIndirectReference? destPageRef)
    {
        var border = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]);

        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Link"))
            .Set(new PdfName("Rect"), Rect.ToArray())
            .Set(new PdfName("Border"), border)
            // Emit the annotation flags (default /F 4 = Print). ISO 19005-2 §6.3.2 requires the
            // Print bit set and Hidden/Invisible/NoView clear on every non-Popup annotation; a Link
            // is exempt from the appearance-stream requirement but not the flag requirements
            // (verified against veraPDF). The default keeps writer output PDF/A-conformant.
            .Set(new PdfName("F"), new PdfInteger((int)Flags));

        if (Uri is not null)
        {
            // /A << /S /URI /URI (url) >>
            // Percent-encode non-ASCII codepoints (U+0080+) as UTF-8 bytes so the URI
            // is representable as ASCII; avoids lossy Latin-1 '?' substitution.
            var action = new PdfDictionary()
                .Set(new PdfName("S"), new PdfName("URI"))
                .Set(new PdfName("URI"), new PdfLiteralString(
                    System.Text.Encoding.Latin1.GetBytes(PercentEncodeNonAscii(Uri))));
            dict.Set(new PdfName("A"), action);
        }
        else if (DestName is not null)
        {
            // /Dest (name): a byte string looked up in the /Dests name tree (Table 176), not an
            // /A GoTo action: Table 176 does not permit Dest beside A, and a link needs only one.
            dict.Set(new PdfName("Dest"), new PdfLiteralString(DestinationName.Encode(DestName)));
        }
        else if (destPageRef is not null)
        {
            // /Dest [pageRef /XYZ left top null]
            var dest = new PdfArray([
                destPageRef,
                new PdfName("XYZ"),
                new PdfReal(DestLeft),
                new PdfReal(DestTop),
                PdfNull.Instance,
            ]);
            dict.Set(new PdfName("Dest"), dest);
        }

        return dict;
    }

    /// <summary>
    /// Percent-encodes non-ASCII codepoints (U+0080 and above) as their UTF-8 byte sequence,
    /// producing a string that is representable in ASCII (and therefore in Latin-1).
    /// ASCII characters are passed through unchanged.
    /// </summary>
    private static string PercentEncodeNonAscii(string uri)
    {
        var sb = new System.Text.StringBuilder(uri.Length);
        Span<byte> utf8 = stackalloc byte[4]; // max UTF-8 length of a single scalar value
        // Enumerate Unicode scalar values (runes), not UTF-16 code units, so that a
        // codepoint above the BMP (a surrogate pair, e.g. an emoji) is encoded as its
        // full multi-byte UTF-8 sequence rather than two replacement characters.
        foreach (var rune in uri.AsSpan().EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                sb.Append((char)rune.Value);
            }
            else
            {
                var n = rune.EncodeToUtf8(utf8);
                for (var i = 0; i < n; i++)
                    sb.Append('%').Append(utf8[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }
}
