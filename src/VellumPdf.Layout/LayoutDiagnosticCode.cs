// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout;

/// <summary>
/// The condition a <see cref="LayoutDiagnostic"/> reports. A save reports each one through
/// <see cref="Document.Diagnostics"/> instead of an exception, and refuses it only when
/// <see cref="Document.StrictDiagnostics"/> is set.
/// </summary>
/// <remarks>
/// <b>The numeric values are append-only.</b> A value is what a caller persists, so a member is
/// never renumbered or reused, and a later release adds members without moving these. <b>Do not
/// switch exhaustively over this enumeration</b>: a value this release does not name can reach
/// you.
/// <para>Values are allocated in hundred-wide blocks: 1xx text and fonts, 2xx lists, 3xx tables,
/// 4xx running bands, 5xx images, 6xx conformance claims, 7xx structure and tagging, 8xx
/// pagination, 9xx the channel's own bookkeeping. Numbers inside a block that this enumeration
/// does not name yet are reserved for later members, so do not assume a gap is free.</para>
/// <para>Each member below says which input it reports. The severity of every member is fixed;
/// see <see cref="LayoutDiagnostic.Severity"/>.</para>
/// </remarks>
public enum LayoutDiagnosticCode
{
    // Reserved, not defined here, so that later members do not collide:
    //   102 NotdefGlyph, 104 PreformattedLineWrapped (Warning), 105 SymbolFontMeasuredAsZero
    //   (Error), 203 ListIndentClamped, 204 ListDepthCapped, 205 ListEntriesCapped, 206
    //   ListEntrySkipped (all Error), 301 ControlCharacterInCell (Warning), 302 SurplusCell
    //   (Error), 402 UnknownBandToken, 601 UnembeddedFont, one 7xx member for a link dropped from
    //   a cell or band under a PDF/UA claim, and the 8xx block for pagination reports.

    /// <summary>
    /// A <see cref="Elements.TextRun"/> with a null style that yields no word, so it saved without
    /// a style. <see cref="LayoutDiagnostic.Count"/> is the number of such runs in the paragraph.
    /// </summary>
    /// <remarks>
    /// A run with a null style that does yield a word is refused by the save with
    /// <see cref="ArgumentNullException"/>, and is not reported here. Strict mode refuses this
    /// one. It is reported when the paragraph is drawn from its first line, so a
    /// <see cref="Rendering.ParagraphRenderer"/> you build to start at a later line reports
    /// nothing.
    /// </remarks>
    NullTextRunStyle = 101,

    /// <summary>
    /// A character that the font's encoding cannot represent was written as <c>?</c>. A Standard
    /// 14 font's WinAnsiEncoding cannot represent every character, and a custom renderer's text
    /// is recorded whatever font is selected. Each distinct scalar value is one item, whose
    /// <see cref="LayoutDiagnostic.Count"/> is the number of times it was substituted. It mirrors
    /// <see cref="Document.TextEncodingWarnings"/>.
    /// </summary>
    /// <remarks>
    /// Text is substituted, so strict mode refuses it. Text the library wrote itself, such as a
    /// list marker, is not listed when the document draws the list; a list drawn through a
    /// <see cref="Core.DrawContext"/> you built can list it.
    /// </remarks>
    UnencodableCharacter = 103,

    /// <summary>
    /// A list item that is a child of a child, or deeper, holds a null text, and the save drew
    /// nothing of it. <see cref="LayoutDiagnostic.Count"/> is the number of such items in the list,
    /// counted once for each second-level item that reaches them, so an item shared by several
    /// parents of one second-level item counts once.
    /// </summary>
    /// <remarks>
    /// Items below the second level are not laid out or drawn, so this saves. Strict mode refuses
    /// it. A <see cref="Rendering.ListRenderer"/> you build to start at an item other than the
    /// first reports nothing.
    /// </remarks>
    NullGrandchildText = 201,

    /// <summary>
    /// A list item that is another item's child, or deeper, below the top level, was given a null
    /// child with <see cref="Elements.ListItem.AddChild(Elements.ListItem)"/>, and the save skipped
    /// it. <see cref="LayoutDiagnostic.Count"/> is the number of items that hold at least one null
    /// child, not the number of nulls, counted as for
    /// <see cref="NullGrandchildText"/>.
    /// </summary>
    /// <remarks>
    /// Items below the second level are not laid out or drawn, so this saves. Strict mode refuses
    /// it. A <see cref="Rendering.ListRenderer"/> you build to start at an item other than the
    /// first reports nothing.
    /// </remarks>
    NullGrandchild = 202,

    /// <summary>
    /// A running band's resolved text was cut to fit the content box. It mirrors
    /// <see cref="Document.BandTruncations"/>, and there is one item per band.
    /// </summary>
    /// <remarks>
    /// Text the caller wrote is lost, so strict mode refuses it.
    /// <see cref="LayoutDiagnostic.PageNumber"/> is the page that lost the most, as it is on
    /// <see cref="Elements.BandTruncationWarning.PageNumber"/>, not the first page cut.
    /// </remarks>
    BandTruncated = 401,

    /// <summary>
    /// The save dropped reports because the channel holds at most 1,000 distinct items.
    /// <see cref="LayoutDiagnostic.Count"/> is the number of occurrences dropped, not the number of
    /// subjects.
    /// </summary>
    /// <remarks>
    /// It is the last item, and present only when something was dropped. It is a warning, but
    /// strict mode still refuses a save whose dropped reports included an error.
    /// </remarks>
    DiagnosticsDropped = 901,
}
