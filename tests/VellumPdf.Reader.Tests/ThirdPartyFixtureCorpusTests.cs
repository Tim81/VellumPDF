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
/// fixture. A byte-content check alone cannot distinguish, say, a file that merely mentions
/// "/Linearized" in a comment from one a linearizer actually produced.
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
        // defined live in the SECOND revision's /XRefStm -- exactly the "hidden object" convention
        // ISO 32000-2 §7.5.8.4 describes, and the one the #121 review found mishandled. qpdf and
        // poppler both resolve object 3 the same way VellumPdf does; see README for the three-way
        // comparison this fixture's assertions rest on.
        ("hybrid-spec-convention.pdf",
            "6fe78b1957e53120953ae315cac31686b6a825c20cc478a8c1ea7f4ce7beb84d",
            ["/Prev 402", "/XRefStm 822", "/Type /ObjStm"],
            []),

        // Hand-built variant of the fixture above: the free entry and the /XRefStm definition sit
        // in the SAME revision instead of a /Prev-linked previous one. ISO 32000-2 §7.5.8.4 does not
        // describe this shape -- ambiguous per pdf-association/pdf-issues#146, unlike the two-
        // revision case above. qpdf resolves object 4 to null here; poppler discards the xref and
        // reconstructs. Neither is a conformance verdict, so this pins VellumPdf's own current
        // behaviour only -- see README before trusting it as a spec statement.
        ("hybrid-samesection-undefined.pdf",
            "28d80b1f9e1fa8a9e473368eb9017639a5569eea044e9b6fa94922fc10b01939",
            ["/XRefStm 411", "0000000000 00001 f"],
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
        // covers both the object-stream axis and the cross-reference-stream axis.
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

        // Hand-built: the catalog is "1 1 obj", not "1 0 obj" -- a reference at a nonzero
        // generation read from a document, rather than one constructed in C#
        // (GenerationNumberTests already covers the latter). This is the base
        // nonzero-generation.pdf below is built from.
        ("nonzero-gen-base.pdf",
            "6f6469059a550fec6b0715c941404f442665f5698345a301812c8637e9ee4b38",
            ["1 1 obj", "/Root 1 1 R"],
            []),

        // poppler pdfattach applied to nonzero-gen-base.pdf: an appended revision on a document
        // whose catalog sits at a nonzero generation, the exact shape the #121 review found
        // untested because "the only AppendRevision coverage runs on self-produced generation-0
        // documents". Poppler rewrote the catalog again in the new revision, still at generation 1,
        // so "1 1 obj" and "/Root 1 1 R" each appear twice.
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
        // /Length 64 while the true body -- ending where 'endstream' actually starts -- is 41
        // bytes. In range, so PdfObjectParser takes the /Length-preferred branch, finds it doesn't
        // land on 'endstream', and falls back to ScanToEndstream (#105).
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
    /// poppler's incremental update begins with the base document's bytes verbatim -- that is what
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
    /// broken-startxref.pdf is baseline.pdf with exactly its startxref offset corrupted -- same
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
