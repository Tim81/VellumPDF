// Copyright 2026 Timothy van der Ham (@Tim81)
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
    public required double Llx { get; init; }
    public required double Lly { get; init; }
    public required double Urx { get; init; }
    public required double Ury { get; init; }

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
        var rect = new PdfArray([
            new PdfReal(Llx), new PdfReal(Lly), new PdfReal(Urx), new PdfReal(Ury)
        ]);
        var border = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]);

        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Link"))
            .Set(new PdfName("Rect"), rect)
            .Set(new PdfName("Border"), border);

        if (Uri is not null)
        {
            // /A << /S /URI /URI (url) >>
            var action = new PdfDictionary()
                .Set(new PdfName("S"), new PdfName("URI"))
                .Set(new PdfName("URI"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes(Uri)));
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
}
