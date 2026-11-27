// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Images;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using LayoutDocument = VellumPdf.Layout.Document;

namespace VellumPdf.Samples.Manual;

/// <summary>
/// Adds the manual's building blocks to a <see cref="LayoutDocument"/>: numbered headings with
/// anchors, paragraphs written in a small inline markup, lists, tables, callouts and command
/// blocks. Each method is a few lines of Layout API calls, so the chapters read as content.
/// </summary>
/// <remarks>
/// The markup is <c>**bold**</c>, <c>*italic*</c> and <c>`code`</c>. A <c>[[3.2]]</c> is a
/// cross-reference: the words "section 3.2, page N", where the number links to the heading and
/// the library prints the page once the layout has settled. A <c>[[nl:4.5]]</c> is the same in
/// Dutch.
/// </remarks>
public sealed partial class ManualWriter
{
    private static readonly Regex Spaces = MyWhitespace();

    private readonly LayoutDocument _doc;
    private readonly ManualStyles _s;
    private int _chapter;
    private int _section;
    private int _appendix;
    private int _indexAnchors;

    /// <summary>Creates a writer that adds to <paramref name="doc"/>.</summary>
    /// <param name="doc">The document to fill.</param>
    /// <param name="styles">The styles to draw with.</param>
    public ManualWriter(LayoutDocument doc, ManualStyles styles)
    {
        _doc = doc;
        _s = styles;
    }

    /// <summary>
    /// The width of the content box, in points, read from the document's page size and margins.
    /// Mirrored margins swap the two sides and leave the sum as it is.
    /// </summary>
    public double ContentWidth => _doc.PageSize.Width - _doc.Margins.Left - _doc.Margins.Right;

    /// <summary>The styles the writer draws with.</summary>
    public ManualStyles Styles => _s;

    /// <summary>The headings added so far, for the contents list.</summary>
    public List<OutlineEntry> Outline { get; } = [];

    /// <summary>
    /// The index entries registered so far: a term and the anchor of the element it was
    /// registered on, in document order.
    /// </summary>
    public List<(string Term, string Anchor)> IndexEntries { get; } = [];

    /// <summary>Starts a chapter on the next right-hand page.</summary>
    /// <param name="title">The chapter title.</param>
    /// <param name="language">A BCP 47 tag when the chapter is not in English.</param>
    public void Chapter(string title, string? language = null)
    {
        _chapter++;
        _section = 0;
        AddHeading($"{_chapter}", title, 0, _s.Chapter, language, $"ch{_chapter}", PageBreakMode.Recto,
            new EdgeInsets(0, 0, 0, 0), false);
    }

    /// <summary>Starts an appendix.</summary>
    /// <param name="title">The appendix title.</param>
    /// <param name="breakBefore">
    /// Where the appendix starts; the default is the next right-hand page.
    /// </param>
    public void Appendix(string title, PageBreakMode breakBefore = PageBreakMode.Recto)
    {
        var letter = (char)('A' + _appendix++);
        _section = 0;
        _chapter = 0;
        AddHeading($"Appendix {letter}", title, 0, _s.Chapter, null, $"app-{letter}", breakBefore,
            new EdgeInsets(breakBefore == PageBreakMode.None ? 24 : 0, 0, 0, 0), true);
    }

    /// <summary>Adds a numbered section heading that stays with the text after it.</summary>
    /// <param name="title">The section title.</param>
    /// <param name="language">A BCP 47 tag when the section is not in English.</param>
    /// <param name="index">The index terms this section is the place for.</param>
    public void Section(string title, string? language = null, string[]? index = null)
    {
        _section++;
        var prefix = _chapter > 0 ? $"{_chapter}.{_section}" : $"{(char)('A' + _appendix - 1)}.{_section}";
        var anchor = $"sec-{prefix}";
        foreach (var term in index ?? [])
            IndexEntries.Add((term, anchor));
        AddHeading(prefix, title, 1, _s.Section, language, anchor, PageBreakMode.None,
            new EdgeInsets(16, 0, 0, 0), false);
    }

