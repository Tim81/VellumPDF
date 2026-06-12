// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Regression tests for the layout-level v1.5.1 re-review findings.
/// All documents are rendered in-memory; no external resources are required.
/// </summary>
public sealed class RegressionV151bTests
{
    // ── Test 2: List split-item draws its marker only once (#71 marker repeat) ─

    /// <summary>
    /// A single ordered list item whose text content spans multiple pages previously
    /// emitted its marker ("1.") at the top of EVERY continuation page.  The fix
    /// suppresses the marker on continuation pages so it appears exactly once.
    ///
    /// To make the item span at least two pages we use a very small page (300×120 pt)
    /// and a long string of distinct word tokens.  We then assert that the string "1."
    /// appears exactly once across all decompressed content streams.
    ///
    /// Pre-fix behaviour: "1." was emitted once per page the item occupied, so the
    /// count would be ≥ 2.  Post-fix: count must be exactly 1.
    /// </summary>
    [Fact]
    public void OrderedList_splitItem_markerAppearsExactlyOnce()
    {
        using var doc = new Document();
        // Small page to force the single item to spill onto at least a second page.
        doc.PageSize = new PdfRectangle(0, 0, 300, 120);
        doc.Margins = new EdgeInsets(10);

        var style = new TextStyle { FontSize = 10 };

        // Build enough content to require at least 2 pages at the chosen size.
        // 60 unique tokens at ~20 px each far exceeds the 100-pt content height.
        var words = Enumerable.Range(1, 60).Select(n => $"MKR{n:D3}").ToArray();
        var text = string.Join(" ", words);

        var list = new ListElement(ListStyle.OrderedDecimal)
            .Add(text, style);
        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);

        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        // The document must span more than one page (sanity check).
        // We verify by checking that the content is non-trivial.
        Assert.True(ms.Length > 200,
            "Expected a non-trivial multi-page PDF.");

        // The marker "1." must appear exactly once across all content streams.
        var markerCount = PdfTestUtil.CountOccurrences(decompressed, "1.");
        Assert.Equal(1, markerCount);
    }

    /// <summary>
    /// Complementary: a list with a single short item (fits on one page) still renders
    /// its marker exactly once (no regression from the fix). Uses a unique per-item
    /// marker "SINGLEITEM001" to avoid false positives from substring matching.
    /// </summary>
    [Fact]
    public void OrderedList_singleShortItem_markerAppearsOnce()
    {
        using var doc = new Document();

        var list = new ListElement(ListStyle.OrderedDecimal)
            .Add("SINGLEITEM001");
        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        // Content must be present.
        Assert.Contains("SINGLEITEM001", decompressed);

        // The marker "1." must appear exactly once on the single page.
        // Use a pattern unlikely to appear as a substring in item text.
        var markerCount = PdfTestUtil.CountOccurrences(decompressed, "1.");
        Assert.Equal(1, markerCount);
    }
}
