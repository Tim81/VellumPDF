// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>What the element refuses at the call, and what the save refuses before any layout (#591): exact types, parameter names and messages.</summary>
public sealed class PreformattedRefusalTests
{
    // ── The constructor: text ────────────────────────────────────────────────

    private static bool IsRefused(char c) =>
        (char.GetUnicodeCategory(c) == UnicodeCategory.Control && c is not ('\t' or '\n' or '\r'))
        || (int)c is 0x2028 or 0x2029
        || char.IsSurrogate(c);

    /// <summary>Every UTF-16 code unit between two letters: refused exactly when it is a control other than tab, CR and LF, a mandatory line break, or half a surrogate pair.</summary>
    [Fact]
    public void EveryCodeUnit_isRefusedExactlyWhenTheRuleSaysSo()
    {
        var refused = 0;
        for (var u = 0; u <= 0xFFFF; u++)
        {
            var c = (char)u;
            var text = "x" + c + "y";
            if (!IsRefused(c))
            {
                Assert.Equal(text, new Preformatted(text).Text);
                continue;
            }

            refused++;
            var ex = Assert.Throws<ArgumentException>(() => new Preformatted(text));
            Assert.Equal("text", ex.ParamName);
            Assert.Contains($"U+{u:X4} at UTF-16 index 1", ex.Message, StringComparison.Ordinal);
        }

        // 62 controls (U+0000-U+001F less three, U+007F-U+009F), the two line breaks, 2,048 surrogates.
        Assert.Equal(29 + 33 + 2 + 2048, refused);
    }

    // Built in code: a lone surrogate in an attribute argument is replaced by U+FFFD on its way
    // into the assembly metadata.
    private static string Units(params int[] units) => new([.. units.Select(u => (char)u)]);

    public static TheoryData<string, int> UnpairedSurrogates() => new()
    {
        { Units(0xD800), 0 },
        { Units(0xDC00, 'x'), 0 },
        { Units(0xDC00, 0xD800), 0 },
        { Units('a', 'b', 0xD83D), 2 },
        { Units(0xD83D, 0xD83D, 0xDE00), 0 },
    };

    [Theory]
    [MemberData(nameof(UnpairedSurrogates))]
    public void AnUnpairedSurrogate_isRefused_byItsIndex(string text, int index)
    {
        var ex = Assert.Throws<ArgumentException>(() => new Preformatted(text));

        Assert.Equal("text", ex.ParamName);
        Assert.Contains($"at UTF-16 index {index},", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACharacterIsNamedByItsCodePoint_andTheReason()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Preformatted("ab\u000Bc"));

        Assert.Equal(
            "The text holds U+000B at UTF-16 index 2, which is a control character other than tab, carriage return and line feed. A preformatted block does not accept it. (Parameter 'text')",
            ex.Message);
    }

    [Fact]
    public void AValidSurrogatePair_isAccepted()
    {
        Assert.Equal("a\U0001F600b", new Preformatted("a\U0001F600b").Text);
    }

