// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Marks a renderer of this library that never lays a list out, so that a document does not
/// lay out once more to find out what such a renderer holds.
/// </summary>
/// <remarks>
/// Internal, so that only the library's own packages can set it: a renderer of a caller's cannot
/// claim to hold no list. A 2.x compatibility device, removed in 3.0 (#688), with the extra layout
/// it spares (<see cref="DocumentRenderer"/>).
/// </remarks>
internal interface IListFreeRenderer
{
}
