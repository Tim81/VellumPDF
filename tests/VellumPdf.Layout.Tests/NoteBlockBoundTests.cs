// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The count of an element near the continuation limit (#686, ListDocumentLimitRuleTests) reads a
/// note block as the characters of its paragraphs, and a list before the block is laid out the new
/// way unless that count is over the limit. The bound must stay above the continuations a block
/// takes, whatever its paragraphs ask for.
/// </summary>
public sealed class NoteBlockBoundTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static T Pick<T>(Random rng, params T[] values) => values[rng.Next(values.Length)];

    private static long BoundOf(NoteBlock block)
    {
        var probe = new DocumentRenderer(new PdfDocument(), new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        return probe.ContinuationBoundOf(new NoteBlockRenderer(block));
    }

    // The pages the block takes with the document's limit set to its own bound, or -1 when the save is
    // refused for a reason other than the limit; the limit itself fails the test.
    private static int PagesUnderItsOwnBound(NoteBlock block, double width, double height, string what)
    {
        var bound = BoundOf(block);
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var document = new DocumentRenderer(pdf, new PdfRectangle(0, 0, width, height), new EdgeInsets(10))
        {
            ContinuationLimit = (int)Math.Min(bound, int.MaxValue),
        };
        document.Add(new NoteBlockRenderer(block));
        try
        {
            using var ms = new MemoryStream();
            document.Render(ms);
            return pdf.Pages.Count;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("page continuations", StringComparison.Ordinal))
        {
            Assert.Fail($"{what}: limit {bound} reached, page {width}x{height}");
            return -1;
        }
        catch (InvalidOperationException)
        {
            // A line too tall for the page, or a box with no room: refusals that are not the limit.
            return -1;
        }
    }

    /// <summary>
    /// Many paragraphs of one character on a page that holds one line each: 1,000 pages, which are
    /// 999 continuations, against a bound of two plus two for each paragraph.
    /// </summary>
    [Fact]
    public void AThousandParagraphsOfOneCharacterTakeAThousandPagesWithinTheBound()
    {
        var block = new NoteBlock { Padding = new EdgeInsets(1) };
        for (var i = 0; i < 1000; i++)
            block.Add("x", H(10));

        // The content box is 20 pt tall: one 12 pt line, and no room for a second.
        var pages = PagesUnderItsOwnBound(block, 100, 40, "1000 paragraphs");
        Assert.Equal(1000, pages);
        Assert.Equal(2002, BoundOf(block));
    }

    /// <summary>
    /// Paragraphs that wrap, break at a line end, hold a hard break alone and carry gaps, in a block
    /// that keeps together or with the next, over page heights from one line to many, never reach the
    /// limit set to the block's bound.
    /// </summary>
    [Fact]
    public void ARandomBlockStaysWithinItsBound()
    {
        string[] texts =
        [
            "", "x", "x\nx", "\n", "\n\n", "ab cd ef gh ij kl", "WWWWWWWWWW", "a\tb\nc", "one two three four five six seven eight nine ten",
        ];
        var rng = new Random(20261127);
        var taken = 0;
        for (var seed = 0; seed < 500; seed++)
        {
            var block = new NoteBlock
            {
                KeepTogether = rng.Next(4) == 0,
                KeepWithNext = rng.Next(4) == 0,
                BorderWidth = rng.Next(0, 3),
                Padding = new EdgeInsets(rng.Next(0, 5), rng.Next(0, 5), rng.Next(0, 5), rng.Next(0, 5)),
            };
            for (var p = rng.Next(1, 25); p > 0; p--)
            {
                block.Add(new Paragraph(Pick(rng, texts), H(Pick(rng, 4.0, 6.0, 10.0, 14.0, 20.0)))
                {
                    SpaceAfter = Pick(rng, 0.0, 0.0, 2.0, 9.0),
                });
            }

            var pages = PagesUnderItsOwnBound(block, Pick(rng, 60.0, 100.0, 200.0), 30 + rng.Next(5, 90), $"seed {seed}");
            if (pages > 0) taken += pages;
        }

        Assert.True(taken > 1000, $"only {taken} pages across the sample");
    }

    // ── The lists of a document with a note block ────────────────────────────

    private const int Limit = 60;

    // What a save gives, as one string: the SHA of the file, or the exception's type and message.
    private static string Save(Action<DocumentRenderer> add, double pageHeight = 80)
    {
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, pageHeight), new EdgeInsets(10))
        {
            ContinuationLimit = Limit,
        };
        add(renderer);
        try
        {
            using var ms = new MemoryStream();
            renderer.Render(ms);
            return "OK " + Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16];
        }
        catch (Exception ex)
        {
            return "EX " + ex.GetType().Name + " " + ex.Message;
        }
    }

    private static ListElement Mover(double size) => new ListElement(ListStyle.OrderedDecimal)
        .Add(new ListItem("a", H(10)))
        .Add(new ListItem([new TextRun("mover", H(size))]) { Style = H(10) });

    private static Paragraph Lead() => new("lead", H(10)) { Margins = EdgeInsets.Zero };

    private static string AfterAList(NoteBlock block, bool twin) => Save(r =>
    {
        r.Add(new ParagraphRenderer(Lead()));
        var list = new ListRenderer(Mover(40));
        r.Add(twin ? list.Unclamped() : list);
        r.Add(new NoteBlockRenderer(block));
    });

    /// <summary>
    /// A block whose count is within the limit leaves the list before it to lay out the new way,
    /// which moves the entry that the twin does not. A block whose count is over the limit turns
    /// every list into its twin, because where it starts then depends on where the lists put it.
    /// </summary>
    [Fact]
    public void ABlockWithinTheLimitLeavesAListNewAndABlockOverItTurnsTheListIntoItsTwin()
    {
        var small = new NoteBlock().Add("short", H(10));
        Assert.True(BoundOf(small) <= Limit);
        Assert.NotEqual(AfterAList(small, twin: true), AfterAList(small, twin: false));

        // 30 paragraphs of two characters count 2 + 30 * 3 = 92, over the limit of 60.
        var large = new NoteBlock();
        for (var i = 0; i < 30; i++)
            large.Add("ab", H(10));
        Assert.True(BoundOf(large) > Limit);
        Assert.Equal(AfterAList(large, twin: true), AfterAList(large, twin: false));
    }

    /// <summary>The count of a block is two, plus one and the characters of each paragraph.</summary>
    [Fact]
    public void TheBoundIsTwoPlusOneAndTheCharactersOfEachParagraph()
    {
        var block = new NoteBlock()
            .Add("abc", H(10))
            .Add(new Paragraph([new TextRun("de", H(10)), new TextRun("f", H(10))]));
        Assert.Equal(2 + (1 + 3) + (1 + 3), BoundOf(block));
        Assert.Equal(2, BoundOf(new NoteBlock()));
    }
}
