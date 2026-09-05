// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>
/// Exercises <see cref="SimpleFontReader"/> against hand-built font dictionaries (the
/// <see cref="FontTestSupport"/> style), covering §9.6.5's encoding resolution,
/// <c>/Differences</c>, <c>/Widths</c>, and the diagnostics each malformation reports.
/// </summary>
public sealed class SimpleFontReaderTests
{
    private static DecodedGlyph Decode(PdfFontReader reader, byte code)
    {
        ReadOnlySpan<byte> bytes = [code];
        var offset = 0;
        Assert.True(reader.TryDecodeNext(bytes, ref offset, out var glyph));
        Assert.Equal(1, offset);
        return glyph;
    }

    private static SimpleFontReader Build(
        PdfDocumentReader doc, PdfDictionary fontDict, DiagnosticSink sink,
        int? objectNumber = null, int? generation = null, int? pageIndex = null) =>
        SimpleFontReader.Create(doc, fontDict, objectNumber, generation, sink, pageIndex);

    private static PdfDictionary Type1(string baseFont) =>
        new PdfDictionary().Set(PdfName.Subtype, "Type1").Set(PdfName.BaseFont, baseFont);

    // ── 1: no /Encoding, no /Widths ──────────────────────────────────────────────────────────────

    [Fact]
    public void Helvetica_noEncoding_noWidths_nonsymbolic()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var reader = Build(doc, Type1("Helvetica"), sink);

        var a = Decode(reader, 0x41);
        Assert.Equal("A", a.Unicode);
        Assert.Equal(667, a.Width);

        // The discriminating cell against WinAnsi's quotesingle: StandardEncoding's 0x27 is
        // quoteright, U+2019, not the ASCII apostrophe.
        var quote = Decode(reader, 0x27);
        Assert.Equal("’", quote.Unicode);

