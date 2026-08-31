// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Round-trips every one of the 17 fixtures in Fixtures/Encrypted/ through
/// <see cref="PdfDocumentReader.SaveDecrypted(Stream)"/> (#186): open encrypted, save decrypted,
/// reopen with no password at all, and confirm the object graph survived. The per-fixture password
/// table mirrors <c>Fixtures/Encrypted/README.md</c>'s matrix.
/// </summary>
public sealed class SaveDecryptedFixtureRoundTripTests
{
    // file -> password. Matches the README's matrix: fifteen fixtures share "u"/"o", two take an
    // empty user password, and three (long/same/non-ASCII) have their own.
    // Internal rather than private: SaveDecryptedQpdfOracleTests reuses this exact matrix rather
    // than maintaining its own copy that could silently drift from the fixture list here.
    internal static readonly (string Name, string? Password)[] AllFixtures =
    [
        ("enc-rc4-40.pdf", "u"),
        ("enc-rc4-128.pdf", "u"),
        ("enc-rc4-128-v4.pdf", "u"),
        ("enc-aes-128.pdf", "u"),
        ("enc-aes-256-r5.pdf", "u"),
        ("enc-aes-256-r6.pdf", "u"),
        ("enc-aes-128-cleartextmd.pdf", "u"),
        ("enc-256-cleartextmd.pdf", "u"),
        ("enc-rc4-objstm.pdf", "u"),
        ("enc-aes-128-emptyuser.pdf", null),
        ("enc-aes-128-nestedstrings.pdf", "u"),
        ("enc-aes-128-longpassword.pdf", "0123456789abcdefghijklmnopqrstuvwxyzABCD"),
        ("enc-aes-128-samepassword.pdf", "same"),
        ("enc-aes-128-pdfdocpassword.pdf", "pässwörd"),
        ("enc-aes-128-tworevisions.pdf", null),
        ("enc-aes-128-linearized.pdf", "u"),
        ("enc-256-linearized-objstm-cleartextmd.pdf", "u"),
    ];

    // Rows whose decrypted content is directly comparable to plaintext-baseline.pdf's object graph
    // byte-for-byte (see the README's "What the tests should assert" section) — everything except
    // the two rows the README calls out as structurally different: nestedstrings inserts an extra
    // object and renumbers, and tworevisions carries a whole extra appended revision.
    private static readonly HashSet<string> BaselineComparableFixtures =
    [
        "enc-rc4-40.pdf", "enc-rc4-128.pdf", "enc-rc4-128-v4.pdf", "enc-aes-128.pdf",
        "enc-aes-256-r5.pdf", "enc-aes-256-r6.pdf", "enc-aes-128-cleartextmd.pdf",
        "enc-256-cleartextmd.pdf", "enc-rc4-objstm.pdf", "enc-aes-128-emptyuser.pdf",
        "enc-aes-128-longpassword.pdf", "enc-aes-128-samepassword.pdf",
        "enc-aes-128-pdfdocpassword.pdf", "enc-aes-128-linearized.pdf",
        "enc-256-linearized-objstm-cleartextmd.pdf",
    ];

