// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Forms;

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
}
