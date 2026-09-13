// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Forms;

/// <summary>
/// Optional appearance and behaviour settings for AcroForm fields.
/// </summary>
public sealed class FormFieldOptions
{
    /// <summary>Font size in points for the field's default appearance. Must be finite. Default is 12.</summary>
    /// <remarks>
    /// A non-finite size is refused. <see cref="PdfDocument.Save(System.IO.Stream)"/>,
    /// <see cref="PdfDocument.SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>,
    /// and <see cref="PdfDocument.PrepareForSigning(SignaturePlaceholderOptions)"/> each throw
    /// <see cref="InvalidOperationException"/> naming the field and the size, because neither
    /// <c>NaN</c> nor either infinity is a PDF number (ISO 32000-2, 7.3.3) and so none of them is
    /// a valid <c>Tf</c> operand.
    /// <para><b>Attention</b>: a size at or below zero is <b>not</b> refused. All five field types
    /// round the value to three decimal places, so a positive size below <c>0.0005</c> is written
    /// as <c>0</c>. At or below zero they diverge:
    /// <see cref="PdfDocument.AddRadioButtonGroup"/>'s on-state appearance substitutes a size
    /// derived from the widget rectangle, and the other four write the value as given. They also
    /// differ at every value in where the size lands, which the next paragraph sets out.</para>
    /// <para>A written zero means two different things, depending on which writer reads it. In a
    /// field's own <c>/DA</c> string, ISO 32000-2, 12.7.4.3 says a zero size means the font "shall
    /// be auto-sized", computed as an implementation-dependent function, so a consumer that
    /// regenerates the appearance from <c>/DA</c> enlarges the text rather than hiding it. Only
    /// <see cref="PdfDocument.AddTextField"/> and <see cref="PdfDocument.AddChoiceField"/> write a
    /// <c>/DA</c> from this value; a check box and a push button write none, and a radio group's
    /// is a fixed auto-size string. In the appearance stream's own <c>Tf</c> operand, 9.3.1
    /// (Table 103) says "zero sized text shall not mark or clip any pixels", so the text this
    /// library draws is invisible at that size. The widget is not: a text field, a choice field
    /// and a push button still paint their background and border, which no font size reaches. A
    /// check box and a radio group draw no background, so those two really do vanish. A later
    /// major version will reject a size at or below zero.</para>
    /// <para>A refused save leaves the document unusable. The validation runs after
    /// <see cref="PdfDocument.Save(System.IO.Stream)"/> marks the document written, so there is
    /// no fixing this value and calling <see cref="PdfDocument.Save(System.IO.Stream)"/> again;
    /// a second call reports that the document has already been written. Correct the value on a
    /// new <see cref="PdfDocument"/> instead.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="PdfDocument.Save(System.IO.Stream)"/>,
    /// <see cref="PdfDocument.SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>,
    /// or <see cref="PdfDocument.PrepareForSigning(SignaturePlaceholderOptions)"/>, whichever
    /// call serialises the field, rather than from this property, when the size is not finite.
    /// </exception>
    public double FontSize { get; init; } = 12;

    /// <summary>When true the field is read-only (Ff bit 1).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>When true the field is required (Ff bit 2).</summary>
    public bool Required { get; init; }

    /// <summary>When true a text field accepts multi-line input (Ff bit 13).</summary>
    public bool Multiline { get; init; }
}
