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
    /// #237</see> — which entry should win is explicitly unresolved there. VellumPdf.Reader's own
    /// precedence code (see the <c>localFreed</c> comment in <c>XrefParser.ParseOneRevision</c>)
    /// applies the same rule to it deliberately, as a superset on a contested construct rather than
    /// a settled reading. qpdf resolves this object to null and poppler discards the xref and
    /// reconstructs — neither agrees with VellumPdf, so this fixture pins current behaviour, not a
    /// conformance claim. See README.md.
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
    /// Object 5 was live at generation 0, deleted, then the number was reused at generation 1. An
    /// xref entry's generation has to match the object header for the reference to resolve, so a
    /// reader that ignores generations entirely would return the same object for both. Mutation
    /// testing found the deletion tracking itself carries no weight here: removing it still kills
    /// two pre-existing <see cref="GenerationNumberTests"/>, yet this test keeps passing, because
    /// revision 3's definition wins regardless of whether the free entry was ever recorded. What
    /// this pins is correct end-to-end behaviour on a real three-revision, third-party-shaped
    /// document — confidence in the mechanism, not new discrimination beyond that existing
    /// coverage. qpdf agrees on both halves.
    /// </summary>
    [Fact]
    public void FreedObjectReuse_resolvesTheReusedObject_andNotTheDeletedGeneration()
    {
        using var reader = PdfReader.Open(Load("freed-object-reuse.pdf"));

        var reused = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(5, 1)));
        var note = Assert.IsType<PdfLiteralString>(reused.Get(new PdfName("Note")));
        Assert.Equal("REUSEDATGEN1", Encoding.Latin1.GetString(note.Bytes.Span));

        Assert.Null(reader.Resolve(new PdfIndirectReference(5, 0)));
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
    /// The startxref offset points past end-of-file. Same requirement as above. Once #184's
    /// xref-rebuild fallback lands, a broken startxref is a candidate for recovery by full-file
    /// scan rather than a hard failure — this test failing at that point is the success signal
    /// that the fallback reached this fixture, not a regression to chase.
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
