// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout;

/// <summary>How seriously <see cref="Document.Diagnostics"/> rates one item.</summary>
/// <remarks>
/// Strict mode (<see cref="Document.StrictDiagnostics"/>) refuses a save that reports an
/// <see cref="Error"/> and ignores a <see cref="Warning"/>.
/// </remarks>
public enum LayoutDiagnosticSeverity
{
    /// <summary>
    /// The library handled the input correctly, and the caller may still want to know about it.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// The save kept the input but lost, misplaced or substituted content, or wrote a file that
    /// fails a conformance level the document claims.
    /// </summary>
    Error = 2,
}
