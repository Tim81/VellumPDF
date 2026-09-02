// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using System.Text;
using CsCheck;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// CsCheck-driven fuzzing of the reader's parsing layer (#99): byte-level mutations of the
/// committed <c>Fixtures/Encrypted</c>, <c>Fixtures/ThirdParty</c>, and <c>Fixtures/Fuzz</c>
/// corpora, thrown at <see cref="PdfLexer.NextToken"/>, <see cref="PdfObjectParser.ParseObject"/>,
/// and <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/> under both
/// <see cref="PdfReaderOptions.AllowReconstruction"/> settings.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a robustness oracle, not a conformance oracle</strong> — see
/// <c>Fixtures/Fuzz/README.md</c> for the distinction ISO 32000-2 forces. It asserts one thing
/// only: whatever the reader does with a hostile byte array, it never does so via a crash-class
/// exception (<see cref="IndexOutOfRangeException"/>, <see cref="NullReferenceException"/>,
/// <see cref="OverflowException"/>, or anything else outside <see cref="IsDeclaredVocabulary"/>).
/// Parsing a mutated input successfully, or throwing one of the three declared types, are both
/// acceptable outcomes here; ISO 32000-2 §7.3.10 requires an undefined indirect reference to
/// "not be considered an error" at all, and §7.3.9 makes a null dictionary entry "equivalent to
/// omitting the entry entirely", so degrading to null is sometimes the ONLY conforming outcome —
/// whether a specific input should recover or should error is what the value-level corpus
/// known-answer tests pin, one shape at a time, not this harness.
/// </para>
/// <para>
/// <strong>Out-of-memory is a bound, not a ban.</strong> Annex I.2 lists an out-of-memory
/// condition among the errors a processor "should nevertheless... always" report, so hitting one
/// on a truly unbounded input would be conforming. What makes an <see cref="OutOfMemoryException"/>
/// a finding here instead is <see cref="ReaderLimits"/> (#376): every <see cref="PdfReader.Open"/>
/// call below runs with <see cref="ReaderLimits.MinMaxDecodedBytes"/> in force, so an input that
/// would exceed that ceiling has a documented exit — <see cref="InvalidDataException"/>, the shape
/// #208 and #215 both fixed — and an escape past it means the ceiling has a hole, not that the
/// input was merely too big.
/// </para>
/// <para>
/// <strong>Determinism.</strong> A failing case's exception message carries CsCheck's own printed
/// seed. Setting the <c>CsCheck_Seed</c> environment variable to that value and re-running forces
/// the same failing input, without needing to capture or hand-carry the bytes themselves.
/// </para>
/// <para>
/// <strong>Budget.</strong> <see cref="FuzzBudget.Iterations"/> defaults to a few thousand — fast
/// enough to run in every PR — and widens when <c>VELLUMPDF_FUZZ_ITER</c> is set, which
/// <c>.github/workflows/fuzz-nightly.yml</c> does for a much larger scheduled run.
/// </para>
/// </remarks>
public sealed class ParserFuzzTests
{
    // A correct lexer can never emit more tokens than there are bytes to consume one from — every
    // token kind advances Position by at least one byte (SkipWhitespaceAndComments notwithstanding,
    // since it runs between tokens, not instead of one). Exceeding the input length by even a
    // little means some token was returned without the cursor moving: a stuck-cursor infinite loop,
    // not merely a dense token stream on a legitimately busy input.
    private const int TokenCountSlack = 16;

    private static readonly TimeSpan LexerWallClockCeiling = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ParserWallClockCeiling = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReaderWallClockCeiling = TimeSpan.FromSeconds(4);

    // Bounds how far a single mutation chain can grow a seed. 256 KiB is generous against every
    // committed fixture (all comfortably under that today) while keeping a worst-case iteration's
    // own cost bounded independent of the wall-clock ceilings above.
    private const int MaxMutatedLength = 262_144;

    private static readonly IReadOnlyList<byte[]> SeedCorpus = BuildSeedCorpus();

