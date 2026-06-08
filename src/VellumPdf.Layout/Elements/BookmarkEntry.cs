// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A pending bookmark to be placed at the position of the next rendered element.
/// </summary>
internal sealed class BookmarkEntry(string Title, int Level)
{
    public string Title { get; } = Title;
    public int Level { get; } = Level;
}