    /// <summary>Adds an unnumbered subsection heading.</summary>
    /// <param name="title">The subsection title.</param>
    /// <param name="language">A BCP 47 tag when the subsection is not in English.</param>
    public void Sub(string title, string? language = null)
    {
        _doc.Add(new Heading(title, _s.Subsection)
        {
            Level = 2,
            KeepWithNext = true,
            Language = language,
            Margins = new EdgeInsets(10, 0, 0, 0),
        });
    }

    /// <summary>Adds a paragraph written in the inline markup.</summary>
    /// <param name="markup">The text.</param>
    /// <param name="language">A BCP 47 tag when the paragraph is not in English.</param>
    /// <param name="keepWithNext">
    /// Whether the paragraph stays on a page with the element after it.
    /// </param>
    /// <param name="index">The index terms this paragraph is the place for.</param>
    public void P(string markup, string? language = null, bool keepWithNext = false, string[]? index = null)
    {
        _doc.Add(new Paragraph(Runs(markup, _s.Body))
        {
            Anchor = IndexAnchor(index),
            Language = language,
            KeepWithNext = keepWithNext,
            MinLinesBeforeBreak = 2,
            MinLinesAfterBreak = 2,
            Alignment = HorizontalAlignment.Left,
            Margins = new EdgeInsets(6, 0, 0, 0),
        });
    }

    /// <summary>Adds a bulleted list.</summary>
    /// <param name="items">One markup string per item.</param>
    public void Bullets(params string[] items) => AddList(ListStyle.Unordered, null, items);

    /// <summary>Adds a bulleted list in another language.</summary>
    /// <param name="language">A BCP 47 tag for every item.</param>
    /// <param name="items">One markup string per item.</param>
    public void BulletsIn(string language, params string[] items) =>
        AddList(ListStyle.Unordered, language, items);

    /// <summary>Adds a numbered list of steps.</summary>
    /// <param name="items">One markup string per step.</param>
    public void Steps(params string[] items) => AddList(ListStyle.OrderedDecimal, null, items);

    /// <summary>Adds a numbered list of steps in another language.</summary>
    /// <param name="language">A BCP 47 tag for every step.</param>
    /// <param name="items">One markup string per step.</param>
    public void StepsIn(string language, params string[] items) =>
        AddList(ListStyle.OrderedDecimal, language, items);

    /// <summary>Adds a numbered list in which one step has a nested bulleted list.</summary>
    /// <param name="steps">One markup string per step.</param>
    /// <param name="nestedUnder">The zero-based step that gets the nested items.</param>
    /// <param name="nested">The nested items.</param>
    public void StepsWithNested(string[] steps, int nestedUnder, params string[] nested)
    {
        var list = new ListElement(ListStyle.OrderedDecimal)
        {
            DefaultStyle = _s.Body,
            Indent = 20,
            Margins = new EdgeInsets(2, 0, 0, 0),
        };
        for (var i = 0; i < steps.Length; i++)
        {
            var item = new ListItem(Runs(steps[i], _s.Body));
            if (i == nestedUnder)
            {
                foreach (var n in nested)
                    item.AddChild(new ListItem(Runs(n, _s.Body)));
            }

            list.Add(item);
        }

        _doc.Add(list);
    }

    /// <summary>
    /// Adds a small note that names a workaround the sample needed because the Layout API cannot
    /// yet do something. The note is on the page so that the gap shows to the reader of the
    /// sample.
    /// </summary>
    /// <param name="text">What is missing, and what the manual does instead.</param>
    public void Placeholder(string text) =>
        _doc.Add(new Paragraph($"Placeholder: {Collapse(text)}", _s.Small)
        {
            Margins = new EdgeInsets(4, 0, 0, 0),
        });

