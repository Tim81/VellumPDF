// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Layout;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Golden / snapshot tests for the Layout layer (issue #5).
/// Structural projection only — the Layout <see cref="Document"/> does not expose
/// Timestamp/DocumentId pins, so byte-stable raw output is not achievable here.
/// </summary>
public sealed class GoldenTests
{
    // ── 6. Table_projection ───────────────────────────────────────────────────

    [Fact]
    public async Task Table_projection()
    {
        using var doc = new Document();
        var table = new TableElement();
        table.SetColumnWidths(180, 180, 180);

        var header = table.AddHeaderRow();
        header.AddCell("Name").AddCell("Role").AddCell("Department");

        var r1 = table.AddRow();
        r1.AddCell("Alice").AddCell("Engineer").AddCell("Platform");

        var r2 = table.AddRow();
        r2.AddCell("Bob").AddCell("Designer").AddCell("Product");

        var r3 = table.AddRow();
        r3.AddCell("Carol").AddCell("Manager").AddCell("Engineering");

        doc.Add(table);

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var pdfText = Encoding.Latin1.GetString(bytes);

        // Page count
        var countMatch = Regex.Match(pdfText, @"/Count (\d+)");
        var pageCount = countMatch.Success ? int.Parse(countMatch.Groups[1].Value) : 0;

        // Indirect object count
        var objectCount = Regex.Matches(pdfText, @"\d+ 0 obj").Count;

        var hasHelvetica = pdfText.Contains("/Helvetica");
        var hasType1 = pdfText.Contains("/Type1");

        // Decompress streams and check for text operators and cell content
        var decompressed = PdfTestUtil.DecompressAllFlatStreams(bytes);
        var hasBT = decompressed.Contains("BT");
        var hasET = decompressed.Contains("ET");
        var hasNameCell = decompressed.Contains("Name") || pdfText.Contains("Name");
        var hasAliceCell = decompressed.Contains("Alice") || pdfText.Contains("Alice");
        var hasBobCell = decompressed.Contains("Bob") || pdfText.Contains("Bob");

        var projection = $"""
            PageCount: {pageCount}
            IndirectObjectCount: {objectCount}
            HasHelvetica: {hasHelvetica}
            HasType1: {hasType1}
            HasBT: {hasBT}
            HasET: {hasET}
            HasNameCell: {hasNameCell}
            HasAliceCell: {hasAliceCell}
            HasBobCell: {hasBobCell}
            """;

        await Verify(projection);
    }
}
