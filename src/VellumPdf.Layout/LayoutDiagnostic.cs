// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace VellumPdf.Layout;

/// <summary>
/// One condition a save noticed and kept going through, listed in
/// <see cref="Document.Diagnostics"/>
/// instead of thrown.
/// </summary>
/// <remarks>
/// A save reports each <see cref="Code"/> at most once per subject and counts the repeats in
/// <see cref="Count"/>, so a document that substitutes the same character a million times yields
/// one item. Nothing here retains caller text: <see cref="Message"/> names a code point or a band,
/// not the string that held it.
/// <para>Instances are created by the library. Do not parse <see cref="Message"/> or
/// <see cref="ToString"/>; branch on <see cref="Code"/>.</para>
/// </remarks>
public sealed class LayoutDiagnostic
{
    /// <summary>The condition observed.</summary>
    public LayoutDiagnosticCode Code { get; }

    /// <summary>
    /// How seriously to take <see cref="Code"/>, looked up from a fixed table keyed on the code,
    /// not chosen per call site.
    /// </summary>
    public LayoutDiagnosticSeverity Severity { get; }

    /// <summary>A description of what was observed. Not a compatibility contract.</summary>
    public string Message { get; }

    /// <summary>
    /// The clause of a standard the input breaks, such as <c>"ISO 19005-2:2011, 6.2.11.8"</c>, or
    /// <see langword="null"/> when no clause of a standard the library holds or the document claims
    /// is broken.
    /// </summary>
    public string? Clause { get; }

    /// <summary>The kind of the element the first occurrence was found in.</summary>
    public LayoutElementKind ElementKind { get; }

    /// <summary>
    /// The zero-based position, in the order added, of the element the first occurrence was found
    /// in, or <see langword="null"/> for a running band or a document-wide item.
    /// </summary>
    public int? ElementIndex { get; }

    /// <summary>
    /// The one-based page of the first occurrence, or <see langword="null"/> when the item is not
    /// bound to a page. For <see cref="LayoutDiagnosticCode.BandTruncated"/> it is the page that
    /// lost the most, as <see cref="Elements.BandTruncationWarning.PageNumber"/> is.
    /// </summary>
    public int? PageNumber { get; }

    /// <summary>
    /// How many times the condition occurred for this subject, saturating at
    /// <see cref="int.MaxValue"/>.
    /// </summary>
    public int Count { get; internal set; }

    internal LayoutDiagnostic(
        LayoutDiagnosticCode code,
        string message,
        string? clause,
        LayoutElementKind elementKind,
        int? elementIndex,
        int? pageNumber,
        int count)
    {
        Code = code;
        Severity = SeverityOf(code);
        Message = message;
        Clause = clause;
        ElementKind = elementKind;
        ElementIndex = elementIndex;
        PageNumber = pageNumber;
        Count = count;
    }

    /// <summary>The fixed severity of each code.</summary>
    internal static LayoutDiagnosticSeverity SeverityOf(LayoutDiagnosticCode code) => code switch
    {
        LayoutDiagnosticCode.DiagnosticsDropped => LayoutDiagnosticSeverity.Warning,
        LayoutDiagnosticCode.PreformattedLineWrapped => LayoutDiagnosticSeverity.Warning,
        LayoutDiagnosticCode.KeepWithNextRelaxed => LayoutDiagnosticSeverity.Warning,
        LayoutDiagnosticCode.KeepTogetherRelaxed => LayoutDiagnosticSeverity.Warning,
        LayoutDiagnosticCode.MinLinesRelaxed => LayoutDiagnosticSeverity.Warning,
        _ => LayoutDiagnosticSeverity.Error,
    };

    /// <summary>
    /// Formats as <c>Severity Code Kind[Index] page N: Message</c>, leaving out the index and the
    /// page when they are null. Not a parsing contract.
    /// </summary>
    public override string ToString()
    {
        var index = ElementIndex is { } i ? string.Create(CultureInfo.InvariantCulture, $"[{i}]") : "";
        var page = PageNumber is { } p ? string.Create(CultureInfo.InvariantCulture, $" page {p}") : "";
        return $"{Severity} {Code} {ElementKind}{index}{page}: {Message}";
    }
}
