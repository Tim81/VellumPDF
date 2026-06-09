// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Forms;

/// <summary>
/// Describes one option in a radio button group: the page it lives on, the
/// widget rectangle, and the export value that identifies the option.
/// </summary>
public readonly record struct RadioOption(PdfPage Page, PdfRectangle Rect, string ExportValue);

/// <summary>
/// Internal discriminated-union that captures one registered AcroForm field.
/// Sealed subclasses carry the per-type payload. Built by PdfDocument, consumed
/// by AcroFormBuilder during Save.
/// </summary>
internal abstract class PdfFormField
{
    internal PdfPage Page { get; }
    internal string Name { get; }
    internal PdfRectangle Rect { get; }
    internal FormFieldOptions Options { get; }

    private protected PdfFormField(PdfPage page, string name, PdfRectangle rect, FormFieldOptions options)
    {
        Page = page;
        Name = name;
        Rect = rect;
        Options = options;
    }

    // ── Concrete field types ─────────────────────────────────────────────────

    internal sealed class TextField(PdfPage page, string name, PdfRectangle rect, string value, FormFieldOptions options)
        : PdfFormField(page, name, rect, options)
    {
        internal string Value { get; } = value;
    }

    internal sealed class CheckBoxField(PdfPage page, string name, PdfRectangle rect, bool checkedState, FormFieldOptions options)
        : PdfFormField(page, name, rect, options)
    {
        internal bool CheckedState { get; } = checkedState;
    }

    internal sealed class ChoiceField(PdfPage page, string name, PdfRectangle rect, IReadOnlyList<string> choiceOptions, string? selected, bool combo, FormFieldOptions options)
        : PdfFormField(page, name, rect, options)
    {
        internal IReadOnlyList<string> ChoiceOptions { get; } = choiceOptions;
        internal string? Selected { get; } = selected;
        internal bool Combo { get; } = combo;
    }

    /// <summary>
    /// A radio button group. The field object has no /Rect of its own; each
    /// <see cref="RadioOptions"/> entry becomes a kid widget annotation.
    /// </summary>
    internal sealed class RadioGroupField(string name, IReadOnlyList<RadioOption> radioOptions, string? selectedExportValue, FormFieldOptions options)
        : PdfFormField(radioOptions.Count > 0 ? radioOptions[0].Page : null!, name, new PdfRectangle(0, 0, 0, 0), options)
    {
        internal IReadOnlyList<RadioOption> RadioOptions { get; } = radioOptions;
        internal string? SelectedExportValue { get; } = selectedExportValue;
    }

    /// <summary>
    /// A push button. The widget and field object are merged (combined annotation+field).
    /// </summary>
    internal sealed class PushButtonField(PdfPage page, string name, PdfRectangle rect, string caption, FormFieldOptions options)
        : PdfFormField(page, name, rect, options)
    {
        internal string Caption { get; } = caption;
    }
}