    /// <summary>Adds a shaded box with a label, for a warning or a hint.</summary>
    /// <param name="label">A short label such as NOTE or CAUTION.</param>
    /// <param name="text">The text of the box, in the inline markup.</param>
    /// <param name="language">A BCP 47 tag when the text is not in English.</param>
    public void Callout(string label, string text, string? language = null)
    {
        // Workaround: the Layout API has no shaded paragraph, so the box is a one-cell table (the
        // note-box issue, #703). A screen reader announces it as a table. The label is a bold run
        // of its own, which a cell built from runs allows.
        var table = new TableElement
        {
            BorderWidth = 0,
            Margins = new EdgeInsets(8, 0, 0, 0),
            KeepTogether = true,
        };
        table.SetColumnWidths(ContentWidth);
        List<TextRun> runs = [new TextRun(label, _s.TableBold), new TextRun("   ", _s.TableBody)];
        runs.AddRange(CellRuns(Collapse(text), _s.TableBody));
        table.AddRow().AddCell(new Cell(runs)
        {
            Background = ManualStyles.Tint,
            Padding = new EdgeInsets(7, 9, 7, 9),
            Style = _s.TableBody,
            Language = language,
        });
        _doc.Add(table);
    }

    /// <summary>Adds a command block, laid out exactly as written.</summary>
    /// <param name="text">The block; tabs move to the next multiple of eight columns.</param>
    /// <param name="anchor">A named destination for the block, or null.</param>
    public void CommandBlock(string text, string? anchor = null) =>
        _doc.Add(new Preformatted(text, _s.CodeBlock)
        {
            Anchor = anchor,
            KeepTogether = true,
            TabWidth = 8,
            Margins = new EdgeInsets(4, 0, 0, 0),
        });

    /// <summary>
    /// Adds a table whose first row is a header that repeats on every page the table covers.
    /// </summary>
    /// <param name="widths">
    /// The relative column widths; they are scaled to fill the content width.
    /// </param>
    /// <param name="header">The header cells.</param>
    /// <param name="rows">
    /// The body rows, each cell in the inline markup. A line feed in a cell is a line break.
    /// </param>
    /// <param name="codeColumn">The zero-based column drawn in monospace, or -1 for none.</param>
    /// <param name="anchor">A named destination for the table, or null.</param>
    /// <param name="language">A BCP 47 tag when the table is not in English.</param>
    public void Table(
        double[] widths, string[] header, IEnumerable<string[]> rows, int codeColumn = -1,
        string? anchor = null, string? language = null)
    {
        var table = new TableElement
        {
            BorderColor = ManualStyles.Grey,
            BorderWidth = 0.4,
            Margins = new EdgeInsets(6, 0, 0, 0),
            Anchor = anchor,
        };
        // A table needs widths in points, and the API has no "fill the text width", so the
        // proportions given here are scaled by hand.
        var scale = ContentWidth / widths.Sum();
        table.SetColumnWidths([.. widths.Select(w => w * scale)]);
        var head = new Row { IsHeader = true, Background = ManualStyles.Brand };
        table.Add(head);
        foreach (var h in header)
        {
            head.AddCell(new Cell(Plain(h))
            {
                Style = _s.TableHead,
                Padding = new EdgeInsets(4, 5, 4, 5),
                Language = language,
            });
        }

        var zebra = false;
        foreach (var row in rows)
        {
            var r = new Row { Background = zebra ? new ColorRgb(0.96, 0.97, 0.97) : null };
            table.Add(r);
            zebra = !zebra;
            for (var c = 0; c < row.Length; c++)
            {
                var style = c == codeColumn ? _s.TableCode : _s.TableBody;
                r.AddCell(new Cell(CellRuns(row[c], style))
                {
                    Style = style,
                    Padding = new EdgeInsets(3.5, 5, 3.5, 5),
                    Language = language,
                });
            }
        }

        _doc.Add(table);
    }

