// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.TestSupport;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// The other half of #99's CI round-trip oracle: <see cref="SaveDecryptedQpdfOracleTests"/> covers
/// the encrypted corpus; this covers the unencrypted third-party one (#196), which
/// <see cref="PdfDocumentReader.SaveDecrypted(Stream)"/> also accepts — its own doc comment calls
/// unencrypted input "accepted... the output degenerates to a normalised single-revision rewrite".
/// That makes every hybrid-reference, freed-and-reused, nonzero-generation, and reconstructed shape
/// this corpus exists to pin a candidate for the same two checks #186 already runs against the
/// encrypted set: does the serializer's OUTPUT parse cleanly under an independent tool
/// (<c>qpdf --check</c>), and does reopening it reproduce the source document's own reachable
/// object graph. Neither check cares what the SOURCE file's dialect was — that is exactly the
/// point of routing this exotic corpus through the same serializer the encrypted corpus already
/// exercises, rather than adding a second, third-party-specific serializer path.
///
/// <c>truncated-tail.pdf</c> and <c>broken-startxref.pdf</c> need
/// <see cref="PdfReaderOptions.AllowReconstruction"/> to open at all; every other fixture opens
/// under default options. <see cref="PdfDocumentReader.SaveDecrypted(Stream)"/>'s own doc comment
/// says a reconstructed document is allowed here, unlike <c>AppendRevision</c>, which refuses one
/// outright — Annex C.4's scan-and-rebuild does not depend on the base file's own byte layout the
/// way an incremental update does.
/// </summary>
public sealed class SaveDecryptedThirdPartyQpdfOracleTests : IDisposable
{
    private readonly string _tempDir;

    public SaveDecryptedThirdPartyQpdfOracleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellum_savedecrypted_thirdparty_qpdf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // file -> AllowReconstruction. Every embedded ThirdParty fixture except attach-payload.txt
    // (not itself embedded; see Fixtures/ThirdParty/README.md) and ExcludedFromRoundTrip below gets
    // a row, so a fixture added to that corpus without either a row here or a documented exclusion
    // would silently skip this half of #99's round-trip oracle.
    private static readonly (string Name, bool AllowReconstruction)[] AllFixtures =
    [
        ("baseline.pdf", false),
        ("hybrid-spec-convention.pdf", false),
        ("hybrid-samesection-undefined.pdf", false),
        ("freed-object-reuse.pdf", false),
        ("objstm-xrefstream.pdf", false),
        ("linearized.pdf", false),
        ("incremental-update.pdf", false),
        ("nonzero-gen-base.pdf", false),
        ("nonzero-generation.pdf", false),
        ("length-mismatch.pdf", false),
        ("broken-startxref.pdf", true),
    ];

    // truncated-tail.pdf is baseline.pdf's own first 1200 bytes (Fixtures/ThirdParty/README.md):
    // enough for AllowReconstruction's best-effort scan to rebuild A cross-reference table, but not
    // enough for every object that table declares to survive intact. SaveDecrypted force-resolves
    // its whole emit set — unlike ordinary lazy resolution, which would just leave the gap unread —
    // so it is the one operation here that notices, and correctly refuses rather than silently
    // emitting a decrypted copy missing content. See
    // SaveDecrypted_severelyTruncatedFixture_refusesWithInvalidDataException below, which is the one
    // place asserting that refusal; excluded from the generic round-trip theory rather than forcing
    // a workaround into it.
    private static readonly HashSet<string> ExcludedFromRoundTrip = ["truncated-tail.pdf"];

    public static TheoryData<string, bool> Fixtures
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var (name, allowReconstruction) in AllFixtures)
                data.Add(name, allowReconstruction);
            return data;
        }
    }

    /// <summary>
    /// Every embedded <c>*.pdf</c> under <c>ThirdParty/</c> has a matching row above — the same
    /// coverage guard <see cref="ThirdPartyFixtureCorpusTests.EveryEmbeddedFixture_isCoveredByTheTheory"/>
    /// applies to the corpus itself, applied here so a fixture added to that corpus cannot silently
    /// skip this oracle.
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
        var covered = AllFixtures.Select(f => f.Name).Concat(ExcludedFromRoundTrip).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            covered.OrderBy(n => n, StringComparer.Ordinal),
            embedded.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>See <see cref="ExcludedFromRoundTrip"/> for why this fixture sits outside the theory.</summary>
    [Fact]
    public void SaveDecrypted_severelyTruncatedFixture_refusesWithInvalidDataException()
    {
        using var reader = Open("truncated-tail.pdf", allowReconstruction: true);

        var ex = Assert.Throws<InvalidDataException>(() => reader.SaveDecrypted(Stream.Null));
        Assert.Contains("object 6 could not be resolved", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_output_passesQpdfCheck(string fixtureName, bool allowReconstruction)
    {
        using var reader = Open(fixtureName, allowReconstruction);

        var path = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(fixtureName) + "-decrypted.pdf");
        using (var fs = File.Create(path))
            reader.SaveDecrypted(fs);

        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);

        Assert.False(timedOut, "qpdf --check timed out, or its output could not be fully captured.");
        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on {fixtureName}'s decrypted output.\n"
            + $"stdout: {stdout}\nstderr: {stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout);
    }

    /// <summary>
    /// The in-process half of the round trip: reopening the output must reproduce the source
    /// reader's own reachable object graph, using the same comparer
    /// <see cref="SaveDecryptedFixtureRoundTripTests"/> runs against the encrypted corpus.
    /// <c>compareTrailer: false</c> throughout — every fixture here keeps its own <c>/ID</c>
    /// (unlike the encrypted corpus's plaintext-baseline comparisons, none of these are being
    /// checked against an unrelated file), but several of them collapse multiple revisions or
    /// dissolve hybrid-reference structure into one classic table, which legitimately changes the
    /// trailer without touching the catalog's own reachable content. <c>minimumComparedLeafCount:
    /// 8</c> — these fixtures are minimal structural probes rather than content-rich documents and
    /// measure 11-17 leaves each; see <see cref="SaveDecryptedGraphComparer.AssertCatalogsEqual"/>'s
    /// own doc comment for why that floor is lower here than the encrypted corpus's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_reopenedGraph_matchesTheSourceDocumentsOwnGraph(string fixtureName, bool allowReconstruction)
    {
        using var reader = Open(fixtureName, allowReconstruction);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        SaveDecryptedGraphComparer.AssertCatalogsEqual(
            reader, reopened, compareTrailer: false, minimumComparedLeafCount: 8);
    }

    private static PdfDocumentReader Open(string name, bool allowReconstruction) =>
        PdfReader.Open(Load(name), new PdfReaderOptions { AllowReconstruction = allowReconstruction });

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
