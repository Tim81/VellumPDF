// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Forms;

/// <summary>
/// Optional appearance and behaviour settings for AcroForm fields.
/// </summary>
public sealed class FormFieldOptions
{
    /// <summary>Font size in points for the field's default appearance. Default is 12.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>When true the field is read-only (Ff bit 1).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>When true the field is required (Ff bit 2).</summary>
    public bool Required { get; init; }

    /// <summary>When true a text field accepts multi-line input (Ff bit 13).</summary>
    public bool Multiline { get; init; }
}
