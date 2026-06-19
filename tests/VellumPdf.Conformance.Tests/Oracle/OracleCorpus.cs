// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>
/// The cross-validation corpus. Each fixture is a real, complete PDF produced by VellumPdf's own
/// writer (so veraPDF can parse it), differing from a known-good baseline only in the one feature
/// under test — keeping the in-process verdict and the veraPDF verdict comparable.
/// </summary>
/// <remarks>
/// The corpus deliberately uses writer-produced documents and same-length byte edits rather than
/// minimal hand-built fixtures: veraPDF validates the whole file, so a fixture must be well-formed
/// in every respect except the property being exercised. It grows as defect-injection support is
/// added; today it anchors the gate with a compliant baseline and two distinct violations.
/// </remarks>
public static class OracleCorpus
{
    public static IReadOnlyList<OracleFixture> All { get; } = Build();

    public static OracleFixture ByName(string name) => All.Single(f => f.Name == name);

    private static IReadOnlyList<OracleFixture> Build()
    {
        var corrupted = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        CorruptXmpConformance(corrupted);

        return
        [
            // A genuine PDF/A-2b document: both validators must agree it is compliant.
            new OracleFixture(
                "pdfa2b-compliant",
                WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b),
                Conformance.PdfConformance.PdfA2B,
                "2b",
                ExpectedCompliant: true),

            // The same document with its XMP pdfaid:conformance value made invalid: non-compliant.
            new OracleFixture(
                "pdfa2b-bad-conformance",
                corrupted,
                Conformance.PdfConformance.PdfA2B,
                "2b",
                ExpectedCompliant: false),

            // A plain (non-PDF/A) document validated as PDF/A-2b: non-compliant.
            new OracleFixture(
                "plain-not-pdfa",
                WriterPdf(VellumPdf.Document.PdfConformance.None),
                Conformance.PdfConformance.PdfA2B,
                "2b",
                ExpectedCompliant: false),
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

    /// <summary>
    /// Flips the XMP <c>pdfaid:conformance</c> value (e.g. <c>B</c>) to an invalid letter in place.
    /// The edit is the same length, so cross-reference offsets stay valid and the file still parses.
    /// </summary>
    private static void CorruptXmpConformance(byte[] bytes)
    {
        var needle = Encoding.ASCII.GetBytes("pdfaid:conformance>");
        var at = IndexOf(bytes, needle);
        if (at < 0)
            throw new InvalidOperationException("Fixture writer did not emit pdfaid:conformance.");
        var valueIndex = at + needle.Length;
        bytes[valueIndex] = (byte)'X';
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
