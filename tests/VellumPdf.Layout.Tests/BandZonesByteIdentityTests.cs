// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Pins what a document with string bands saves, so the zoned form cannot move a byte of it. The
/// value was recorded on the commit before the zoned form existed, with this body: a difference
/// means a document that uses no zone came out differently. The documents are every combination of
/// the shapes of a string band that the generator below reaches: tokens (page, count, chapter),
/// the four alignments, a default and two fixed heights, mirrored margins, a page-label range, a
/// recto break and a template too long for the content box, in a header, a footer or both.
/// </summary>
public sealed class BandZonesByteIdentityTests
{
    private static readonly string[] Templates =
    [
        "{page}",
        "Page {page} of {pages}",
        "A long band template that cannot fit inside the narrow content box of this page at all {page}",
        "",
        "{chapter} | {page}",
        "total {pages}",
    ];

    private static readonly HorizontalAlignment[] Alignments =
    [
        HorizontalAlignment.Left, HorizontalAlignment.Center, HorizontalAlignment.Right, HorizontalAlignment.Justify,
    ];

    private static readonly double?[] Heights = [null, 24, 40];

    private static Document Build(int i)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 260, 300),
            Margins = new EdgeInsets(20, 30, 20, 50),
            MirrorMargins = i % 2 == 1,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        if (i % 5 == 2)
            doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman, "p.", 3);

        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 8 + i % 5 };
        var chapters = i % 3 == 0;
        var headerKind = i % 4;
        if (headerKind is 0 or 2)
            doc.Header = new RunningBand(Templates[i % Templates.Length], style, Alignments[i / 2 % 4])
            {
                Height = Heights[i / 3 % 3],
                ChapterToken = chapters,
            };
        if (headerKind is 1 or 2 or 3)
            doc.Footer = new RunningBand(Templates[(i / 4 + 1) % Templates.Length], style, Alignments[(i + 1) % 4])
            {
                Height = Heights[(i / 5 + 1) % 3],
                ChapterToken = chapters,
            };

        for (var c = 0; c < 3; c++)
        {
            doc.Add(new Heading($"Chapter {c + 1}", Body) { Level = 0, BreakBefore = PageBreakMode.Page });
            doc.Add(new Paragraph(ParagraphText(c * 2), Body));
            doc.Add(new Paragraph(ParagraphText(c * 2 + 1), Body));
            if (i % 7 == 3 && c == 1)
                doc.Add(new PageBreak { Recto = true });
        }

        return doc;
    }

    [Fact]
    public void ADocumentWithAStringBand_everyShape_pinsBytes()
    {
        var hashes = new StringBuilder();
        for (var i = 0; i < 144; i++)
        {
            using var doc = Build(i);
            using var ms = new MemoryStream();
            doc.Save(ms);
            hashes.Append(NormalisedSha256(ms.ToArray())).Append('\n');
        }

        var actual = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(hashes.ToString())));
        Assert.True(Expected == actual, $"computed {actual}");
    }

    private const string Expected = "57ED68E1DA47043E570FF15E89F18C4598262BC5A14A8B5BB07A80557B6742E5";
}
