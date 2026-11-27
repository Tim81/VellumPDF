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
    public static void Write(LayoutDocument doc, ManualStyles s, IReadOnlyList<OutlineEntry> outline)
    {
        TitlePage(doc, s);
        Colophon(doc, s);
        Contents(doc, s, outline);
    }

    // Each gap on this page is the SpaceAfter of the element above it. Only the first, the
    // distance from the top of the page, is a top margin.
    private static void TitlePage(LayoutDocument doc, ManualStyles s)
    {
        doc.Add(new Paragraph("AEROLITH", s.TitleLine) { Margins = new EdgeInsets(120, 0, 0, 0), SpaceAfter = 6 });
        doc.Add(new Paragraph("AL-5", s.TitleName) { SpaceAfter = 4 });
        doc.Add(new Paragraph("Air purifier", s.TitleKind) { SpaceAfter = 18 });
        doc.Add(new LineSeparator
        {
            Color = ManualStyles.Brand,
            LineWidth = 2.5,
            Margins = new EdgeInsets(0, 0, 0, 0),
            SpaceAfter = 18,
        });
        doc.Add(new Paragraph("User manual", s.TitleKind) { SpaceAfter = 150 });
        doc.Add(new Paragraph("Models AL-5 and AL-5W", s.TitleLine) { SpaceAfter = 3 });
        doc.Add(new Paragraph("Firmware 3.2 and later", s.TitleLine) { SpaceAfter = 3 });
        doc.Add(new Paragraph("Edition 4, November 2026", s.TitleLine) { SpaceAfter = 90 });
        doc.Add(new Paragraph(
            "Aerolith is a fictitious product. This manual is a sample document, built to show what a " +
            "long, structured PDF/A document looks like when it is made with the VellumPdf Layout API.",
            s.Small));
    }

    private static void Colophon(LayoutDocument doc, ManualStyles s)
    {
        doc.Add(new PageBreak());
        // The distance from the top of the page is a top margin: an element cannot be placed at a
        // position outside the flow (#313).
        doc.Add(new Paragraph("Colophon", s.Subsection) { Margins = new EdgeInsets(400, 0, 0, 0), SpaceAfter = 6 });
        doc.Add(new Paragraph(
            "Copyright 2026 Aerolith Appliances, a fictitious company. All names, models, figures and " +
            "addresses in this manual are invented. Do not rely on them.", s.Small)
        { SpaceAfter = 6 });
        doc.Add(new Paragraph(
            "The text is set in Liberation Serif, Liberation Sans and Liberation Mono. The file " +
            "conforms to PDF/A-2u, so every font is embedded and all text can be searched and copied.",
            s.Small)
        { SpaceAfter = 6 });

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
        { SpaceAfter = 14 });
        doc.Add(new Paragraph(
            "Placeholder: heading numbers are typed by hand (#301). The gaps on the title page and " +
            "here are SpaceAfter; in the body they are top margins, because a bottom margin adds no " +
            "space (#704). The position of this text on the page is a top margin, because nothing " +
            "can be placed outside the flow (#313).", s.Small));
    }

    private static void Contents(LayoutDocument doc, ManualStyles s, IReadOnlyList<OutlineEntry> outline)
    {
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Heading("Contents", s.Chapter)
        {
            Level = 0,
            Anchor = "contents",
            KeepWithNext = true,
            SpaceAfter = 10,
        });
        doc.Add(new Paragraph(
            "Placeholder: this list is written by hand from the headings the sample adds, because the " +
            "Layout API cannot yet generate one (#294). Each entry links to its heading, and the " +
            "library prints the page.",
            s.Small)
        { KeepWithNext = true });

        var w = new ManualWriter(doc, s);
        foreach (var entry in outline)
            w.ContentsLine(entry, entry.Level == 0 ? s.Bold : s.Body, entry.Level == 0 ? 0 : 18);
    }
}