    /// <summary>Adds a figure on a page of its own, scaled to fit the page.</summary>
    /// <param name="png">The bytes of the image.</param>
    /// <param name="altText">The alternate description.</param>
    /// <param name="anchor">A named destination for the figure.</param>
    public void FigurePage(byte[] png, string altText, string anchor)
    {
        // FitToPage scales the image to the content height of a fresh page, and that is the whole
        // height: the image's own margins are part of the slot it fills, so nothing is left for a
        // caption under it. The figure therefore gets a page to itself, without one.
        _doc.Add(new LayoutImage(PngImageLoader.Load(png))
        {
            FitToPage = true,
            AltText = altText,
            Anchor = anchor,
            Alignment = HorizontalAlignment.Center,
            BreakBefore = PageBreakMode.Page,
        });
    }

    /// <summary>
    /// Adds a line that holds a name, a run of dots and a value at the right margin, the shape of
    /// a price list.
    /// </summary>
    /// <param name="left">The text at the left.</param>
    /// <param name="right">The text at the right margin.</param>
    /// <param name="style">The style of both.</param>
    public void LeaderLine(string left, string right, TextStyle style)
    {
        _doc.Add(new Paragraph($"{left}	{right}", style)
        {
            TabStops = [new TabStop(ContentWidth, TabAlignment.Right, TabLeader.Dots)],
            Margins = new EdgeInsets(2.5, 0, 0, 0),
        });
    }

    /// <summary>
    /// Adds a contents line: the heading's number and title as a link to its anchor, a run of
    /// dots, and the page the heading is on, printed by the library at the right margin.
    /// </summary>
    /// <param name="entry">The heading.</param>
    /// <param name="style">The style of the line.</param>
    /// <param name="indent">The left margin of the line in points.</param>
    public void ContentsLine(OutlineEntry entry, TextStyle style, double indent = 0)
    {
        var line = new List<TextRun>
        {
            new(entry.Label, ManualStyles.WithAnchor(style, entry.Anchor)),
            new("\t", style),
            TextRun.PageReference(entry.Anchor, style),
        };
        _doc.Add(new Paragraph(line)
        {
            Language = entry.Language,
            TabStops = [new TabStop(ContentWidth - indent, TabAlignment.Right, TabLeader.Dots)],
            Margins = new EdgeInsets(2.5, 0, 0, indent),
        });
    }

    /// <summary>
    /// Adds an index line: the term, a run of dots, and the page of each place the term is
    /// registered, each page a link to its place.
    /// </summary>
    /// <param name="term">The term, as it is printed.</param>
    /// <param name="anchors">The anchors of the places, in document order.</param>
    /// <param name="style">The style of the line.</param>
    public void IndexLine(string term, IReadOnlyList<string> anchors, TextStyle style)
    {
        var line = new List<TextRun> { new(term, style), new("\t", style) };
        for (var i = 0; i < anchors.Count; i++)
        {
            if (i > 0)
                line.Add(new TextRun(", ", style));
            line.Add(TextRun.PageReference(anchors[i], style));
        }

        _doc.Add(new Paragraph(line)
        {
            TabStops = [new TabStop(ContentWidth, TabAlignment.Right, TabLeader.Dots)],
            Margins = new EdgeInsets(2.5, 0, 0, 0),
        });
    }

