// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>A chapter, appendix or section heading, as the contents list needs it.</summary>
/// <param name="Number">The number or the appendix name, such as "3.2" or "Appendix B".</param>
/// <param name="Title">The title.</param>
/// <param name="Level">0 for a chapter or an appendix, 1 for a section.</param>
/// <param name="Language">A BCP 47 tag when the heading is not in English.</param>
/// <param name="Anchor">The name of the heading's anchor.</param>
public sealed record OutlineEntry(string Number, string Title, int Level, string? Language, string Anchor)
{
    /// <summary>The heading as it is printed: number, then title.</summary>
    public string Label => Number.StartsWith("Appendix", StringComparison.Ordinal)
        ? $"{Number}. {Title}"
        : $"{Number}  {Title}";
}
