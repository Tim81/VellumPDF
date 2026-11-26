// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Marks a renderer of this library that lives outside this assembly and never lays a list out,
/// so that a document that holds it is not treated as one that holds a renderer of a caller's.
/// </summary>
/// <remarks>
/// Internal, so that only the library's own packages can set it: a renderer of a caller's cannot
/// claim to be the library's own. A 2.x compatibility device, removed in 3.0 (#688), with the rule
/// it is an exception to (<see cref="DocumentRenderer"/>).
/// </remarks>
internal interface IListFreeRenderer
{
}
