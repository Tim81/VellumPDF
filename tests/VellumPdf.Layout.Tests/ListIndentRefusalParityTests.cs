// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The list indent clamp (#476) gives text a width it did not have, and that starts the wrap and
/// the line-height walk the zero-width paragraph returned before. A clamp must not turn a save into
/// a refusal, so an entry the walk would refuse is left as it was, and these rows pin the whole
/// file the code saved before the change: the digests below are those of the base commit, read
/// with the timestamp and document id this class pins.
/// </summary>
public sealed class ListIndentRefusalParityTests
{
    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static string Sha(Document doc)
    {
        doc.Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16];
    }

    private static Document Page(double width, double height, ListElement list)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, width, height) };
        doc.Add(list);
        return doc;
    }

    public static TheoryData<string> RefusedStyles() =>
    [
        "red-nan", "red-posinf", "red-neginf", "green-nan", "blue-posinf", "size-posinf", "size-nan", "size-neginf",
    ];

    private static TextStyle Refused(string name) => name switch
    {
        "red-nan" => new TextStyle { FontSize = 10, Color = new ColorRgb(double.NaN, 0, 0) },
        "red-posinf" => new TextStyle { FontSize = 10, Color = new ColorRgb(double.PositiveInfinity, 0, 0) },
        "red-neginf" => new TextStyle { FontSize = 10, Color = new ColorRgb(double.NegativeInfinity, 0, 0) },
        "green-nan" => new TextStyle { FontSize = 10, Color = new ColorRgb(0, double.NaN, 0) },
        "blue-posinf" => new TextStyle { FontSize = 10, Color = new ColorRgb(0, 0, double.PositiveInfinity) },
        "size-posinf" => new TextStyle { FontSize = double.PositiveInfinity },
        "size-nan" => new TextStyle { FontSize = double.NaN },
        "size-neginf" => new TextStyle { FontSize = double.NegativeInfinity },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static ListItem Entry(TextStyle runStyle, string text = "abc") =>
        new([new TextRun(text, runStyle)]) { Style = H(10) };

    // The digests are the base commit's output for each layout, which does not depend on the style
    // that was discarded: the marker only (flat), the parent and a child marker (nested), 400
    // markers (many).
    private const string FlatDigest = "B43E8CB320B26453";
    private const string NestedDigest = "483FB82145AF91CF";
    private const string ManyDigest = "B9D99E3C97B80840";

    /// <summary>
    /// A flat list whose indent would give the text a width, with a run style the line-height walk
    /// refuses, saves as it did: the marker is drawn, the text is not, nothing is reported. At an
    /// indent of 20 the same style is refused, as it always was.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedStyles))]
    public void FlatEntry_withAStyleTheWalkRefuses_keepsTheOutputItHad(string style)
    {
        foreach (var indent in new[] { 56.0, 1000.0 })
        {
            using var doc = Page(200, 400, new ListElement(ListStyle.OrderedDecimal) { Indent = indent }.Add(Entry(Refused(style))));

            Assert.Equal(FlatDigest, Sha(doc));
            Assert.Empty(doc.Diagnostics);
        }

        using var plain = Page(200, 400, new ListElement(ListStyle.OrderedDecimal) { Indent = 20 }.Add(Entry(Refused(style))));
        Assert.Throws<InvalidOperationException>(() => Sha(plain));
    }

    [Theory]
    [MemberData(nameof(RefusedStyles))]
    public void NestedChild_withAStyleTheWalkRefuses_keepsTheOutputItHad(string style)
    {
        var parent = new ListItem("p", H(10));
        parent.AddChild(Entry(Refused(style)));
        using var doc = Page(400, 400, new ListElement(ListStyle.OrderedDecimal) { Indent = 128 }.Add(parent));

        Assert.Equal(NestedDigest, Sha(doc));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// Four hundred entries on a page that holds one line each: the list is long enough to need
    /// the continuation bound if any entry were clamped. None is, so nothing is walked.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedStyles))]
    public void ManyEntries_withAStyleTheWalkRefuses_keepTheOutputTheyHad(string style)
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        for (var i = 0; i < 400; i++) list.Add(Entry(Refused(style), "W"));
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 40), Margins = new EdgeInsets(10) };
        doc.Add(list);

        Assert.Equal(ManyDigest, Sha(doc));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// The refusal is one entry's, not the list's: a second entry with an accepted style is still
    /// clamped, and the refused entry beside it is still not.
    /// </summary>
    [Fact]
    public void AnAcceptedEntry_besideARefusedOne_isStillClamped()
    {
        using var doc = Page(200, 400, new ListElement(ListStyle.OrderedDecimal) { Indent = 56 }
            .Add(Entry(Refused("red-nan")))
            .Add(Entry(H(10), "abc")));

        using var ms = new MemoryStream();
        doc.Save(ms);
        var content = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        Assert.Equal(["1.", "2.", "abc"], Regex.Matches(content, @"\(((?:\\.|[^\\)])*)\)\s*Tj").Select(m => m.Groups[1].Value).ToArray());
        Assert.Equal(1, Assert.Single(doc.Diagnostics).Count);
    }

    /// <summary>
    /// The predicate is the same rule as <c>ValidateStyle</c>: it says a style is accepted exactly
    /// when the validation does not throw, for every refused style and for the accepted ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedStyles))]
    public void TheClampsPredicate_agreesWithValidateStyle(string style)
    {
        var refused = Refused(style);
        var accepted = H(10);

        Assert.Throws<InvalidOperationException>(() => LayoutValidation.ValidateStyle(refused, "x"));
        LayoutValidation.ValidateStyle(accepted, "x");
        Assert.False(LayoutValidation.AreRunStylesAccepted([new TextRun("abc", refused)]));
        Assert.False(LayoutValidation.AreRunStylesAccepted([new TextRun("ok", accepted), new TextRun("abc", refused)]));
        Assert.True(LayoutValidation.AreRunStylesAccepted([new TextRun("abc", accepted)]));

        // A run that holds no text yields no fragment, so its style is never walked.
        Assert.True(LayoutValidation.AreRunStylesAccepted([new TextRun("abc", accepted), new TextRun("", refused)]));
    }

    /// <summary>
    /// An entry whose line is taller than the area is clamped, as any other, and its line is not
    /// drawn: the file is the one the base commit wrote, without the empty text object a drawn but
    /// empty paragraph would add. The report still counts the entry, whose gutter was narrowed.
    /// </summary>
    [Fact]
    public void AnEntry_whoseLineIsTallerThanTheArea_isWrittenAsItWas()
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 300 }
            .Add(new ListItem([new TextRun("big", H(500))]) { Style = H(10) });
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 400) };
        doc.Add(list);
        doc.Add(new Paragraph("after", H(10)));

        Assert.Equal("60DC9BDAB5A29371", Sha(doc));
        Assert.Equal(
            [LayoutDiagnosticCode.ListIndentClamped, LayoutDiagnosticCode.ListEntryPartDropped],
            doc.Diagnostics.Select(d => d.Code).ToArray());
    }

    /// <summary>
    /// A marker with no room mid-page: the entry moves whole to the next page, where its text is
    /// drawn and the clamp is reported. It is not the output the base commit wrote, which left the
    /// text off page 2.
    /// </summary>
    [Fact]
    public void AMarkerWithNoRoomMidPage_movesTheEntryAndItsClampedTextToTheNextPage()
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 100), Margins = new EdgeInsets(10, 72, 10, 72) };
        for (var i = 0; i < 5; i++) doc.Add(new Paragraph("l" + i, H(10)) { Margins = EdgeInsets.Zero });
        doc.Add(new ListElement(ListStyle.OrderedDecimal) { Indent = 56 }
            .Add(new ListItem("abc", H(10)) { Style = H(30) }));

        using var ms = new MemoryStream();
        doc.Save(ms);
        var content = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        Assert.Matches(@"\(1\.\)\s*Tj[\s\S]*\(abc\)\s*Tj", content);
        Assert.Equal(1, Assert.Single(doc.Diagnostics).Count);
        Assert.Equal(LayoutDiagnosticCode.ListIndentClamped, doc.Diagnostics[0].Code);
        doc.Dispose();
    }

    /// <summary>
    /// A list whose area has width but no height builds no entry, as the list's own layout does,
    /// so the refusal that reaches the caller is the "too tall" one, not the entry's null text.
    /// </summary>
    [Fact]
    public void AListWithNoHeight_isRefusedAsTooTall_beforeAnyEntryIsBuilt()
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000, Margins = new EdgeInsets(1000, 0, 0, 0) }
            .Add(new ListItem((string)null!, H(10)));
        using var doc = Page(200, 400, list);

        var ex = Assert.Throws<InvalidOperationException>(() => Sha(doc));

        Assert.StartsWith("An element is too tall to fit on a single page and cannot be rendered.", ex.Message, StringComparison.Ordinal);
    }
}
