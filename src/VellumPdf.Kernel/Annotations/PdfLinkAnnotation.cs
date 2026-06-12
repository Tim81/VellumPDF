// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Annotations;

/// <summary>
/// Describes a /Link annotation to be attached to a page.
/// Use <see cref="Uri"/> for external hyperlinks or <see cref="DestPage"/> for
/// internal cross-document links.
/// </summary>
public sealed class PdfLinkAnnotation
{
    /// <summary>Annotation bounding rectangle in PDF user-space (points, Y-up).</summary>
    public required PdfRectangle Rect { get; init; }

    /// <summary>External URI action target (mutually exclusive with <see cref="DestPage"/>).</summary>
    public string? Uri { get; init; }

    /// <summary>
    /// Target page for an internal /XYZ destination (mutually exclusive with <see cref="Uri"/>).
    /// Resolved to a page indirect-reference during <see cref="PdfDocument.Save"/>.
    /// </summary>
    public PdfPage? DestPage { get; init; }

    /// <summary>X coordinate of the destination viewport origin (PDF user-space).</summary>
    public double DestLeft { get; init; }

    /// <summary>Y coordinate of the destination viewport origin (PDF user-space).</summary>
    public double DestTop { get; init; }

    /// <summary>Builds the annotation dictionary given the resolved destination page reference.</summary>
    internal PdfDictionary BuildDictionary(PdfIndirectReference? destPageRef)
    {
        var border = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]);

        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Link"))
            .Set(new PdfName("Rect"), Rect.ToArray())
            .Set(new PdfName("Border"), border);

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
        foreach (var c in uri)
        {
            if (c < 0x80)
            {
                sb.Append(c);
            }
            else
            {
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(c.ToString()))
                    sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }
}
