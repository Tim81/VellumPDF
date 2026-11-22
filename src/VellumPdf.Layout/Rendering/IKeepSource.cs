// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// A renderer whose element asks that it stay with what follows it, or in one piece.
/// <see cref="DocumentRenderer"/> reads both from each top-level renderer it places. The overflow
/// pieces a split returns are never offered to it as top-level elements, so a flag only ever
/// applies to the element the caller added.
/// </summary>
/// <remarks>
/// Implement it explicitly. The built-in renderers are public classes, so an implicit
/// implementation would be a new public member.
/// </remarks>
internal interface IKeepSource
{
    bool KeepWithNext { get; }

    bool KeepTogether { get; }
}
