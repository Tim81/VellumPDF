// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using VellumPdf.Core;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>
/// CsCheck property tests over mutated font dictionaries. The first drives
/// <see cref="SimpleFontReader.Create"/> directly: random <c>/Subtype</c>s, <c>/Encoding</c>
/// shapes (including an indirect reference to an existing object and a two-hop chain neither
/// this class nor <see cref="SimpleFontReader"/> follows), <c>/Differences</c> arrays mixing
/// every element type, a non-array <c>/Differences</c> (an integer, a name, a dictionary, or an
/// indirect reference), random <c>/Widths</c> lengths and element types including an indirect
/// reference (to an existing object, a dangling one, or a null object) both as an element and as
/// the whole <c>/Widths</c> value, random <c>/Flags</c>, random <c>/ToUnicode</c> shapes (direct
/// and indirect), and random base font names including a 1 KiB one. The second drives
/// <see cref="PdfDocumentReader.GetFontReader"/> itself, including its own indirect resolution of
/// the font entry and its <c>/Subtype</c>. Both assert: no exception escapes, at most four
/// distinct diagnostic codes are reported per font (400 to 402, plus one of 403/404), and
/// <see cref="PdfFontReader.TryDecodeNext"/> over every byte value never throws.
/// </summary>
public sealed class FontFuzzTests
{
    private static class FuzzBudget
    {
        private const long DefaultIterations = 3_000;

