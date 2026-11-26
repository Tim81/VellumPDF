// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using LayoutDocument = VellumPdf.Layout.Document;

namespace VellumPdf.Samples.Manual;

/// <summary>
/// Builds the user manual of a made-up air purifier, the Aerolith AL-5, as a PDF/A-2u document
/// with the Layout API alone.
/// </summary>
public static class ManualDocument
{
    /// <summary>The address named in the colophon.</summary>
    public const string ColophonAddress = "https://celestium.nl";

    /// <summary>The colophon sentence as it reads on the page, address included.</summary>
    public const string ColophonLine =
        $"Typeset with VellumPdf. Sample text courtesy of the Celestium team ({ColophonAddress}).";

    /// <summary>
    /// The issue date of the edition, used as the document date unless the caller passes another.
    /// </summary>
    public static readonly DateTimeOffset IssueDate = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The document ID of the edition, used unless the caller passes another.</summary>
    public static byte[] DefaultDocumentId() => Convert.FromHexString("A3011E5000000000000000000000A105");

    /// <summary>
    /// Builds the manual. The caller saves it and disposes it.
    /// </summary>
    /// <param name="timestamp">The creation and modification date written to the file.</param>
    /// <param name="documentId">The file identifier, 16 bytes.</param>
    /// <returns>The document, ready to save.</returns>
    public static LayoutDocument Build(DateTimeOffset? timestamp = null, byte[]? documentId = null)
    {
        var outline = CollectOutline();

        var doc = new LayoutDocument
        {
            PageSize = PageSize.A4,
            Margins = new EdgeInsets(PageGeometry.Top, PageGeometry.Outer, PageGeometry.Bottom, PageGeometry.Inner),
            MirrorMargins = true,
            Conformance = PdfConformance.PdfA2u,
            Tagged = true,
            Language = "en-US",
            Timestamp = timestamp ?? IssueDate,
            DocumentId = documentId ?? DefaultDocumentId(),
        };
        doc.Info.Title = "Aerolith AL-5 User Manual";
        doc.Info.Author = "Aerolith Appliances (a fictitious company)";
        doc.Info.Subject = "Operating and maintenance instructions for the Aerolith AL-5 air purifier";
        doc.Info.Keywords = "air purifier, user manual, sample document, PDF/A-2u";

        var styles = ManualStyles.Create(doc);
        doc.Header = new RunningBand("{chapter}", styles.Header, HorizontalAlignment.Center) { ChapterToken = true };
        doc.SetFooter("Aerolith AL-5 user manual      Page {page} of {pages}", styles.Header);

        // The front matter is numbered i, ii, iii. The body restarts at 1 where the first chapter
        // begins.
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
        FrontMatter.Write(doc, styles, outline);
        doc.Add(new PageBreak { Recto = true, PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal) });

        WriteBody(new ManualWriter(doc, styles));
        return doc;
    }

    // The contents list comes before the chapters, so a first pass over a scratch document learns
    // the headings. One list of chapters then drives both the body and the contents.
    private static List<(string Number, string Title, int Level, string? Language)> CollectOutline()
    {
        using var scratch = new LayoutDocument();
        var writer = new ManualWriter(scratch, ManualStyles.Create(scratch));
        WriteBody(writer);
        return writer.Outline;
    }

    private static void WriteBody(ManualWriter w)
    {
        Introduction.Write(w);
        Safety.Write(w);
        Installation.Write(w);
        DailyUse.Write(w);
        Network.Write(w);
        Care.Write(w);
        Troubleshooting.Write(w);
        Specifications.Write(w);
        Appendices.Write(w);
    }
}