    public static TheoryData<string, string?> Fixtures
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var (name, password) in AllFixtures)
                data.Add(name, password);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_reopensWithNoPassword_andEncryptionIsNull(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Null(reopened.Encryption);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_reopenedGraph_matchesTheSourceDocumentsOwnGraph(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        SaveDecryptedGraphComparer.AssertCatalogsEqual(reader, reopened);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_objectNumberSet_matchesComputeEmitSet(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);
        var expectedSet = reader.ComputeEmitSet();

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        var actualSet = new HashSet<int>(reopened.ObjectNumbers);

        Assert.True(
            expectedSet.SetEquals(actualSet),
            $"{fixtureName}: object-number set mismatch. "
            + $"expected-only=[{string.Join(",", expectedSet.Except(actualSet).OrderBy(n => n))}] "
            + $"actual-only=[{string.Join(",", actualSet.Except(expectedSet).OrderBy(n => n))}]");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_generations_matchTheInputsAuthoritativeGeneration(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);
        var emitSet = reader.ComputeEmitSet();

        var expectedGenerations = new Dictionary<int, int>();
        foreach (var n in emitSet)
            expectedGenerations[n] = reader.GenerationOf(n);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        var outputBytes = ms.ToArray();

        var actualGenerations = ParseClassicXrefGenerations(outputBytes);

        foreach (var (objectNumber, expectedGeneration) in expectedGenerations)
        {
            Assert.True(
                actualGenerations.TryGetValue(objectNumber, out var actualGeneration),
                $"{fixtureName}: object {objectNumber} missing from the output's cross-reference table.");
            Assert.True(
                expectedGeneration == actualGeneration,
                $"{fixtureName}: object {objectNumber} generation mismatch "
                + $"(expected {expectedGeneration}, got {actualGeneration}).");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_output_containsNoEncryptionTokens(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        var bytes = ms.ToArray();

        AssertNoTokenBoundaryMatch(bytes, "/OE");
        AssertNoTokenBoundaryMatch(bytes, "/UE");
        AssertNoTokenBoundaryMatch(bytes, "/Standard");
        AssertNoTokenBoundaryMatch(bytes, "/Encrypt");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_output_containsNoObjStmXRefOrLinearizedDicts(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        var bytes = ms.ToArray();

        AssertNoTokenBoundaryMatch(bytes, "/ObjStm");
        AssertNoTokenBoundaryMatch(bytes, "/XRef");
        AssertNoTokenBoundaryMatch(bytes, "/Linearized");
    }

    [Fact]
    public void SaveDecrypted_nestedStrings_valuesSurviveByContent()
    {
        using var reader = OpenFixture("enc-aes-128-nestedstrings.pdf", "u");

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Null(reopened.Encryption);

        var custom = Assert.IsType<PdfDictionary>(
            reopened.ResolveValue(reopened.Catalog.Get(new PdfName("CustomTestData"))!));
        var outer = Assert.IsType<PdfDictionary>(reopened.ResolveValue(custom.Get(new PdfName("Outer"))!));
        var strs = Assert.IsType<PdfArray>(reopened.ResolveValue(outer.Get(new PdfName("Strs"))!));

        Assert.Equal(2, strs.Count);
        Assert.Equal("DirectArrayString", DecodeAscii(strs[0]));
        Assert.Equal("SecondArrayString", DecodeAscii(strs[1]));
    }

    [Fact]
    public void SaveDecrypted_twoRevisions_collapsesToOneRevision_andKeepsTheAttachment()
    {
        using var reader = OpenFixture("enc-aes-128-tworevisions.pdf", null);
        Assert.True(reader.Revisions.Count >= 2, "expected the fixture itself to carry at least two revisions");

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Null(reopened.Encryption);
        Assert.Single(reopened.Revisions);

        // The attachment content survives even though the object graph was fully rebuilt — compared
        // structurally against the source reader's own (decrypted) graph, not a separate baseline
        // file, since this fixture's content is baseline-plus-attachment rather than the baseline.
        SaveDecryptedGraphComparer.AssertCatalogsEqual(reader, reopened);
    }

    [Theory]
    [MemberData(nameof(BaselineFixtures))]
    public void SaveDecrypted_reopenedGraph_matchesThePlaintextBaseline(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);
        using var baseline = PdfReader.Open(Load("plaintext-baseline.pdf"));

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Null(reopened.Encryption);
        SaveDecryptedGraphComparer.AssertCatalogsEqual(baseline, reopened);
    }

    public static TheoryData<string, string?> BaselineFixtures
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var (name, password) in AllFixtures)
                if (BaselineComparableFixtures.Contains(name))
                    data.Add(name, password);
            return data;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PdfDocumentReader OpenFixture(string name, string? password) =>
        PdfReader.Open(Load(name), new PdfReaderOptions { Password = password });

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string DecodeAscii(PdfObject obj)
    {
        var bytes = obj switch
        {
            PdfLiteralString s => s.Bytes,
            PdfHexString h => h.Bytes,
            _ => throw new InvalidOperationException($"Not a string: {obj.GetType().Name}"),
        };
        return Encoding.ASCII.GetString(bytes.Span);
    }

    private static void AssertNoTokenBoundaryMatch(byte[] bytes, string token)
    {
        var needle = Encoding.ASCII.GetBytes(token);
        var span = bytes.AsSpan();
        var searchStart = 0;
        while (true)
        {
            var idx = span[searchStart..].IndexOf(needle);
            if (idx < 0)
                return;

            var absolute = searchStart + idx;
            var precededByDelimiter = absolute == 0 || IsPdfDelimiterOrWhitespace(span[absolute - 1]);
            var afterIndex = absolute + needle.Length;
            var followedByDelimiter = afterIndex >= span.Length || IsPdfDelimiterOrWhitespace(span[afterIndex]);

            Assert.False(
                precededByDelimiter && followedByDelimiter,
                $"Output contains the token '{token}' at a name/keyword boundary (offset {absolute}).");

            searchStart = absolute + 1;
        }
    }

    // PDF whitespace (ISO 32000-2 Table 1) + delimiters (Table 2), matching what makes '/OE' distinct
    // from the interior of '/Outlines'.
    private static bool IsPdfDelimiterOrWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32
        or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
        or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>
    /// Parses every classic xref subsection in <paramref name="bytes"/> into (objectNumber →
    /// generation) for in-use ("n") rows — enough for the generation-preservation assertion without
    /// pulling in the full parser (which resolves through THIS reader's own machinery and would make
    /// the test partly self-referential).
    /// </summary>
    private static Dictionary<int, int> ParseClassicXrefGenerations(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var xrefKeyword = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        Assert.True(xrefKeyword >= 0, "output has no classic xref table");

        var trailerIdx = text.IndexOf("\ntrailer", xrefKeyword, StringComparison.Ordinal);
        Assert.True(trailerIdx > xrefKeyword, "output's xref table has no following trailer");

        var body = text[(xrefKeyword + 6)..trailerIdx];
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var result = new Dictionary<int, int>();
        var i = 0;
        while (i < lines.Length)
        {
            var header = lines[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            i++;
            if (header.Length != 2) continue;
            var first = int.Parse(header[0]);
            var count = int.Parse(header[1]);
            for (var j = 0; j < count; j++, i++)
            {
                var row = lines[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (row[2] != "n") continue;
                var objectNumber = first + j;
                if (objectNumber == 0) continue; // the mandatory free-list head
                result[objectNumber] = int.Parse(row[1]);
            }
        }

        return result;
    }
}
