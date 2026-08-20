// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Opens every fixture in the #196 third-party corpus and asserts on its distinguishing feature,
/// so the corpus is more than bytes that merely happen to contain the right substrings.
/// <see cref="ThirdPartyFixtureCorpusTests"/> guards the bytes; this class exercises the reader
/// against them.
/// </summary>
public sealed class ThirdPartyReaderBehaviorTests
{
    private const string GoldenMarker = "Hello, VellumPdf golden test!";

    // ── Hybrid-reference file (ISO 32000-2 §7.5.8.4) ────────────────────────

    /// <summary>
    /// The documented "hidden object" convention: object 3 is free in the FIRST revision's classic
    /// table, then defined live in the SECOND revision's /XRefStm (a /Prev-linked cross-reference
    /// stream). A pre-1.5 reader follows /Prev, finds the free entry, and sees nothing; a reader
    /// that understands cross-reference streams finds the live definition first and never looks
    /// past it. qpdf's <c>--show-object=3</c> agrees with VellumPdf here (see README.md); poppler's
    /// rendered output only shows the surrounding page survives, since it reads cross-reference
    /// streams too and so is not a stand-in for a pre-1.5 consumer. This fixture still has a real
    /// third-party oracle on the object itself, unlike the same-section variant below — qpdf just
    /// carries that role alone.
    /// </summary>
    [Fact]
    public void Hybrid_hiddenObject_resolvesFromTheNewerRevisionsXRefStm()
    {
        using var reader = PdfReader.Open(Load("hybrid-spec-convention.pdf"));

        Assert.Equal(2, reader.Revisions.Count);

        var obj3 = reader.Resolve(3);
        var dict = Assert.IsType<PdfDictionary>(obj3);
        var note = Assert.IsType<PdfLiteralString>(dict.Get(new PdfName("Note")));
        Assert.Equal("HIDDENVIAXREFSTM", Encoding.Latin1.GetString(note.Bytes.Span));
    }

