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
    /// A single-revision xref-stream PDF with an 8-byte generation field (<c>/W [1 4 8]</c>) whose
    /// value for object 10 overflows <see cref="int"/> — a row this parser never inspected before
    /// this PR, so an odd-but-decodable value here must not newly abort the whole document.
    /// </summary>
    private static byte[] BuildXrefStreamWithOverflowingGenerationPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBytes(byte[] b) => ms.Write(b);

        static byte[] Row148(byte type, long offset, ulong generation)
        {
            var row = new byte[13]; // 1 (type) + 4 (offset) + 8 (generation)
            row[0] = type;
            row[1] = (byte)((offset >> 24) & 0xFF);
            row[2] = (byte)((offset >> 16) & 0xFF);
            row[3] = (byte)((offset >> 8) & 0xFF);
            row[4] = (byte)(offset & 0xFF);
            for (var i = 0; i < 8; i++)
                row[5 + i] = (byte)((generation >> (8 * (7 - i))) & 0xFF);
            return row;
        }

        Write("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var o10 = (int)ms.Position;
        Write("10 0 obj\n<< /Marker /Hit >>\nendobj\n");

        var body = new MemoryStream();
        body.Write(Row148(0, 0, 0));                              // obj 0: free
        body.Write(Row148(1, o1, 0));                              // obj 1: catalog
        body.Write(Row148(1, o10, 0x1_0000_0005));                 // obj 10: generation overflows int
        var o11 = (int)ms.Position;
        body.Write(Row148(1, o11, 0));                             // obj 11: this xref stream itself
        var bodyArr = body.ToArray();

        Write($"11 0 obj\n<< /Type /XRef /Size 12 /W [1 4 8] /Index [0 2 10 2] /Root 1 0 R /Length {bodyArr.Length} >>\nstream\n");
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

    /// <summary>
    /// A catalog at generation 1, referenced from the trailer as <c>1 1 R</c> and recorded in the
    /// xref table at generation 1.
    /// </summary>
    private static byte[] BuildCatalogAtNonZeroGenerationPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 1 obj\n<< /Type /Catalog >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00001 n \n");
        Write("trailer\n<< /Size 2 /Root 1 1 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A reference embedded inside a dictionary VALUE (not a top-level "N G obj" header) at a
    /// nonzero generation — object 1's <c>/Extra</c> entry is <c>10 1 R</c>, and object 10 is
    /// genuinely written and recorded at generation 1.
    /// </summary>
    private static byte[] BuildDictValueReferenceAtNonZeroGenerationPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Extra 10 1 R >>\nendobj\n");
        var obj10Offset = (int)ms.Position;
        Write("10 1 obj\n<< /Marker /Hit >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00000 n \n");
        Write("10 1\n");
        Write($"{obj10Offset:D10} 00001 n \n");
        Write("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A two-revision PDF where object 5's second definition is written at generation 1 (the
    /// canonical shape a real editor produces when it reuses a freed object number). Revision 1's
    /// catalog references <c>5 0 R</c> and object 5 is <c>&lt;&lt; /Marker /Old &gt;&gt;</c>.
    /// Revision 2 rewrites both the catalog (now referencing <c>5 1 R</c>) and object 5 itself
    /// (now <c>5 1 obj</c>, <c>&lt;&lt; /Marker /New &gt;&gt;</c>).
    /// </summary>
    private static byte[] BuildIncrementalUpdateReusesObjectAtNonZeroGenerationPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1V1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Extra 5 0 R >>\nendobj\n");
        var obj5V1Offset = (int)ms.Position;
        Write("5 0 obj\n<< /Marker /Old >>\nendobj\n");

        var xref1Offset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1V1Offset:D10} 00000 n \n");
        Write("5 1\n");
        Write($"{obj5V1Offset:D10} 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xref1Offset}\n%%EOF\n");

        var obj1V2Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Extra 5 1 R >>\nendobj\n");
        var obj5V2Offset = (int)ms.Position;
        Write("5 1 obj\n<< /Marker /New >>\nendobj\n");

        var xref2Offset = (int)ms.Position;
        Write("xref\n");
        Write("1 1\n");
        Write($"{obj1V2Offset:D10} 00000 n \n");
        Write("5 1\n");
        Write($"{obj5V2Offset:D10} 00001 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R /Prev {xref1Offset} >>\n");
        Write($"startxref\n{xref2Offset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A classic table where object 10's generation field is space-padded ("    0") rather than
    /// zero-padded ("00000") — sloppy but unambiguous, and a field this parser never read before
    /// this PR.
    /// </summary>
    private static byte[] BuildClassicXrefWithSpacePaddedGenerationPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var obj10Offset = (int)ms.Position;
        Write("10 0 obj\n<< /Marker /Hit >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00000 n \n");
        Write("10 1\n");
        Write($"{obj10Offset:D10}     0 n \n"); // generation field is "    0", not "00000"
        Write("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A single-revision classic-table PDF where the xref records object 10 at generation 2, but
    /// object 10's own "N G obj" header actually says generation 0 — a malformed but openable
    /// (not rejected at parse time) inconsistency between the two. The xref is authoritative
    /// whenever it parses cleanly (ISO 32000-2 treats it as the source of truth for an object's
    /// generation), so a request for generation 2 succeeds here and one for generation 0 does not
    /// — the opposite of what the header alone would say.
    /// </summary>
    private static byte[] BuildXrefGenerationDisagreesWithHeaderPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var obj10Offset = (int)ms.Position;
        Write("10 0 obj\n<< /Marker /Hit >>\nendobj\n"); // header says generation 0

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00000 n \n");
        Write("10 1\n");
        Write($"{obj10Offset:D10} 00002 n \n"); // xref says generation 2
        Write("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A single-revision classic-table PDF where object 10's generation field is genuinely
    /// unparseable ("abcde") rather than merely sloppy (contrast the space-padded case above,
    /// which <see cref="System.Globalization.NumberStyles.Integer"/> parses to 0 legitimately). The xref cannot be
    /// authoritative for an entry it has no opinion on, so the object's own header — generation 1
    /// here — becomes the sole authority instead of the field being guessed as 0.
    /// </summary>
    private static byte[] BuildClassicXrefWithUnparseableGenerationPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var obj10Offset = (int)ms.Position;
        Write("10 1 obj\n<< /Marker /Hit >>\nendobj\n"); // header says generation 1

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00000 n \n");
        Write("10 1\n");
        Write($"{obj10Offset:D10} abcde n \n"); // unparseable generation field
        Write("trailer\n<< /Size 11 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

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

    [Fact]
    public void ClassicXref_catalogAtNonZeroGeneration_documentOpensSuccessfully()
    {
        // PdfDocumentReader's constructor resolves /Root through Resolve(PdfIndirectReference) — a
        // reference at the wrong generation resolves to null, and a null /Root threw "does not
        // resolve to a dictionary" out of PdfReader.Open. With the parser correctly carrying the
        // "1 1 R" trailer reference's generation through, and the xref recording object 1 at
        // generation 1 to match, the document must open normally.
        var bytes = BuildCatalogAtNonZeroGenerationPdf();
        using var reader = PdfReader.Open(bytes);

        Assert.NotNull(reader.Catalog);
        var typeObj = reader.Catalog.Get(PdfName.Type);
        var typeName = Assert.IsType<PdfName>(typeObj);
        Assert.Equal("Catalog", typeName.Value);
    }

    [Fact]
    public void ClassicXref_dictValueReferenceAtNonZeroGeneration_resolvesFromParsedReference()
    {
        // The actual #121 acceptance criterion: a reference PARSED FROM A DOCUMENT — not
        // hand-constructed with `new PdfIndirectReference(n, g)` — must carry its real generation
        // through to Resolve. ParseIntegerOrReference previously discarded the middle "G" token of
        // "N G R", so every reference read from a file looked like generation 0 regardless of what
        // the file actually said, and this reference at generation 1 would have failed to resolve.
        var bytes = BuildDictValueReferenceAtNonZeroGenerationPdf();
        using var reader = PdfReader.Open(bytes);

        var extraRef = Assert.IsType<PdfIndirectReference>(reader.Catalog.Get(new PdfName("Extra")));
        Assert.Equal(1, extraRef.Generation);

        var resolved = reader.ResolveValue(extraRef);
        var dict = Assert.IsType<PdfDictionary>(resolved);
        var marker = Assert.IsType<PdfName>(dict.Get(new PdfName("Marker")));
        Assert.Equal("Hit", marker.Value);
    }

    [Fact]
    public void ClassicXref_incrementalUpdateReusesObjectAtNonZeroGeneration_resolvesNewestRevision()
    {
        var bytes = BuildIncrementalUpdateReusesObjectAtNonZeroGenerationPdf();
        using var reader = PdfReader.Open(bytes);

        var extraRef = Assert.IsType<PdfIndirectReference>(reader.Catalog.Get(new PdfName("Extra")));
        Assert.Equal(1, extraRef.Generation);

        var resolved = reader.ResolveValue(extraRef);
        var dict = Assert.IsType<PdfDictionary>(resolved);
        var marker = Assert.IsType<PdfName>(dict.Get(new PdfName("Marker")));
        Assert.Equal("New", marker.Value);
    }

    [Fact]
    public void ClassicXref_spacePaddedGenerationField_opensSuccessfully()
    {
        var bytes = BuildClassicXrefWithSpacePaddedGenerationPdf();
        using var reader = PdfReader.Open(bytes);

        var resolved = reader.Resolve(10);
        var dict = Assert.IsType<PdfDictionary>(resolved);
        var marker = Assert.IsType<PdfName>(dict.Get(new PdfName("Marker")));
        Assert.Equal("Hit", marker.Value);
    }

    [Fact]
    public void Resolve_xrefGenerationIsAuthoritative_regardlessOfCallOrder()
    {
        // The xref is authoritative for an object's generation whenever it parses cleanly — the
        // header disagreeing (or simply never having been separately verified) does not matter.
        // A cache entry populated by a generation-agnostic Resolve(int) call must record the SAME
        // authoritative generation a cold generation-bearing call would use, or the answer would
        // depend on whether the cache happened to be warm yet.
        var bytes = BuildXrefGenerationDisagreesWithHeaderPdf();

        // Cold: generation-bearing call first.
        using (var reader = PdfReader.Open(bytes))
        {
            Assert.Null(reader.Resolve(new PdfIndirectReference(10, 0))); // header's generation: no match
            var resolved = reader.Resolve(new PdfIndirectReference(10, 2)); // xref's generation: matches
            var dict = Assert.IsType<PdfDictionary>(resolved);
            Assert.Equal("Hit", ((PdfName)dict.Get(new PdfName("Marker"))!).Value);
        }

        // Warm: generation-agnostic call first, to populate the cache, then the same two checks.
        using (var reader = PdfReader.Open(bytes))
        {
            Assert.NotNull(reader.Resolve(10));
            Assert.Null(reader.Resolve(new PdfIndirectReference(10, 0)));
            Assert.NotNull(reader.Resolve(new PdfIndirectReference(10, 2)));
        }
    }

    [Fact]
    public void Resolve_unparseableXrefGeneration_fallsBackToHeaderAuthority()
    {
        // Object 10's xref generation field is "abcde" (genuinely unparseable, not merely
        // sloppy) — the xref has no opinion on this object's generation, so its own header
        // (generation 1) becomes the sole authority instead of a guessed 0.
        var bytes = BuildClassicXrefWithUnparseableGenerationPdf();
        using var reader = PdfReader.Open(bytes);

        Assert.Null(reader.Resolve(new PdfIndirectReference(10, 0)));

        var resolved = reader.Resolve(new PdfIndirectReference(10, 1));
        var dict = Assert.IsType<PdfDictionary>(resolved);
        Assert.Equal("Hit", ((PdfName)dict.Get(new PdfName("Marker"))!).Value);
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

    [Fact]
    public void XrefStream_type1_generationFieldOverflow_opensInsteadOfThrowing()
    {
        var bytes = BuildXrefStreamWithOverflowingGenerationPdf();
        using var reader = PdfReader.Open(bytes);

        var resolved = reader.Resolve(10);
        var dict = Assert.IsType<PdfDictionary>(resolved);
        var marker = Assert.IsType<PdfName>(dict.Get(new PdfName("Marker")));
        Assert.Equal("Hit", marker.Value);
    }
}
