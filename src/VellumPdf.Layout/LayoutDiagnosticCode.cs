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
/// switch exhaustively over this enumeration</b>: a value this release does not name can reach you.
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
    //   105 SymbolFontMeasuredAsZero (Error), 203 ListIndentClamped, 204 ListDepthCapped, 205
    //   ListEntriesCapped, 206 ListEntrySkipped (all Error), 301 ControlCharacterInCell
    //   (Warning), 302 SurplusCell (Error), 402 UnknownBandToken, one 7xx member for a link
    //   dropped from a cell or band under a PDF/UA claim, and the 8xx block for pagination reports.

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
    /// A character that an embedded font has no glyph for was written as glyph 0, the
    /// <c>.notdef</c> glyph. Each distinct character in each font is one item, whose
    /// <see cref="LayoutDiagnostic.Count"/> is the number of times it was written, and whose
    /// message names the character and the font.
    /// </summary>
    /// <remarks>
    /// <b>Strict mode refuses it.</b> Under a PDF/A claim the item's
    /// <see cref="LayoutDiagnostic.Clause"/> is <c>ISO 19005-2:2011, 6.2.11.8</c>, which forbids a
    /// reference to the <c>.notdef</c> glyph from any text-showing operator, and under PDF/UA-1 it
    /// is <c>ISO 14289-1:2014, 7.21.8</c>. With no claim the clause is null, because plain PDF
    /// permits showing glyph 0 and the item is still reported. The save writes the glyph anyway,
    /// and the font's ToUnicode map lists glyph 0 once, as U+FFFD, when a character was mapped to
    /// it. The entry is there because glyph 0 is itself what those clauses refuse; glyph ids a
    /// renderer passes itself add none.
    /// <para>The built-in elements, running bands and list markers report the character. A custom
    /// renderer that writes glyph ids with
    /// <see cref="VellumPdf.Canvas.PdfCanvas.ShowGlyphs(System.ReadOnlySpan{ushort})"/> on the
    /// document's canvas is reported with the character unknown, named by the resource name of the
    /// last font it selected. Glyph 0 written to a canvas that is not the document's, or by a
    /// renderer's own bytes on the page, is not seen. Text a font draws through a stand-in glyph,
    /// such as U+202F in a font without it, is not glyph 0 and is not reported. A Standard 14
    /// font has no glyph ids here: a character it cannot encode is
    /// <see cref="UnencodableCharacter"/>, and a control character such as a tab is written as
    /// its WinAnsi code, which the font has no glyph for and which is not reported.</para>
    /// <para>What is drawn is whatever the font's <c>.notdef</c> glyph holds. <b>Do not pass</b>
    /// text the font cannot draw to a PDF/A or PDF/UA document; give the element a font that covers
    /// it.</para>
    /// </remarks>
    NotdefGlyph = 102,

    /// <summary>
    /// A character that the font's encoding cannot represent was written as <c>?</c>. That is a
    /// character outside WinAnsiEncoding in Standard 14 text, and in text a custom renderer passes
    /// to the canvas whatever font is selected. Each distinct scalar value is one item, whose
    /// <see cref="LayoutDiagnostic.Count"/> is the number of times it was substituted. It mirrors
    /// <see cref="Document.TextEncodingWarnings"/>, except that it leaves out the characters past
    /// the channel's limit of 1,000 items, which <see cref="DiagnosticsDropped"/> counts, and the
    /// text the library wrote itself.
    /// </summary>
    /// <remarks>
    /// A character outside the Basic Multilingual Plane is one item and is written as two question
    /// marks. Text is substituted, so strict mode refuses it. Text the library wrote itself, such
    /// as a list marker, is not listed when the document draws the list; a list drawn through a
    /// <see cref="Core.DrawContext"/> you built can list it.
    /// </remarks>
    UnencodableCharacter = 103,

    /// <summary>
    /// A line of a <see cref="Elements.Preformatted"/> was wider than the content box, so the save
    /// split it into segments and drew a continuation marker after each but the last.
    /// <see cref="LayoutDiagnostic.Count"/> is the number of source lines that needed a marker.
    /// </summary>
    /// <remarks>
    /// No character is lost, so this is a warning and strict mode saves. It is reported once per
    /// element, from the page its first line is drawn on, and its page number is that page. It is
    /// reported when the block is drawn from its first line, and a
    /// <see cref="Core.DrawContext"/> you build carries no channel, so a block drawn through one
    /// reports nothing.
    /// </remarks>
    PreformattedLineWrapped = 104,

    /// <summary>
    /// A <see cref="Elements.Paragraph"/> holds a tab stop beyond the width its renderer was laid
    /// out at, and a run of it that has a style holds a tab, so the stop was treated as absent.
    /// <see cref="LayoutDiagnostic.Count"/> is the number of stops beyond that width.
    /// </summary>
    /// <remarks>
    /// The text is not where the stop said, so strict mode refuses it. A paragraph the document
    /// lays out itself never reaches this: the save refuses a stop beyond the content width before
    /// the layout starts, with <see cref="InvalidOperationException"/>. It arises from a
    /// <see cref="Rendering.ParagraphRenderer"/> that a renderer of yours lays out in a narrower
    /// area than the page's. A tab then uses the next stop within the width, and advances as one
    /// space only when no such stop lies to its right. It is reported when the paragraph is drawn
    /// from its first line, so a renderer you build to start at a later line reports nothing.
    /// </remarks>
    TabStopBeyondWidth = 106,

    /// <summary>
    /// A list item that is a child of a child, or deeper, holds a null text, and the save drew
    /// nothing of it. <see cref="LayoutDiagnostic.Count"/> is the number of such items the list's
    /// element reached first. An item is counted once in the whole save, under the first list the
    /// document draws, in drawing order, that reaches it, however many second-level items or lists
    /// reach it.
    /// </summary>
    /// <remarks>
    /// Items below the second level are not laid out or drawn, so this saves. Strict mode refuses
    /// it. Only a list the document draws from its first item takes part. A
    /// <see cref="Rendering.ListRenderer"/> you build to start at an item other than the first, or
    /// that a renderer of yours lays out and does not draw, reports nothing and leaves its items
    /// for a list that does. Among the lists that take part, the total does not depend on their
    /// order.
    /// </remarks>
    NullGrandchildText = 201,

    /// <summary>
    /// A list item that is another item's child, or deeper, below the top level, was given a null
    /// child with <see cref="Elements.ListItem.AddChild(Elements.ListItem)"/>, and the save skipped
    /// it. <see cref="LayoutDiagnostic.Count"/> is the number of items that hold at least one null
    /// child, not the number of nulls. Each such item is counted once in the whole save, as for
    /// <see cref="NullGrandchildText"/>.
    /// </summary>
    /// <remarks>
    /// Items below the second level are not laid out or drawn, so this saves. Strict mode refuses
    /// it. Only a list the document draws from its first item takes part. A
    /// <see cref="Rendering.ListRenderer"/> you build to start at an item other than the first, or
    /// that a renderer of yours lays out and does not draw, reports nothing and leaves its items
    /// for a list that does, as for <see cref="NullGrandchildText"/>.
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
    /// An element wrote a font selection for a named Standard 14 font while
    /// <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/>, so the file refers to a font that is
    /// not embedded. Each distinct element and font is one item, and
    /// <see cref="LayoutDiagnostic.Count"/> is the number of font selections the element wrote in
    /// that font, which is not the number of runs or lines.
    /// </summary>
    /// <remarks>
    /// <b>Strict mode refuses it.</b> The item's <see cref="LayoutDiagnostic.Clause"/> is <c>ISO
    /// 19005-2:2011, 6.2.11.4.1</c> under a PDF/A claim and <c>ISO 14289-1:2014, 7.21.4.1</c> under
    /// PDF/UA-1; both require the font programs of every font used for rendering to be embedded,
    /// and neither exempts the Standard 14. The save writes the file unchanged.
    /// <see cref="Document.SetDefaultFont(Core.TextStyle)"/> applies only to
    /// <see cref="Document.Add(string, Core.TextStyle?)"/> with a string, so a paragraph, heading,
    /// list, table or running band without its own embedded style reaches this.
    /// <para>A selection is reported when the font operator is written, whether or not any text
    /// follows it, so an empty table cell in Helvetica is reported. That follows the validator,
    /// which flags a selected font even when no glyph is shown; ISO 14289-1, 7.21.4.1, counts a
    /// font as used only when a glyph is referenced from a content stream. Not reported: a Standard
    /// 14
    /// font that a custom renderer selects on the canvas itself, or fetches with
    /// <see cref="Core.DrawContext.GetFont(VellumPdf.Fonts.Standard14)"/> and never selects, a
    /// <see cref="Core.DrawContext"/> you construct, and a <see cref="VellumPdf.Fonts.Standard14"/>
    /// value the enumeration does not name, which makes the save throw
    /// <see cref="IndexOutOfRangeException"/>, and the human-readable text of a one-dimensional
    /// barcode, which is set in <c>Barcode1D.TextFont</c> (Helvetica by default) and selected
    /// without this channel. <b>Do not pass</b> a style with a Standard
    /// 14 font to an element of a PDF/A or PDF/UA document, and set the style of lists, tables and
    /// running bands yourself.</para>
    /// </remarks>
    UnembeddedFont = 601,

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
