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
    /// A non-finite size is refused. <see cref="PdfDocument.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> naming the field and the size, because neither
    /// <c>NaN</c> nor either infinity is a PDF number (ISO 32000-2, 7.3.3) and so none of them is
    /// a valid <c>Tf</c> operand.
    /// <para>Attention: a size at or below zero is <b>not</b> refused, and the field types do not
    /// agree on what they do with it. <see cref="PdfDocument.AddRadioButtonGroup"/>'s on-state
    /// appearance substitutes a size derived from the widget rectangle whenever this value is not
    /// positive; <see cref="PdfDocument.AddTextField"/>, <see cref="PdfDocument.AddCheckBox"/>,
    /// <see cref="PdfDocument.AddChoiceField"/>, and <see cref="PdfDocument.AddPushButton"/> round
    /// it to three decimal places rather than writing it unchanged. A positive value below
    /// <c>0.0005</c> rounds to <c>0</c>, so <c>1e-7</c> writes the same zero-width <c>Tf</c>
    /// operand as zero itself, and because <c>1e-7</c> is still greater than zero, the radio
    /// group's substitution above does not fire for it either. A later major version may reject
    /// a size at or below zero.</para>
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
