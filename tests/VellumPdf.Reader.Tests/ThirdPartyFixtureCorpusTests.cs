// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Guards the committed third-party corpus (#196) itself, before any reader behaviour test trusts
/// it. Every prior reader fixture came from VellumPdf's own writer, so the reader had only ever been
/// exercised against its own dialect of PDF — generation 0 everywhere, no hybrid-reference file, no
/// object-stream layout but its own. The #121 review found three defects that this single root
/// cause explained; this corpus is the fixtures that would have caught them.
///
/// As with <see cref="EncryptedFixtureCorpusTests"/>, the SHA-256 is what actually identifies a
/// fixture today; it alone rules out a swapped or truncated file. The token checks earn their keep
/// on regeneration instead: if a fixture is ever rebuilt and its digest updated to match, a token
/// still catches a rebuild that lazily dropped the structural property the fixture exists to pin
/// — a rebuilt "linearized.pdf" that no longer carries "/Linearized", for instance.
/// </summary>
public sealed class ThirdPartyFixtureCorpusTests
{
    // file, SHA-256, tokens that must be present (Latin-1 byte scan), tokens that must be absent.
    // MustContain pins the structural property that makes the fixture worth having; MustNotContain
    // pins its absence where that absence is itself the point (a damaged file with no /Prev, an
    // object-stream file with no classic xref table left behind).
    private static readonly (string Name, string Sha256, string[] MustContain, string[] MustNotContain)[]
        Corpus =
    [
        // Hand-built: two revisions, object 3 free in the FIRST revision's classic table and
        // defined live in the SECOND revision's /XRefStm — exactly the "hidden object" convention
        // ISO 32000-2 §7.5.8.4 describes, and the one the #121 review found mishandled. qpdf resolves
        // object 3 the same way VellumPdf does; poppler's output only shows the surrounding page
        // survives, since it reads xref streams too and is no stand-in for a pre-1.5 consumer — see
        // README for the two-way comparison this fixture's assertions rest on. The fourth token pins
        // the free entry's own subsection block (object 2 live, object 3 free, object 4 live) rather
        // than the bare "0000000000 00001 f" line alone: before #206 this fixture's tokens pinned no
        // free entry at all, so a regeneration that dropped it would have passed unnoticed. The bare
        // line alone also would not rule out a regeneration that moved the free entry onto a
        // different object's row within this same fixture — nothing in the bare token names which
        // object number it frees. It happens to recur byte-for-byte in the same-section fixture below
        // and in freed-object-reuse.pdf too (verified by byte scan), but that is a separate fact
        // about the corpus as a whole; MustContain is evaluated per fixture, so a token also
        // appearing in some other file rules nothing out here on its own.
        ("hybrid-spec-convention.pdf",
            "6fe78b1957e53120953ae315cac31686b6a825c20cc478a8c1ea7f4ce7beb84d",
            ["/Prev 402", "/XRefStm 822", "/Type /ObjStm",
             "0000000064 00000 n \n0000000000 00001 f \n0000000121 00000 n"],
            []),

        // Built by eng/generate-hybrid-samesection-fixture.py, not edited by hand: the free entry
        // and the /XRefStm definition sit in the SAME revision instead of a /Prev-linked previous
        // one. VellumPdf.Reader now returns null for object 4 here, matching qpdf; the fixtures
        // README's "Two hybrid fixtures" section carries the full argument for that reading and its
        // sourcing against pdf-association/pdf-issues#237 (open) — kept there rather than repeated
        // here so it has one home instead of six.
        //
        // A regeneration could smuggle object 7 a live classic row (something a pre-1.5 reader
        // would find, defeating the "defined ONLY by the /XRefStm" property this fixture pins) in
        // at least three ways: widen the header to "0 8" and append a live row before "trailer"
        // (variant A); leave the header at "0 7" and append a second subsection "7 1" with object 7
        // live, after object 6's row (variant B); or leave the header at "0 7" and PREPEND a
        // subsection "7 1" before it — legal, since ISO 32000-2 §7.5.4 lets cross-reference
        // subsections "appear in any order" (variant C). The first two tokens below each rule out
        // two of the three, not one each, and no single token here rules out all three: the header
        // token ("xref\n0 7\n") is unchanged by B (still present) but is broken by both A (the "0 8"
        // rewrite) and C (something now sits between "xref\n" and "0 7\n"); the table-end token
        // ("...f \ntrailer") is unchanged by C (still present) but is broken by both A and B, which
        // each insert a row between object 6's free entry and "trailer". Together the two catch all
        // three variants (verified directly by constructing all three against this token set). The
        // fourth token pins the free entry's own subsection block (object 3 live, object 4 free,
        // object 5 live), not the bare "0000000000 00001 f" line, which recurs identically in
        // freed-object-reuse.pdf; moving the free entry onto object 5's row instead leaves that bare
        // line matching but breaks this block (verified directly). Object 7 is defined ONLY by the
        // /XRefStm — absent from the classic table entirely — so the fifth and sixth tokens (the
        // /Index declaring it, and its payload) are what stop the object-4 null from being vacuous: a
        // reader that skipped the /XRefStm outright would also fail to resolve object 7, not just
        // correctly null object 4.
        // A #372 review round found two more ways to smuggle object 7 a live classic-table row,
        // neither closed by the six tokens above (see
        // HybridSameSection_twoFurtherSmuggleVariants_evadeExistingTokens_butNotTheCandidateOne):
        // appending a second subsection that reproduces the table-end token's own trailing bytes,
        // and appending a whole second revision. Both point a new row at object 7's existing bytes
        // at offset 411, and a classic entry's 10-digit offset field cannot hide that, so
        // "0000000411 00000 n" closes both. It does not close every route: a regeneration that
        // appends a DUPLICATE of object 7's bytes at a fresh offset and points the new row there
        // writes no such token and still defeats the "defined only by the /XRefStm" property this
        // fixture exists to pin (constructed and confirmed in the #206 review). No token in this row
        // catches that one; the SHA does, for a mutation, and nothing does for a deliberate
        // regeneration. Absent from the real fixture today (verified directly).
        ("hybrid-samesection-undefined.pdf",
            "279475690b26798a0b26c2aaa6a59f8fd761852c6762600dc922a9e299bb1755",
            ["xref\n0 7\n",
             "0000000000 00000 f \ntrailer",
             "/XRefStm 472",
             "0000000121 00000 n \n0000000000 00001 f \n0000000341 00000 n",
             "/Index [4 1 7 1]", "SAMESECTIONSTREAM"],
            ["0000000411 00000 n"]),

        // Hand-built, three revisions, two deleted objects. Object 5 lives at generation 0, revision 2
        // deletes it with a free entry recording 1 as the next generation, and revision 3 reuses the
        // number as "5 1 obj". Object 7 lives at generation 0, is deleted the same way in revision 2,
        // and is never redefined — revision 3 says nothing about it, so resolving it null depends on
        // that deletion surviving the merge. Revision 2's free list is linked per ISO 32000-2 §7.5.4:
        // head → 5 → 7 → 0. #196 names this axis and no other fixture reaches it: a reference's
        // generation has to match the xref entry's recorded generation, not the object header — the
        // two nonzero-generation files carry a generation that was never recycled, so neither
        // exercises a generation actually being reused. qpdf agrees on both deleted objects:
        // --show-object=5,1 yields the reused object, and --show-object=5 (generation 0 by default)
        // and --show-object=7 both yield null. The null on object 5 is not proof the free entry was
        // honoured, though — a control with no free entry anywhere (revision 1 defines "5 0 obj",
        // revision 2 defines "5 1 obj" directly) gives qpdf and VellumPdf the same answer, because the
        // merged xref just maps object 5 to generation 1 regardless. qpdf does discriminate on object
        // 7: it resolves object 7 in that same no-free-entry control and returns null here.
        ("freed-object-reuse.pdf",
            "5de56a22432b4f9f9cbb925384ce1d5a2575f9b308061bac94cdaa7291ce649f",
            ["5 1 obj", "0000000000 00001 f", "/Prev "],
            []),

        // The shared qpdf-normalized base every qpdf/poppler-derived fixture below descends from,
        // mirroring Fixtures/Encrypted/plaintext-baseline.pdf. Plain single-revision, classic xref —
        // none of the axes below apply to it yet, which is itself worth pinning: it is the fixed
        // point every derived fixture's diff is measured against.
        ("baseline.pdf",
            "538817fc8fdee2ff06eb374d1a72d95fcd6d3282410b743e836ea67dd8cf973f",
            [],
            ["/ObjStm", "/Linearized", "/XRefStm", "/Prev"]),

        // qpdf --object-streams=generate: compressed objects plus a cross-reference stream, and
        // qpdf drops the classic xref table entirely when it does this, so this single fixture
        // covers both the object-stream axis and the cross-reference-stream axis. "\nxref\n" is
        // LF-only and would miss a CRLF-written "xref" keyword; the SHA-256 above is what actually
        // pins this file today, so a regenerated fixture written with CRLF endings would need this
        // token widened to catch the classic table's absence on its own.
        ("objstm-xrefstream.pdf",
            "8cd6029fe121352ac402dda14423a6f5244f709a568b9fca29a23d87421b4ef2",
            ["/Type /ObjStm"],
            ["\nxref\n"]),

        // qpdf --linearize.
        ("linearized.pdf",
            "404dd83ce175a5060503ed3710cbb19e5d1e97a09225a060812d04faf12137c3",
            ["/Linearized"],
            []),

        // poppler pdfattach produced this incremental update; AppendRevision did not. Verified
        // elsewhere in this file to begin with baseline.pdf's bytes verbatim. Two revisions means
        // two "%%EOF" markers, two "startxref" keywords, and a /Prev chaining the second
        // revision's trailer back to the first.
        ("incremental-update.pdf",
            "fbd36d40bde0739dc85992b0cbfb17f4003761dc8a01b524a888b37e7ca47001",
            ["/Prev"],
            []),

        // Hand-built: the catalog is "1 1 obj", not "1 0 obj" — a reference at a nonzero
        // generation read from a document, rather than one constructed in C#
        // (GenerationNumberTests already covers the latter). This is the base
        // nonzero-generation.pdf below is built from.
        ("nonzero-gen-base.pdf",
            "6f6469059a550fec6b0715c941404f442665f5698345a301812c8637e9ee4b38",
            ["1 1 obj", "/Root 1 1 R"],
            []),

        // poppler pdfattach applied to nonzero-gen-base.pdf: an appended revision on a document
        // whose catalog sits at a nonzero generation — the shape the #121 review found the reader
        // untested against, since every prior appended-revision fixture came from AppendRevision
        // itself on a self-produced generation-0 document. This fixture exercises the READER
        // against a poppler-appended revision; nothing in this PR calls AppendRevision. Poppler
        // rewrote the catalog again in the new revision, still at generation 1, so "1 1 obj" and
        // "/Root 1 1 R" each appear twice.
        ("nonzero-generation.pdf",
            "c1baaa3075278948cb5adfa418903c26031c9ba46c472e6a57b0562fca61be03",
            ["/Root 1 1 R", "/Prev 412"],
            []),

        // Truncated well before the xref table: no xref, no trailer, no startxref, no %%EOF. What
        // an interrupted transfer leaves behind.
        ("truncated-tail.pdf",
            "3407781190bbf04e7f3ea302e3a1334a03ee79c1fec2859f188d4444d81bcf21",
            [],
            ["startxref", "%%EOF"]),

        // baseline.pdf with its startxref value overwritten to point past end-of-file. Same length
        // as baseline.pdf; only those four digits differ.
        ("broken-startxref.pdf",
            "89a0267f5b56c6c27bdb855ee921f5f175b9bece8ade7051a5676c8db8b2a571",
            ["startxref\n9999"],
            []),

        // Hand-built (qpdf recomputes /Length on every write, so it cannot produce this):
        // /Length 64, in range for the file, but landing short of 'endstream'. The asserted body
        // content is 45 bytes; the gap from body start to where 'endstream' actually begins is 46,
        // one more to cover the trailing EOL (qpdf --check agrees: "recovered stream length: 46");
        // ISO 32000-2 §7.3.8.2 says /Length itself should carry the 45-byte reading. PdfObjectParser
        // takes the /Length-preferred branch, finds it doesn't land on 'endstream', and falls back to
        // scanning for the marker. The file has only one 'endstream' after the body start, so this
        // pins that fallback rule, not ScanToEndstream's own preference tiers (#105).
        ("length-mismatch.pdf",
            "1885ccc6dfd85c1ef2f3f941e55ce60a973fbdce9ebdde01e4f11f1cae7cc4eb",
            ["/Length 64", "LENGTHMISMATCH"],
            []),
    ];