    private static readonly Gen<MutationOp> MutationOpGen =
        Gen.Select(
            Gen.Int[0, 5],
            Gen.Int[0, int.MaxValue],
            Gen.Byte,
            Gen.Int[1, 64],
            (kind, position, value, length) => new MutationOp(kind, position, value, length));

    private static readonly Gen<byte[]> FuzzInputGen =
        Gen.Select(
            Gen.Int[0, SeedCorpus.Count - 1],
            MutationOpGen.Array[1, 8],
            (seedIndex, ops) => Mutate(SeedCorpus[seedIndex], ops));

    /// <summary>
    /// Guards the harness itself: a broken embedded-resource glob or a renamed fixture folder would
    /// silently shrink this to just the one synthetic seed rather than failing loudly.
    /// </summary>
    [Fact]
    public void SeedCorpus_loadsEveryFixtureFolder()
    {
        Assert.True(
            SeedCorpus.Count >= 25,
            $"only {SeedCorpus.Count} fuzz seed(s) loaded — expected the Encrypted (18) and "
            + "ThirdParty (12) corpora plus at least the synthetic wide-dictionary seed. Check the "
            + "embedded-resource globs in VellumPdf.Reader.Tests.csproj.");
    }

    [Fact]
    public void Lexer_neverThrowsOutsideTheDeclaredVocabulary()
        => FuzzInputGen.Sample(AssertLexerIsRobust, iter: FuzzBudget.Iterations);

    [Fact]
    public void ObjectParser_neverThrowsOutsideTheDeclaredVocabulary()
        => FuzzInputGen.Sample(AssertParserIsRobust, iter: FuzzBudget.Iterations);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reader_neverThrowsOutsideTheDeclaredVocabulary(bool allowReconstruction)
        => FuzzInputGen.Sample(bytes => AssertReaderIsRobust(bytes, allowReconstruction), iter: FuzzBudget.Iterations);