    [Fact]
    public void ANullText_isRefused()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new Preformatted(null!));

        Assert.Equal("text", ex.ParamName);
    }

    [Fact]
    public void ANullBlock_isRefusedByAdd()
    {
        using var doc = new Document();

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Add((Preformatted)null!));

        Assert.Equal("preformatted", ex.ParamName);
    }

    // ── The constructor: style ───────────────────────────────────────────────

    public static TheoryData<string, TextStyle, string> StyleRefusals() => new()
    {
        { "NaN size", new TextStyle { FontSize = double.NaN }, "The style's FontSize is NaN. It must be a finite number of at least 0.000005 points; a smaller size is written as zero." },
        { "zero size", new TextStyle { FontSize = 0 }, "The style's FontSize is 0. It must be a finite number of at least 0.000005 points; a smaller size is written as zero." },
        { "negative size", new TextStyle { FontSize = -1 }, "The style's FontSize is -1. It must be a finite number of at least 0.000005 points; a smaller size is written as zero." },
        { "size below the written threshold", new TextStyle { FontSize = 4.9e-6 }, "The style's FontSize is 4.9E-06. It must be a finite number of at least 0.000005 points; a smaller size is written as zero." },
        { "infinite size", new TextStyle { FontSize = double.PositiveInfinity }, "The style's FontSize is positive infinity. It must be a finite number of at least 0.000005 points; a smaller size is written as zero." },
        { "size above the ceiling", new TextStyle { FontSize = Math.BitIncrement(1e30) }, "The style's FontSize is 1.0000000000000002E+30. It must be at most 1E+30 points, which keeps the numbers the block writes far inside the 3.403 \u00D7 10^38 limit of ISO 32000-1, Annex C, Table C.1." },
        { "size of 1e39", new TextStyle { FontSize = 1e39 }, "The style's FontSize is 1E+39. It must be at most 1E+30 points, which keeps the numbers the block writes far inside the 3.403 \u00D7 10^38 limit of ISO 32000-1, Annex C, Table C.1." },
        { "NaN leading", new TextStyle { Leading = double.NaN }, "The style's Leading is NaN. It must be a finite number; zero or less means automatic leading." },
        { "infinite leading", new TextStyle { Leading = double.PositiveInfinity }, "The style's Leading is positive infinity. It must be a finite number; zero or less means automatic leading." },
        { "red above 1", new TextStyle { Color = new ColorRgb(1.5, 0, 0) }, "The style's Color has a red channel of 1.5. Each channel must be a number from 0 to 1 (ISO 32000-2:2020, 8.6.8)." },
        { "green below 0", new TextStyle { Color = new ColorRgb(0, -0.1, 0) }, "The style's Color has a green channel of -0.1. Each channel must be a number from 0 to 1 (ISO 32000-2:2020, 8.6.8)." },
        { "blue NaN", new TextStyle { Color = new ColorRgb(0, 0, double.NaN) }, "The style's Color has a blue channel of NaN. Each channel must be a number from 0 to 1 (ISO 32000-2:2020, 8.6.8)." },
        { "unnamed font", new TextStyle { Font = (Standard14)99 }, "The style's font is the Standard14 value 99, which the enumeration does not name." },
        { "negative font", new TextStyle { Font = (Standard14)(-1) }, "The style's font is the Standard14 value -1, which the enumeration does not name." },
        { "Symbol", new TextStyle { Font = Standard14.Symbol }, "The style's font is Symbol, which has no WinAnsiEncoding glyphs and measures as zero wide, so no line could be wrapped." },
        { "ZapfDingbats", new TextStyle { Font = Standard14.ZapfDingbats }, "The style's font is ZapfDingbats, which has no WinAnsiEncoding glyphs and measures as zero wide, so no line could be wrapped." },
        { "link", new TextStyle { LinkUri = "https://example.com/" }, "The style has a LinkUri. A preformatted block does not draw links, so it would be dropped." },
    };

    [Theory]
    [MemberData(nameof(StyleRefusals))]
    public void ARefusedStyle_isRefusedAtTheConstructor(string name, TextStyle style, string message)
    {
        Assert.NotEmpty(name);

        var ex = Assert.Throws<ArgumentException>(() => new Preformatted("x", style));

        Assert.Equal("style", ex.ParamName);
        Assert.Equal(message + " (Parameter 'style')", ex.Message);
    }

    [Fact]
    public void TheLargestFontSize_isAccepted_andSaves()
    {
        using var doc = new Document();
        doc.Add(new Preformatted("x", new TextStyle { Font = Standard14.Courier, FontSize = 1e30, Leading = 12 }));

        var content = Assert.Single(PageContents(Save(doc)));

        Assert.Matches(@"/F1 1(0{30})(\.0+)? Tf\n", content);
    }

    [Fact]
    public void TheSmallestWrittenSize_isAccepted_andSaves()
    {
        using var doc = new Document();
        doc.Add(new Preformatted("x", new TextStyle { Font = Standard14.Courier, FontSize = 5e-6 }));

        var content = Assert.Single(PageContents(Save(doc)));

        Assert.Contains("/F1 0.00001 Tf\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatARefusedStyleIsNot_isAccepted()
    {
        using var doc = new Document();

        // A proportional font draws and its tabs still expand; a null handle draws Helvetica; a
        // zero or negative leading is automatic.
        doc.Add(new Preformatted("a\tb", new TextStyle { Font = Standard14.Helvetica }));
        doc.Add(new Preformatted("n", new TextStyle { FontRef = new FontReference((EmbeddedFontHandle)null!) }));
        doc.Add(new Preformatted("l", new TextStyle { Leading = -3 }));

        var contents = PageContents(Save(doc));

        Assert.Equal(["a       b", "n", "l"], Literals(Assert.Single(contents)));
    }

    [Fact]
    public void AnEmbeddedHandleFromAnotherDocument_isRefusedBySave()
    {
        using var owner = new Document();
        var foreign = MonoStyle(owner);
        using var doc = new Document();
        doc.Add(new Preformatted("x", foreign));

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));

        Assert.Equal("handle", ex.ParamName);
    }

    // ── The initialisers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(32, true)]
    [InlineData(33, false)]
    [InlineData(-4, false)]
    public void TabWidth_isFrom1To32(int value, bool accepted)
    {
        if (accepted)
        {
            Assert.Equal(value, new Preformatted("x") { TabWidth = value }.TabWidth);
            return;
        }

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Preformatted("x") { TabWidth = value });
        Assert.Equal("value", ex.ParamName);
        Assert.Equal(8, new Preformatted("x").TabWidth);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0, 0, "Top")]
    [InlineData(0, -1, 0, 0, "Right")]
    [InlineData(0, 0, double.PositiveInfinity, 0, "Bottom")]
    [InlineData(0, 0, 0, double.NegativeInfinity, "Left")]
    public void Margins_refuseAnEdgeThatIsNotFiniteOrIsNegative(double top, double right, double bottom, double left, string edge)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new Preformatted("x") { Margins = new EdgeInsets(top, right, bottom, left) });

        Assert.Equal("value", ex.ParamName);
        Assert.StartsWith($"The {edge} margin must be a finite number that is not negative.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EdgeInsets.Zero, new Preformatted("x").Margins);
    }

    [Fact]
    public void ABreakBeforeTheEnumerationDoesNotName_isRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Preformatted("x") { BreakBefore = (PageBreakMode)7 });

        Assert.Equal("value", ex.ParamName);
        Assert.Equal(PageBreakMode.Recto, new Preformatted("x") { BreakBefore = PageBreakMode.Recto }.BreakBefore);
    }

    [Fact]
    public void TheDefaultStyle_isCourier10_andIsNotTheDocumentsDefault()
    {
        using var doc = new Document();
        doc.SetDefaultFont(new TextStyle { Font = Standard14.TimesBold, FontSize = 20 });

        var block = new Preformatted("x");

        Assert.Equal(Standard14.Courier, block.Style.Font);
        Assert.Equal(10, block.Style.FontSize);
        Assert.Equal(12, block.Style.EffectiveLeading);
        Assert.Equal(ColorRgb.Black, block.Style.Color);
        Assert.NotSame(TextStyle.Default, block.Style);
        Assert.Same(block.Style, new Preformatted("y").Style);
        var own = new TextStyle { FontSize = 14 };
        Assert.Same(own, new Preformatted("z", own).Style);
    }

    // ── The save, before any layout ──────────────────────────────────────────

    private static DateTimeOffset Stamp => new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static Document NewDocument() => new()
    {
        Timestamp = Stamp,
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
    };

    /// <summary>The save throws, writes nothing, lists nothing, and leaves the document saveable once the named thing is changed.</summary>
    private static string RefusedMessage(Document doc)
    {
        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
        Assert.Equal(0, ms.Length);
        Assert.Empty(doc.Diagnostics);
        return ex.Message;
    }

    private static string PagesOf(Document doc) => string.Join("\f", PageContents(Save(doc)));

    [Fact]
    public void AContentBoxShorterThanOneLine_isRefusedBeforeLayout_andARetrySavesOnce()
    {
        using var doc = NewDocument();
        doc.Margins = new EdgeInsets(370, 72, 370, 72);
        doc.Add(new Preformatted("x", new TextStyle { Font = Standard14.Courier, FontSize = 100 }));

        Assert.Equal(
            "Preformatted element 0 cannot hold a line: its content box is 451.28 by 101.89 pt and one line is 120 pt tall. Reduce the font size, the element's margins or the document's margins, or increase the page size.",
            RefusedMessage(doc));

        doc.Margins = new EdgeInsets(72);
        var retry = PagesOf(doc);

        using var fresh = NewDocument();
        fresh.Add(new Preformatted("x", new TextStyle { Font = Standard14.Courier, FontSize = 100 }));
        Assert.Equal(PagesOf(fresh), retry);
    }

    [Fact]
    public void AContentBoxNoWiderThanZero_isRefusedBeforeLayout_andARetrySaves()
    {
        using var doc = NewDocument();
        doc.PageSize = new PdfRectangle(0, 0, 100, 300);
        doc.Margins = new EdgeInsets(10);
        doc.Add(new Paragraph("before"));
        doc.Add(new Preformatted("x", Courier()) { Margins = new EdgeInsets(0, 40, 0, 40) });

        Assert.Equal(
            "Preformatted element 1 cannot hold a line: its content box is 0 by 280 pt and one line is 12 pt tall. Reduce the font size, the element's margins or the document's margins, or increase the page size.",
            RefusedMessage(doc));

        doc.PageSize = new PdfRectangle(0, 0, 200, 300);
        var pages = PageContents(Save(doc));

        Assert.Equal(["before", "x"], Literals(Assert.Single(pages)));
    }

    public static TheoryData<PdfConformance, string, string> ClaimClauses() => new()
    {
        { PdfConformance.PdfA2b, "PDF/A-2b", "ISO 19005-2:2011, 6.2.11.4.1" },
        { PdfConformance.PdfA2u, "PDF/A-2u", "ISO 19005-2:2011, 6.2.11.4.1" },
        { PdfConformance.PdfA2a, "PDF/A-2a", "ISO 19005-2:2011, 6.2.11.4.1" },
        { PdfConformance.PdfUA1, "PDF/UA-1", "ISO 14289-1:2014, 7.21.4.1" },
    };

    private static Document Claimed(PdfConformance claim)
    {
        var doc = NewDocument();
        doc.Conformance = claim;
        doc.Info.Title = "t";
        doc.Language = "en-US";
        return doc;
    }

    [Theory]
    [MemberData(nameof(ClaimClauses))]
    public void TheDefaultCourier_isRefusedUnderAClaim_andARetryWithNoClaimSaves(PdfConformance claim, string name, string clause)
    {
        using var doc = Claimed(claim);
        doc.Add(new Preformatted("x"));

        Assert.Equal(
            $"Preformatted element 0 is set in Courier, which is not embedded, and the document claims {name}. {clause} requires every font it uses to be embedded. Set the style's FontRef to an embedded font, or set Document.Conformance to None.",
            RefusedMessage(doc));

        doc.Conformance = PdfConformance.None;
        Assert.Equal(["x"], Literals(Assert.Single(PageContents(Save(doc)))));
    }

    [Fact]
    public void ACharacterTheFontLacks_isRefusedUnderAClaim_namingItsPosition()
    {
        Assert.Equal(0, Gid(MonoPath, 0x26B9));
        using var doc = Claimed(PdfConformance.PdfA2u);
        doc.Add(new Preformatted("\nab\ta\u26B9b", MonoStyle(doc)));

        Assert.Equal(
            "Preformatted element 0 holds U+26B9 at index 5 of its text (line 2), which font TT1 has no glyph for, and the document claims PDF/A-2u. ISO 19005-2:2011, 6.2.11.8 does not allow a reference to the .notdef glyph. Use a font that covers the character, or set Document.Conformance to None.",
            RefusedMessage(doc));
    }

    [Fact]
    public void ALineNumber_countsCrLfOnce_andALoneCrOnce()
    {
        using var doc = Claimed(PdfConformance.PdfA2u);
        doc.Add(new Preformatted("a\rb\r\nc\u26B9", MonoStyle(doc)));

        Assert.Equal(
            "Preformatted element 0 holds U+26B9 at index 6 of its text (line 3), which font TT1 has no glyph for, and the document claims PDF/A-2u. ISO 19005-2:2011, 6.2.11.8 does not allow a reference to the .notdef glyph. Use a font that covers the character, or set Document.Conformance to None.",
            RefusedMessage(doc));
    }

    [Fact]
    public void ACharacterOutsideTheBmp_isNamedByItsScalar_notByItsHighSurrogate()
    {
        Assert.Equal(0, Gid(MonoPath, 0x1F600));
        using var doc = Claimed(PdfConformance.PdfA2u);
        doc.Add(new Preformatted("a\U0001F600b", MonoStyle(doc)));

        Assert.Equal(
            "Preformatted element 0 holds U+1F600 at index 1 of its text (line 1), which font TT1 has no glyph for, and the document claims PDF/A-2u. ISO 19005-2:2011, 6.2.11.8 does not allow a reference to the .notdef glyph. Use a font that covers the character, or set Document.Conformance to None.",
            RefusedMessage(doc));
    }

    [Fact]
    public void TheCharacterCheckCountsATabsSpaces_underTheClauseForUa()
    {
        // A font with no space glyph is not in the tree, so a tab is checked through the space it
        // becomes: here the space is present, the check passes, and only the missing letter refuses.
        Assert.NotEqual(0, Gid(MonoPath, ' '));
        using var doc = Claimed(PdfConformance.PdfUA1);
        doc.Add(new Preformatted("a\t\u26B9", MonoStyle(doc)));

        Assert.Equal(
            "Preformatted element 0 holds U+26B9 at index 2 of its text (line 1), which font TT1 has no glyph for, and the document claims PDF/UA-1. ISO 14289-1:2014, 7.21.8 does not allow a reference to the .notdef glyph. Use a font that covers the character, or set Document.Conformance to None.",
            RefusedMessage(doc));
    }

    [Fact]
    public void ARefusedClaimThenNoClaim_savesWhatAFreshDocumentSaves()
    {
        string Build(bool refuseFirst)
        {
            using var doc = NewDocument();
            var style = MonoStyle(doc);
            doc.Add(new Preformatted("a\u26B9b\n  c", style));
            if (refuseFirst)
            {
                doc.Conformance = PdfConformance.PdfA2u;
                _ = RefusedMessage(doc);
                doc.Conformance = PdfConformance.None;
            }

            return PlacementLoopSupport.NormalisedSha256(Save(doc), foldToUnicodeLineEnds: true);
        }

        Assert.Equal(Build(refuseFirst: false), Build(refuseFirst: true));
    }

    [Fact]
    public void ACharacterTheFontLacks_withNoClaim_isDrawnAsGlyphZero_andReportedAsNotdefGlyph()
    {
        using var doc = NewDocument();
        doc.Add(new Preformatted("a\u26B9b\u26B9", MonoStyle(doc)));

        var hex = Assert.Single(HexStrings(Assert.Single(PageContents(Save(doc)))));

        Assert.Equal($"<{Gid(MonoPath, 'a'):X4}0000{Gid(MonoPath, 'b'):X4}0000>", hex);
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NotdefGlyph, item.Code);
        Assert.Equal(102, (int)item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Null(item.Clause);
        Assert.Equal(LayoutElementKind.Preformatted, item.ElementKind);
        Assert.Equal(0, item.ElementIndex);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(2, item.Count);
        Assert.StartsWith("U+26B9 has no glyph in ", item.Message);
    }

    [Fact]
    public void ACharacterTheFontLacks_withNoClaim_isRefusedByStrictDiagnostics()
    {
        using var doc = NewDocument();
        doc.StrictDiagnostics = true;
        doc.Add(new Preformatted("a\u26B9", MonoStyle(doc)));
        using var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.StartsWith("StrictDiagnostics refused the save: NotdefGlyph in Preformatted 0, page 1: U+26B9 has no glyph in ", ex.Message);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void ANarrowNoBreakSpace_isCovered_andDrawnAsTheNoBreakSpacesGlyph()
    {
        Assert.NotEqual(0, Gid(MonoPath, 0xA0));
        Assert.Equal(Gid(MonoPath, 0xA0), Gid(MonoPath, 0x202F));
        using var doc = Claimed(PdfConformance.PdfA2u);
        doc.Add(new Preformatted("1\u202F000", MonoStyle(doc)));

        var hex = Assert.Single(HexStrings(Assert.Single(PageContents(Save(doc)))));

        Assert.Equal(HexOf(MonoPath, "1\u00A0000"), hex);
    }

    // ── U+FEFF and U+FFFE ────────────────────────────────────────────────────

    [Theory]
    [InlineData(PdfConformance.PdfA2u, "PDF/A-2u", "ISO 19005-2:2011, 6.2.11.7.2")]
    [InlineData(PdfConformance.PdfA2a, "PDF/A-2a", "ISO 19005-2:2011, 6.2.11.7.2")]
    [InlineData(PdfConformance.PdfUA1, "PDF/UA-1", "ISO 14289-1:2014, 7.21.7")]
    public void AByteOrderMark_isRefused_underTheClaimsThatBindToUnicode_evenWhenTheFontMapsIt(PdfConformance claim, string name, string clause)
    {
        Assert.NotEqual(0, Gid(DejaVuPath, 0xFEFF));
        using var doc = Claimed(claim);
        doc.Add(new Preformatted("ab\uFEFF", new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10 }));

        Assert.Equal(
            $"Preformatted element 0 holds U+FEFF at index 2 of its text (line 1), and the document claims {name}. {clause} does not allow a ToUnicode value of U+FEFF or U+FFFE. Remove the character, or set Document.Conformance to None.",
            RefusedMessage(doc));
    }

    [Fact]
    public void AByteOrderMark_isAccepted_underPdfA2b()
    {
        using var doc = Claimed(PdfConformance.PdfA2b);
        doc.Add(new Preformatted("ab\uFEFF", new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10 }));

        var hex = Assert.Single(HexStrings(Assert.Single(PageContents(Save(doc)))));

        Assert.Equal(HexOf(DejaVuPath, "ab\uFEFF"), hex);
    }

    [Fact]
    public void TheFirstFailingCheckWins_geometryBeforeFontBeforeCharacters()
    {
        using var doc = Claimed(PdfConformance.PdfA2u);
        doc.Add(new Preformatted("x"));
        doc.Add(new Preformatted("y", new TextStyle { Font = Standard14.Courier, FontSize = 1000 }));

        // Element 0 is the first to fail, on its font; element 1 would fail on its height.
        Assert.StartsWith("Preformatted element 0 is set in Courier", RefusedMessage(doc), StringComparison.Ordinal);
    }
}
