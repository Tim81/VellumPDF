// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>
/// The cross-validation corpus. Each fixture is a real, complete PDF produced by VellumPdf's own
/// writer (so veraPDF can parse it), differing from a known-good baseline only by a single
/// same-length byte edit — keeping the in-process verdict and the veraPDF verdict comparable.
/// </summary>
/// <remarks>
/// All fixtures derive from one PDF/A-2b baseline and apply in-place, same-length edits so
/// cross-reference offsets stay valid. The corpus anchors the gate on the byte-level structural and
/// metadata rules. Object-graph rules (fonts, output intents, blend modes, annotations, actions,
/// logical structure) and the 2u/2a/UA flavours are the next expansion: each needs a writer-produced
/// document confirmed veraPDF-valid except for the injected defect, which requires a local veraPDF
/// run to author safely.
/// </remarks>
public static class OracleCorpus
{
    public static IReadOnlyList<OracleFixture> All { get; } = Build();

    public static OracleFixture ByName(string name) => All.Single(f => f.Name == name);

    private static IReadOnlyList<OracleFixture> Build()
    {
        // One baseline, cloned per fixture so the documents are byte-identical except for the edit.
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);

        return
        [
            new OracleFixture("pdfa2b-compliant", Clone(baseline),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            new OracleFixture("pdfa2b-bad-conformance", Edit(baseline, CorruptXmpConformance),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            new OracleFixture("pdfa2b-bad-part", Edit(baseline, CorruptXmpPart),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            new OracleFixture("pdfa2b-bad-binary-marker", Edit(baseline, CorruptBinaryMarker),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A plain (non-PDF/A) document validated as PDF/A-2b: non-compliant.
            new OracleFixture("plain-not-pdfa", WriterPdf(VellumPdf.Document.PdfConformance.None),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),
        ];
    }

    private static byte[] WriterPdf(VellumPdf.Document.PdfConformance conformance)
    {
        using var doc = new PdfDocument { Conformance = conformance };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Clone(byte[] source) => (byte[])source.Clone();

    private static byte[] Edit(byte[] source, Action<byte[]> mutate)
    {
        var copy = (byte[])source.Clone();
        mutate(copy);
        return copy;
    }

    /// <summary>Flips XMP <c>pdfaid:conformance</c> (<c>B</c>) to an invalid letter, in place.</summary>
    private static void CorruptXmpConformance(byte[] bytes)
        => OverwriteValueAfter(bytes, "pdfaid:conformance>", expected: (byte)'B', replacement: (byte)'X');

    /// <summary>Flips XMP <c>pdfaid:part</c> (<c>2</c>) to an invalid value, in place.</summary>
    private static void CorruptXmpPart(byte[] bytes)
        => OverwriteValueAfter(bytes, "pdfaid:part>", expected: (byte)'2', replacement: (byte)'9');

    /// <summary>Blanks the four high binary-marker bytes so the §6.1.2 marker comment is invalid.</summary>
    private static void CorruptBinaryMarker(byte[] bytes)
    {
        // The writer emits the marker comment bytes 0xE2 0xE3 0xCF 0xD3 on the line after the header.
        byte[] marker = [0xE2, 0xE3, 0xCF, 0xD3];
        var at = IndexOf(bytes, marker);
        if (at < 0)
            throw new InvalidOperationException("Fixture writer did not emit the expected binary marker.");
        for (var i = 0; i < marker.Length; i++)
            bytes[at + i] = (byte)' ';
    }

    private static void OverwriteValueAfter(byte[] bytes, string needle, byte expected, byte replacement)
    {
        var at = IndexOf(bytes, Encoding.ASCII.GetBytes(needle));
        if (at < 0)
            throw new InvalidOperationException($"Fixture writer did not emit '{needle}'.");
        var valueIndex = at + needle.Length;
        if (bytes[valueIndex] != expected)
            throw new InvalidOperationException(
                $"Expected '{(char)expected}' after '{needle}' but found '{(char)bytes[valueIndex]}'; "
                + "the writer's output changed and the corruption would target the wrong byte.");
        bytes[valueIndex] = replacement;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}
