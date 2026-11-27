// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #703 pins for documents that use no note block: the bytes after normalisation. Each value was
/// recorded on the commit before the block existed, with the same bodies, so a difference means a
/// document that never asks for a block came out differently. These bodies name no member the
/// block added, which is why they also compile there.
/// </summary>
public sealed class NoteBlockByteIdentityTests
{
    private static Document NewDoc(double width, double height, bool tagged, bool footer)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, width, height),
            Margins = new EdgeInsets(24),
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Tagged = tagged,
        };
        if (tagged)
        {
            doc.Language = "en-US";
            doc.Info.Title = "t";
        }

        if (footer) doc.Footer = new RunningBand("{page} of {pages}");
        return doc;
    }

    private static string Hash(Document doc)
    {
        try
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            var bytes = ms.ToArray();
            return $"{NormalisedSha256(bytes)} pages={PageCount(bytes)}";
        }
        catch (Exception ex)
        {
            return $"EX {ex.GetType().Name}";
        }
    }

    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    private static string Words(Random rng, int count)
    {
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa"];
        return string.Join(' ', Enumerable.Range(0, count).Select(_ => words[rng.Next(words.Length)]));
    }

    private static TextStyle Style(Random rng) => new()
    {
        Font = rng.Next(3) switch { 0 => Standard14.Helvetica, 1 => Standard14.TimesRoman, _ => Standard14.Courier },
        FontSize = 8 + rng.Next(8),
    };

    private static Document Generate(int seed)
    {
        var rng = new Random(seed);
        var doc = NewDoc(
            rng.Next(3) switch { 0 => 300, 1 => 360, _ => 420 },
            rng.Next(3) switch { 0 => 260, 1 => 340, _ => 420 },
            tagged: rng.Next(3) == 0,
            footer: rng.Next(3) == 0);
        doc.MirrorMargins = rng.Next(4) == 0;

        var anchors = 0;
        var count = 3 + rng.Next(12);
        for (var e = 0; e < count; e++)
        {
            switch (rng.Next(9))
            {
                case 0:
                case 1:
                    doc.Add(new Paragraph(Words(rng, 5 + rng.Next(120)), Style(rng))
                    {
                        Alignment = (HorizontalAlignment)rng.Next(4),
                        KeepWithNext = rng.Next(5) == 0,
                        KeepTogether = rng.Next(6) == 0,
                        MinLinesBeforeBreak = 1 + rng.Next(3),
                        MinLinesAfterBreak = 1 + rng.Next(3),
                        SpaceAfter = rng.Next(3) == 0 ? rng.Next(20) : 0,
                        Anchor = rng.Next(8) == 0 ? "a" + anchors++.ToString(CultureInfo.InvariantCulture) : null,
                    });
                    break;
                case 2:
                    doc.Add(new Heading(Words(rng, 1 + rng.Next(8)), Style(rng)) { KeepWithNext = rng.Next(2) == 0 });
                    break;
                case 3:
                    var list = new ListElement(rng.Next(2) == 0 ? ListStyle.Unordered : ListStyle.OrderedDecimal);
                    for (var i = 1 + rng.Next(8); i > 0; i--)
                        list.Add(Words(rng, 1 + rng.Next(30)), Style(rng));
                    doc.Add(list);
                    break;
                case 4:
                    var table = new TableElement();
                    var columns = 2 + rng.Next(2);
                    for (var r = 1 + rng.Next(6); r > 0; r--)
                    {
                        var row = table.AddRow(isHeader: r == 1 && rng.Next(3) == 0);
                        for (var c = 0; c < columns; c++)
                            row.AddCell(new Cell(Words(rng, 1 + rng.Next(14))) { Style = Style(rng) });
                    }

                    doc.Add(table);
                    break;
                case 5:
                    doc.Add(new Preformatted(string.Join('\n', Enumerable.Range(0, 1 + rng.Next(12)).Select(_ => Words(rng, 1 + rng.Next(8)))))
                    {
                        KeepTogether = rng.Next(4) == 0,
                    });
                    break;
                case 6:
                    doc.Add(new PageBreak { Recto = rng.Next(4) == 0 });
                    break;
                case 7:
                    doc.Add(new LineSeparator());
                    break;
                default:
                    doc.Add(new Paragraph(Words(rng, 5 + rng.Next(60)), Style(rng)) { BreakBefore = rng.Next(4) == 0 ? PageBreakMode.Page : PageBreakMode.None });
                    break;
            }
        }

        return doc;
    }

    /// <summary>
    /// 100 generated documents, saved, hashed after normalisation, and the hashes hashed together:
    /// one value stands for the lot. A refused document counts by its exception type, so a document
    /// that started to throw, or stopped, moves the value too. The generator draws paragraphs with
    /// every flow request, headings, lists, tables, preformatted text, breaks and separators, with
    /// and without tagging, a footer and mirrored margins.
    /// </summary>
    [Fact]
    public void AHundredGeneratedDocumentsWithNoNoteBlock_pinTheirBytes()
    {
        var hashes = Enumerable.Range(0, 100).Select(i => Hash(Generate(703_000 + i))).ToList();
        var combined = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes))));

        Pin("EB874593EBCFCBD4A35D6638E0C8F40C2486CA4E8451CAE9073A4572CBF48038 refused=0", combined + " refused=" + hashes.Count(h => h.StartsWith("EX", StringComparison.Ordinal)));
    }

    [Fact]
    public void AListThenAParagraphThenATable_pinsItsBytes()
    {
        using var doc = NewDoc(300, 300, tagged: true, footer: true);
        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 1; i <= 14; i++)
            list.Add("Item " + i.ToString(CultureInfo.InvariantCulture) + " of the list, long enough to wrap onto a second line of the page.");
        doc.Add(list);
        doc.Add(new Paragraph(string.Join(' ', Enumerable.Repeat("after the list", 30))));
        var table = new TableElement();
        for (var r = 0; r < 12; r++)
            table.AddRow().AddCell("row " + r.ToString(CultureInfo.InvariantCulture)).AddCell("cell");
        doc.Add(table);

        Pin("9E867FE7A814DBD71780BC619537BEB74E2D2E44B48D4B736027533E21282872 pages=4", Hash(doc));
    }
}
