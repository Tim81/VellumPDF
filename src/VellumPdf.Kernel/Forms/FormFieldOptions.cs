// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Forms;

/// <summary>
/// Optional appearance and behaviour settings for AcroForm fields.
/// </summary>
public sealed class FormFieldOptions
{
    /// <summary>Font size in points for the field's default appearance. Default is 12.</summary>
    /// <remarks>
    /// A non-finite size is refused. <see cref="PdfDocument.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> naming the field and the size, because neither
    /// <c>NaN</c> nor either infinity is a PDF number (ISO 32000-2, 7.3.3) and so none of them is
    /// a valid <c>Tf</c> operand.
    /// <para>Attention: a size at or below zero is <b>not</b> refused, and the field types do not
    /// agree on what they do with it. <see cref="PdfDocument.AddRadioButtonGroup"/>'s on-state
    /// appearance substitutes a size derived from the widget rectangle whenever this value is not
    /// positive; <see cref="PdfDocument.AddTextField"/>, <see cref="PdfDocument.AddCheckBox"/>,
    /// <see cref="PdfDocument.AddChoiceField"/>, and <see cref="PdfDocument.AddPushButton"/> write
    /// it into the appearance stream unchanged. A later major version may reject it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="PdfDocument.Save(System.IO.Stream)"/> rather than from this
    /// property, when the size is not finite.
    /// </exception>
    public double FontSize { get; init; } = 12;

    /// <summary>When true the field is read-only (Ff bit 1).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>When true the field is required (Ff bit 2).</summary>
    public bool Required { get; init; }

    /// <summary>When true a text field accepts multi-line input (Ff bit 13).</summary>
    public bool Multiline { get; init; }
}
