// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout;

/// <summary>Which kind of element a <see cref="LayoutDiagnostic"/> was found in.</summary>
/// <remarks>
/// <b>Do not switch exhaustively over this enumeration.</b> Later releases append members, so a
/// value this release does not name can reach you. The kind is that of the element the document
/// added, not of a renderer the layout builds from it, so the second page of a long heading is
/// still a <see cref="Heading"/>.
/// </remarks>
public enum LayoutElementKind
{
    /// <summary>A document-wide item, or one that belongs to no element.</summary>
    Document = 0,

    /// <summary>A paragraph, including one added from a string.</summary>
    Paragraph = 1,

    /// <summary>A heading.</summary>
    Heading = 2,

    /// <summary>A list.</summary>
    List = 3,

    /// <summary>A table.</summary>
    Table = 4,

    /// <summary>An image.</summary>
    Image = 5,

    /// <summary>A line separator.</summary>
    LineSeparator = 6,

    /// <summary>A pie chart.</summary>
    PieChart = 7,

    /// <summary>The running header band.</summary>
    Header = 8,

    /// <summary>The running footer band.</summary>
    Footer = 9,

    /// <summary>A renderer the library does not know, added with <see cref="Document.Add(VellumPdf.Layout.Core.IRenderer)"/>.</summary>
    Custom = 10,
}