    /// <summary>The page's content stream is unaffected by the hidden object and must still resolve.</summary>
    [Fact]
    public void Hybrid_pageContents_resolvesAlongsideTheHiddenObject()
    {
        using var reader = PdfReader.Open(Load("hybrid-spec-convention.pdf"));
        Assert.Contains("BASE", ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same free-then-redefine shape as above, but within a SINGLE revision rather than across
    /// a /Prev chain. ISO 32000-2 §7.5.8.4's normative sentence covers a free entry in a PREVIOUS
    /// revision, tested above; it does not cover this same-section shape, which is the subject of
    /// the open <see href="https://github.com/pdf-association/pdf-issues/issues/237">pdf-issues
    /// #237</see>. The erratum is unresolved, but the discussion so far favours the free entry
    /// winning — qpdf resolves this object to null and poppler discards the xref and reconstructs,
    /// so neither agrees with VellumPdf either. VellumPdf.Reader's own precedence code (see the
    /// <c>localFreed</c> comment in <c>XrefParser.ParseOneRevision</c>) applies the same rule to it
    /// deliberately, as a superset on a contested construct rather than a settled reading; tracked
    /// as <see href="https://github.com/Tim81/VellumPDF/issues/206">#206</see>. This fixture pins
    /// current behaviour, not a conformance claim. See README.md.
    /// </summary>
    [Fact]
    public void HybridSameSection_object4_resolvesFromXRefStm_notTheClassicTableFreeEntry()
    {
        using var reader = PdfReader.Open(Load("hybrid-samesection-undefined.pdf"));

        var obj4 = reader.Resolve(4);
        Assert.NotNull(obj4);
        var dict = Assert.IsType<PdfDictionary>(obj4);
        Assert.True(dict.TryGet(PdfName.Length, out _));
    }

    /// <summary>The page's /Contents is object 4; it must resolve to the real content stream.</summary>
    [Fact]
    public void HybridSameSection_pageContents_resolvesThroughXRefStm()
    {
        using var reader = PdfReader.Open(Load("hybrid-samesection-undefined.pdf"));
        Assert.Contains("HYBRIDXREFSTM", ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    // ── Baseline ──────────────────────────────────────────────────────────

    /// <summary>The plain qpdf-normalized base every other qpdf/poppler-derived fixture descends from.</summary>
    [Fact]
    public void Baseline_pageContents_decodesToTheGoldenMarker()
    {
        using var reader = PdfReader.Open(Load("baseline.pdf"));
        Assert.Contains(GoldenMarker, ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    // ── Object streams + cross-reference stream ─────────────────────────────

    [Fact]
    public void ObjStmXrefStream_catalog_resolvesAsCatalog()
    {
        using var reader = PdfReader.Open(Load("objstm-xrefstream.pdf"));
        var typeName = Assert.IsType<PdfName>(reader.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);
    }

    [Fact]
    public void ObjStmXrefStream_pageContents_decodesToTheGoldenMarker()
    {
        using var reader = PdfReader.Open(Load("objstm-xrefstream.pdf"));
        Assert.Contains(GoldenMarker, ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    // ── Linearized ────────────────────────────────────────────────────────

    [Fact]
    public void Linearized_pageContents_decodesToTheGoldenMarker()
    {
        using var reader = PdfReader.Open(Load("linearized.pdf"));
        Assert.Contains(GoldenMarker, ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    // ── Incremental update (poppler pdfattach) ───────────────────────────────

    [Fact]
    public void IncrementalUpdate_isRecognizedAsTwoRevisions()
    {
        using var reader = PdfReader.Open(Load("incremental-update.pdf"));
        Assert.Equal(2, reader.Revisions.Count);
    }

    /// <summary>The appended revision only adds an attachment; the original page must still resolve.</summary>
    [Fact]
    public void IncrementalUpdate_pageContents_stillResolves_fromTheBaseRevision()
    {
        using var reader = PdfReader.Open(Load("incremental-update.pdf"));
        Assert.Contains(GoldenMarker, ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalUpdate_attachment_resolvesFromTheAppendedRevision()
    {
        using var reader = PdfReader.Open(Load("incremental-update.pdf"));
        Assert.Contains("third-party fixture attachment for #196",
            ResolveEmbeddedFileText(reader), StringComparison.Ordinal);
    }

    // ── Non-zero generation ──────────────────────────────────────────────────

    /// <summary>
    /// The catalog is "1 1 obj", a reference at a nonzero generation read straight from a document
    /// (ISO 32000-2 §7.3.10) rather than built in C# — the gap the #121 review found in
    /// GenerationNumberTests. A successful open already proves generation 1 resolved (the
    /// constructor requires /Root to resolve); the explicit checks below pin the mismatch rule too.
    /// </summary>
    [Fact]
    public void NonzeroGeneration_catalog_resolvesOnlyAtItsRecordedGeneration()
    {
        using var reader = PdfReader.Open(Load("nonzero-gen-base.pdf"));

        Assert.NotNull(reader.Resolve(new PdfIndirectReference(1, 1)));
        Assert.Null(reader.Resolve(new PdfIndirectReference(1, 0)));
    }

    [Fact]
    public void NonzeroGeneration_pageContents_decodesToItsMarker()
    {
        using var reader = PdfReader.Open(Load("nonzero-gen-base.pdf"));
        Assert.Contains("NONZEROGEN", ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    /// <summary>
    /// An appended revision on a document whose catalog sits at a nonzero generation — the shape
    /// the #121 review found the reader untested against, since every prior appended-revision
    /// fixture came from <c>AppendRevision</c> itself on a self-produced generation-0 document.
    /// This exercises the reader against a poppler-appended revision; it does not call
    /// <c>AppendRevision</c>. Poppler rewrote the catalog again in the new revision, still at
    /// generation 1.
    /// </summary>
    [Fact]
    public void NonzeroGenerationIncremental_isRecognizedAsTwoRevisions()
    {
        using var reader = PdfReader.Open(Load("nonzero-generation.pdf"));
        Assert.Equal(2, reader.Revisions.Count);
    }

    [Fact]
    public void NonzeroGenerationIncremental_catalog_stillResolvesAtGeneration1()
    {
        using var reader = PdfReader.Open(Load("nonzero-generation.pdf"));
        Assert.NotNull(reader.Resolve(new PdfIndirectReference(1, 1)));
        Assert.Null(reader.Resolve(new PdfIndirectReference(1, 0)));
    }

    [Fact]
    public void NonzeroGenerationIncremental_pageContents_stillResolves_fromTheBaseRevision()
    {
        using var reader = PdfReader.Open(Load("nonzero-generation.pdf"));
        Assert.Contains("NONZEROGEN", ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    [Fact]
    public void NonzeroGenerationIncremental_attachment_resolvesFromTheAppendedRevision()
    {
        using var reader = PdfReader.Open(Load("nonzero-generation.pdf"));
        Assert.Contains("third-party fixture attachment for #196",
            ResolveEmbeddedFileText(reader), StringComparison.Ordinal);
    }

    // ── Freed object number, reused at a bumped generation ──────────────────

    [Fact]
    public void FreedObjectReuse_isRecognizedAsThreeRevisions()
    {
        using var reader = PdfReader.Open(Load("freed-object-reuse.pdf"));
        Assert.Equal(3, reader.Revisions.Count);
    }

    /// <summary>
    /// Object 5 was live at generation 0, deleted with a free entry recording 1 as the next
    /// generation, then the number was reused at generation 1. Object 7 was live at generation 0,
    /// deleted the same way, and never redefined. It is the reference's generation that has to
    /// match the xref entry's recorded generation for a resolve to succeed — ISO 32000-2 never
    /// requires the "N G obj" header to agree, and <see cref="PdfDocumentReader"/> does not check
    /// it when the xref parsed cleanly. Mutation testing on object 5 alone found the deletion
    /// tracking carries no weight there: removing it still kills two pre-existing
    /// <see cref="GenerationNumberTests"/>, yet this test kept passing, because revision 3's
    /// definition wins regardless of whether the free entry was ever recorded. Object 7 is what
    /// closes that gap — nothing redefines it, so returning null for it requires the deletion to
    /// have actually been honoured. qpdf agrees on both.
    /// </summary>
    [Fact]
    public void FreedObjectReuse_resolvesTheReusedObject_andNotTheDeletedGeneration()
    {
        using var reader = PdfReader.Open(Load("freed-object-reuse.pdf"));

        // Ask for the stale generation while the cache is cold: resolving 5 1 first caches object 5 at
        // generation 1, and the 5 0 lookup then exits on the cached generation without reaching the
        // resolution path at all. Cold, it goes through that path — though it still does not isolate
        // one mechanism, because the xref entry and the object header both say generation 1, so either
        // check alone returns null. The assertion below on object 7 is the load-bearing one.
        Assert.Null(reader.Resolve(new PdfIndirectReference(5, 0)));

        // Object 7 is the half that depends on the deletion having been recorded: revision 2 frees it
        // and nothing redefines it, so returning null requires having honoured the free entry. Object 5
        // cannot show that — revision 3's definition wins whether or not the deletion was tracked.
        // qpdf agrees: --show-object=7 is null here, and resolves the object in a control built without
        // that free entry.
        Assert.Null(reader.Resolve(new PdfIndirectReference(7, 0)));

        var reused = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(5, 1)));
        var note = Assert.IsType<PdfLiteralString>(reused.Get(new PdfName("Note")));
        Assert.Equal("REUSEDATGEN1", Encoding.Latin1.GetString(note.Bytes.Span));
    }

    /// <summary>The three revisions must not disturb the page, which never changed.</summary>
    [Fact]
    public void FreedObjectReuse_pageContents_stillResolves()
    {
        using var reader = PdfReader.Open(Load("freed-object-reuse.pdf"));
        Assert.Contains("FREEDREUSE", ResolveFirstPageText(reader), StringComparison.Ordinal);
    }

    // ── Damaged files ─────────────────────────────────────────────────────

    /// <summary>
    /// No 'startxref' survives the truncation. <see cref="PdfReader.Open(byte[])"/> must fail with
    /// the reader's vocabulary for a malformed file, not with an exception that reveals an unguarded
    /// array access or unbounded allocation.
    /// </summary>
    [Fact]
    public void TruncatedTail_throwsInvalidDataException()
    {
        var bytes = Load("truncated-tail.pdf");
        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes));
    }

    /// <summary>
    /// The startxref offset points past end-of-file. Same requirement as above. #184's xref-rebuild
    /// fallback (tracked separately in #197) is opt-in and defaults off — the option that would turn
    /// it on starts <see langword="false"/>, and a damaged file still throws exactly as it does
    /// today unless a caller asks for reconstruction explicitly. This test opens with the default
    /// options, so it pins that default-options behaviour and should keep passing once #184 lands;
    /// if it ever fails, that is a regression, not a success signal.
    /// </summary>
    [Fact]
    public void BrokenStartxref_throwsInvalidDataException()
    {
        var bytes = Load("broken-startxref.pdf");
        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes));
    }

    /// <summary>
    /// Unlike the two fixtures above, a bad /Length is not fatal: the parser falls back to
    /// scanning for 'endstream' once the declared length fails to land on it. The fixture has only
    /// one 'endstream' after the body start, so it pins the /Length-preferred branch's "verify
    /// endstream follows, else fall back" rule specifically, rather than ScanToEndstream (#105)'s
    /// own preference tiers. The assertion checks the full recovered body, so a scan that stopped
    /// at the wrong marker and silently truncated or extended the content would still be caught.
    /// </summary>
    [Fact]
    public void LengthMismatch_recoversTheFullStreamBody()
    {
        using var reader = PdfReader.Open(Load("length-mismatch.pdf"));
        Assert.Equal("BT /F1 24 Tf 40 100 Td (LENGTHMISMATCH) Tj ET", ResolveFirstPageText(reader));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Catalog → /Pages → /Kids[0] → /Contents, decoded as Latin-1 text.</summary>
    private static string ResolveFirstPageText(PdfDocumentReader reader)
    {
        var pagesObj = reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!);
        var pages = Assert.IsType<PdfDictionary>(pagesObj);

        var kidsObj = reader.ResolveValue(pages.Get(PdfName.Kids)!);
        var kids = Assert.IsType<PdfArray>(kidsObj);

        var pageObj = reader.ResolveValue(kids[0]);
        var page = Assert.IsType<PdfDictionary>(pageObj);

        var contentsRef = Assert.IsType<PdfIndirectReference>(page.Get(PdfName.Contents));
        var stream = reader.ResolveStream(contentsRef);
        Assert.NotNull(stream);

        var decoded = reader.GetDecodedStreamData(stream);
        Assert.NotNull(decoded);
        return Encoding.Latin1.GetString(decoded);
    }

    /// <summary>Catalog → /Names → /EmbeddedFiles → /Names[1] → /EF → /F, decoded as Latin-1 text.</summary>
    private static string ResolveEmbeddedFileText(PdfDocumentReader reader)
    {
        var namesRootObj = reader.ResolveValue(reader.Catalog.Get(new PdfName("Names"))!);
        var namesRoot = Assert.IsType<PdfDictionary>(namesRootObj);

        var embeddedFilesObj = reader.ResolveValue(namesRoot.Get(new PdfName("EmbeddedFiles"))!);
        var embeddedFiles = Assert.IsType<PdfDictionary>(embeddedFilesObj);

        var namesArrObj = reader.ResolveValue(embeddedFiles.Get(new PdfName("Names"))!);
        var namesArr = Assert.IsType<PdfArray>(namesArrObj);

        // [name-string, filespec-ref, ...] pairs (ISO 32000-2 §7.9.6); the filespec is odd-indexed.
        var filespecObj = reader.ResolveValue(namesArr[1]);
        var filespec = Assert.IsType<PdfDictionary>(filespecObj);

        var efObj = reader.ResolveValue(filespec.Get(new PdfName("EF"))!);
        var ef = Assert.IsType<PdfDictionary>(efObj);

        var fileRef = Assert.IsType<PdfIndirectReference>(ef.Get(new PdfName("F")));
        var stream = reader.ResolveStream(fileRef);
        Assert.NotNull(stream);

        var decoded = reader.GetDecodedStreamData(stream);
        Assert.NotNull(decoded);
        return Encoding.Latin1.GetString(decoded);
    }

    private static byte[] Load(string name)
    {
        const string Prefix = "ThirdParty/";
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
