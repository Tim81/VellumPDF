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

    /// <summary>
    /// Every one of the 17 committed fixtures is generation 0 (see the fixture README's "Known
    /// gaps": qpdf normalises generations to 0 on write), which means a serialiser that dropped
    /// <c>GenerationOf</c>'s result entirely and just hard-coded 0 for every object would pass every
    /// row of the theory above just as well as the real implementation (review round 2, defect 3).
    /// <c>Fixtures/ThirdParty/nonzero-generation.pdf</c> is the one embedded fixture that actually
    /// carries a nonzero generation (its catalog resolves at generation 1 — see
    /// <c>ThirdPartyReaderBehaviorTests</c>), and it is unencrypted, so this exercises the "degenerate
    /// to a normalised rewrite" path rather than decryption.
    /// </summary>
    [Fact]
    public void SaveDecrypted_thirdPartyNonZeroGenerationFixture_preservesTheGeneration()
    {
        var bytes = LoadThirdParty("nonzero-generation.pdf");
        using var reader = PdfReader.Open(bytes);

        var catalogGeneration = reader.GenerationOf(1);
        Assert.True(
            catalogGeneration > 0,
            "expected nonzero-generation.pdf's catalog (object 1) to carry a nonzero generation — "
            + "if this fails, the fixture itself has been flattened and no longer proves anything.");

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        var outputBytes = ms.ToArray();

        var actualGenerations = ParseClassicXrefGenerations(outputBytes);
        Assert.True(actualGenerations.TryGetValue(1, out var actualGeneration), "object 1 missing from output xref");
        Assert.Equal(catalogGeneration, actualGeneration);
    }

    /// <summary>
    /// <c>qpdf --check</c> has no opinion on whether a stream's declared <c>/Length</c> matches its
    /// actual body — and for the four RC4 fixtures (which do not shrink the body the way AES padding
    /// can) skipping the <c>/Length</c> rewrite entirely still passed every in-process test AND qpdf
    /// (review round 2, defect 5), since the stale ciphertext-length value happened to already equal
    /// the plaintext length. Checked directly here instead: every stream's declared <c>/Length</c>
    /// against its own actual body size, independent of qpdf.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_everyStreamsLength_equalsItsActualBodyLength(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());

        var checkedStreamCount = 0;
        foreach (var objectNumber in reopened.ObjectNumbers)
        {
            var stream = reopened.ResolveStream(objectNumber);
            if (stream is null)
                continue;

            checkedStreamCount++;
            var declaredLength = Assert.IsType<PdfInteger>(stream.Dictionary.Get(PdfName.Length)).Value;
            Assert.True(
                declaredLength == stream.RawBody.Length,
                $"{fixtureName}: object {objectNumber}'s /Length ({declaredLength}) does not match "
                + $"its actual body length ({stream.RawBody.Length}).");
        }

        Assert.True(checkedStreamCount > 0, $"{fixtureName}: no streams found in the output — hollow test.");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_output_containsNoEncryptionTokens(string fixtureName, string? password)
    {
        using var reader = OpenFixture(fixtureName, password);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        var bytes = ms.ToArray();

        AssertNoTokenBoundaryMatch(bytes, "/O");
        AssertNoTokenBoundaryMatch(bytes, "/U");
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

        // Named explicitly (review round 2, low #14), not just implied by the graph comparison
        // passing: poppler's pdfattach named the attachment "attach.txt" carrying "attachment
        // payload" (see the fixture README's exact build command).
        Assert.Equal("attach.txt", ResolveEmbeddedFileName(reopened));
        Assert.Contains("attachment payload", ResolveEmbeddedFileText(reopened), StringComparison.Ordinal);

        // Superseded-object case: pdfattach's second revision redefines object 1 (the catalog)
        // itself, adding /Names — the base revision's own catalog has no such key — so the output
        // must reflect the NEWER definition. The generation-preservation theory above already checks
        // this object number resolves at all; this pins WHICH content won for it specifically.
        Assert.NotNull(reopened.Catalog.Get(new PdfName("Names")));
    }

    private static string ResolveEmbeddedFileName(PdfDocumentReader reader)
    {
        var (_, filespec) = ResolveFirstEmbeddedFile(reader);
        // pdfattach writes /UF only (no /F): a PDF text string, which ISO 32000-2 §7.9.2.2 encodes
        // as UTF-16BE with a leading U+FEFF byte-order mark when it isn't PDFDocEncoding.
        var nameRaw = filespec.Get(new PdfName("F")) ?? filespec.Get(new PdfName("UF"));
        var nameObj = reader.ResolveValue(nameRaw!);
        var bytes = nameObj switch
        {
            PdfLiteralString s => s.Bytes,
            PdfHexString h => h.Bytes,
            _ => throw new InvalidOperationException($"Not a string: {nameObj?.GetType().Name}"),
        };
        var span = bytes.Span;
        return span.Length >= 2 && span[0] == 0xFE && span[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(span[2..])
            : Encoding.ASCII.GetString(span);
    }

    private static string ResolveEmbeddedFileText(PdfDocumentReader reader)
    {
        var (embeddedFileStreamRef, filespec) = ResolveFirstEmbeddedFile(reader);
        _ = filespec;
        var stream = reader.ResolveStream(embeddedFileStreamRef) ?? throw new InvalidOperationException("expected a stream");
        var decoded = reader.GetDecodedStreamData(stream) ?? reader.DecryptedStreamView(stream).RawBody.ToArray();
        return Encoding.Latin1.GetString(decoded);
    }

    // [name-string, filespec-ref, ...] pairs (ISO 32000-2 §7.9.6); the filespec is odd-indexed. The
    // embedded-file STREAM is a level below the filespec, at /EF /F.
    private static (PdfIndirectReference StreamRef, PdfDictionary Filespec) ResolveFirstEmbeddedFile(PdfDocumentReader reader)
    {
        var namesRoot = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(new PdfName("Names"))!));
        var embeddedFiles = Assert.IsType<PdfDictionary>(reader.ResolveValue(namesRoot.Get(new PdfName("EmbeddedFiles"))!));
        var namesArr = Assert.IsType<PdfArray>(reader.ResolveValue(embeddedFiles.Get(new PdfName("Names"))!));
        var filespec = Assert.IsType<PdfDictionary>(reader.ResolveValue(namesArr[1]));
        var ef = Assert.IsType<PdfDictionary>(reader.ResolveValue(filespec.Get(new PdfName("EF"))!));
        var streamRef = Assert.IsType<PdfIndirectReference>(ef.Get(new PdfName("F")));
        return (streamRef, filespec);
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
        // compareTrailer: false — baseline is an independent file with its own random /ID, not the
        // fixture's own output, so /ID legitimately differs even though the reachable content matches.
        SaveDecryptedGraphComparer.AssertCatalogsEqual(baseline, reopened, compareTrailer: false);
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

    private static byte[] LoadThirdParty(string name)
    {
        const string Prefix = "ThirdParty/";
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name)
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
    // Internal rather than private: SaveDecryptedTests reuses this exact parser for its own
    // nonzero-generation assertion (#186 review round 2, defect 3) rather than keeping a second copy.
    internal static Dictionary<int, int> ParseClassicXrefGenerations(byte[] bytes)
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
                // A diagnostic assert, not a bare index: a classic xref row is always exactly three
                // fields (offset, generation, n/f). If the writer ever emitted something else, an
                // unguarded row[2] would throw a bare IndexOutOfRangeException naming neither the
                // fixture nor the offending line, forcing a re-run under a debugger to find out why.
                Assert.True(row.Length == 3, $"malformed xref row {i} (expected 3 fields): \"{lines[i]}\"");
                if (row[2] != "n") continue;
                var objectNumber = first + j;
                if (objectNumber == 0) continue; // the mandatory free-list head
                result[objectNumber] = int.Parse(row[1]);
            }
        }

        return result;
    }
}
