// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Reader;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Documents that set no <c>SpaceAfter</c> keep their bytes. The value pinned below was recorded on
/// the commit before the property existed, with the same generator, so a difference means a
/// document that asks for no gap came out differently. The generator builds only members that
/// commit has.
/// </summary>
public sealed class SpaceAfterByteIdentityTests
{
    private const int Documents = 100;

    private static string Words(Random rng, int count) =>
        string.Join(' ', Enumerable.Range(0, count).Select(i => "w" + (rng.Next(900) + 100).ToString(CultureInfo.InvariantCulture) + i.ToString(CultureInfo.InvariantCulture)));

    private static Document Build(int seed)
    {
        var rng = new Random(seed);
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 200 + rng.Next(3) * 40),
            Margins = new EdgeInsets(20 + rng.Next(3) * 4),
            MirrorMargins = rng.Next(4) == 0,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        if (rng.Next(3) == 0) doc.Footer = new RunningBand("{page}/{pages}");
        if (rng.Next(4) == 0) doc.Header = new RunningBand("Header");

        var count = 3 + rng.Next(12);
        for (var i = 0; i < count; i++)
        {
            var keepNext = rng.Next(4) == 0;
            var keepTogether = rng.Next(4) == 0;
            var breakBefore = rng.Next(12) == 0 ? PageBreakMode.Page : PageBreakMode.None;
            switch (rng.Next(10))
            {
                case 0:
                case 1:
                case 2:
                    doc.Add(new Paragraph(Words(rng, 1 + rng.Next(60)), Body)
                    {
                        KeepWithNext = keepNext,
                        KeepTogether = keepTogether,
                        BreakBefore = breakBefore,
                        Margins = new EdgeInsets(rng.Next(3) * 3, 0, rng.Next(3) * 5, 0),
                    });
                    break;
                case 3:
                    doc.Add(new Heading(Words(rng, 1 + rng.Next(6)), Body) { KeepWithNext = keepNext, BreakBefore = breakBefore });
                    break;
                case 4:
                    var list = new ListElement(rng.Next(2) == 0 ? ListStyle.OrderedDecimal : ListStyle.Unordered)
                    {
                        KeepWithNext = keepNext,
                        KeepTogether = keepTogether,
                    };
                    for (var k = 0; k < 1 + rng.Next(8); k++) list.Add(Words(rng, 1 + rng.Next(14)), Body);
                    doc.Add(list);
                    break;
                case 5:
                    var table = new TableElement { KeepWithNext = keepNext, KeepTogether = keepTogether };
                    for (var r = 0; r < 1 + rng.Next(8); r++)
                    {
                        var row = table.AddRow();
                        row.AddCell(Words(rng, 1 + rng.Next(4)));
                        row.AddCell(Words(rng, 1 + rng.Next(4)));
                    }

                    doc.Add(table);
                    break;
                case 6:
                    doc.Add(new Preformatted(string.Join('\n', Enumerable.Range(0, 1 + rng.Next(14)).Select(l => "line\t" + l.ToString(CultureInfo.InvariantCulture))))
                    {
                        KeepWithNext = keepNext,
                        KeepTogether = keepTogether,
                    });
                    break;
                case 7:
                    doc.Add(new LineSeparator());
                    break;
                case 8:
                    if (rng.Next(2) == 0)
                        doc.Add(new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng())) { Width = 20, Height = 20 + rng.Next(40), KeepWithNext = keepNext });
                    else
                        doc.Add(new PieChart { Slices = [new PieSlice(1, ColorRgb.Black), new PieSlice(2, ColorRgb.Black)], Diameter = 20 + rng.Next(40) });
                    break;
                default:
                    doc.Add(new PageBreak());
                    break;
            }
        }

        return doc;
    }

    [Fact]
    public void AHundredMixedDocumentsWithNoGap_keepTheirBytes()
    {
        var hashes = new StringBuilder();
        var pages = 0;
        for (var seed = 0; seed < Documents; seed++)
        {
            using var doc = Build(seed);
            using var ms = new MemoryStream();
            doc.Save(ms);
            var bytes = ms.ToArray();
            using (var reader = PdfReader.Open(bytes)) pages += reader.PageCount;
            hashes.Append(NormalisedSha256(bytes)).Append('\n');
        }

        var combined = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(hashes.ToString())));
        Assert.True("9F5EDBF3C1AE89615ABD3FC60FB424BF462EF98B7603AB98D02ECD6DE5E33CD4 pages=418" == $"{combined} pages={pages}", $"computed {combined} pages={pages}");
    }
}