    private static void AssertLexerIsRobust(byte[] bytes)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var lexer = new PdfLexer(bytes);
            var tokenCount = 0;
            while (lexer.NextToken().Kind != TokenKind.EndOfInput)
            {
                tokenCount++;
                Assert.True(
                    tokenCount <= bytes.Length + TokenCountSlack,
                    $"lexer emitted {tokenCount} tokens from a {bytes.Length}-byte input without "
                    + "reaching end of input — the cursor is not advancing.");
            }
        }
        catch (Exception ex) when (IsDeclaredVocabulary(ex))
        {
            // Acceptable outcome — see the class doc.
        }

        AssertWithinCeiling(stopwatch, LexerWallClockCeiling, bytes, "lexer");
    }

    private static void AssertParserIsRobust(byte[] bytes)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = new PdfObjectParser(bytes).ParseObject();
        }
        catch (Exception ex) when (IsDeclaredVocabulary(ex))
        {
            // Acceptable outcome — see the class doc.
        }

        AssertWithinCeiling(stopwatch, ParserWallClockCeiling, bytes, "object parser");
    }

    private static void AssertReaderIsRobust(byte[] bytes, bool allowReconstruction)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var options = new PdfReaderOptions
            {
                AllowReconstruction = allowReconstruction,
                // Bounds the OOM half of the oracle — see the class doc's "Out-of-memory is a
                // bound, not a ban" paragraph.
                MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes,
            };
            using var reader = PdfReader.Open(bytes, options);

            // Open() alone does not reach every lazily-resolved path — several #196-era defects
            // lived specifically in resolution, not in opening the file — so touch every object
            // number the xref claims to know about.
            foreach (var objectNumber in reader.ObjectNumbers)
                reader.Resolve(objectNumber);

            // The page-tree walk (#98) is lazy too, and reads through the same mutated object
            // graph — a hostile /Kids array or inherited attribute chain must degrade the same way
            // everything else in this harness does, not throw outside the declared vocabulary.
            _ = reader.PageCount;
            foreach (var page in reader.Pages)
            {
                _ = page.MediaBox;
                _ = page.CropBox;
                _ = page.Rotate;
            }
        }
        catch (Exception ex) when (IsDeclaredVocabulary(ex))
        {
            // Acceptable outcome — see the class doc.
        }

        AssertWithinCeiling(stopwatch, ReaderWallClockCeiling, bytes, $"reader (AllowReconstruction={allowReconstruction})");
    }

    private static void AssertWithinCeiling(Stopwatch stopwatch, TimeSpan ceiling, byte[] bytes, string component)
    {
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed <= ceiling,
            $"{component} took {stopwatch.Elapsed} on a {bytes.Length}-byte input, past the {ceiling} ceiling.");
    }

    private static bool IsDeclaredVocabulary(Exception ex) =>
        ex is InvalidDataException or UnsupportedPdfFeatureException or PdfPasswordException;

    // ── Mutation ─────────────────────────────────────────────────────────────

    private readonly record struct MutationOp(int Kind, int Position, byte Value, int Length);

    private static byte[] Mutate(byte[] seed, MutationOp[] ops)
    {
        var buffer = new List<byte>(seed);
        foreach (var op in ops)
        {
            if (buffer.Count == 0)
            {
                buffer.Add(op.Value);
                continue;
            }

            var position = op.Position % buffer.Count;
            switch (op.Kind)
            {
                case 0: // flip a single bit
                    buffer[position] ^= (byte)(1 << (op.Value % 8));
                    break;
                case 1: // replace a byte outright
                    buffer[position] = op.Value;
                    break;
                case 2: // delete a byte
                    buffer.RemoveAt(position);
                    break;
                case 3: // insert a byte
                    buffer.Insert(position, op.Value);
                    break;
                case 4: // duplicate a slice in place (grows the buffer)
                    var length = Math.Min(op.Length, buffer.Count - position);
                    if (length > 0 && buffer.Count + length <= MaxMutatedLength)
                        buffer.InsertRange(position, buffer.GetRange(position, length));
                    break;
                case 5: // truncate after this position
                    var cut = position + 1;
                    if (cut < buffer.Count)
                        buffer.RemoveRange(cut, buffer.Count - cut);
                    break;
            }

            if (buffer.Count > MaxMutatedLength)
                buffer.RemoveRange(MaxMutatedLength, buffer.Count - MaxMutatedLength);
        }

        return buffer.Count == 0 ? [0] : [.. buffer];
    }

    // ── Seed corpus ──────────────────────────────────────────────────────────

    private static IReadOnlyList<byte[]> BuildSeedCorpus()
    {
        var seeds = new List<byte[]>();
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            seeds.Add(ms.ToArray());
        }

        // #99 asks to seed from the minimized inputs in MalformedInputTests.cs,
        // EncryptDictionaryDenialOfServiceTests.cs, and PdfDictionaryIndexTests.cs too. All three
        // are font/image/PdfDictionary-level, not raw parser byte blobs, so there is nothing to
        // lift verbatim — what carries over is the SHAPE #208 fixed: a dictionary wide enough to
        // matter, reached before any password check runs. This is deliberately far short of that
        // fix's own 100,000-key regression pin (EncryptDictionaryDenialOfServiceTests already owns
        // that number under its own Timeout); this is a mutation SEED, not a repeat of that test.
        seeds.Add(BuildWideDictionaryObject());

        return seeds;
    }

    private static byte[] BuildWideDictionaryObject()
    {
        var sb = new StringBuilder("<< /Filter /Standard /V 2 /R 3 /Length 128");
        for (var i = 0; i < 500; i++)
            sb.Append(" /Junk").Append(i).Append(' ').Append(i);
        sb.Append(" >>");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── Budget ───────────────────────────────────────────────────────────────

    private static class FuzzBudget
    {
        private const long DefaultIterations = 3_000;

        /// <summary>
        /// Iterations per fuzz <see cref="Fact"/>/<see cref="Theory"/> case. Overridable via
        /// <c>VELLUMPDF_FUZZ_ITER</c> so the nightly workflow can run a much larger budget without
        /// the PR-gating default paying for it.
        /// </summary>
        internal static long Iterations
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("VELLUMPDF_FUZZ_ITER");
                return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultIterations;
            }
        }
    }
}