        internal static long Iterations
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("VELLUMPDF_FUZZ_ITER");
                return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultIterations;
            }
        }
    }

    // Object numbers pre-registered in the fixture document built by OpenFixture(), used by the
    // indirect-shape generators below so a resolve inside SimpleFontReader.Create or
    // GetFontReader hits an object present in the document rather than a dangling reference.
    private const int EncodingDictObject = 50;
    private const int EncodingChainHeadObject = 51;
    private const int EncodingChainTargetObject = 52;
    private const int ToUnicodeStreamObject = 60;
    private const int NullObject = 61;
    private const int WidthsNumberObject = 62;
    private const int WidthsArrayObject = 63;
    private const int FontDictObject = 100;
    private const int NonDictionaryObject = 102;

    private static PdfDocumentReader OpenFixture() => FontTestSupport.Open(
        new FontTestSupport.Obj(EncodingDictObject, "<< /BaseEncoding /WinAnsiEncoding /Differences [65 /A] >>"),
        new FontTestSupport.Obj(EncodingChainHeadObject, $"{EncodingChainTargetObject} 0 R"),
        new FontTestSupport.Obj(EncodingChainTargetObject, "<< /BaseEncoding /MacRomanEncoding >>"),
        new FontTestSupport.Obj(ToUnicodeStreamObject, "<< >>", "/CIDInit /ProcSet findresource begin\n"u8.ToArray()),
        new FontTestSupport.Obj(NullObject, "null"),
        new FontTestSupport.Obj(WidthsNumberObject, "123"),
        new FontTestSupport.Obj(WidthsArrayObject, "[100 200 300]"),
        new FontTestSupport.Obj(FontDictObject, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        new FontTestSupport.Obj(NonDictionaryObject, "42"));

    // PdfName's own constructor rejects an empty string (ArgumentException); the case where a
    // parsed PDF represents a bare "/" as a zero-length name never reaches PdfName's constructor
    // through the parser either, so this generator does not attempt to build one.
    private static readonly Gen<string> NameGen = Gen.OneOf(
        Gen.Const("A"), Gen.Const("space"), Gen.Const("g123"),
        Gen.String[1, 12], Gen.String[120, 200]);

    private static readonly Gen<PdfObject> DifferencesElementGen = Gen.OneOf(
        Gen.Int[-10, 300].Select(i => (PdfObject)new PdfInteger(i)),
        NameGen.Select(n => (PdfObject)new PdfName(n)),
        Gen.Int[0, 50].Select(i => (PdfObject)new PdfIndirectReference(i, 0)),
        Gen.Double[-100, 100].Select(d => (PdfObject)new PdfReal(d)),
        Gen.Const((PdfObject)new PdfDictionary()),
        Gen.Const((PdfObject)new PdfArray()));

    private static readonly Gen<PdfArray> DifferencesGen =
        DifferencesElementGen.Array[0, 12].Select(items => new PdfArray(items));

    // /Differences present but not an array at all: an integer, a name, a dictionary, and an
    // indirect reference that is still a reference after one hop (EncodingChainHeadObject's own
    // content is a reference to EncodingChainTargetObject), the same shape
    // Differences_selfReferentialChain_stillAReferenceAfterOneHop_reports401Once pins directly.
    private static readonly Gen<PdfObject> NonArrayDifferencesGen = Gen.OneOf(
        Gen.Int[-10, 300].Select(i => (PdfObject)new PdfInteger(i)),
        NameGen.Select(n => (PdfObject)new PdfName(n)),
        Gen.Const((PdfObject)new PdfDictionary()),
        Gen.Const((PdfObject)new PdfIndirectReference(EncodingChainHeadObject, 0)));

    private static readonly Gen<PdfObject> DifferencesValueGen = Gen.OneOf(
        DifferencesGen.Select(a => (PdfObject)a),
        NonArrayDifferencesGen);

    private static readonly Gen<PdfObject?> EncodingGen = Gen.OneOf(
        Gen.Const((PdfObject?)null),
        Gen.Const((PdfObject?)new PdfName("StandardEncoding")),
        Gen.Const((PdfObject?)new PdfName("WinAnsiEncoding")),
        Gen.Const((PdfObject?)new PdfName("MacRomanEncoding")),
        Gen.Const((PdfObject?)new PdfName("Bogus")),
        Gen.Const((PdfObject?)new PdfInteger(42)),
        // Resolves in one hop to the encoding dictionary at EncodingDictObject.
        Gen.Const((PdfObject?)new PdfIndirectReference(EncodingDictObject, 0)),
        // Resolves in one hop to ANOTHER reference (EncodingChainHeadObject's own content is
        // "EncodingChainTargetObject 0 R"): the two-hop chain this reader does not follow.
        Gen.Const((PdfObject?)new PdfIndirectReference(EncodingChainHeadObject, 0)),
        DifferencesValueGen.Select(diffs =>
        {
            var dict = new PdfDictionary().Set(new PdfName("Differences"), diffs);
            return (PdfObject?)dict;
        }),
        Gen.Select(Gen.OneOf(Gen.Const("WinAnsiEncoding"), Gen.Const("Bogus"), Gen.Const("MacRomanEncoding")),
            DifferencesValueGen,
            (baseName, diffs) => (PdfObject?)new PdfDictionary()
                .Set(new PdfName("BaseEncoding"), new PdfName(baseName))
                .Set(new PdfName("Differences"), diffs)));

    // Includes a reference to an object the fixture defines (WidthsNumberObject, a bare integer),
    // a dangling one, and one to a null object, so a /Widths element can resolve, dangle, or
    // resolve to PdfNull, alongside the direct-value shapes.
    private static readonly Gen<PdfObject> WidthsElementGen = Gen.OneOf(
        Gen.Int[-100, 2000].Select(i => (PdfObject)new PdfInteger(i)),
        Gen.Double[-100, 2000].Select(d => (PdfObject)new PdfReal(d)),
        NameGen.Select(n => (PdfObject)new PdfName(n)),
        Gen.Const((PdfObject)new PdfIndirectReference(WidthsNumberObject, 0)),
        Gen.Const((PdfObject)new PdfIndirectReference(999, 0)),
        Gen.Const((PdfObject)new PdfIndirectReference(NullObject, 0)));

    // /Widths itself as an indirect reference: to an array the fixture defines
    // (WidthsArrayObject), a dangling one, and one to a null object.
    private static readonly Gen<PdfObject?> WidthsGen = Gen.OneOf(
        Gen.Const((PdfObject?)null),
        WidthsElementGen.Array[0, 10].Select(items => (PdfObject?)new PdfArray(items)),
        Gen.Const((PdfObject?)new PdfInteger(5)),
        Gen.Const((PdfObject?)new PdfIndirectReference(WidthsArrayObject, 0)),
        Gen.Const((PdfObject?)new PdfIndirectReference(999, 0)),
        Gen.Const((PdfObject?)new PdfIndirectReference(NullObject, 0)));

    private static readonly Gen<string> BaseFontGen = Gen.OneOf(
        Gen.Const("Helvetica"), Gen.Const("Symbol"), Gen.Const("ZapfDingbats"),
        Gen.Const("Arial,Bold"), Gen.Const("Foo"), Gen.String[1, 20],
        Gen.Const(new string('B', 1024)));

    private static readonly Gen<int> FlagsGen = Gen.OneOf(
        Gen.Const(0), Gen.Const(4), Gen.Const(32), Gen.Const(36), Gen.Int[-1000, 1000]);

    // Subtypes this reader's own Create() doesn't gate on (unlike GetFontReader, which decides
    // whether to call Create at all): every value here still reaches Create, so this only varies
    // whether the "trueType" branch of step 5 (the StandardEncoding fill) fires.
    private static readonly Gen<PdfObject?> SubtypeGen = Gen.OneOf(
        Gen.Const((PdfObject?)new PdfName("Type1")),
        Gen.Const((PdfObject?)new PdfName("MMType1")),
        Gen.Const((PdfObject?)new PdfName("TrueType")),
        Gen.Const((PdfObject?)new PdfName("Type0")),
        Gen.Const((PdfObject?)new PdfName("Type3")),
        Gen.Const((PdfObject?)new PdfName("Bogus")),
        Gen.Const((PdfObject?)new PdfInteger(7)),
        Gen.Const((PdfObject?)null)); // omitted entirely

    private static readonly Gen<PdfObject?> ToUnicodeGen = Gen.OneOf(
        Gen.Const((PdfObject?)null),
        Gen.Const((PdfObject?)new PdfStream("/CIDInit /ProcSet findresource begin\n"u8.ToArray())),
        Gen.Const((PdfObject?)new PdfIndirectReference(ToUnicodeStreamObject, 0)));

    private static readonly Gen<PdfDictionary> FontDictGen = Gen.Select(
        BaseFontGen, EncodingGen, WidthsGen, FlagsGen, SubtypeGen, ToUnicodeGen,
        (baseFont, encoding, widths, flags, subtype, toUnicode) =>
        {
            var dict = new PdfDictionary();
            if (subtype is not null)
                dict.Set(PdfName.Subtype, subtype);
            dict.Set(PdfName.BaseFont, baseFont);
            if (encoding is not null)
                dict.Set(PdfName.Encoding, encoding);
            if (widths is not null)
            {
                dict.Set(new PdfName("FirstChar"), new PdfInteger(0));
                dict.Set(new PdfName("LastChar"), new PdfInteger(widths is PdfArray a ? a.Count - 1 : -1));
                dict.Set(new PdfName("Widths"), widths);
            }
            var descriptor = new PdfDictionary().Set(new PdfName("Flags"), new PdfInteger(flags));
            dict.Set(new PdfName("FontDescriptor"), descriptor);
            if (toUnicode is not null)
                dict.Set(new PdfName("ToUnicode"), toUnicode);
            return dict;
        });

    private static void AssertOnlyDocumentedCodes(DiagnosticSink sink)
    {
        var distinctCodes = sink.Diagnostics.Select(d => d.Code).Distinct().ToList();
        Assert.True(
            distinctCodes.Count <= 4,
            $"expected at most 4 distinct codes, got {distinctCodes.Count}: {string.Join(", ", distinctCodes)}");
        foreach (var code in distinctCodes)
        {
            Assert.True(
                code is PdfReaderDiagnosticCode.FontUnreadable
                    or PdfReaderDiagnosticCode.FontEncodingMalformed
                    or PdfReaderDiagnosticCode.FontWidthsMalformed
                    or PdfReaderDiagnosticCode.FontNoUnicodeRoute
                    or PdfReaderDiagnosticCode.UnmappedGlyphs,
                $"unexpected code {code}");
        }
    }

    private static void DecodeEveryByte(PdfFontReader reader)
    {
        for (var b = 0; b < 256; b++)
        {
            ReadOnlySpan<byte> bytes = [(byte)b];
            var offset = 0;
            reader.TryDecodeNext(bytes, ref offset, out _);
        }
    }

    [Fact]
    public void Create_neverThrows_reportsAtMostFourDistinctCodes_decodeNeverThrows()
    {
        using var doc = OpenFixture();
        // threads: 1: every sample resolves against the one shared doc (the indirect /Encoding
        // shapes each need an object already present in it to resolve against), and
        // PdfDocumentReader's own object cache is a plain Dictionary, not built for concurrent
        // access from multiple worker threads. Running serially keeps the test out of that
        // cache's concurrency behaviour, which is not what this test is about.
        FontDictGen.Sample(
            fontDict =>
            {
                var sink = new DiagnosticSink(cap: 50);
                var reader = SimpleFontReader.Create(doc, fontDict, null, null, sink, null);
                AssertOnlyDocumentedCodes(sink);
                DecodeEveryByte(reader);
            },
            iter: FuzzBudget.Iterations, threads: 1);
    }

    // Direct dictionaries drawn from FontDictGen exercise GetFontReader's own dispatch on
    // /Subtype; the three indirect shapes exercise its own two ResolveValue calls (the font entry
    // itself, then /Subtype) against an existing dictionary object, an existing non-dictionary
    // object, and a dangling reference, none of which should ever escape as an exception.
    private static readonly Gen<PdfObject> FontEntryGen = Gen.OneOf(
        FontDictGen.Select(d => (PdfObject)d),
        Gen.Const((PdfObject)new PdfIndirectReference(FontDictObject, 0)),
        Gen.Const((PdfObject)new PdfIndirectReference(NonDictionaryObject, 0)),
        Gen.Const((PdfObject)new PdfIndirectReference(999, 0)),
        Gen.Const((PdfObject)new PdfInteger(3)));

    [Fact]
    public void GetFontReader_neverThrows_reportsAtMostFourDistinctCodes_decodeNeverThrows()
    {
        using var doc = OpenFixture();
        // threads: 1: see Create_neverThrows' own comment; FontCache adds its own plain
        // Dictionary on top, written on every cache miss for the same shared doc.
        FontEntryGen.Sample(
            entry =>
            {
                var sink = new DiagnosticSink(cap: 50);
                var reader = doc.GetFontReader(entry, sink, pageIndex: null);
                AssertOnlyDocumentedCodes(sink);
                if (reader is not null)
                    DecodeEveryByte(reader);
            },
            iter: FuzzBudget.Iterations, threads: 1);
    }
}
