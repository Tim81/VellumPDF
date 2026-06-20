// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>
/// The cross-validation corpus. Each fixture is a real, complete PDF produced by VellumPdf's own
/// writer (so veraPDF can parse it), differing from a known-good baseline only by a single
/// same-length byte edit — keeping the in-process verdict and the veraPDF verdict comparable.
/// </summary>
/// <remarks>
/// Most fixtures derive from one PDF/A-2b baseline and apply in-place, same-length edits so
/// cross-reference offsets stay valid; this anchors the gate on the byte-level structural and
/// metadata rules. A few fixtures are instead whole writer-produced documents (e.g.
/// <c>pdfa2b-link</c>, which exercises the §6.5.3 annotation rule). Further object-graph
/// rules (fonts, output intents, blend modes, actions, logical structure) and the 2u/2a/UA flavours
/// are the next expansion: each needs a writer-produced document veraPDF agrees on, so the
/// cross-validation gate (CI) is what confirms each new fixture's expected verdict.
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

            // A PDF/A-2b document carrying a writer-produced /Link annotation. The writer sets /F 4
            // (Print) per §6.5.3, so the Link is conformant — Link is exempt from the appearance-stream
            // requirement but must still satisfy the flag requirements. End-to-end guard that the
            // writer emits conformant Link annotations, cross-checked by veraPDF.
            new OracleFixture("pdfa2b-link", WriterPdfWithLink(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A PDF/A-2b document that draws text with a non-embedded standard-14 font. PDF/A requires
            // every font embedded (ISO 19005-2 §6.2.11.4.1 / §6.3.4), so both veraPDF and the in-process
            // FontEmbeddingRule reject it. Cross-validates the font-embedding rule's negative path.
            // Uses only the built-in standard-14 metrics, so no external font asset is needed.
            new OracleFixture("pdfa2b-nonembedded-font", WriterPdfNonEmbeddedFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A PDF/A-2b document that draws text with a properly embedded (subset) TrueType font via
            // the Type0/CIDFontType2/Identity-H path. veraPDF accepts it (all §6.2.11.x font checks
            // pass) and so does the in-process validator — the positive end-to-end font fixture.
            new OracleFixture("pdfa2b-embedded-font", WriterPdfWithEmbeddedFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),
        ];
    }

    private static byte[] WriterPdfWithEmbeddedFont()
    {
        var ttf = LoadAsset("DejaVuSans.ttf");
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var handle = doc.UseTrueTypeFont(ttf);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        const string text = "Embedded DejaVu Sans subset";
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] LoadAsset(string logicalName)
    {
        using var s = typeof(OracleCorpus).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded asset '{logicalName}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] WriterPdfNonEmbeddedFont()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("Non-embedded standard-14 font").EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] WriterPdfWithLink()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(72, 72, 200, 90),
            Uri = "https://example.com/",
        });
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
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
        // Guard against the (astronomically unlikely) case of this 4-byte sequence appearing first
        // inside a compressed stream: the real marker is on the header line, right after a '%'.
        if (at is < 2 or > 20 || bytes[at - 1] != (byte)'%')
            throw new InvalidOperationException(
                $"Binary marker not found on the header line where expected (offset {at}).");
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
