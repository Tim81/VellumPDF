// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// A renderer with two layouts: the one it uses, which can take more page continuations than one
/// element may, and a second that takes what the table took before the first existed. After the
/// first call the document asks for a bound on the continuations of the first. When the bound
/// allows it to reach the limit, the document places the element once without drawing, and if
/// that reaches the limit it uses the second layout instead.
/// </summary>
/// <remarks>
/// Implement it explicitly. The built-in renderers are public classes, so an implicit
/// implementation would be a new public member.
/// </remarks>
internal interface IContinuationGuard
{
    /// <summary>
    /// An upper bound on the page continuations that placing the renderer needs, known once it has
    /// been laid out for the first time in the area the document offers. Zero when it knows none.
    /// </summary>
    long ContinuationBound { get; }

    /// <summary>
    /// A renderer that lays the same element out in the second way, from the first area on. It
    /// carries nothing the first layout decided.
    /// </summary>
    IRenderer WithoutBreaks();
}
