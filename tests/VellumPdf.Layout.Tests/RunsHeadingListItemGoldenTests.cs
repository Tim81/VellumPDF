// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Pins for the string constructors of <see cref="Heading"/> and <see cref="ListItem"/> (#367):
/// the normalised bytes of documents built only through them. The values were recorded on the
/// commit before runs were added, so a difference here means the output of an existing input moved.
/// </summary>
public sealed class RunsHeadingListItemGoldenTests
{
    private static readonly string Regular = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");

    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    private static Document NewDocument(bool tagged = true) => new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        Tagged = tagged,
        Language = "nl-NL",
        PageSize = new PdfRectangle(0, 0, 300, 300),
        Margins = EdgeInsets.Zero,
    };

    private static string Hash(Document doc, bool fold = false)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return NormalisedSha256(ms.ToArray(), fold);
    }

    private const string Long =
        "A heading long enough to wrap over several lines on a narrow page, so that the layout cuts it more than once";

    private static Document Headings()
    {
        var doc = NewDocument();
        doc.Info.Title = "Headings";
        doc.Add(new Heading("Installatie"));
        doc.Add(new Heading("Sub", new TextStyle { Font = Standard14.HelveticaBold, FontSize = 20 }) { Level = 1 });
        doc.Add(new Heading("Linked title", new TextStyle { FontSize = 16, LinkUri = "https://example.com/a" })
        {
            Level = 2,
            BookmarkTitle = "Other title",
            Language = "en-GB",
            BookmarkExpanded = false,
        });
        doc.Add(new Heading(Long, new TextStyle { FontSize = 12 })
        {
            Alignment = HorizontalAlignment.Justify,
            Margins = new EdgeInsets(5, 10, 5, 10),
        });
        doc.Add(new Heading(string.Empty) { Level = 7 });
        doc.Add(new Heading("Centred", new TextStyle { FontSize = 18, Color = new ColorRgb(0.5, 0, 0) })
        {
            Alignment = HorizontalAlignment.Center,
        });
        return doc;
    }

    private static Document PagedHeadings()
    {
        var doc = NewDocument();
        for (var i = 0; i < 40; i++)
            doc.Add(new Heading($"Chapter {i + 1} " + Long, new TextStyle { FontSize = 14 }) { Level = i % 3 });
        return doc;
    }

    private static Document EmbeddedHeadings()
    {
        var doc = NewDocument();
        doc.Conformance = PdfConformance.PdfA2u;
        doc.Info.Title = "Embedded";
        var style = new TextStyle { FontRef = doc.LoadTrueTypeFont(Regular), FontSize = 16 };
        doc.Add(new Heading("Installatie", style));
        doc.Add(new Heading(Long, style) { Level = 1, Language = "en-GB" });
        return doc;
    }

    private static Document Lists()
    {
        var doc = NewDocument();
        doc.Info.Title = "Lists";
        doc.Add(new ListElement(ListStyle.Unordered)
            .Add("First")
            .Add("Second item that is long enough to wrap onto a second line of the narrow page, and further")
            .Add("Bold", new TextStyle { Font = Standard14.HelveticaBold, FontSize = 14 })
            .Add("Linked", new TextStyle { FontSize = 12, LinkUri = "https://example.com/b" }));
        var tree = new ListItem("Parent", new TextStyle { FontSize = 11 }) { Language = "fr-FR" };
        tree.AddChild("Child one");
        tree.AddChild("Child two", new TextStyle { Font = Standard14.TimesItalic, FontSize = 10 });
        tree.AddChild(new ListItem("Child three").AddChild("Grandchild"));
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [tree, new ListItem("Next")]));
        doc.Add(new ListElement(ListStyle.OrderedAlpha, [new ListItem("a"), new ListItem("b")]));
        doc.Add(new ListElement(ListStyle.OrderedRoman, Enumerable.Range(1, 30).Select(i => new ListItem($"Roman {i}"))));
        doc.Add(new ListElement(ListStyle.Unordered, [new ListItem("Top").AddChild(new ListItem("Child").AddChild(null!))]));
        doc.Add(new ListElement(ListStyle.Unordered, [new ListItem("Top").AddChild(new ListItem("Child").AddChild(new ListItem((string)null!)))]));
        return doc;
    }

    private static Document EmbeddedLists()
    {
        var doc = NewDocument();
        doc.Conformance = PdfConformance.PdfA2u;
        doc.Info.Title = "Embedded lists";
        var style = new TextStyle { FontRef = doc.LoadTrueTypeFont(Regular), FontSize = 12 };
        doc.Add(new ListElement(ListStyle.OrderedDecimal) { DefaultStyle = style }
            .Add("One")
            .Add(new ListItem("Two").AddChild("Child")));
        return doc;
    }

    [Fact]
    public void Headings_pinTheBytes() =>
        Pin("B4DCE55E1C20273C36FE0C0CD8D8C53889E1B2F7CF71468D53772574DC07AD06", Hash(Headings()));

    [Fact]
    public void PagedHeadings_pinTheBytes() =>
        Pin("8487CBC0BA24DFC4CE398386F19EE94F74A190743E29B1565FE6BA6C4899CA8C", Hash(PagedHeadings()));

    // The embedded-font pins include the font program change that followed them: FontBBox from the
    // head table rounded outward, no cmap table in a glyf program used with a CIDFont, and /Length1
    // on the FontFile2 stream. One value serves both platforms: the hash inflates every Flate
    // stream and folds the ToUnicode line ends.
    [Fact]
    public void EmbeddedHeadings_pinTheBytes() =>
        Pin("A14298A33695CED45DF1E66B0640444280DAF3F210C17497B5E63C8EF866EF33", Hash(EmbeddedHeadings(), fold: true));

    [Fact]
    public void Lists_pinTheBytes() =>
        Pin("02A7120D5C7D66218CAA14961943FDDE37DDC79FEFA015C9D13EE22ECCA8E000", Hash(Lists()));

    // Includes the same font program change as EmbeddedHeadings_pinTheBytes above.
    [Fact]
    public void EmbeddedLists_pinTheBytes() =>
        Pin("298EABD9F41EF3B0AC352A5A2C9434CFEC09B0E203071C77F735B70E5026A928", Hash(EmbeddedLists(), fold: true));

    [Fact]
    public void NullTextItemAtTheTop_stillThrowsNamingText()
    {
        var doc = NewDocument();
        doc.Add(new ListElement(ListStyle.Unordered, [new ListItem((string)null!)]));
        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save(new MemoryStream()));
        Assert.Equal("text", ex.ParamName);
    }
}
