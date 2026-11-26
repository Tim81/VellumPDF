// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// A renderer whose element asks for a gap between it and the element placed after it.
/// <see cref="DocumentRenderer"/> reads it from each top-level renderer it places. The pieces a
/// split returns are never offered to it as top-level elements, so the value only ever comes from
/// the element the caller added.
/// </summary>
/// <remarks>
/// Implement it explicitly. The built-in renderers are public classes, so an implicit
/// implementation would be a new public member. 3.0 reads the bottom margin instead and removes
/// this interface with <c>SpaceAfter</c> (#704).
/// </remarks>
internal interface ISpaceAfterSource
{
    /// <summary>The gap in points: finite and not negative, as the element's accessor guarantees.</summary>
    double SpaceAfter { get; }
}
