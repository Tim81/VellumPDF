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
    public void Differences_unresolvedElementType_reports401WithIndirectReferenceMessage()
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
        Assert.Equal(
            "/Differences contains an indirect reference, which this reader does not follow "
                + "inside /Differences.",
            d.Message);
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

    [Theory]
    [InlineData("dictionary")]
    [InlineData("integer")]
    public void Differences_presentButNotAnArray_reports401Once(string shape)
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        PdfObject differences = shape switch
        {
            "dictionary" => new PdfDictionary(),
            "integer" => new PdfInteger(7),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
        Assert.Contains("not an array", d.Message);
    }

    [Fact]
    public void Differences_selfReferentialChain_stillAReferenceAfterOneHop_reports401Once()
    {
        // Object 4's own content is "5 0 R": resolving /Differences (a reference to object 4)
        // takes exactly one hop and returns that value unresolved, still a PdfIndirectReference,
        // the same unresolved-second-hop shape /BaseEncoding can carry, exercised here for
        // /Differences itself rather than being silently dropped as if absent.
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(4, "5 0 R"),
            new FontTestSupport.Obj(5, "[1 2 3]"));
        var sink = new DiagnosticSink(50);
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), new PdfIndirectReference(4, 0));
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
        Assert.Contains("not an array", d.Message);
    }

    [Fact]
    public void Differences_badElement_stopsApplyingArray_laterNamesKeepBaseEncoding()
    {
        // /Differences [65 /A 9 0 R /zcaron /Zcaron]: object 9 does not exist, so the reference is
        // reported and the array stops being applied there; /zcaron and /Zcaron must keep their
        // StandardEncoding names rather than landing on B (0x42) and C (0x43), which is where
        // they would land if this reader resumed after the bad element with the running code
        // left unchanged.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray()
            .Add(new PdfInteger(65)).Add(new PdfName("A"))
            .Add(new PdfIndirectReference(9, 0))
            .Add(new PdfName("zcaron")).Add(new PdfName("Zcaron"));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("A", Decode(reader, 0x41).Unicode); // applied before the bad element.
        Assert.Equal("B", Decode(reader, 0x42).Unicode); // StandardEncoding, not overwritten.
        Assert.Equal("C", Decode(reader, 0x43).Unicode); // StandardEncoding, not overwritten.
        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
        Assert.Contains("does not follow inside /Differences", d.Message);
    }

    [Fact]
    public void Differences_realElement_reports401WithExactMessage()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray().Add(new PdfReal(65.0)).Add(new PdfName("A"));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
        Assert.Equal(
            "/Differences contains an element that is neither an integer nor a name (the number 65).",
            d.Message);
    }

    [Fact]
    public void Differences_indirectReferenceElement_reports401WithExactMessage()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray().Add(new PdfIndirectReference(1, 0)).Add(new PdfName("A"));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
        Assert.Equal(
            "/Differences contains an indirect reference, which this reader does not follow "
                + "inside /Differences.",
            d.Message);
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

    [Fact]
    public void Encoding_chainedBaseEncodingReference_reports401WithReferenceMessage()
    {
        // 4 0 R -> 5 0 R -> /WinAnsiEncoding: resolving /BaseEncoding (a reference to object 4)
        // takes one hop and returns object 4's own content, itself the reference "5 0 R", never
        // following on to the name at object 5. The message must say so rather than "names an
        // encoding this reader does not know", which is true of a bad name, not an unresolved
        // reference.
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(4, "5 0 R"),
            new FontTestSupport.Obj(5, "/WinAnsiEncoding"));
        var sink = new DiagnosticSink(50);
        var encoding = new PdfDictionary().Set(new PdfName("BaseEncoding"), new PdfIndirectReference(4, 0));
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
        Assert.Contains("indirect reference this reader does not follow past one hop", d.Message);
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

    // Discriminating cell for §9.6.5.4's closing rule: StandardEncoding's 0xB2 is dagger, a cell
    // Annex D.2's MacRoman column leaves blank (MacRomanEncoding puts dagger at 0xA0 instead).
    private static PdfDictionary MacRomanBaseDictionary() =>
        new PdfDictionary().Set(new PdfName("BaseEncoding"), "MacRomanEncoding");

    private static PdfDictionary NonsymbolicDescriptor() =>
        new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(32));

    [Fact]
    public void NonsymbolicTrueType_dictionaryWithMacRomanBase_fillsUndefinedCellsFromStandard()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), NonsymbolicDescriptor())
            .Set(PdfName.Encoding, MacRomanBaseDictionary());
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("†", Decode(reader, 0xB2).Unicode); // filled from StandardEncoding.
        Assert.Equal("†", Decode(reader, 0xA0).Unicode); // MacRoman's own dagger, untouched.
        Assert.Equal("'", Decode(reader, 0x27).Unicode); // MacRoman's quotesingle wins over Standard's quoteright.
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
    }

    [Fact]
    public void NonsymbolicTrueType_namedMacRomanEncoding_isNotFilledFromStandard()
    {
        // The fill belongs to §9.6.5.4's dictionary bullet only; a name /Encoding takes Annex D.2's
        // MacRoman column as it stands.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), NonsymbolicDescriptor())
            .Set(PdfName.Encoding, "MacRomanEncoding");
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0xB2).Unicode);
        Assert.Equal("†", Decode(reader, 0xA0).Unicode);
    }

    [Fact]
    public void NonsymbolicType1_dictionaryWithMacRomanBase_isNotFilledFromStandard()
    {
        // §9.6.5.2 states no fill rule for Type1 fonts.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Foo")
            .Set(new PdfName("FontDescriptor"), NonsymbolicDescriptor())
            .Set(PdfName.Encoding, MacRomanBaseDictionary());
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0xB2).Unicode);
    }

    [Fact]
    public void SymbolicTrueType_dictionaryWithMacRomanBase_isNotFilledFromStandard()
    {
        // §9.6.5.4's table-building paragraph applies to a dictionary /Encoding only when the
        // Nonsymbolic flag is set.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(4)))
            .Set(PdfName.Encoding, MacRomanBaseDictionary());
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0xB2).Unicode);
    }

    [Theory]
    [InlineData(36, false)] // Symbolic and Nonsymbolic both set: Symbolic wins, no fill.
    [InlineData(0, true)] // both clear: Symbolic is clear, so the state is nonsymbolic; fill.
    public void TrueTypeWithDisagreeingFlags_symbolicFlagDecidesTheFill(int flags, bool filled)
    {
        // Table 121 forbids both shapes; §9.8.2 says which flag a processor reads when they occur:
        // "A PDF processor should always check the Symbolic flag to determine whether the state is
        // Symbolic or NonSymbolic". The fill follows that, not the Nonsymbolic bit's own value.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var descriptor = new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(flags));
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), descriptor)
            .Set(PdfName.Encoding, MacRomanBaseDictionary());
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(filled ? "†" : null, Decode(reader, 0xB2).Unicode);
        Assert.Equal("†", Decode(reader, 0xA0).Unicode); // MacRoman's own dagger either way.
    }

    [Fact]
    public void NonsymbolicTrueType_dictionaryWithMacExpertBase_isNotFilledFromStandard()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), NonsymbolicDescriptor())
            .Set(PdfName.Encoding, new PdfDictionary().Set(new PdfName("BaseEncoding"), "MacExpertEncoding"));
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0x41).Unicode);
        Assert.Null(Decode(reader, 0xB2).Unicode);
    }

    [Fact]
    public void TrueTypeWithDescriptorButNoFlags_dictionaryWithMacRomanBase_isNotFilledFromStandard()
    {
        // A descriptor without /Flags has no Nonsymbolic flag to be "set" (§9.6.5.4), so the
        // fill must not run; this is the shape that separates a gate on the resolved /Flags from
        // a gate on the descriptor's mere presence, which the Table 112 fallback would then read
        // as nonsymbolic and fill.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), new PdfDictionary())
            .Set(PdfName.Encoding, MacRomanBaseDictionary());
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0xB2).Unicode); // not filled.
        Assert.Equal("†", Decode(reader, 0xA0).Unicode); // MacRoman's own dagger, untouched.
    }

    [Fact]
    public void DescriptorlessTrueType_dictionaryWithMacRomanBase_isNotFilledFromStandard()
    {
        // §9.6.5.4 conditions the fill on "the font descriptor's Nonsymbolic flag", a flag of a
        // descriptor that is present: with no /FontDescriptor at all the precondition is not met
        // and the fill must not run, even though this reader's Table 112 fallback elsewhere
        // treats a missing descriptor as nonsymbolic.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(PdfName.Encoding, MacRomanBaseDictionary());
        var reader = Build(doc, fontDict, sink);

        Assert.Null(Decode(reader, 0xB2).Unicode); // not filled: the twelve cells stay undefined.
        Assert.Equal("†", Decode(reader, 0xA0).Unicode); // MacRoman's own dagger, untouched.
    }

    [Fact]
    public void SymbolicTrueType_namedWinAnsiEncoding_isHonoured_pinning()
    {
        // §9.6.5.4, verbatim: "When the font has no Encoding entry, or the font descriptor's
        // Symbolic flag is set (in which case the Encoding entry is ignored), this shall occur:
        // ...". This reader does not implement that alternative (it needs a font-program cmap this
        // reader does not read) and instead honours a present /Encoding even for a symbolic
        // TrueType font; this pins that departure, not a defect.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var descriptor = new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(4));
        var fontDict = new PdfDictionary()
            .Set(PdfName.Subtype, "TrueType").Set(PdfName.BaseFont, "Foo")
            .Set(new PdfName("FontDescriptor"), descriptor)
            .Set(PdfName.Encoding, "WinAnsiEncoding");
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("A", Decode(reader, 0x41).Unicode);
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

    [Fact]
    public void SymbolBaseFont_differences_overrideTheBuiltInEncoding()
    {
        // Table 112: with no /BaseEncoding, /Differences describes differences from the font's
        // built-in encoding; §9.6.5.2: an /Encoding entry "shall override a Type 1 font's mapping
        // from character codes to character names". Symbol is not exempt.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var differences = new PdfArray().Add(new PdfInteger(0x61)).Add(new PdfName("gamma"));
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var reader = Build(doc, Type1("Symbol").Set(PdfName.Encoding, encoding), sink);

        var gamma = Decode(reader, 0x61);
        Assert.Equal("γ", gamma.Unicode);
        Assert.Equal(411, gamma.Width); // the AFM width of gamma, not alpha's 631.

        var beta = Decode(reader, 0x62); // untouched by /Differences: still the built-in beta.
        Assert.Equal("β", beta.Unicode);
        Assert.Equal(549, beta.Width);

        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void SymbolBaseFont_namedWinAnsiEncoding_replacesTheBuiltInEncoding()
    {
        // A named /Encoding replaces the whole base table (§9.6.5, Table 112). Symbol's AFM has
        // no glyph named "a", so the width falls back to MissingWidth, 0 here.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Symbol").Set(PdfName.Encoding, "WinAnsiEncoding");
        var reader = Build(doc, fontDict, sink);

        var a = Decode(reader, 0x61);
        Assert.Equal("a", a.Unicode);
        Assert.Equal(0, a.Width);

        Assert.Empty(sink.Diagnostics);
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

    [Fact]
    public void WinAnsiFont_unmappedDifferenceName_withToUnicode_reportsNeither404Nor403()
    {
        // The unparsed /ToUnicode stream may map code 65 (§9.10.2 gives it priority over the
        // glyph-name route), so a missing glyph-name mapping is not yet an unmapped glyph.
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                + "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /g123] >> "
                + "/ToUnicode 7 0 R >>"),
            new FontTestSupport.Obj(7, "<< >>", "/CIDInit /ProcSet findresource begin\n"u8.ToArray()));
        var sink = new DiagnosticSink(50);
        var reader = Build(doc, Assert.IsType<PdfDictionary>(doc.Resolve(5)), sink);

        Assert.True(reader.HasToUnicode);
        Assert.Null(Decode(reader, 0x41).Unicode);
        Assert.Equal("B", Decode(reader, 0x42).Unicode);
        Assert.Empty(sink.Diagnostics);
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
    public void Helvetica_noWidths_descriptorPresentNoFlags_stillFillsAfmWidth()
    {
        // The AFM width fill depends only on /Widths being absent and the font resolving to one
        // of the standard 14; a present /FontDescriptor without /Flags neither enables nor
        // disables it. The §9.6.5.4 encoding fill is the one that reads /Flags, and
        // TrueTypeWithDescriptorButNoFlags_dictionaryWithMacRomanBase_isNotFilledFromStandard
        // pins that side.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(new PdfName("FontDescriptor"), new PdfDictionary());
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(556, Decode(reader, 0xB2).Width); // dagger's Helvetica AFM width, not MissingWidth.
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

    // ── 12: dangling reference, null entries ─────────────────────────────────────────────────────

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

    [Fact]
    public void Widths_directNull_treatedAsAbsent_usesAfmWidth_no402()
    {
        // ISO 32000-2 §7.3.7: a dictionary entry whose value is null is treated the same as if
        // the entry does not exist, so this must behave exactly like NonStandardFont's own
        // /Widths-absent case above, not like a malformed one.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(new PdfName("Widths"), PdfNull.Instance);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal(667, Decode(reader, 0x41).Width);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontWidthsMalformed);
    }

    [Fact]
    public void Widths_referenceToNullObject_treatedAsAbsent_usesAfmWidth_no402()
    {
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                + "/Widths 7 0 R >>"),
            new FontTestSupport.Obj(7, "null"));
        var sink = new DiagnosticSink(50);
        var fontDict = (PdfDictionary)doc.Resolve(5)!;
        var reader = Build(doc, fontDict, sink, objectNumber: 5, generation: 0);

        Assert.Equal(667, Decode(reader, 0x41).Width);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontWidthsMalformed);
    }

    [Fact]
    public void Widths_wrongTypeNotNull_stillReports402()
    {
        // The negative control for the two tests above: a present, wrong-typed, non-null /Widths
        // must still be reported, so the null normalisation is not swallowing malformed entries.
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica")
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), new PdfInteger(5));
        Build(doc, fontDict, sink);

        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontWidthsMalformed);
        Assert.Contains("not an array", d.Message);
    }

    [Fact]
    public void Encoding_directNull_treatedAsAbsent_usesStandardEncoding_no401()
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(50);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, PdfNull.Instance);
        var reader = Build(doc, fontDict, sink);

        Assert.Equal("’", Decode(reader, 0x27).Unicode); // StandardEncoding's quoteright.
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
    }

    [Fact]
    public void Encoding_referenceToNullObject_treatedAsAbsent_no401()
    {
        using var doc = FontTestSupport.Open(
            new FontTestSupport.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                + "/Encoding 7 0 R >>"),
            new FontTestSupport.Obj(7, "null"));
        var sink = new DiagnosticSink(50);
        var fontDict = (PdfDictionary)doc.Resolve(5)!;
        var reader = Build(doc, fontDict, sink, objectNumber: 5, generation: 0);

        Assert.Equal("’", Decode(reader, 0x27).Unicode); // StandardEncoding's quoteright.
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

    [Fact]
    public void GetFontReader_fontEntryNamesADeepLengthChain_reports400Once_noThrow()
    {
        // The /Font entry itself (object 3) is a stream whose /Length chains 120 links deep, past
        // MaxResolveDepth (100). ResolveValue(rawFontEntry) re-enters resolution while parsing that
        // stream's own structure, so the depth limit throws before the dictionary-type check below
        // it ever runs; GetFontReader must catch that itself rather than let it escape.
        var bytes = FontTestSupport.BuildDeepIndirectLengthChain(firstChainObject: 3, chainLen: 120);
        using var doc = PdfReader.Open(bytes);
        var sink = new DiagnosticSink(50);

        var result = doc.GetFontReader(new PdfIndirectReference(3, 0), sink, null);

        Assert.Null(result);
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.FontUnreadable, d.Code);
    }

    [Fact]
    public void GetFontReader_disposedReader_throwsObjectDisposedException()
    {
        var doc = FontTestSupport.OpenMinimal();
        doc.Dispose();
        var sink = new DiagnosticSink(50);

        Assert.Throws<ObjectDisposedException>(() => doc.GetFontReader(Type1("Helvetica"), sink, null));
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

        // Measured 6,464 bytes on this runtime (the per-font string/width/Unicode tables, the
        // ToArray() copies of the shared encoding statics, and the Unicode strings themselves);
        // 64 KiB is a generous bound that still fails if Create starts copying the
        // 100,000-element array instead of indexing into it.
        Assert.True(allocated < 64 * 1024, $"Create allocated {allocated} bytes, expected < 64 KiB.");
    }

    [Fact]
    public void Create_allocatesUnder64KiB_forA100000ElementDifferencesArray()
    {
        using var doc = FontTestSupport.OpenMinimal();

        // 100,000 (code, oversized-name) pairs, alternating over all 256 codes: every element is
        // read (unlike the /Widths array above, where only LastChar - FirstChar + 1 elements are
        // read), so this is the array-length cap this class' own comment on that test says the
        // parser has none of; the 401 message for the first oversized element must not be built
        // for every later one, only to be discarded by ReportOnce.
        var oversizedName = new PdfName(new string('a', 129));
        var differences = new PdfArray();
        for (var i = 0; i < 100_000; i++)
        {
            differences.Add(new PdfInteger(i % 256));
            differences.Add(oversizedName);
        }
        var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
        var fontDict = Type1("Helvetica").Set(PdfName.Encoding, encoding);

        // Warm-up: JIT and any lazy static (AdobeGlyphList's own load) must not be charged to the
        // measured call.
        Build(doc, Type1("Helvetica"), new DiagnosticSink(50));

        var sink = new DiagnosticSink(50);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var reader = Build(doc, fontDict, sink);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Measured 13,336 bytes on this runtime; 64 KiB is the same generous bound the /Widths
        // KAT above uses.
        Assert.True(allocated < 64 * 1024, $"Create allocated {allocated} bytes, expected < 64 KiB.");
        // Every code winds up undefined, so this font also has no Unicode route at all
        // (FontNoUnicodeRoute, 403), legitimately, alongside the 401 this test pins.
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontEncodingMalformed);
        for (var code = 0; code < 256; code++)
            Assert.Null(Decode(reader, (byte)code).Unicode);
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