        Assert.Empty(sink.Diagnostics);
    }

    // ── 2: /Encoding /WinAnsiEncoding ────────────────────────────────────────────────────────────

    [Fact]
    public void Helvetica_winAnsiEncoding()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, "WinAnsiEncoding");
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("'", Decode(reader, 0x27).Unicode);
        Assert.Equal("€", Decode(reader, 0x80).Unicode);
        Assert.Equal(" ", Decode(reader, 0xA0).Unicode);
        Assert.Equal("-", Decode(reader, 0xAD).Unicode);
        Assert.Equal("•", Decode(reader, 0x7F).Unicode);
        Assert.Equal("•", Decode(reader, 0x81).Unicode);
    }

    // ── 3: /Encoding /MacRomanEncoding ───────────────────────────────────────────────────────────

    [Fact]
    public void Helvetica_macRomanEncoding()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, "MacRomanEncoding");
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(" ", Decode(reader, 0xCA).Unicode);
        Assert.Equal("¤", Decode(reader, 0xDB).Unicode); // currency
    }

    // ── 4: indirect /Encoding, /BaseEncoding + /Differences ─────────────────────────────────────

    [Fact]
    public void IndirectEncodingDictionary_baseEncodingAndDifferences()
    {
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                + "/Encoding 7 0 R >>"),
            new FontTestSupport.Obj(7, "<< /Type /Encoding /BaseEncoding /WinAnsiEncoding "
                + "/Differences [65 /Bsmall 66 /C /D 200 /Euro] >>"));

        var sink = new DiagnosticSink(50);
        var fontDict = (PdfDictionary)doc.Resolve(5)!;
        var reader = Build(doc, fontDict, sink, objectNumber: 5, generation: 0);

        Assert.Equal("\uF762", Decode(reader, 0x41).Unicode); // Bsmall, AGL private-use mapping.
        Assert.Equal("C", Decode(reader, 0x42).Unicode);
        Assert.Equal("D", Decode(reader, 0x43).Unicode);
        Assert.Equal("€", Decode(reader, 0xC8).Unicode); // Euro
        Assert.Equal("D", Decode(reader, 0x44).Unicode); // unchanged WinAnsi "D", never touched.
    }

    // ── 5: /Differences malformations ───────────────────────────────────────────────────────────

    [Fact]
    public void Differences_codeOutOfRange_reports401Once_tableUnchanged()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray().Add(new PdfInteger(300)).Add(new PdfName("A"));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("A", Decode(reader, 0x41).Unicode); // StandardEncoding's own 0x41, untouched.
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontEncodingMalformed, d.Code);
    }

    [Fact]
    public void Differences_overflowPast255_assignsUpTo255_reports401Once()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray()
            .Add(new PdfInteger(250))
            .Add(new PdfName("A")).Add(new PdfName("B")).Add(new PdfName("C"))
            .Add(new PdfName("D")).Add(new PdfName("E")).Add(new PdfName("F"))
            .Add(new PdfName("G"));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("A", Decode(reader, 250).Unicode);
        Assert.Equal("F", Decode(reader, 255).Unicode);
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontEncodingMalformed, d.Code);
    }

    [Fact]
    public void Differences_unresolvedElementType_reports401WithDoesNotResolveMessage()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray()
            .Add(new PdfInteger(65)).Add(new PdfIndirectReference(5, 0));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("A", Decode(reader, 0x41).Unicode); // kept its base StandardEncoding name.
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontEncodingMalformed, d.Code);
        Assert.Contains("does not resolve", d.Message);
    }

    [Fact]
    public void Differences_nameLongerThanBound_reports401Once_codeStaysUndefined()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var longName = new string('a', 129);
        var differences = new PdfArray().Add(new PdfInteger(65)).Add(new PdfName(longName));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        var reader = Build(doc, fontDict, sink);

        // Decoding the now-unmapped 0x41 also trips 404 (Helvetica's other codes are mapped),
        // which is a separate, legitimate condition; this checks for the 401 specifically.
        Assert.Null(Decode(reader, 0x41).Unicode);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
    }

    // ── 6: /Encoding shapes ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Encoding_unknownName_reports401_usesStandardEncoding()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, "Bogus");
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("’", Decode(reader, 0x27).Unicode); // StandardEncoding's quoteright.
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontEncodingMalformed, d.Code);
    }

    [Fact]
    public void Encoding_integer_reports401()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, new PdfInteger(42));
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontEncodingMalformed, d.Code);
    }

    [Fact]
    public void Encoding_standardEncodingName_acceptedSilently()
    {
        // Table D.1's own note ("PDF processors shall not have a predefined encoding named
        // StandardEncoding") is about built-in encodings, not about what a font's /Encoding may
        // name; accepting it here is a deliberate leniency (see the class doc).
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, "StandardEncoding");
        Build(doc, fontDict, sink);

        Assert.Empty(sink.Diagnostics);
    }

    // ── 7: symbolic flag ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SymbolicFlagSet_noEncoding_notEmbedded_allCellsNull_reports403()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var descriptor = new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(4));
        var fontDict = Type1("Foo").Set(new PdfName("FontDescriptor"), descriptor);
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0x41).Unicode);
        // "Foo" is also not a standard 14 font, so FontWidthsMalformed fires alongside the 403
        // this test is pinning; both are legitimate for this font, so this checks for the 403
        // specifically rather than asserting it is the only diagnostic.
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontNoUnicodeRoute);
    }

    [Fact]
    public void FlagsNonsymbolicOnly_usesStandardEncoding()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var descriptor = new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(32));
        var fontDict = Type1("Foo").Set(new PdfName("FontDescriptor"), descriptor);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("’", Decode(reader, 0x27).Unicode); // StandardEncoding's quoteright.
    }

    [Fact]
    public void FlagsBothSymbolicAndNonsymbolic_symbolicWins()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var descriptor = new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(36));
        var fontDict = Type1("Foo").Set(new PdfName("FontDescriptor"), descriptor);
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0x41).Unicode); // all-null table, symbolic with no encoding.
    }

    // ── 8: embedded TrueType, no /Encoding ───────────────────────────────────────────────────────

    [Fact]
    public void EmbeddedNonsymbolicTrueType_noEncoding_usesStandardEncoding()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var descriptor = new PdfDictionary()
            .Set(new PdfName("Flags"), new PdfInteger(32))
            .Set(new PdfName("FontFile2"), new PdfStream([1, 2, 3]));
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), descriptor);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("’", Decode(reader, 0x27).Unicode); // the stated deviation.
    }

    // ── 9: Symbol / ZapfDingbats base fonts ──────────────────────────────────────────────────────

    [Fact]
    public void SymbolBaseFont_noEncoding()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Symbol");
        var reader = Build(doc, fontDict, sink);

        var alpha = Decode(reader, 0x61);
        Assert.Equal("α", alpha.Unicode);
        Assert.Equal(631, alpha.Width);

        Assert.Equal("∀", Decode(reader, 0x22).Unicode); // universal
    }

    [Fact]
    public void ZapfDingbatsBaseFont()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("ZapfDingbats");
        var reader = Build(doc, fontDict, sink);

        var a1 = Decode(reader, 0x21);
        Assert.Equal("✁", a1.Unicode);
        Assert.Equal(974, a1.Width);

        var a191 = Decode(reader, 0xFE);
        Assert.Equal("➾", a191.Unicode);
        Assert.Equal(918, a191.Width);

        var a89 = Decode(reader, 0x80); // the AFM-only code.
        Assert.Equal("❨", a89.Unicode);
        Assert.Equal(390, a89.Width);
    }

    // ── 10: 403 vs 404 ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SymbolicNonStandardFont_noEncodingNoToUnicode_reports403Never404()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var descriptor = new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(4));
        var fontDict = Type1("Foo").Set(new PdfName("FontDescriptor"), descriptor);
        var reader = Build(doc, fontDict, sink);

        Decode(reader, 0x41);
        Decode(reader, 0x42);

        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontNoUnicodeRoute);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnmappedGlyphs);
    }

    [Fact]
    public void WinAnsiFont_unmappedDifferenceName_reports404OnceOnFirstDecode_never403()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray().Add(new PdfInteger(65)).Add(new PdfName("g123"));
        var encoding = new PdfDictionary()
            .Set(new PdfName("BaseEncoding"), new PdfName("WinAnsiEncoding"))
            .Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0x41).Unicode);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnmappedGlyphs);

        Decode(reader, 0x41); // a second decode of the same unmapped code: nothing new reported.
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnmappedGlyphs);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontNoUnicodeRoute);
    }

    // ── 11: /Widths ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Widths_explicitArray_missingWidthForOutOfRangeCodes()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var widths = new PdfArray().Add(new PdfInteger(500)).Add(new PdfInteger(600)).Add(new PdfInteger(700));
        var fontDict = Type1("Helvetica")
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(67))
            .Set(new PdfName("Widths"), widths);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(500, Decode(reader, 65).Width);
        Assert.Equal(600, Decode(reader, 66).Width);
        Assert.Equal(700, Decode(reader, 67).Width);
        Assert.Equal(0, Decode(reader, 68).Width);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Widths_missingWidthFromDescriptor()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var widths = new PdfArray().Add(new PdfInteger(500)).Add(new PdfInteger(600)).Add(new PdfInteger(700));
        var descriptor = new PdfDictionary().Set(new PdfName("MissingWidth"), new PdfInteger(250));
        var fontDict = Type1("Helvetica")
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(67))
            .Set(new PdfName("Widths"), widths)
            .Set(new PdfName("FontDescriptor"), descriptor);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(250, Decode(reader, 68).Width);
    }

    [Fact]
    public void Widths_shortArray_reports402Once_missingWidthForShortfall()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var widths = new PdfArray().Add(new PdfInteger(500));
        var fontDict = Type1("Helvetica")
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(67))
            .Set(new PdfName("Widths"), widths);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(500, Decode(reader, 65).Width);
        Assert.Equal(0, Decode(reader, 66).Width);
        Assert.Equal(0, Decode(reader, 67).Width);
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontWidthsMalformed, d.Code);
    }

    [Fact]
    public void Widths_nonNumberElement_reports402Once_missingWidthForThatCode()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var widths = new PdfArray().Add(new PdfInteger(500)).Add(new PdfName("x")).Add(new PdfInteger(700));
        var fontDict = Type1("Helvetica")
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(67))
            .Set(new PdfName("Widths"), widths);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(0, Decode(reader, 66).Width);
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontWidthsMalformed, d.Code);
    }

    [Fact]
    public void Widths_indirectArrayAndElement_accepted()
    {
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                + "/FirstChar 65 /LastChar 65 /Widths 12 0 R >>"),
            new FontTestSupport.Obj(12, "[13 0 R]"),
            new FontTestSupport.Obj(13, "999"));

        var sink = new DiagnosticSink(50);
        var fontDict = (PdfDictionary)doc.Resolve(5)!;
        var reader = Build(doc, fontDict, sink, objectNumber: 5, generation: 0);

        Assert.Equal(999, Decode(reader, 65).Width);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void FirstCharOutOfRange_reports402Once()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var widths = new PdfArray().Add(new PdfInteger(500));
        var fontDict = Type1("Helvetica")
            .Set(new PdfName("FirstChar"), new PdfInteger(300))
            .Set(new PdfName("LastChar"), new PdfInteger(300))
            .Set(new PdfName("Widths"), widths);
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontWidthsMalformed, d.Code);
    }

    [Fact]
    public void NonStandardFont_noWidths_reports402Once_allMissingWidth()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Foo");
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(0, Decode(reader, 0x41).Width);
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontWidthsMalformed, d.Code);
    }

    // ── 12: dangling reference ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DanglingEncodingReference_treatedAsAbsent_no401()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, new PdfIndirectReference(99, 0));
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("’", Decode(reader, 0x27).Unicode); // falls back to StandardEncoding.
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
    }

    // ── 13: GetFontReader ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetFontReader_type0AndType3_returnNull_noDiagnostic()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);

        var type0 = new PdfDictionary().Set(PdfName.Subtype, "Type0");
        Assert.Null(doc.GetFontReader(type0, sink, null));

        var type3 = new PdfDictionary().Set(PdfName.Subtype, "Type3");
        Assert.Null(doc.GetFontReader(type3, sink, null));

        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void GetFontReader_unknownSubtype_reports400Once()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = new PdfDictionary().Set(PdfName.Subtype, "Foo");
        Assert.Null(doc.GetFontReader(fontDict, sink, null));

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontUnreadable, d.Code);
    }

    [Fact]
    public void GetFontReader_notADictionary_reports400Once()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        Assert.Null(doc.GetFontReader(new PdfInteger(1), sink, null));

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontUnreadable, d.Code);
    }

    [Fact]
    public void GetFontReader_sameIndirectFont_returnsSameInstance()
    {
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        var sink = new DiagnosticSink(50);

        var first = doc.GetFontReader(new PdfIndirectReference(5, 0), sink, null);
        var second = doc.GetFontReader(new PdfIndirectReference(5, 0), sink, null);
        Assert.Same(first, second);
    }

    [Fact]
    public void GetFontReader_twoDirectDictionaries_returnTwoInstances()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var a = doc.GetFontReader(Type1("Helvetica"), sink, null);
        var b = doc.GetFontReader(Type1("Helvetica"), sink, null);
        Assert.NotSame(a, b);
    }

    // ── 14: diagnostics carry object number, generation, page index ─────────────────────────────

    [Fact]
    public void Diagnostics_carryObjectNumberGenerationAndPageIndex()
    {
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Foo "
                + "/Encoding /Bogus >>"));
        var sink = new DiagnosticSink(50);
        var fontDict = (PdfDictionary)doc.Resolve(5)!;
        Build(doc, fontDict, sink, objectNumber: 5, generation: 0, pageIndex: 2);

        // This font also has no /Widths, so FontWidthsMalformed fires alongside the pinned
        // FontEncodingMalformed; both must carry the same object number, generation and page.
        Assert.NotEmpty(sink.Diagnostics);
        foreach (var d in sink.Diagnostics)
        {
            Assert.Equal(5, d.ObjectNumber);
            Assert.Equal(0, d.Generation);
            Assert.Equal(2, d.PageIndex);
        }
    }

    // ── 16: allocation bound ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_allocatesUnder64KiB_forA100000ElementWidthsArray()
    {
        using var doc = FontTestSupport.OpenMinimal();

        // One PdfInteger instance shared by every slot: the parser has no array-length cap of its
        // own, so a hostile /Widths can be arbitrarily long, but Create reads at most
        // LastChar - FirstChar + 1 (here, 1) elements and never copies the array itself.
        var shared = new PdfInteger(500);
        var widths = new PdfArray();
        for (var i = 0; i < 100_000; i++)
            widths.Add(shared);
        var fontDict = Type1("Helvetica")
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), widths);

        // Warm-up: JIT and any lazy static (AdobeGlyphList's own load) must not be charged to the
        // measured call.
        Build(doc, Type1("Helvetica"), new DiagnosticSink(50));

        var before = GC.GetAllocatedBytesForCurrentThread();
        Build(doc, fontDict, new DiagnosticSink(50));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Measured 31,752 bytes on this runtime (the per-font string/width/Unicode tables, the
        // ToArray() copies of the shared encoding statics, and the Unicode strings themselves);
        // 64 KiB is a generous bound that still fails if Create starts copying the
        // 100,000-element array instead of indexing into it.
        Assert.True(allocated < 64 * 1024, $"Create allocated {allocated} bytes, expected < 64 KiB.");
    }

    // ── 17: DiagnosticExcerpt quoting ────────────────────────────────────────────────────────────

    [Fact]
    public void BaseFontMessage_quotedThroughDiagnosticExcerpt()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var oneMiBName = new string('B', 1024 * 1024);
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "Type1")
            .Set(PdfName.BaseFont, oneMiBName);
        Build(doc, fontDict, sink);

        // No standard 14 font resolves from this oversized name, so FontWidthsMalformed fires
        // alongside the pinned FontUnreadable.
        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontUnreadable);
        Assert.True(d.Message.Length < 200, $"message was {d.Message.Length} characters long.");
        // DiagnosticExcerpt.Quote's exact shape: the first 32 characters, an ellipsis, and the
        // decoded value's own byte length in parentheses.
        Assert.Contains(new string('B', 32) + "... (1048576 bytes)", d.Message);
    }
}