    /// <summary>Turns inline markup into styled runs.</summary>
    /// <param name="markup">The text.</param>
    /// <param name="baseStyle">The style of unmarked text.</param>
    /// <param name="bold">The style of bold text, or null for the body bold.</param>
    /// <param name="italic">The style of italic text, or null for the body italic.</param>
    /// <param name="code">The style of monospace text, or null for the body monospace.</param>
    /// <param name="collapse">Whether white space, line feeds included, becomes one space.</param>
    /// <returns>The runs, in order.</returns>
    public List<TextRun> Runs(
        string markup, TextStyle baseStyle, TextStyle? bold = null, TextStyle? italic = null,
        TextStyle? code = null, bool collapse = true)
    {
        var boldStyle = bold ?? _s.Bold;
        var italicStyle = italic ?? _s.Italic;
        var codeStyle = code ?? _s.Code;
        var text = collapse ? Collapse(markup) : markup;
        var runs = new List<TextRun>();
        var buffer = new StringBuilder();
        var current = baseStyle;
        var isBold = false;
        var isItalic = false;
        var isCode = false;

        void Flush()
        {
            if (buffer.Length > 0)
                runs.Add(new TextRun(buffer.ToString(), current));
            buffer.Clear();
        }

        void Restyle()
        {
            Flush();
            current = isCode ? codeStyle : isBold ? boldStyle : isItalic ? italicStyle : baseStyle;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text.AsSpan(i).StartsWith("[["))
            {
                var end = text.IndexOf("]]", i, StringComparison.Ordinal);
                var spec = text[(i + 2)..end];
                var dutch = spec.StartsWith("nl:", StringComparison.Ordinal);
                var number = dutch ? spec[3..] : spec;
                var anchor = $"sec-{number}";
                buffer.Append(dutch ? "paragraaf " : "section ");
                Flush();
                runs.Add(new TextRun(number, ManualStyles.WithAnchor(current, anchor, ManualStyles.LinkBlue)));
                buffer.Append(dutch ? ", pagina " : ", page ");
                Flush();
                runs.Add(TextRun.PageReference(anchor, ManualStyles.WithColor(current, ManualStyles.LinkBlue)));
                i = end + 1;
            }
            else if (text.AsSpan(i).StartsWith("**"))
            {
                isBold = !isBold;
                Restyle();
                i++;
            }
            else if (text[i] == '*')
            {
                isItalic = !isItalic;
                Restyle();
            }
            else if (text[i] == '`')
            {
                isCode = !isCode;
                Restyle();
            }
            else
            {
                buffer.Append(text[i]);
            }
        }

        Flush();
        return runs;
    }

    /// <summary>
    /// Collapses runs of white space, so a wrapped source string reads as one line.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with every run of white space as one space.</returns>
    public static string Collapse(string text) => Spaces.Replace(text.Trim(), " ");

    // A header cell holds one string in one style, so it gets the text without the markers.
    private static string Plain(string markup) =>
        markup.Replace("*", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);

    // The runs of a body cell: sans bold for **bold**, the cell's own style for everything else,
    // since the sample loads no sans italic. Line feeds stay, because they are line breaks there.
    private List<TextRun> CellRuns(string markup, TextStyle style) =>
        Runs(markup, style, bold: _s.TableBold, italic: style, code: _s.TableCode, collapse: false);

    private string? IndexAnchor(string[]? terms)
    {
        if (terms is not { Length: > 0 })
            return null;
        var anchor = $"ix-{++_indexAnchors}";
        foreach (var term in terms)
            IndexEntries.Add((term, anchor));
        return anchor;
    }

    private void AddList(ListStyle style, string? language, string[] items)
    {
        var list = new ListElement(style)
        {
            DefaultStyle = _s.Body,
            Indent = 20,
            Margins = new EdgeInsets(2, 0, 0, 0),
        };
        foreach (var item in items)
            list.Add(new ListItem(Runs(item, _s.Body)) { Language = language });
        _doc.Add(list);
    }

    private void AddHeading(
        string number, string title, int level, TextStyle style, string? language, string anchor,
        PageBreakMode breakBefore, EdgeInsets margins, bool appendix)
    {
        var entry = new OutlineEntry(number, title, level, language, anchor);
        var text = entry.Label;
        Outline.Add(entry);
        _doc.Add(new Heading(text, style)
        {
            Level = level,
            Anchor = anchor,
            Language = language,
            BreakBefore = breakBefore,
            KeepWithNext = true,
            Margins = margins,
        });
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MyWhitespace();
}
