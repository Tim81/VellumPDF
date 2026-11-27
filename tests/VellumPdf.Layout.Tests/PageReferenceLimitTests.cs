// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A page reference and the page-continuation limit. The document counts the continuations an
/// element after a list can need before any label is known, so a reference counts as the longest
/// label the document's ranges can print. The tests compare a document that holds a reference with
/// one that holds the label it settles on as literal text.
/// </summary>
public sealed class PageReferenceLimitTests
{
    private const int Limit = 60;

    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    // A list whose second entry moves to the next page when the list is laid out new, and so
    // starts the paragraph after it lower than its twin does; see ListDocumentLimitRuleTests.
    private static ListElement Mover() => new ListElement(ListStyle.OrderedDecimal)
        .Add(new ListItem("a", H(10)))
        .Add(new ListItem([new TextRun("mover", H(40))]) { Style = H(10) });

    // What a save of the document gives, as the text it draws or the refusal, and the label the
    // reference settled on. With a literal, that text stands where the reference was. The last word
    // of the paragraph is <extra> characters of W, so its length moves the bound by one.
    private static (string Verdict, string Label) Outcome(
        int words, int extra, string? literal, int firstNumber, int limit = Limit, string? prefix = null)
    {
        var pdf = new PdfDocument { Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 80), new EdgeInsets(10))
        {
            ContinuationLimit = limit,
            DocumentPageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, prefix, firstNumber),
        };
        renderer.Add(new ParagraphRenderer(new Paragraph("lead", H(10)) { Margins = EdgeInsets.Zero }));
        renderer.Add(new ListRenderer(Mover()));
        var text = string.Join(" ", Enumerable.Repeat("WWWWW", words)) + " " + new string('W', extra) + " ";
        renderer.Add(new ParagraphRenderer(new Paragraph(
            [new TextRun(text, H(20)), literal is null ? TextRun.PageReference("t", H(20)) : new TextRun(literal, H(20))])
        { Margins = EdgeInsets.Zero }));
        renderer.Add(new ParagraphRenderer(new Paragraph("t", H(10)) { Anchor = "t", Margins = EdgeInsets.Zero }));

        string verdict;
        try
        {
            using var ms = new MemoryStream();
            renderer.Render(ms);
            var placements = ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()));
            verdict = "OK " + string.Join("|", placements.Select(p => $"{p.X:F2},{p.Y:F2},{p.Text}"));
        }
        catch (InvalidOperationException ex)
        {
            verdict = "EX " + ex.Message[..Math.Min(40, ex.Message.Length)];
        }

        return (verdict, renderer.ReferenceTable?.States[^1][0] ?? literal ?? "");
    }

    // The characters of the paragraph, label included, that the literal document's scan counts:
    // two for the placement, the words with their spaces, the cut word and its space, the label.
    private static int Bound(int words, int extra, int labelLength) => 2 + (words * 6) + extra + 1 + labelLength;

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void AReference_getsTheVerdictOfTheLiteralLabel_exceptWhereItsBoundIsWithinTheNumeralAllowanceOfTheLimit(int first)
    {
        var mismatches = new List<int>();
        var matches = 0;
        var matchedJustOverTheLimit = false;
        for (var words = 5; words <= 10; words++)
        {
            for (var extra = 0; extra <= 5; extra++)
            {
                var reference = Outcome(words, extra, null, first);
                var literal = Outcome(words, extra, reference.Label, first);
                Assert.Equal(reference.Label, literal.Label);
                var bound = Bound(words, extra, reference.Label.Length);
                if (literal.Verdict == reference.Verdict)
                {
                    matches++;
                    matchedJustOverTheLimit |= bound is > Limit and <= Limit + 2;
                }
                else
                {
                    mismatches.Add(bound);
                }
            }
        }

        // Where the two differ, the literal document's bound is within the allowance of the limit,
        // which is where counting the longest label can say "near" for a label that is not.
        foreach (var bound in mismatches)
            Assert.InRange(bound, Limit - PageReferenceTable.NumeralBound, Limit);

        // A bound that falls just past the limit gives the twin in both documents, which is what a
        // reference whose label was not counted at all would not.
        Assert.True(matchedJustOverTheLimit, "no length put the literal bound just past the limit and matched");
        Assert.True(matches >= 12, $"{matches} of 36 lengths matched; the literal bounds that differ are {string.Join(",", mismatches)}");
    }

    [Fact]
    public void ALongPrefix_isCountedFromTheRangesOfTheDocument()
    {
        // A 300-character prefix is known before any layout runs, so the bound counts it for every
        // reference. With the limit at 300, a paragraph whose literal label is that long is near the
        // limit, and so is the one that holds the reference, whatever the words before it.
        var prefix = new string('p', 300);
        var matches = 0;
        for (var words = 4; words <= 16; words++)
        {
            for (var extra = 0; extra <= 5; extra++)
            {
                var reference = Outcome(words, extra, null, 9, limit: 300, prefix: prefix);
                var literal = Outcome(words, extra, reference.Label, 9, limit: 300, prefix: prefix);
                Assert.StartsWith(prefix, reference.Label, StringComparison.Ordinal);
                Assert.Equal(literal.Verdict, reference.Verdict);
                matches++;
            }
        }

        Assert.Equal(13 * 6, matches);
    }

    // A one-cell table after the list: the cell holds the words and then either the reference or
    // the label it settles on, as a run (<paramref name="runCell"/>) or in a string.
    private static string CellVerdict(int words, int extra, string? literal, bool runCell, int limit, out string label)
    {
        var pdf = new PdfDocument { Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 80), new EdgeInsets(10))
        {
            ContinuationLimit = limit,
            DocumentPageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, null, 9),
        };
        renderer.Add(new ParagraphRenderer(new Paragraph("lead", H(10)) { Margins = EdgeInsets.Zero }));
        renderer.Add(new ListRenderer(Mover()));
        var text = string.Join(" ", Enumerable.Repeat("WWWWW", words)) + " " + new string('W', extra) + " ";
        var table = new VellumPdf.Layout.Elements.Table.TableElement().SetColumnWidths(180);
        table.AddRow().AddCell(runCell
            ? new VellumPdf.Layout.Elements.Table.Cell([new TextRun(text, H(20)), literal is null ? TextRun.PageReference("t", H(20)) : new TextRun(literal, H(20))])
            : new VellumPdf.Layout.Elements.Table.Cell(text + literal) { Style = H(20) });
        renderer.Add(new TableRenderer(table));
        renderer.Add(new ParagraphRenderer(new Paragraph("t", H(10)) { Anchor = "t", Margins = EdgeInsets.Zero }));

        string verdict;
        try
        {
            using var ms = new MemoryStream();
            renderer.Render(ms);
            var placements = ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()));

            // Where the list's second entry is placed is what the limit's verdict moves.
            var mover = placements.First(p => p.Text == "mover");
            verdict = $"OK {mover.X:F2},{mover.Y:F2}";
        }
        catch (InvalidOperationException ex)
        {
            verdict = "EX " + ex.Message[..Math.Min(40, ex.Message.Length)];
        }

        label = renderer.ReferenceTable?.States[^1][0] ?? literal ?? "";
        return verdict;
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    public void ARunCellWithAReference_getsTheVerdictOfTheStringCellHoldingTheLabel_exceptWithinTheNumeralAllowanceOfTheLimit(int limit)
    {
        var mismatches = new List<int>();
        var matches = 0;
        for (var words = 1; words <= 11; words++)
        {
            for (var extra = 0; extra <= 5; extra++)
            {
                var reference = CellVerdict(words, extra, null, runCell: true, limit, out var label);
                var stringCell = CellVerdict(words, extra, label, runCell: false, limit, out _);
                var literalRun = CellVerdict(words, extra, label, runCell: true, limit, out _);

                // A run cell and a string cell of one text are laid out alike.
                Assert.Equal(stringCell, literalRun);

                // The scan counts the cell's characters and a row: two for the placement, one for
                // the row, the text and the label.
                var bound = 2 + 1 + (words * 6) + extra + 1 + label.Length;
                if (reference == stringCell) matches++;
                else mismatches.Add(bound);
            }
        }

        foreach (var bound in mismatches)
            Assert.InRange(bound, limit - PageReferenceTable.NumeralBound, limit);

        Assert.True(matches > 0, "no length matched");
        Assert.True(mismatches.Count > 0, $"every one of {matches} lengths matched, so the allowance is not exercised");
    }
}
