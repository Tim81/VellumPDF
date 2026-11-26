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
/// Preformatted text rewrites its text before it wraps: a tab becomes up to <c>TabWidth</c>
/// spaces, and the wrap then cuts an expanded line after at least one cluster. So a line of such
/// text does not hold a character of the original, and the count of an element near the limit
/// (ListDocumentLimitRuleTests) must read a tab as the columns it can become. The limit is small
/// here so a document reaches it in a blink.
/// </summary>
public sealed class ListDocumentLimitPreformattedTests
{
    private const int Limit = 60;

    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static TextStyle Courier(double size) => new() { FontRef = new FontReference(Standard14.Courier), FontSize = size };

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

    // Tabs alone: a tab at a stop moves a full TabWidth, so each one becomes 32 columns, which a
    // Courier line 180 points wide wraps into two or three lines of its own.
    private static Preformatted Tabs(int count) =>
        new(new string('\t', count) + "\n\n", Courier(20)) { TabWidth = 32, Margins = EdgeInsets.Zero };

    private static string Shifted(int tabs, int leads, double mover, bool twin) => Save(r =>
    {
        for (var i = 0; i < leads; i++) r.Add(new ParagraphRenderer(Lead()));
        var list = new ListRenderer(Mover(mover));
        r.Add(twin ? list.Unclamped() : list);
        r.Add(new PreformattedRenderer(Tabs(tabs)));
    });

    /// <summary>
    /// Preformatted text of tabs reaches the limit with far fewer characters than the limit, and
    /// starts lower when a list before it moves an entry, so the limit's verdict on it flips: one
    /// lead line and a moved entry, or two lead lines and a taller one. The document is the twin's
    /// whichever way it would flip. The element's characters, counted one each, are within the
    /// limit where it is refused.
    /// </summary>
    [Theory]
    [InlineData(1, 40.0)]
    [InlineData(2, 30.0)]
    public void PreformattedTabsNearTheLimit_getTheVerdictTheyHadWhenAListMovesAnEntry(int leads, double mover)
    {
        // The first count of tabs the twin refuses, which is where the limit falls.
        var edge = 1;
        while (edge < Limit && Shifted(edge, leads, mover, twin: true).StartsWith("OK", StringComparison.Ordinal)) edge++;

        // Two plus the characters is the count that stopped holding the lists back: the tabs and
        // the two line ends.
        Assert.InRange(edge + 4, 1, Limit);

        for (var tabs = edge - 12; tabs <= edge + 12; tabs++)
            Assert.Equal(Shifted(tabs, leads, mover, twin: true), Shifted(tabs, leads, mover, twin: false));
    }

    /// <summary>
    /// The fixture's list does move an entry: with preformatted text too short to come near the
    /// limit, the document is not the twin's.
    /// </summary>
    [Theory]
    [InlineData(1, 40.0)]
    [InlineData(2, 30.0)]
    public void AControlWithAShortPreformattedText_isLaidOutNewAndMovesTheEntry(int leads, double mover)
    {
        var twin = Shifted(1, leads, mover, twin: true);

        Assert.StartsWith("OK ", twin);
        Assert.NotEqual(twin, Shifted(1, leads, mover, twin: false));
    }

    /// <summary>
    /// The bound counts a tab as its width and every other character as one, with line ends and
    /// the two for the retry and the piece that ends.
    /// </summary>
    [Theory]
    [InlineData(1, 2 + 8 + 3 * 0)]
    [InlineData(8, 2 + 8 + 3 * 7)]
    [InlineData(32, 2 + 8 + 3 * 31)]
    public void TheBoundOfPreformattedText_countsATabAsItsWidth(int tabWidth, long expected)
    {
        var renderer = new PreformattedRenderer(new Preformatted("a\tb\t\tc\r\n", Courier(10)) { TabWidth = tabWidth });
        var document = new DocumentRenderer(new PdfDocument(), new PdfRectangle(0, 0, 200, 80), new EdgeInsets(10));

        Assert.Equal(expected, document.ContinuationBoundOf(renderer));
    }

    /// <summary>
    /// For random preformatted text at narrow widths, with tabs, line ends of the three kinds,
    /// combining marks and wide letters, a document whose limit is the bound is never refused for
    /// the limit: the bound is never below the continuations taken.
    /// </summary>
    [Fact]
    public void TheBoundOfPreformattedText_isNeverBelowTheContinuationsTaken()
    {
        string[] pieces = ["\t", "\t", "\t", "a", "b", "W", " ", "\n", "\r", "\r\n", "́", "é", "中"];
        var rng = new Random(20260901);
        var taken = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            var text = string.Concat(Enumerable.Range(0, rng.Next(0, 60)).Select(_ => pieces[rng.Next(pieces.Length)]));
            var element = new Preformatted(text, Courier(8 + rng.Next(13)))
            {
                TabWidth = 1 + rng.Next(32),
                Margins = EdgeInsets.Zero,
            };
            var width = 30 + rng.Next(90);
            var height = 30 + rng.Next(70);

            var probe = new DocumentRenderer(new PdfDocument(), new PdfRectangle(0, 0, width, height), new EdgeInsets(10));
            var bound = probe.ContinuationBoundOf(new PreformattedRenderer(element));

            var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
            var document = new DocumentRenderer(pdf, new PdfRectangle(0, 0, width, height), new EdgeInsets(10))
            {
                ContinuationLimit = (int)bound,
            };
            document.Add(new PreformattedRenderer(element));
            try
            {
                using var ms = new MemoryStream();
                document.Render(ms);
                taken += pdf.Pages.Count;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("page continuations", StringComparison.Ordinal))
            {
                Assert.Fail($"seed {seed}: limit {bound} reached by {text.Length} characters, tab width {element.TabWidth}, page {width}x{height}");
            }
            catch (InvalidOperationException)
            {
                // Too small a page for one line: a refusal that is not the limit.
            }
        }

        // The sample reaches many pages, so the property is not met by documents of one page.
        Assert.True(taken > 400, $"only {taken} pages across the sample");
    }
}
