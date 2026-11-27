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
    //   105 SymbolFontMeasuredAsZero (Error), 204 ListDepthCapped, 205 ListEntriesCapped, 206
    //   ListEntrySkipped (all Error), 302 SurplusCell (Error), 402 UnknownBandToken. The rest of
    //   the 8xx block stays free for later pagination reports.

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
    /// <see cref="UnencodableCharacter"/>, and a control character such as U+0001 is written as
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
    /// reported when the block is drawn from its first line.
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
    /// A table cell holds a tab, a line break or another control white-space character, and the
    /// save left it out of what it drew, because reading it would have left a row that cannot be
    /// cut no room on a page, or would have taken the table more page continuations than one
    /// element may take.
    /// <see cref="LayoutDiagnostic.Count"/> is the number of pages on which the cell was drawn from
    /// its first line, so a header row that repeats on every page counts once per page, and any
    /// other cell counts once, however many pages a cut row spans. The message names the row and
    /// the cell, counted from zero in the order they were added.
    /// </summary>
    /// <remarks>
    /// <b>The words such a character separated run together in the cell, in drawing and in
    /// extracted text, and nothing else is lost, so this is a warning and strict mode saves.</b>
    /// A cell that keeps its breaks is not reported. <see cref="Elements.Table.Cell.Content"/>
    /// says which rows, when, and which cells. <b>Do not pass</b> a repeated header row or a span
    /// group that has no room on a page: put the breaks in rows that can be cut, or shorten the
    /// text. A cell with more lines than a document can hold is the other case: shorten the text
    /// or split the table.
    /// </remarks>
    CellWhiteSpaceDropped = 301,

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
    /// A list's <see cref="Elements.ListElement.Indent"/> left the text of one or more entries no
    /// width beside its gutter, so the save gave that text a width: the width of its widest word,
    /// or all the room right of its marker's inset when the word is wider.
    /// <see cref="LayoutDiagnostic.Count"/> is the number of entries whose gutter was narrowed.
    /// </summary>
    /// <remarks>
    /// The text is drawn, and it may overprint its marker, so strict mode refuses it. It is
    /// reported once for a list the document draws from its first entry; a
    /// <see cref="Rendering.ListRenderer"/> you build to start at a later entry clamps its entries
    /// and reports nothing. An indent that leaves the text a width, however narrow, is not clamped
    /// and not reported. Neither is an entry with no word that measures wider than zero, or one
    /// with a run whose font size or colour channel is not finite, whose text is still discarded.
    /// No entry of a list that is laid out as it was before the clamp is clamped;
    /// <see cref="Elements.ListElement"/> lists the six cases, and
    /// <see cref="ListIndentClampSkipped"/> and <see cref="ListEntryPartDropped"/> report them.
    /// <b>Do not pass</b> an indent that leaves an entry's text no width: 3.0 will refuse it. See
    /// <see cref="Elements.ListElement.Indent"/>.
    /// </remarks>
    ListIndentClamped = 203,

    /// <summary>
    /// A list the document laid out without two changes, the clamp described at
    /// <see cref="ListIndentClamped"/> and the moving of entries that do not fit what is left of a
    /// page to the next page, and that left as it was at least one entry those changes would have
    /// altered. <see cref="LayoutDiagnostic.Count"/> is the number of such entries: those whose
    /// gutter the clamp would have narrowed, and those whose marker or text does not fit what is
    /// left of a page, which the list placed where they fall and the new layout would have moved
    /// to the next page. An entry is counted once. A list with no such entry does not report this
    /// code; the remarks give the exceptions.
    /// </summary>
    /// <remarks>
    /// <b>The text of the entries the clamp would have narrowed is discarded, and only their
    /// markers are drawn.</b> An entry that does not fit what is left of a page is placed there
    /// with the part that fits, as it was, and <see cref="ListEntryPartDropped"/> reports it. The
    /// list then saves, or throws <see cref="InvalidOperationException"/> for the continuation
    /// limit, as a list with every gutter left as <see cref="Elements.ListElement.Indent"/> and the
    /// marker widths give it would and every entry kept where it falls.
    /// <para>Why a list is laid out that way: either laying it out the new way would have needed
    /// more page continuations than one element may take, or the document holds a renderer of
    /// yours, a list entry in a font it cannot draw, or an element after a list that can need about
    /// as many continuations as the limit allows. The three document-wide causes turn every list of
    /// the document back, because a font the document cannot draw makes the save throw where its
    /// text is drawn, an element near the limit is refused or saved according to where it starts,
    /// and a renderer of yours reads where it starts, and the clamp or a moved entry ahead of any
    /// of them can change where that is. The document decides this once, before it lays anything
    /// out, from the elements added to it. The message names every cause it read there: the
    /// continuation limit for the list, or any of the font, the element near the limit and the
    /// renderer of yours.</para>
    /// <para><b>A list that left no such entry does not report this code</b>, whichever cause
    /// applied, so a document whose lists lose nothing saves under strict mode with the bytes it
    /// saves without it. That covers an empty list and a list none of whose entries the clamp would
    /// have narrowed or the new layout would have moved. An entry counts unless drawing it in the
    /// new layout would draw text in a font the document cannot draw: that makes the save throw,
    /// so leaving it as it was gives nothing up.</para>
    /// <para>Three lists depart from the definition above: two do not report this code although
    /// they left such an entry, and one reports it although the new layout would have placed its
    /// entries as the twin did.</para>
    /// <list type="bullet">
    /// <item>A list a renderer of yours holds is laid out as before and does not report this code
    /// or <see cref="ListEntryPartDropped"/>, because the document did not place it.</item>
    /// <item>A <see cref="Rendering.ListRenderer"/> built to start at a later entry does not report
    /// this code, although it can report <see cref="ListEntryPartDropped"/>, so such a list can
    /// save under strict mode although it differs from the new layout.</item>
    /// <item>A list that holds an entry no page holds is placed where each entry falls by the new
    /// layout too. On its own it reports <see cref="ListEntryPartDropped"/> and not this code.
    /// When the continuation limit or a document-wide cause also lays it out as its twin, it
    /// reports this code, because the twin does not look for such an entry and counts that list's
    /// entries as it counts any other's. It reports <see cref="ListEntryPartDropped"/> only for a
    /// part it placed and could not draw. An entry whose text the clamp would have narrowed is
    /// counted here instead, so the same entries can report 208 alone, and 207 alone once a
    /// renderer of yours is added to the document.</item>
    /// </list>
    /// <para>A moved entry is counted by the piece of the list that starts it, in the layout the
    /// list was given, so the count of a list that runs over several pages adds up its pieces and
    /// the page is that of the first piece that reports. The count can differ from the number of
    /// entries the new layout would move, because once the first of them has moved the others fall
    /// elsewhere. The report is one item for each such list. Strict mode refuses this code.
    /// <b>Do not pass</b> an indent that leaves an entry's text no width, or a font the document
    /// cannot draw: 3.0 will refuse both. See <see cref="Elements.ListElement"/>.</para>
    /// </remarks>
    ListIndentClampSkipped = 207,

    /// <summary>
    /// A list entry was placed with its marker or its text not drawn, because it does not fit where
    /// it falls and the list did not move it to the next page.
    /// <see cref="LayoutDiagnostic.Count"/> is the number of such entries in the pieces the
    /// document drew.
    /// </summary>
    /// <remarks>
    /// <b>A part of the entry is missing from the page.</b> A line that does not fit is not drawn,
    /// while a marker or a text that wraps keeps the lines that fit. An entry is left where it
    /// falls, and reported, when its list is laid out as it was before the clamp and the moving of
    /// entries and the document placed the list; <see cref="Elements.ListElement"/> lists the
    /// six cases. In one of them no page holds the entry, because a line of its marker, or a line
    /// of its text at the width the clamp would give it, is taller than a page. A list that a
    /// renderer of yours lays out is laid out that way too, and reports nothing: the document did
    /// not place it. An entry whose text has no width is not reported, and neither is one that
    /// moves to the next page whole. It is reported from the piece of the list that holds the
    /// entry, once per list however many entries it counts. Strict mode refuses this code.
    /// <b>Do not</b> give an entry a marker or a line of text taller than a page: the part that
    /// does not fit is not drawn.
    /// </remarks>
    ListEntryPartDropped = 208,

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
    /// A link in the <see cref="Core.TextStyle.LinkUri"/> of a table cell, of
    /// <see cref="Elements.Table.TableElement.DefaultCellStyle"/> or of a running band was not
    /// written, because the document's conformance is PDF/UA-1, where an untagged link breaks ISO
    /// 14289-1, 7.18.5. <see cref="LayoutDiagnostic.Count"/> is the number of links not written:
    /// the lines of the table's cells that would have carried one, or, for a band, the pages it was
    /// drawn on.
    /// </summary>
    /// <remarks>
    /// The text is drawn, and only the link is left out, as it was before cells and bands wrote
    /// links. It is reported once per table element and once per band kind. <b>Strict mode refuses
    /// it.</b> A link in a paragraph, heading or list item is written whatever the conformance, and
    /// is not reported here. <b>Do not pass</b> a link to a cell or band of a PDF/UA-1 document; a
    /// later major version refuses it where the style is set.
    /// </remarks>
    LinkNotWritten = 701,

    /// <summary>
    /// A run of elements that asked to stay with the element after it, by
    /// <c>KeepWithNext</c>, was placed without that request, because the group of elements did not
    /// hold together on a page of its own, or the run already started at the top of a page, where
    /// moving it would leave that page empty. The element is the first of the run, and
    /// <see cref="LayoutDiagnostic.PageNumber"/> the page that element's first piece is placed
    /// on, after its own keep-together or line count has moved it.
    /// </summary>
    /// <remarks>
    /// Nothing the caller wrote is lost, so this is a warning and strict mode saves. When this
    /// request conflicts with a keep-together or a line count, this one is dropped and the other
    /// holds. The drawing pass reports it, and a pass that only measures reports nothing.
    /// </remarks>
    KeepWithNextRelaxed = 801,

    /// <summary>
    /// An element that asked to stay whole, by <c>KeepTogether</c>, was split across pages because
    /// it did not fit on an empty page, or because it already started at the top of one, where
    /// moving it gains nothing. <see cref="LayoutDiagnostic.PageNumber"/> is the page the element
    /// starts on.
    /// </summary>
    /// <remarks>
    /// Nothing the caller wrote is lost, so this is a warning and strict mode saves. An element
    /// that was moved to the next page, or that fitted where it stood, reports nothing.
    /// </remarks>
    KeepTogetherRelaxed = 802,

    /// <summary>
    /// A paragraph that set <c>MinLinesBeforeBreak</c> or <c>MinLinesAfterBreak</c> was split where
    /// the page ended, because no break inside it leaves that many lines on both sides, and it
    /// already started at the top of a page. <see cref="LayoutDiagnostic.PageNumber"/> is the page
    /// of the first such split. Each further split of the same paragraph adds to
    /// <see cref="LayoutDiagnostic.Count"/>.
    /// </summary>
    /// <remarks>
    /// Nothing the caller wrote is lost, so this is a warning and strict mode saves. A paragraph
    /// that was moved to the next page to meet the counts reports nothing.
    /// </remarks>
    MinLinesRelaxed = 803,

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
