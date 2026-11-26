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
/// anchors,
/// paragraphs written in a small inline markup, lists, tables, callouts and command blocks. Each
/// method is a few lines of Layout API calls, so the chapters read as content.
/// </summary>
/// <remarks>
/// The markup is <c>**bold**</c>, <c>*italic*</c> and <c>`code`</c>. A <c>[[3.2]]</c> is a
/// cross-reference. The sample cannot link it or give its page number, so it writes the section
/// number and a visible <c>[p. ?]</c> where the page number belongs.
/// </remarks>
public sealed partial class ManualWriter
{
    /// <summary>The visible stand-in for a page number the Layout API cannot supply.</summary>
    public const string PagePlaceholder = "[p. ?]";

    private static readonly Regex Spaces = MyWhitespace();

    private readonly LayoutDocument _doc;
    private readonly ManualStyles _s;
    private int _chapter;
    private int _section;
    private int _appendix;

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
    public List<(string Number, string Title, int Level, string? Language)> Outline { get; } = [];

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
    public void Section(string title, string? language = null)
    {
        _section++;
        var prefix = _chapter > 0 ? $"{_chapter}.{_section}" : $"{(char)('A' + _appendix - 1)}.{_section}";
        AddHeading(prefix, title, 1, _s.Section, language, $"sec-{prefix}", PageBreakMode.None,
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
    public void P(string markup, string? language = null, bool keepWithNext = false)
    {
        _doc.Add(new Paragraph(Runs(markup, _s.Body))
        {
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
    /// yet
    /// do something. The note is on the page so that the gap shows to the reader of the sample.
    /// </summary>
    /// <param name="text">What is missing, and what the manual does instead.</param>
    public void Placeholder(string text) =>
        _doc.Add(new Paragraph($"Placeholder: {Collapse(text)}", _s.Small)
        {
            Margins = new EdgeInsets(4, 0, 0, 0),
        });

    /// <summary>Adds a shaded box with a label, for a warning or a hint.</summary>
    /// <param name="label">A short label such as NOTE or CAUTION.</param>
    /// <param name="text">The text of the box, plain.</param>
    /// <param name="language">A BCP 47 tag when the text is not in English.</param>
    public void Callout(string label, string text, string? language = null)
    {
        // Workaround: the Layout API has no shaded paragraph, so the box is a one-cell table. It is
        // tagged as a table, and the label cannot be bold. The manual says so where it first uses
        // one.
        var table = new TableElement
        {
            BorderWidth = 0,
            Margins = new EdgeInsets(8, 0, 0, 0),
            KeepTogether = true,
        };
        table.SetColumnWidths(ContentWidth);
        table.AddRow().AddCell(new Cell($"{label}   {Plain(Collapse(text))}")
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
    /// <param name="rows">The body rows. A line feed in a cell is a line break.</param>
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
                r.AddCell(new Cell(Plain(row[c]))
                {
                    Style = c == codeColumn ? _s.TableCode : _s.TableBody,
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
    /// a price list or a table of contents.
    /// </summary>
    /// <param name="left">The text at the left.</param>
    /// <param name="right">The text at the right margin.</param>
    /// <param name="style">The style of both.</param>
    /// <param name="indent">The left margin of the line in points.</param>
    /// <param name="language">A BCP 47 tag when the line is not in English.</param>
    public void LeaderLine(string left, string right, TextStyle style, double indent = 0, string? language = null)
    {
        _doc.Add(new Paragraph($"{left}\t{right}", style)
        {
            Language = language,
            TabStops = [new TabStop(ContentWidth - indent, TabAlignment.Right, TabLeader.Dots)],
            Margins = new EdgeInsets(2.5, 0, 0, indent),
        });
    }

    /// <summary>Turns inline markup into styled runs.</summary>
    /// <param name="markup">The text.</param>
    /// <param name="baseStyle">The style of unmarked text.</param>
    /// <returns>The runs, in order.</returns>
    public List<TextRun> Runs(string markup, TextStyle baseStyle)
    {
        var text = Collapse(markup);
        var runs = new List<TextRun>();
        var buffer = new StringBuilder();
        var current = baseStyle;
        var bold = false;
        var italic = false;
        var code = false;

        void Flush()
        {
            if (buffer.Length > 0)
                runs.Add(new TextRun(buffer.ToString(), current));
            buffer.Clear();
        }

        void Restyle()
        {
            Flush();
            current = code ? _s.Code : bold ? _s.Bold : italic ? _s.Italic : baseStyle;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text.AsSpan(i).StartsWith("[["))
            {
                var end = text.IndexOf("]]", i, StringComparison.Ordinal);
                var number = text[(i + 2)..end];
                buffer.Append(CultureInfo.InvariantCulture, $"section {number} {PagePlaceholder}");
                i = end + 1;
            }
            else if (text.AsSpan(i).StartsWith("**"))
            {
                bold = !bold;
                Restyle();
                i++;
            }
            else if (text[i] == '*')
            {
                italic = !italic;
                Restyle();
            }
            else if (text[i] == '`')
            {
                code = !code;
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

    // A table cell holds one string in one style, so the markup that paragraphs use cannot reach
    // it. The cell gets the text without the markers.
    private static string Plain(string markup) => Reference().Replace(
        markup.Replace("*", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal),
        m => $"section {m.Groups[1].Value} {PagePlaceholder}");

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
        var text = appendix ? $"{number}. {title}" : $"{number}  {title}";
        Outline.Add((number, title, level, language));
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

    [GeneratedRegex(@"\[\[([0-9A-Z.]+)\]\]")]
    private static partial Regex Reference();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MyWhitespace();
}
