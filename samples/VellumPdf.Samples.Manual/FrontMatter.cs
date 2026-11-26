// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using LayoutDocument = VellumPdf.Layout.Document;

namespace VellumPdf.Samples.Manual;

/// <summary>The title page, its reverse with the colophon, and the contents.</summary>
public static class FrontMatter
{
    /// <summary>Adds the front matter, which the document numbers i, ii and iii.</summary>
    /// <param name="doc">The document to fill.</param>
    /// <param name="s">The styles to draw with.</param>
    /// <param name="outline">The chapter and section headings of the body, in order.</param>
    public static void Write(
        LayoutDocument doc, ManualStyles s, IReadOnlyList<(string Number, string Title, int Level, string? Language)> outline)
    {
        TitlePage(doc, s);
        Colophon(doc, s);
        Contents(doc, s, outline);
    }

    private static void TitlePage(LayoutDocument doc, ManualStyles s)
    {
        // The Layout API has no space-after: a bottom margin adds nothing before the next
        // element, so every gap on this page is the top margin of the element that follows it.
        doc.Add(new Paragraph("AEROLITH", s.TitleLine) { Margins = new EdgeInsets(120, 0, 0, 0) });
        doc.Add(new Paragraph("AL-5", s.TitleName) { Margins = new EdgeInsets(6, 0, 0, 0) });
        doc.Add(new Paragraph("Air purifier", s.TitleKind) { Margins = new EdgeInsets(4, 0, 0, 0) });
        doc.Add(new LineSeparator
        {
            Color = ManualStyles.Brand,
            LineWidth = 2.5,
            Margins = new EdgeInsets(18, 0, 0, 0),
        });
        doc.Add(new Paragraph("User manual", s.TitleKind) { Margins = new EdgeInsets(18, 0, 0, 0) });
        doc.Add(new Paragraph("Models AL-5 and AL-5W", s.TitleLine) { Margins = new EdgeInsets(150, 0, 0, 0) });
        doc.Add(new Paragraph("Firmware 3.2 and later", s.TitleLine) { Margins = new EdgeInsets(3, 0, 0, 0) });
        doc.Add(new Paragraph("Edition 4, November 2026", s.TitleLine) { Margins = new EdgeInsets(3, 0, 0, 0) });
        doc.Add(new Paragraph(
            "Aerolith is a fictitious product. This manual is a sample document, built to show what a " +
            "long, structured PDF/A document looks like when it is made with the VellumPdf Layout API.",
            s.Small)
        { Margins = new EdgeInsets(90, 0, 0, 0) });
    }

    private static void Colophon(LayoutDocument doc, ManualStyles s)
    {
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("Colophon", s.Subsection) { Margins = new EdgeInsets(400, 0, 0, 0) });
        doc.Add(new Paragraph(
            "Copyright 2026 Aerolith Appliances, a fictitious company. All names, models, figures and " +
            "addresses in this manual are invented. Do not rely on them.", s.Small)
        { Margins = new EdgeInsets(6, 0, 0, 0) });
        doc.Add(new Paragraph(
            "The text is set in Liberation Serif, Liberation Sans and Liberation Mono. The file " +
            "conforms to PDF/A-2u, so every font is embedded and all text can be searched and copied.",
            s.Small)
        { Margins = new EdgeInsets(6, 0, 0, 0) });

        // The one external link in the manual. Three runs share a line: the text before the
        // address, the address as a link, and the text after it.
        var line = ManualDocument.ColophonLine;
        var at = line.IndexOf(ManualDocument.ColophonAddress, StringComparison.Ordinal);
        var link = ManualStyles.WithLink(s.Small, ManualDocument.ColophonAddress);
        doc.Add(new Paragraph(
        [
            new TextRun(line[..at], s.Small),
            new TextRun(ManualDocument.ColophonAddress, link),
            new TextRun(line[(at + ManualDocument.ColophonAddress.Length)..], s.Small),
        ])
        { Margins = new EdgeInsets(6, 0, 0, 0) });
        doc.Add(new Paragraph(
            "Placeholder: the fonts are read by name from resources of the Standard14 package, because no " +
            "public Layout call returns them. Heading numbers are typed by hand. The gaps between " +
            "elements, and the position of this page's text, are top margins, because a bottom margin " +
            "adds no space.", s.Small)
        { Margins = new EdgeInsets(14, 0, 0, 0) });
    }

    private static void Contents(
        LayoutDocument doc, ManualStyles s, IReadOnlyList<(string Number, string Title, int Level, string? Language)> outline)
    {
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Heading("Contents", s.Chapter)
        {
            Level = 0,
            Anchor = "contents",
            KeepWithNext = true,
            Margins = new EdgeInsets(0, 0, 0, 0),
        });
        doc.Add(new Paragraph(
            "Placeholder: the page numbers below are not real. The Layout API can draw the dots and " +
            "the right-hand column, but it cannot yet say which page a heading landed on, so each " +
            "entry shows " + ManualWriter.PagePlaceholder + " until a table-of-contents feature exists.",
            s.Small)
        { KeepWithNext = true, Margins = new EdgeInsets(10, 0, 0, 0) });

        var w = new ManualWriter(doc, s);
        foreach (var (number, title, level, language) in outline)
        {
            var label = number.StartsWith("Appendix", StringComparison.Ordinal)
                ? $"{number}. {title}" : $"{number}  {title}";
            if (level == 0)
                w.LeaderLine(label, ManualWriter.PagePlaceholder, s.Bold, 0, language);
            else
                w.LeaderLine(label, ManualWriter.PagePlaceholder, s.Body, 18, language);
        }
    }
}
