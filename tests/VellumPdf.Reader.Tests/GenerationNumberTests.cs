// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Object generation numbers (issue #121): the xref table is keyed on object number alone, but
/// ISO 32000-2 §7.3.10 requires a reference's generation to match the object's actual generation.
/// Without this, <c>10 2 R</c> against an xref that holds object 10 at generation 0 resolved to the
/// wrong (generation-0) object instead of nothing.
/// </summary>
public sealed class GenerationNumberTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] SaveDocToBytes(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A single-revision classic-table PDF where object 10 is recorded at the given generation.
    /// Object 10's body is <c>&lt;&lt; /Marker /Hit &gt;&gt;</c>.
    /// </summary>
    private static byte[] BuildClassicXrefPdf(int obj10Generation)
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var obj10Offset = (int)ms.Position;
        Write($"10 {obj10Generation} obj\n<< /Marker /Hit >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00000 n \n");
        Write("10 1\n");
        Write($"{obj10Offset:D10} {obj10Generation:D5} n \n");
        Write("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A two-revision classic-table PDF. Revision 1 defines object 5 (<c>&lt;&lt; /Marker /Alive &gt;&gt;</c>).
    /// Revision 2 frees it via a classic 'f' entry, with no /Prev-chain object 5 replacement.
    /// </summary>
    private static byte[] BuildClassicXrefWithFreedObjectPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var obj5Offset = (int)ms.Position;
        Write("5 0 obj\n<< /Marker /Alive >>\nendobj\n");

        var xref1Offset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00000 n \n");
        Write("5 1\n");
        Write($"{obj5Offset:D10} 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xref1Offset}\n%%EOF\n");

        // Revision 2: free object 5. The next-generation field is not meaningful to this parser
        // (freeing does not carry a resolvable generation) and is set to a nonzero placeholder to
        // confirm it is ignored rather than mistaken for an offset.
        var xref2Offset = (int)ms.Position;
        Write("xref\n");
        Write("5 1\n");
        Write($"{0:D10} 00001 f \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R /Prev {xref1Offset} >>\n");
        Write($"startxref\n{xref2Offset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] Row(byte type, long f2, long f3) =>
    [
        type,
        (byte)((f2 >> 24) & 0xFF), (byte)((f2 >> 16) & 0xFF), (byte)((f2 >> 8) & 0xFF), (byte)(f2 & 0xFF),
        (byte)((f3 >> 8) & 0xFF), (byte)(f3 & 0xFF),
    ];

    /// <summary>
    /// A single-revision xref-stream PDF (/W [1 4 2], uncompressed body) where object 10 is
    /// recorded at the given generation via a type-1 row. Object 10's body is
    /// <c>&lt;&lt; /Marker /Hit &gt;&gt;</c>. /Index is [0 2 10 2] so the body only needs rows for
    /// objects 0, 1, 10, and 11 (the xref stream itself).
    /// </summary>
    private static byte[] BuildXrefStreamPdf(int obj10Generation)
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBytes(byte[] b) => ms.Write(b);

        Write("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var o10 = (int)ms.Position;
        Write($"10 {obj10Generation} obj\n<< /Marker /Hit >>\nendobj\n");

        var body = new MemoryStream();
        body.Write(Row(0, 0, 0));                    // obj 0: free
        body.Write(Row(1, o1, 0));                    // obj 1: catalog, gen 0
        body.Write(Row(1, o10, obj10Generation));     // obj 10: marker, gen under test
        var o11 = (int)ms.Position;
        body.Write(Row(1, o11, 0));                   // obj 11: this xref stream itself
        var bodyArr = body.ToArray();

        Write($"11 0 obj\n<< /Type /XRef /Size 12 /W [1 4 2] /Index [0 2 10 2] /Root 1 0 R /Length {bodyArr.Length} >>\nstream\n");
        WriteBytes(bodyArr);
        Write("\nendstream\nendobj\n");
        Write($"startxref\n{o11}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A two-revision xref-stream PDF. Revision 1 defines object 5 as a type-1 entry
    /// (<c>&lt;&lt; /Marker /Alive &gt;&gt;</c>). Revision 2 frees it via a type-0 row and adds no
    /// replacement, chained back to revision 1 via /Prev.
    /// </summary>
    private static byte[] BuildXrefStreamWithFreedObjectPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBytes(byte[] b) => ms.Write(b);

        Write("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var o5 = (int)ms.Position;
        Write("5 0 obj\n<< /Marker /Alive >>\nendobj\n");

        var body1 = new MemoryStream();
        body1.Write(Row(0, 0, 0));   // obj 0: free
        body1.Write(Row(1, o1, 0));  // obj 1: catalog
        body1.Write(Row(1, o5, 0));  // obj 5: marker
        var o6 = (int)ms.Position;
        body1.Write(Row(1, o6, 0));  // obj 6: this xref stream (revision 1)
        var body1Arr = body1.ToArray();

        Write($"6 0 obj\n<< /Type /XRef /Size 7 /W [1 4 2] /Index [0 2 5 2] /Root 1 0 R /Length {body1Arr.Length} >>\nstream\n");
        WriteBytes(body1Arr);
        Write("\nendstream\nendobj\n");
        var xref1Offset = o6;
        Write($"startxref\n{xref1Offset}\n%%EOF\n");

        // Revision 2: free object 5, chained back to revision 1.
        var body2 = new MemoryStream();
        body2.Write(Row(0, 0, 0)); // obj 5: free
        var o7 = (int)ms.Position;
        body2.Write(Row(1, o7, 0)); // obj 7: this xref stream (revision 2)
        var body2Arr = body2.ToArray();

        Write($"7 0 obj\n<< /Type /XRef /Size 8 /W [1 4 2] /Index [5 1 7 1] /Root 1 0 R /Prev {xref1Offset} /Length {body2Arr.Length} >>\nstream\n");
        WriteBytes(body2Arr);
        Write("\nendstream\nendobj\n");
        Write($"startxref\n{o7}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── Classic xref table ───────────────────────────────────────────────────

    [Fact]
    public void ClassicXref_referenceGenerationMismatch_resolvesToNull()
    {
        var bytes = BuildClassicXrefPdf(obj10Generation: 0);
        using var reader = PdfReader.Open(bytes);

        // Object 10 exists at generation 0; a reference asking for generation 2 must not resolve
        // to it (ISO 32000-2 §7.3.10), even though the object number matches.
        var resolved = reader.Resolve(new PdfIndirectReference(10, 2));

        Assert.Null(resolved);
    }

    [Fact]
    public void ClassicXref_referenceGenerationMatch_resolvesNormally()
    {
        var bytes = BuildClassicXrefPdf(obj10Generation: 0);
        using var reader = PdfReader.Open(bytes);

        // No regression: 10 0 R against a generation-0 object still resolves.
        var resolved = reader.Resolve(new PdfIndirectReference(10, 0));

        var dict = Assert.IsType<PdfDictionary>(resolved);
        var marker = Assert.IsType<PdfName>(dict.Get(new PdfName("Marker")));
        Assert.Equal("Hit", marker.Value);
    }

    [Fact]
    public void ClassicXref_nonZeroGeneration_referenceMatchesActualGeneration()
    {
        var bytes = BuildClassicXrefPdf(obj10Generation: 2);
        using var reader = PdfReader.Open(bytes);

        Assert.Null(reader.Resolve(new PdfIndirectReference(10, 0)));

        var resolved = reader.Resolve(new PdfIndirectReference(10, 2));
        var dict = Assert.IsType<PdfDictionary>(resolved);
        var marker = Assert.IsType<PdfName>(dict.Get(new PdfName("Marker")));
        Assert.Equal("Hit", marker.Value);
    }

    [Fact]
    public void ClassicXref_freedObject_notResurrectedByOlderRevision()
    {
        var bytes = BuildClassicXrefWithFreedObjectPdf();
        using var reader = PdfReader.Open(bytes);

        // Object 5 existed in revision 1 but was freed in revision 2 (the newest); it must not
        // resolve at all, regardless of generation.
        Assert.Null(reader.Resolve(5));
        Assert.Null(reader.Resolve(new PdfIndirectReference(5, 0)));
        Assert.DoesNotContain(5, reader.ObjectNumbers);
    }

    // ── Cross-reference stream ───────────────────────────────────────────────

    [Fact]
    public void XrefStream_type1_referenceGenerationMismatch_resolvesToNull()
    {
        var bytes = BuildXrefStreamPdf(obj10Generation: 3);
        using var reader = PdfReader.Open(bytes);

        Assert.Null(reader.Resolve(new PdfIndirectReference(10, 0)));
    }

    [Fact]
    public void XrefStream_type1_referenceGenerationMatch_resolvesNormally()
    {
        var bytes = BuildXrefStreamPdf(obj10Generation: 3);
        using var reader = PdfReader.Open(bytes);

        var resolved = reader.Resolve(new PdfIndirectReference(10, 3));
        var dict = Assert.IsType<PdfDictionary>(resolved);
        var marker = Assert.IsType<PdfName>(dict.Get(new PdfName("Marker")));
        Assert.Equal("Hit", marker.Value);
    }

    [Fact]
    public void XrefStream_type2_compressedObject_isAlwaysGenerationZero()
    {
        using var doc = new PdfDocument();
        doc.UseObjectStreams = true;
        doc.AddPage();
        var bytes = SaveDocToBytes(doc);

        using var reader = PdfReader.Open(bytes);

        // The pages dict is packed into an ObjStm (a type-2 entry). ISO 32000-2 §7.5.7: objects
        // compressed into an object stream are always generation 0.
        var pagesRef = Assert.IsType<PdfIndirectReference>(reader.Catalog.Get(PdfName.Pages));

        Assert.Null(reader.Resolve(new PdfIndirectReference(pagesRef.ObjectNumber, 1)));
        Assert.NotNull(reader.Resolve(new PdfIndirectReference(pagesRef.ObjectNumber, 0)));
    }

    [Fact]
    public void XrefStream_type0_freedObject_notResurrectedByOlderRevision()
    {
        var bytes = BuildXrefStreamWithFreedObjectPdf();
        using var reader = PdfReader.Open(bytes);

        Assert.Null(reader.Resolve(5));
        Assert.DoesNotContain(5, reader.ObjectNumbers);
    }
}
