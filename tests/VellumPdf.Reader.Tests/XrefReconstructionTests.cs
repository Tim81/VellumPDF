// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// #184: when startxref is missing, unusable, or points at something that isn't recognisable as a
/// classic xref table or a cross-reference stream, the reader can rebuild the cross-reference
/// table by scanning the file for indirect-object headers instead of failing outright — but only
/// when a caller opts in (<see cref="PdfReaderOptions.AllowReconstruction"/>): reconstruction is a
/// best-effort recovery over structure the file's own xref has already failed to describe
/// correctly, so it defaults to off.
/// </summary>
public sealed class XrefReconstructionTests
{
    private static readonly PdfReaderOptions Reconstructing = new() { AllowReconstruction = true };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a well-formed single-revision classic-xref PDF with three objects (catalog, pages,
    /// page) and returns both the bytes and the byte offset of the "startxref" keyword, so callers
    /// can corrupt or strip the tail without hand-rolling the whole document.
    /// </summary>
    private static (byte[] Bytes, int StartxrefKeywordOffset, int XrefOffset) BuildClassicXrefPdf()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        var xref = (int)ms.Position;
        W("xref\n0 4\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00000 n \n");
        W($"{o3:D10} 00000 n \n");
        W("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        var startxrefKeywordOffset = (int)ms.Position;
        W($"startxref\n{xref}\n%%EOF\n");

        return (ms.ToArray(), startxrefKeywordOffset, xref);
    }

    private static byte[] Compress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    // ── Opt-in gate ──────────────────────────────────────────────────────────

    [Fact]
    public void Reconstruction_isOptIn_defaultOptionsStillThrows()
    {
        // Same broken-startxref file the reconstruction tests below recover from, opened WITHOUT
        // AllowReconstruction. It must fail exactly as it did before #184 existed — reconstruction
        // must never happen implicitly.
        var (baseBytes, _, xrefOffset) = BuildClassicXrefPdf();
        var corrupted = Encoding.ASCII.GetString(baseBytes)
            .Replace($"startxref\n{xrefOffset}\n", "startxref\n0\n", StringComparison.Ordinal);
        var bytes = Encoding.ASCII.GetBytes(corrupted);

        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes));
        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes, options: null));
        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = false }));
    }

    [Fact]
    public void WasReconstructed_isFalse_forAnOrdinaryDocument()
    {
        var (bytes, _, _) = BuildClassicXrefPdf();

        using var reader = PdfReader.Open(bytes);

        Assert.False(reader.WasReconstructed);
    }

    // ── Basic recovery ───────────────────────────────────────────────────────

    [Fact]
    public void Corrupted_startxref_offset_opens_via_reconstruction()
    {
        // Point startxref at byte 0 — the '%' of "%PDF-1.4", which is neither the 'xref' keyword
        // nor a plausible "N G obj" header. The offset itself is in range and numeric, so this is
        // squarely the ":93-95 the xref at that offset does not parse" reconstruction trigger.
        var (baseBytes, _, xrefOffset) = BuildClassicXrefPdf();
        var text = Encoding.ASCII.GetString(baseBytes);
        var corrupted = text.Replace($"startxref\n{xrefOffset}\n", "startxref\n0\n", StringComparison.Ordinal);
        Assert.NotEqual(text, corrupted); // sanity: the replacement actually happened
        var bytes = Encoding.ASCII.GetBytes(corrupted);

        using var reader = PdfReader.Open(bytes, Reconstructing);

        Assert.True(reader.WasReconstructed);
        Assert.NotNull(reader.Catalog);
        var typeName = Assert.IsType<PdfName>(reader.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);

        var pagesObj = reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!);
        var pagesDict = Assert.IsType<PdfDictionary>(pagesObj);
        Assert.Equal(1, ((PdfInteger)pagesDict.Get(PdfName.Count)!).Value);
    }

    [Fact]
    public void Startxref_line_removed_entirely_opens_via_reconstruction()
    {
        // Strip the trailing "startxref\nN\n%%EOF\n" entirely, leaving the objects, the xref table,
        // and the "trailer<<...>>" section intact — a plausible truncation.
        var (baseBytes, startxrefKeywordOffset, _) = BuildClassicXrefPdf();
        var bytes = baseBytes[..startxrefKeywordOffset];

        using var reader = PdfReader.Open(bytes, Reconstructing);

        Assert.NotNull(reader.Catalog);
        var typeName = Assert.IsType<PdfName>(reader.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);
    }

    [Fact]
    public void Reconstruction_recovers_root_via_type_catalog_scan_when_trailer_is_also_gone()
    {
        // Strip everything from the xref table onward — no 'xref', no 'trailer', no /Root anywhere
        // as an indirect-reference token. Recovery falls all the way back to locating the object
        // that declares /Type /Catalog directly.
        var (baseBytes, _, xrefOffset) = BuildClassicXrefPdf();
        var bytes = baseBytes[..xrefOffset];

        using var reader = PdfReader.Open(bytes, Reconstructing);

        Assert.NotNull(reader.Catalog);
        var typeName = Assert.IsType<PdfName>(reader.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);
    }

    [Fact]
    public void Reconstruction_takes_the_last_definition_of_a_repeated_object_number()
    {
        // Two "1 0 obj" headers for the same object number; the later one in the file must win,
        // mirroring how a /Prev chain resolves duplicates in a well-formed incremental update.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        W("1 0 obj\n<< /Type /Catalog /Marker (old) >>\nendobj\n");
        W("1 0 obj\n<< /Type /Catalog /Marker (new) >>\nendobj\n");
        // No xref/trailer/startxref at all — forces reconstruction outright.
        var bytes = ms.ToArray();

        using var reader = PdfReader.Open(bytes, Reconstructing);

        var marker = Assert.IsType<PdfLiteralString>(reader.Catalog.Get(new PdfName("Marker")));
        Assert.Equal("new", Encoding.ASCII.GetString(marker.Bytes.Span));
    }

    [Fact]
    public void Truncated_garbage_with_no_objects_still_throws_InvalidDataException()
    {
        // No startxref and no "N G obj" headers anywhere: reconstruction itself must fail cleanly,
        // not silently succeed with an empty document.
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nthis is not a PDF body at all\n");

        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes, Reconstructing));
    }

    [Fact(Timeout = 10_000)]
    public void Reconstruction_hostileInputWithManyFalseMarkers_throwsAndDoesNotHang()
    {
        // No 'startxref' anywhere, forcing reconstruction. The buffer packs in tens of thousands of
        // "trailer<<" occurrences that never close, and thousands of bare object headers with no
        // /Root or /Type /Catalog anywhere — exactly the pattern that would make an unbounded
        // reconstruction scan quadratic (repeated full-file rescans per candidate). The bounded
        // candidate cap and per-candidate byte cap must keep this fast and still fail cleanly.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        for (var i = 0; i < 50_000; i++)
            sb.Append("trailer<< /NotClosed ");
        for (var i = 0; i < 20_000; i++)
            sb.Append(i).Append(" 0 obj\nendobj\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());

        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes, Reconstructing));
    }

    [Fact]
    public void Reconstruction_recoversTheGenerationFromTheObjectHeader()
    {
        // Reconstruction runs precisely when the cross-reference table is unusable, so the object
        // header is the only authority left for a generation. Assuming 0 would make a legitimately
        // nonzero-generation object unresolvable at every generation (#121) — the same silent-wrong
        // failure the xref-side generation handling exists to avoid.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Extra 10 4 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        W("10 4 obj\n<< /Marker /Recovered >>\nendobj\n");
        // startxref points nowhere usable, forcing reconstruction.
        W("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 11 /Root 1 0 R >>\n");
        W("startxref\n999999\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), Reconstructing);

        var extra = reader.ResolveValue(reader.Catalog.Get(new PdfName("Extra"))!);
        var dict = Assert.IsType<PdfDictionary>(extra);
        Assert.Equal("Recovered", Assert.IsType<PdfName>(dict.Get(new PdfName("Marker"))).Value);
    }

    // ── C4: a nonzero-generation /Root synthesized by a fallback path ──────────

    [Fact]
    public void Reconstruction_synthesizedRootAtNonzeroGeneration_stillResolves()
    {
        // The catalog sits at "1 4 obj" (generation 4), reached only through the generic /Root
        // scan fallback (no 'trailer' keyword at all in this file). A synthesized reference that
        // hardcodes generation 0 fails to resolve: the xref-side generation is authoritative once
        // recovered (#121), so a 0-vs-4 mismatch resolves to nothing, and PdfDocumentReader's
        // constructor reports "/Root does not resolve to a dictionary" for what is, underneath,
        // a perfectly recoverable document.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 4 obj\n<< /Type /Catalog >>\nendobj\n");
        // A "/Root 1 4 R"-shaped token for the generic scan to find; not inside a real trailer.
        W("2 0 obj\n<< /NotARealTrailer true /Root 1 4 R >>\nendobj\n");
        // startxref points nowhere usable, forcing reconstruction.
        W("startxref\n999999\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), Reconstructing);

        Assert.Equal("Catalog", Assert.IsType<PdfName>(reader.Catalog.Get(PdfName.Type)).Value);
    }

    // ── C1: trailer synthesis must not discard /Encrypt ─────────────────────

    [Fact]
    public void Reconstruction_ofAnEncryptedXrefStreamDocument_stillThrowsUnsupportedPdfFeature()
    {
        // PDF 1.5+ normal layout: no "trailer<<...>>" section at all — /Root and /Encrypt live
        // directly in the cross-reference stream's own dictionary. Recovery falls through to the
        // generic "/Root N G R" scan, which used to synthesize a trailer containing ONLY /Root,
        // silently discarding /Encrypt and opening the file as if it were plain (#184 C1).
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Filter /Standard /V 2 /R 3 >>\nendobj\n"); // stand-in /Encrypt dict

        // A genuine xref stream, but startxref (below) won't be usable, so recovery must find
        // /Root and /Encrypt via the generic scan rather than by parsing this stream at all.
        var body = new byte[] { 1, 0, 0, 0, 0, 0 }; // one dummy row; irrelevant, never parsed
        var compressed = Compress(body);
        W($"2 0 obj\n<< /Type /XRef /Size 4 /W [1 4 0] /Root {o1 - o1 + 1} 0 R "
            + $"/Encrypt 3 0 R /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        ms.Write(compressed);
        W("\nendstream\nendobj\n");

        // startxref points at garbage, forcing reconstruction instead of the normal xref-stream path.
        W("startxref\n0\n%%EOF\n");
        var bytes = ms.ToArray();

        Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(bytes, Reconstructing));
        _ = o3;
    }

    [Fact]
    public void Reconstruction_encryptFoundOnlyViaFallbackScan_stillThrowsUnsupportedPdfFeature()
    {
        // The 'trailer' keyword occurrences in the file never yield a dictionary with /Root — so
        // FindTrailerWithRoot finds nothing (or exhausts its candidate budget on decoys) — but a
        // literal "/Root N G R" and "/Encrypt N G R" both exist elsewhere in the file for the
        // generic-scan fallback to find.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Filter /Standard /V 2 /R 3 >>\nendobj\n");
        // A decoy "trailer" with no /Root, so FindTrailerWithRoot finds nothing usable.
        W("trailer<< /NotRoot true >>\n");
        // The generic scan finds these two directly.
        W($"/Root {(int)0 + 1} 0 R /Encrypt {2} 0 R\n");
        W("startxref\n0\n%%EOF\n");
        var bytes = ms.ToArray();

        Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(bytes, Reconstructing));
        _ = (o1, o2);
    }

    // ── C2: a catalog packed inside an object stream ────────────────────────

    [Fact]
    public void Reconstruction_recoversACatalogPackedInAnObjectStream()
    {
        // The catalog (object 1) is NOT a top-level "1 0 obj" header at all — it's compressed
        // inside object stream 5's body, the normal layout for a modern (xref-stream +
        // object-stream) PDF. The header scan alone can't see it; without decoding known object
        // streams, the /Type /Catalog fallback would find the literal bytes "/Type /Catalog"
        // inside the container's own (undecoded) stream body and misidentify the CONTAINER as the
        // catalog (#184 C2) — this asserts the actual catalog resolves, not the container.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");

        // ObjStm body: header "1 0" (object 1 at relative offset 0), then the object's own bytes.
        var objBody = "<< /Type /Catalog /Marker /FromObjStm >>"u8.ToArray();
        var header = "1 0 "u8.ToArray();
        var objStmBody = new byte[header.Length + objBody.Length];
        header.CopyTo(objStmBody, 0);
        objBody.CopyTo(objStmBody, header.Length);

        W($"5 0 obj\n<< /Type /ObjStm /N 1 /First {header.Length} /Length {objStmBody.Length} >>\nstream\n");
        ms.Write(objStmBody);
        W("\nendstream\nendobj\n");

        // startxref points nowhere usable, forcing reconstruction; no /Root token anywhere, so
        // recovery must fall all the way to the /Type /Catalog scan, which must resolve through
        // the object-stream expansion rather than matching the container's own bytes.
        W("startxref\n999999\n%%EOF\n");
        var bytes = ms.ToArray();

        using var reader = PdfReader.Open(bytes, Reconstructing);

        var typeObj = reader.Catalog.Get(PdfName.Type);
        var typeName = Assert.IsType<PdfName>(typeObj);
        Assert.Equal("Catalog", typeName.Value);
        var marker = Assert.IsType<PdfName>(reader.Catalog.Get(new PdfName("Marker")));
        Assert.Equal("FromObjStm", marker.Value);
    }

    // ── C3: an embedded PDF must not hijack the reconstructed graph ─────────

    [Fact]
    public void Reconstruction_ignoresObjectHeadersInsideAStreamBody()
    {
        // Object 1 (the real catalog) is declared first. A LATER object is a stream whose binary
        // body itself contains "1 0 obj ... endobj"-shaped bytes — the shape a PDF stored as an
        // /EmbeddedFile attachment would have, and the specific case (#184 C3). Under a byte-level
        // scan with no notion of stream bodies, that embedded header is later in the file and
        // would win under last-definition-wins, replacing the real catalog with the attachment's.
        // Honouring /Length to skip the stream body prevents that.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        W("1 0 obj\n<< /Type /Catalog /Marker /RealCatalog >>\nendobj\n");

        const string embedded = "1 0 obj\n<< /Type /NotACatalog >>\nendobj\n";
        var embeddedBytes = Encoding.ASCII.GetBytes(embedded);
        W($"9 0 obj\n<< /Type /EmbeddedFile /Length {embeddedBytes.Length} >>\nstream\n");
        ms.Write(embeddedBytes);
        W("\nendstream\nendobj\n");

        // No 'trailer' section and no "/Root N G R" token anywhere — recovery must fall to the
        // /Type /Catalog scan, which must find object 1's REAL header, not the one inside object
        // 9's stream body.
        W("startxref\n999999\n%%EOF\n");
        var bytes = ms.ToArray();

        using var reader = PdfReader.Open(bytes, Reconstructing);

        var marker = Assert.IsType<PdfName>(reader.Catalog.Get(new PdfName("Marker")));
        Assert.Equal("RealCatalog", marker.Value);
    }

    [Fact]
    public void Reconstruction_classicTrailerWithEncrypt_stillThrowsUnsupportedPdfFeature()
    {
        // The recovered trailer is found via the real "trailer<<...>>" section (FindTrailerWithRoot),
        // which preserves every key it declares, /Encrypt included — this is the primary recovery
        // path, distinct from the two fallback-synthesis paths the tests above cover.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        W("xref\n0 2\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W("trailer\n<< /Size 2 /Root 1 0 R /Encrypt 2 0 R >>\n");
        // startxref points at garbage, forcing reconstruction to find the 'trailer' section above
        // by scanning rather than by following startxref.
        W("startxref\n0\n%%EOF\n");
        var bytes = ms.ToArray();

        Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(bytes, Reconstructing));
    }

    // ── WasReconstructed refuses AppendRevision ─────────────────────────────

    [Fact]
    public void WasReconstructed_isTrue_afterReconstruction()
    {
        var (baseBytes, _, xrefOffset) = BuildClassicXrefPdf();
        var corrupted = Encoding.ASCII.GetString(baseBytes)
            .Replace($"startxref\n{xrefOffset}\n", "startxref\n0\n", StringComparison.Ordinal);
        var bytes = Encoding.ASCII.GetBytes(corrupted);

        using var reader = PdfReader.Open(bytes, Reconstructing);

        Assert.True(reader.WasReconstructed);
    }

    [Fact]
    public void AppendRevision_onAReconstructedDocument_throwsInvalidOperationException()
    {
        // A reconstructed document's object graph is a best-effort guess; AppendRevision must
        // refuse outright rather than let a caller (DssBuilder, ArchiveTimestampBuilder) build a
        // PAdES revision on top of it and hand back an artifact this library cannot reliably
        // reopen (#184).
        var (baseBytes, _, xrefOffset) = BuildClassicXrefPdf();
        var corrupted = Encoding.ASCII.GetString(baseBytes)
            .Replace($"startxref\n{xrefOffset}\n", "startxref\n0\n", StringComparison.Ordinal);
        var bytes = Encoding.ASCII.GetBytes(corrupted);

        using var reader = PdfReader.Open(bytes, Reconstructing);
        Assert.True(reader.WasReconstructed);

        var ex = Assert.Throws<InvalidOperationException>(
            () => reader.AppendRevision([(99, 0, new PdfDictionary())]));
        Assert.Contains("reconstructed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
