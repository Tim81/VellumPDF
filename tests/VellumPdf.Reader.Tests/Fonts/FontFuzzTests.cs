// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using VellumPdf.Core;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>
/// CsCheck property test over mutated font dictionaries: random <c>/Encoding</c> shapes,
/// <c>/Differences</c> arrays mixing every element type, random <c>/Widths</c> lengths and
/// element types, random <c>/Flags</c>, and random base font names including a 1 KiB one.
/// <see cref="SimpleFontReader.Create"/> is asserted to never throw and to report at most four
/// distinct diagnostic codes per font (400 to 402, plus one of 403/404), and
/// <see cref="PdfFontReader.TryDecodeNext"/> over every byte value is asserted to never throw.
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

    private static readonly Gen<PdfObject?> EncodingGen = Gen.OneOf(
        Gen.Const((PdfObject?)null),
        Gen.Const((PdfObject?)new PdfName("StandardEncoding")),
        Gen.Const((PdfObject?)new PdfName("WinAnsiEncoding")),
        Gen.Const((PdfObject?)new PdfName("MacRomanEncoding")),
        Gen.Const((PdfObject?)new PdfName("Bogus")),
        Gen.Const((PdfObject?)new PdfInteger(42)),
        DifferencesGen.Select(diffs =>
        {
            var dict = new PdfDictionary().Set(new PdfName("Differences"), diffs);
            return (PdfObject?)dict;
        }),
        Gen.Select(Gen.OneOf(Gen.Const("WinAnsiEncoding"), Gen.Const("Bogus"), Gen.Const("MacRomanEncoding")),
            DifferencesGen,
            (baseName, diffs) => (PdfObject?)new PdfDictionary()
                .Set(new PdfName("BaseEncoding"), new PdfName(baseName))
                .Set(new PdfName("Differences"), diffs)));

    private static readonly Gen<PdfObject> WidthsElementGen = Gen.OneOf(
        Gen.Int[-100, 2000].Select(i => (PdfObject)new PdfInteger(i)),
        Gen.Double[-100, 2000].Select(d => (PdfObject)new PdfReal(d)),
        NameGen.Select(n => (PdfObject)new PdfName(n)));

    private static readonly Gen<PdfObject?> WidthsGen = Gen.OneOf(
        Gen.Const((PdfObject?)null),
        WidthsElementGen.Array[0, 10].Select(items => (PdfObject?)new PdfArray(items)),
        Gen.Const((PdfObject?)new PdfInteger(5)));

    private static readonly Gen<string> BaseFontGen = Gen.OneOf(
        Gen.Const("Helvetica"), Gen.Const("Symbol"), Gen.Const("ZapfDingbats"),
        Gen.Const("Arial,Bold"), Gen.Const("Foo"), Gen.String[1, 20],
        Gen.Const(new string('B', 1024)));

    private static readonly Gen<int> FlagsGen = Gen.OneOf(
        Gen.Const(0), Gen.Const(4), Gen.Const(32), Gen.Const(36), Gen.Int[-1000, 1000]);

    private static readonly Gen<PdfDictionary> FontDictGen = Gen.Select(
        BaseFontGen, EncodingGen, WidthsGen, FlagsGen,
        (baseFont, encoding, widths, flags) =>
        {
            var dict = new PdfDictionary()
                .Set(PdfName.Subtype, "Type1")
                .Set(PdfName.BaseFont, baseFont);
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
            return dict;
        });

    [Fact]
    public void Create_neverThrows_reportsAtMostFourDistinctCodes_decodeNeverThrows()
    {
        using var doc = FontTestSupport.OpenMinimal();
        FontDictGen.Sample(
            fontDict =>
            {
                var sink = new DiagnosticSink(cap: 50);
                var reader = SimpleFontReader.Create(doc, fontDict, null, null, sink, null);

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

                for (var b = 0; b < 256; b++)
                {
                    ReadOnlySpan<byte> bytes = [(byte)b];
                    var offset = 0;
                    reader.TryDecodeNext(bytes, ref offset, out _);
                }
            },
            iter: FuzzBudget.Iterations);
    }
}