    public static TheoryData<string, string, string[], string[]> Fixtures
    {
        get
        {
            var data = new TheoryData<string, string, string[], string[]>();
            foreach (var (name, sha, mustContain, mustNotContain) in Corpus)
                data.Add(name, sha, mustContain, mustNotContain);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_isExactlyTheFileItClaimsToBe(
        string name, string sha256, string[] mustContain, string[] mustNotContain)
    {
        var bytes = Load(name);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var text = Encoding.Latin1.GetString(bytes);
        foreach (var token in mustContain)
            Assert.Contains(token, text, StringComparison.Ordinal);
        foreach (var token in mustNotContain)
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The csproj embeds <c>Fixtures/ThirdParty/*.pdf</c> under folder-qualified logical names, so a
    /// fixture dropped into the folder without a matching row here would otherwise ship untested.
    /// Fail loudly and name what to add, mirroring <see cref="EncryptedFixtureCorpusTests"/>'s guard.
    /// </summary>
    [Fact]
    public void EveryEmbeddedFixture_isCoveredByTheTheory()
    {
        const string Prefix = "ThirdParty/";
        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)
                        && n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[Prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        var covered = Corpus.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(covered.OrderBy(n => n, StringComparer.Ordinal),
            embedded.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// poppler's incremental update begins with the base document's bytes verbatim — that is what
    /// makes it an appended revision rather than a rewrite. Checked byte-for-byte rather than
    /// inferred from /Prev, since a rewritten file could carry a /Prev key without the prefix
    /// property actually holding.
    /// </summary>
    [Fact]
    public void IncrementalUpdate_beginsWithBaseline_verbatim()
    {
        var baseline = Load("baseline.pdf");
        var incremental = Load("incremental-update.pdf");
        Assert.True(incremental.Length > baseline.Length);
        Assert.Equal(baseline, incremental[..baseline.Length]);
    }

    /// <summary>Same prefix property as above, for the nonzero-generation pair.</summary>
    [Fact]
    public void NonzeroGeneration_appendedRevision_beginsWithBase_verbatim()
    {
        var basePdf = Load("nonzero-gen-base.pdf");
        var appended = Load("nonzero-generation.pdf");
        Assert.True(appended.Length > basePdf.Length);
        Assert.Equal(basePdf, appended[..basePdf.Length]);
    }

    /// <summary>
    /// broken-startxref.pdf is baseline.pdf with exactly its startxref offset corrupted — same
    /// length, and identical apart from the four digits the corruption changed.
    /// </summary>
    [Fact]
    public void BrokenStartxref_differsFromBaseline_onlyInTheOffsetDigits()
    {
        var baseline = Load("baseline.pdf");
        var broken = Load("broken-startxref.pdf");
        Assert.Equal(baseline.Length, broken.Length);

        var differing = 0;
        for (var i = 0; i < baseline.Length; i++)
            if (baseline[i] != broken[i])
                differing++;
        Assert.Equal(4, differing);
    }

    /// <summary>
    /// The commit message's "three ways to smuggle a live entry in" undercounts: a reviewer in the
    /// #372 round built two more, neither closed by the header or table-end tokens this corpus
    /// entry already carries for <c>hybrid-samesection-undefined.pdf</c>. Both reuse object 7's
    /// real, already-present byte offset (411) rather than duplicating its dictionary bytes, and
    /// reusing that offset is what a fixed-width xref entry has no way to hide:
    /// "0000000411 00000 n" has to appear verbatim. That is why the token closes these two — not
    /// because reuse is the only route. A third, appending a duplicate of object 7's bytes at a
    /// fresh offset and pointing the new row there, writes no such token and is closed by nothing
    /// in this corpus row (see the comment on that row). Both variants below are built from the real
    /// embedded fixture, not a hand-typed approximation, and both are checked against every existing
    /// MustContain token for this fixture (proving the current guard misses them) before checking
    /// that the candidate MustNotContain token would have caught them.
    ///
    /// Variant D appends a whole new classic-table subsection ("7 2") between the original table's
    /// last row and "trailer" — legal, since a table may have more than one subsection (ISO
    /// 32000-2 §7.5.4) — and gives that subsection's own last row the same bytes
    /// ("0000000000 00000 f ") the table-end token pins, so the substring "...f \ntrailer" still
    /// matches even though what actually precedes "trailer" changed. The header token
    /// ("xref\n0 7\n") is untouched.
    ///
    /// Variant E appends an entire second revision after the original %%EOF, with its own classic
    /// table defining object 7 live at its real, still-present offset and a /Prev chain back to the
    /// original xref. Every byte of the original file is unchanged, so all six existing tokens
    /// still match trivially — <c>Fixture_isExactlyTheFileItClaimsToBe</c> scans the WHOLE file for
    /// each MustContain substring, so appended bytes can only add matches, never remove one.
    /// </summary>
    [Fact]
    public void HybridSameSection_twoFurtherSmuggleVariants_evadeExistingTokens_butNotTheCandidateOne()
    {
        var baseBytes = Load("hybrid-samesection-undefined.pdf");
        var (_, _, existingMustContain, existingMustNotContain) =
            Corpus.Single(f => f.Name == "hybrid-samesection-undefined.pdf");
        const string CandidateToken = "0000000411 00000 n";
        Assert.Contains(CandidateToken, existingMustNotContain); // this test exists to justify that row

        var variantD = BuildVariantD_AppendedSubsectionEndingInTheSamePattern(baseBytes);
        var variantE = BuildVariantE_AppendedSecondRevision(baseBytes);

        foreach (var variant in new[] { variantD, variantE })
        {
            var text = Encoding.Latin1.GetString(variant);

            // Evades every token this corpus entry carried before the #372 round: the check that
            // matters is the real per-fixture theory (Fixture_isExactlyTheFileItClaimsToBe), so this
            // reproduces exactly what it does rather than a hand-rolled approximation of it.
            foreach (var token in existingMustContain)
                Assert.Contains(token, text, StringComparison.Ordinal);

            // ... but is caught by the candidate MustNotContain, now added to the corpus row above.
            // Asserting the assertion FAILS, not just that the substring is present, exercises the
            // exact guard shape Fixture_isExactlyTheFileItClaimsToBe applies to MustNotContain.
            Assert.Throws<Xunit.Sdk.DoesNotContainException>(
                () => Assert.DoesNotContain(CandidateToken, text, StringComparison.Ordinal));
        }
    }

    private static byte[] BuildVariantD_AppendedSubsectionEndingInTheSamePattern(byte[] baseBytes)
    {
        var text = Encoding.Latin1.GetString(baseBytes);
        var trailerIdx = text.IndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailerIdx > 0);

        // Object 7's real bytes ("7 0 obj\n...") sit at offset 411 in the base file already; this
        // subsection just gives the classic table its own, otherwise-absent, live row pointing at
        // them. The second row (a fresh dummy free entry for object 8) exists only to reproduce the
        // table-end token's exact trailing bytes.
        const string InsertedSubsection = "7 2\n0000000411 00000 n \n0000000000 00000 f \n";
        var mutated = text[..trailerIdx] + InsertedSubsection + text[trailerIdx..];
        // /Size must cover the new object 8 for the file to stay internally consistent.
        mutated = mutated.Replace("/Size 8 /Root 1 0 R /XRefStm 472", "/Size 9 /Root 1 0 R /XRefStm 472", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(mutated);
    }

    private static byte[] BuildVariantE_AppendedSecondRevision(byte[] baseBytes)
    {
        var ms = new MemoryStream();
        ms.Write(baseBytes);
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        // A second revision whose only content is a classic table resurrecting object 7 live, at
        // its real, already-present offset (411) — no new object bytes are needed, only a new xref
        // row pointing at the old ones.
        var xrefOffset = (int)ms.Position;
        W("xref\n7 1\n0000000411 00000 n \ntrailer\n<< /Size 9 /Root 1 0 R /Prev 596 >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
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
