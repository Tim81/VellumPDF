// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// #610: bytes allocated by <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/> with
/// <see cref="PdfReaderOptions.AllowReconstruction"/> on a hostile run of openers.
///
/// <para>
/// The reconstruction walk resyncs one byte after a failed construct, so a run of <c>[</c> starts a
/// fresh parse at every byte. Before #610 each start failed by an exception unwound through up to
/// 257 nested frames and cost about 72 KB, so the 1 MiB aggregate budget (which counts bytes advanced,
/// not bytes allocated) let a 4 KiB file allocate 293 MB. These pins assert an absolute ceiling per
/// shape and size, measured on 2026-11-19 (Release, net10.0, workstation GC) and set at 1.6 times
/// the measured value, so a reintroduced per-level cost fails them by a wide margin. Every ceiling
/// is below the figure the 2.3.3 code allocates for that input except the balanced shape at 512
/// bytes, where both builds allocate about 47 KB because nothing in it fails; the other balanced
/// sizes and every other shape guard a revert. Allocation is per thread
/// (<see cref="GC.GetAllocatedBytesForCurrentThread"/>), deterministic for a given input, and no
/// assertion here reads a clock.
/// </para>
/// <para>
/// The linearity pin covers the shapes whose measured figures are linear in length. The
/// <c>&lt;&lt;</c> shapes cost about 72 bytes per input byte, the fixed price of the parser and
/// lexer objects each failed start constructs; the nesting shapes plateau once the 1 MiB budget
/// floor stops the walk, so their per-byte figure falls as the input grows. Neither is a function of
/// nesting depth, because scan mode recognises without building an integer, name or container.
/// </para>
/// </summary>
public sealed class ReconstructionAllocationTests
{
    private static readonly int[] _sizes = [512, 4096, 65536, 262144];

    private static byte[] Rep(string unit, int length)
    {
        var sb = new StringBuilder(length + unit.Length);
        while (sb.Length < length)
            sb.Append(unit);
        sb.Length = length;
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Prefixed(string prefix, byte[] rest) =>
        [.. Encoding.ASCII.GetBytes(prefix), .. rest];

    // A small valid document whose startxref is broken, with the hostile run as the body of object 4.
    private static byte[] Embedded(byte[] run) =>
    [
        .. Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n2 0 obj\n"
            + "<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n3 0 obj\n"
            + "<</Type/Page/Parent 2 0 R/MediaBox[0 0 10 10]>>\nendobj\n4 0 obj\n"),
        .. run,
        .. Encoding.ASCII.GetBytes("\nendobj\ntrailer\n<</Root 1 0 R/Size 5>>\nstartxref\n999999\n%%EOF\n"),
    ];

    private static byte[] Secondary(string unit, int n)
    {
        var body = "A" + NewLine + "endstream" + NewLine + "endobj" + NewLine + Encoding.ASCII.GetString(Rep(unit, n));
        return Encoding.ASCII.GetBytes(
            "%PDF-1.4" + NewLine + "1 0 obj" + NewLine + "<</Length " + body.Length + ">>" + NewLine + "stream" + NewLine
            + body + NewLine + "endstream" + NewLine + "endobj" + NewLine);
    }

    private const string NewLine = "\n";

    private static byte[] Shape(string shape, int n) => shape switch
    {
        "[" => Rep("[", n),
        "<<" => Rep("<<", n),
        "trailer+[" => Prefixed("trailer ", Rep("[", n)),
        "trailer+<<" => Prefixed("trailer ", Rep("<<", n)),
        "[1[1" => Rep("[1", n),
        "<</a<</a" => Rep("<</a", n),
        "<</a[<</a[" => Rep("<</a[", n),
        "balanced" => [.. Rep("[", n / 2), .. Rep("]", n / 2)],
        "embedded" => Embedded(Rep("[", n)),
        "hdr+[x300" => Rep("0 0 obj " + new string('[', 300) + " ", n),
        // The header probe's dictionary branch: the value after "0 0 obj" is a deep dictionary.
        "hdr+<</ax300" => Rep("0 0 obj " + string.Concat(Enumerable.Repeat("<</a", 300)) + " ", n),
        // The secondary-region probe: a stream whose verified /Length swallows an earlier genuine
        // 'endstream', so the bytes after it are walked again in secondary mode and every header there
        // is probed. n is the length of the hostile run after that 'endstream'.
        "secondary[x300" => Secondary("0 0 obj " + new string('[', 300) + " ", n),
        "secondary<</ax300" => Secondary("0 0 obj " + string.Concat(Enumerable.Repeat("<</a", 300)) + " ", n),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // Ceiling in bytes for each of _sizes, in order: 1.6 x the figure measured on the #610 build.
    private static readonly Dictionary<string, long[]> _ceilings = new()
    {
        ["["] = [122_000, 948_000, 946_000, 1_886_000],
        ["<<"] = [63_000, 476_000, 7_554_000, 30_204_000],
        ["trailer+["] = [122_000, 948_000, 946_000, 1_886_000],
        ["trailer+<<"] = [63_000, 476_000, 7_554_000, 30_204_000],
        ["[1[1"] = [63_000, 476_000, 477_000, 948_000],
        ["<</a<</a"] = [34_000, 240_000, 242_000, 477_000],
        ["<</a[<</a["] = [51_000, 621_000, 383_000, 760_000],
        ["balanced"] = [76_000, 489_000, 946_000, 1_886_000],
        ["embedded"] = [139_000, 964_000, 953_000, 1_895_000],
        ["hdr+[x300"] = [124_000, 940_000, 1_579_000, 3_176_000],
        ["hdr+<</ax300"] = [38_000, 355_000, 403_000, 814_000],
        ["secondary[x300"] = [11_000, 24_000, 243_000, 951_000],
        ["secondary<</ax300"] = [10_000, 14_000, 70_000, 253_000],
    };

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var shape in _ceilings.Keys)
            foreach (var size in _sizes)
                data.Add(shape, size);
        return data;
    }

    // Several measurements per input, minimum taken. A thread's allocation counter also counts
    // whatever the runtime itself allocates on that thread while the measured call runs (a lazily
    // built static, a first-use cache, a JIT tier change), and a one-off lands in whichever
    // measurement happens to trigger it. An input's own cost recurs on every run and a one-off does
    // not, so the minimum is the input's cost. The ceilings and bounds below are unchanged: they
    // were measured on a warm path, which is what the minimum reports.
    private const int Runs = 5;

    // One throwaway run so one-time JIT and static-initialiser allocations are not charged to the
    // measured ones (the convention EncryptedReconstructionTests uses).
    private static void WarmUp(byte[] input, PdfReaderOptions options)
    {
        try { PdfReader.Open(input, options).Dispose(); } catch (InvalidDataException) { }
    }

    private static long MeasureOnce(byte[] input, PdfReaderOptions options, out string outcome)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            using var reader = PdfReader.Open(input, options);
            outcome = $"opened, {reader.PageCount} page(s)";
        }
        catch (InvalidDataException e)
        {
            outcome = "InvalidDataException: " + e.Message;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long Measure(byte[] input, PdfReaderOptions options, out string outcome)
    {
        WarmUp(input, options);
        var min = long.MaxValue;
        outcome = "";
        for (var i = 0; i < Runs; i++)
            min = Math.Min(min, MeasureOnce(input, options, out outcome));
        return min;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Open_WithReconstruction_AllocatesWithinTheShapeCeiling(string shape, int size)
    {
        var input = Shape(shape, size);
        var ceiling = _ceilings[shape][Array.IndexOf(_sizes, size)];

        var allocated = Measure(input, new PdfReaderOptions { AllowReconstruction = true }, out var outcome);

        Assert.True(
            allocated <= ceiling,
            $"{shape} at {input.Length} bytes allocated {allocated:N0} bytes (ceiling {ceiling:N0}); outcome: {outcome}.");
    }

    [Theory]
    [InlineData("[")]
    [InlineData("<<")]
    [InlineData("trailer+[")]
    [InlineData("trailer+<<")]
    [InlineData("balanced")]
    [InlineData("[1[1")]
    [InlineData("<</a<</a")]
    [InlineData("hdr+[x300")]
    public void Open_WithReconstruction_GrowsAtMostLinearlyWithLength(string shape)
    {
        // 4 KiB is eight times 512 B. The constant is 64 KiB: the fixed cost of the walk's own
        // collections and the Open call, which dominates the 512-byte figure for the cheaper shapes.
        const long constant = 64 * 1024;
        var options = new PdfReaderOptions { AllowReconstruction = true };
        var small = Measure(Shape(shape, 512), options, out _);
        var medium = Measure(Shape(shape, 4096), options, out _);
        var large = Measure(Shape(shape, 65536), options, out _);

        Assert.True(medium <= 8 * small + constant, $"{shape}: 4 KiB allocated {medium:N0}, 512 B allocated {small:N0}.");
        Assert.True(large <= 16 * medium + constant, $"{shape}: 64 KiB allocated {large:N0}, 4 KiB allocated {medium:N0}.");
    }

    [Fact]
    public void Open_WithReconstruction_OffCostsTheSameForAnyRun()
    {
        // The control: reconstruction off never enters the walk, so the run's length is irrelevant.
        // The two inputs are measured in alternation rather than one after the other. The runtime's
        // own allocation on this thread is not constant for the life of the process: with reconstruction
        // off, the same Open call measured 936, 912 and 744 bytes at different points in one run (the
        // cause was not isolated). Five readings of one input followed by five of the other can
        // straddle such a step and differ by it, while the minimum over alternated readings is taken
        // from the same states for both inputs.
        var off = new PdfReaderOptions { AllowReconstruction = false };
        var shortRun = Shape("[", 512);
        var longRun = Shape("[", 262144);
        WarmUp(shortRun, off);
        WarmUp(longRun, off);
        long a = long.MaxValue, b = long.MaxValue;
        for (var i = 0; i < Runs; i++)
        {
            a = Math.Min(a, MeasureOnce(shortRun, off, out _));
            b = Math.Min(b, MeasureOnce(longRun, off, out _));
        }

        Assert.Equal(a, b);
        Assert.True(a < 4096, $"allocated {a:N0}");
    }

    [Fact]
    public void Open_EmbeddedRunInAnObjectBody_RecoversTheSameObjects()
    {
        // The issue's embedded case: a small file with a broken startxref and a run of '[' as an
        // object body. It must still open with objects 1-3 intact, whatever the run costs.
        var input = Shape("embedded", 4096);
        using var reader = PdfReader.Open(input, new PdfReaderOptions { AllowReconstruction = true });

        Assert.True(reader.WasReconstructed);
        Assert.Equal(1, reader.PageCount);
        foreach (var n in new[] { 1, 2, 3 })
            Assert.Contains(n, reader.ObjectNumbers);
        Assert.Equal(
            "<< /Type /Catalog /Pages 2 0 R >>",
            Encoding.ASCII.GetString(XrefReconstructionTests.WriteToBytes(reader.Resolve(1)!)).Replace("\n", " ").Trim());
    }

    // An uncompressed object stream whose header gives every member the same offset, at a run of '['
    // in the body. Phase B3 of reconstruction (PdfDocumentReader.TryParseObjectStreamMemberDirect)
    // parses every member of every container looking for a catalog, so it fails once per member.
    private static byte[] ObjectStreamWithMembersAtARun(int members, bool catalogLast)
    {
        var header = new StringBuilder();
        for (var i = 0; i < members; i++)
            header.Append(catalogLast && i == members - 1 ? "5000 301 " : $"{i + 10} 0 ");
        var run = new string('[', 300);
        var body = header + run + (catalogLast ? " <</Type/Catalog/Pages 2 0 R>>" : "");
        return Encoding.Latin1.GetBytes(
            "%PDF-1.5\n1 0 obj\n<</Type/ObjStm/N " + members + "/First " + header.Length + "/Length " + body.Length
            + ">>\nstream\n" + body + "\nendstream\nendobj\ntrailer\n<</Size 5>>\nstartxref\n0\n%%EOF\n");
    }

    // 1.6 x the figures measured on the #610 build: 400,664 / 1,085,296 / 4,531,312 bytes. The same
    // files allocated 74.9 MB / 299 MB / 1.2 GB on v2.3.3.
    [Theory]
    [InlineData(1000, 641_000L)]
    [InlineData(4000, 1_737_000L)]
    [InlineData(16000, 7_250_000L)]
    public void Open_ObjectStreamMembersAtARun_AllocatesWithinTheCeiling(int members, long ceiling)
    {
        var input = ObjectStreamWithMembersAtARun(members, catalogLast: false);
        var allocated = Measure(input, new PdfReaderOptions { AllowReconstruction = true }, out var outcome);

        Assert.StartsWith("InvalidDataException: Malformed PDF: reconstruction could not find a usable document catalog", outcome);
        Assert.True(allocated <= ceiling, $"{members} members, {input.Length} bytes: allocated {allocated:N0} (ceiling {ceiling:N0}).");
    }

    [Fact]
    public void Open_ObjectStreamMembersAtARun_StillFindsTheCatalogMemberAfterThem()
    {
        // B3 tries members in offset order; 299 fail at the run and the 300th is a catalog. The walk
        // must pass the failures and find it, as v2.3.3 does.
        using var reader = PdfReader.Open(
            ObjectStreamWithMembersAtARun(300, catalogLast: true), new PdfReaderOptions { AllowReconstruction = true });

        Assert.True(reader.WasReconstructed);
        var catalog = Assert.IsType<VellumPdf.Core.PdfDictionary>(reader.Resolve(5000));
        Assert.Equal("Catalog", ((VellumPdf.Core.PdfName)catalog.Get(VellumPdf.Core.PdfName.Type)!).Value);
    }

    // Phase B3's accept path: members that PARSE. Every member points at one valid value (a flat
    // array, or a dictionary that is not a catalog), so nothing fails and the cost is whatever each
    // member's parse builds. The array case is the reviewer's: on v2.3.3, 4,000 members at one
    // 4,000-element array (35 KB) allocated 1.16 GB.
    private static byte[] ObjectStreamWithMembersAtOneValue(int members, int elements, bool dictionary, bool catalogFirst = false)
    {
        var header = new StringBuilder();
        var catalog = "<</Type/Catalog/Pages 2 0 R>> ";
        if (catalogFirst)
            header.Append("9 0 ");
        var valueOffset = catalogFirst ? catalog.Length : 0;
        for (var i = 0; i < members; i++)
            header.Append(i + 10).Append(' ').Append(valueOffset).Append(' ');
        var items = string.Concat(Enumerable.Repeat("0 ", elements));
        var value = dictionary ? "<</Type/Pages/Big [" + items + "]>>" : "[" + items + "]";
        var body = header + (catalogFirst ? catalog : "") + value;
        return Encoding.Latin1.GetBytes(
            "%PDF-1.5\n1 0 obj\n<</Type/ObjStm/N " + (members + (catalogFirst ? 1 : 0)) + "/First " + header.Length + "/Length " + body.Length
            + ">>\nstream\n" + body + "\nendstream\nendobj\ntrailer\n<</Size 5>>\nstartxref\n0\n%%EOF\n");
    }

    // 1.6 x the figures measured on the #610 build (file bytes in the comment); v2.3.3 allocated
    // 73,162,264 / 292,126,896 / 1,168,692,680 / 1,160,932,896 bytes for the array rows and
    // 73,578,280 / 293,790,616 for the dictionary rows.
    [Theory]
    [InlineData(1000, 1000, false, 644_000L)]   // 8,049 B, measured 402,328
    [InlineData(4000, 1000, false, 1_741_000L)] // 29,051 B, measured 1,086,960
    [InlineData(16000, 1000, false, 7_253_000L)] // 119,064 B, measured 4,532,976
    [InlineData(4000, 4000, false, 1_749_000L)] // 35,051 B, measured 1,092,960
    [InlineData(1000, 1000, true, 761_000L)]    // 8,069 B, measured 475,440
    [InlineData(4000, 1000, true, 1_857_000L)]  // 29,071 B, measured 1,160,328
    public void Open_ObjectStreamMembersAtOneValidValue_AllocatesWithinTheCeiling(
        int members, int elements, bool dictionary, long ceiling)
    {
        var input = ObjectStreamWithMembersAtOneValue(members, elements, dictionary);
        var allocated = Measure(input, new PdfReaderOptions { AllowReconstruction = true }, out var outcome);

        Assert.StartsWith("InvalidDataException: Malformed PDF: reconstruction could not find a usable document catalog", outcome);
        Assert.True(allocated <= ceiling, $"{members} members, {input.Length} bytes: allocated {allocated:N0} (ceiling {ceiling:N0}).");
    }

    [Fact]
    public void Open_ObjectStreamMembersAtOneValidValue_StillElectsTheCatalogMember()
    {
        // 2,000 members share the offset of an array; member 9 is a catalog at a lower offset. B3 walks
        // members by offset, so it must elect 9 and leave the array members alone, as v2.3.3 does.
        using var reader = PdfReader.Open(
            ObjectStreamWithMembersAtOneValue(2000, 500, dictionary: false, catalogFirst: true),
            new PdfReaderOptions { AllowReconstruction = true });

        Assert.True(reader.WasReconstructed);
        Assert.Equal(new VellumPdf.Core.PdfIndirectReference(9, 0), reader.Trailer.Get(VellumPdf.Core.PdfName.Root));
        var catalog = Assert.IsType<VellumPdf.Core.PdfDictionary>(reader.Resolve(9));
        Assert.Equal("Catalog", ((VellumPdf.Core.PdfName)catalog.Get(VellumPdf.Core.PdfName.Type)!).Value);
    }

    // Phase B3's accept path with a nested value at EVERY level. Member i points at the i-th opener of a
    // chain ("<</a" repeated, or "[" repeated), so each member is a distinct span that parses, and each
    // span contains the whole flat array at the bottom. On v2.3.3, and on this branch before the
    // catalog was decided from the member's own keys, every such member was built in full: levels x
    // array size, 989 MB from 1,323 bytes of FlateDecode data at 255 levels and 50,000 elements.
    // A trailing catalog member (object 9, after the chain) is added when asked for. The ceilings are 1.6
    // times the figures measured on this build (Release, net10.0). The "[" chain is the one a check of
    // the first token alone settles: a member that is not a dictionary is refused without being built.
    private static byte[] ObjectStreamWithNestedMembers(
        int levels, int elements, string open, string close, bool catalogAfter = false)
    {
        var chain = string.Concat(Enumerable.Repeat(open, levels))
            + "[" + string.Concat(Enumerable.Repeat("0 ", elements)) + "]"
            + string.Concat(Enumerable.Repeat(close, levels));
        var header = new StringBuilder();
        for (var i = 0; i < levels; i++)
            header.Append(i + 10).Append(' ').Append(i * open.Length).Append(' ');
        if (catalogAfter)
            header.Append("9 ").Append(chain.Length + 1).Append(' ');
        var body = header + chain + (catalogAfter ? " <</Type/Catalog/Pages 2 0 R>>" : "");
        return Encoding.Latin1.GetBytes(
            "%PDF-1.5\n1 0 obj\n<</Type/ObjStm/N " + (levels + (catalogAfter ? 1 : 0)) + "/First " + header.Length
            + "/Length " + body.Length + ">>\nstream\n" + body
            + "\nendstream\nendobj\ntrailer\n<</Size 5>>\nstartxref\n0\n%%EOF\n");
    }

    [Theory]
    [InlineData(255, 5000, "<</a", ">>", 248_000L)] // 13,586 B, measured 154,720; 112,622,160 on v2.3.3
    [InlineData(64, 5000, "<</a", ">>", 78_000L)]   // 10,931 B, measured 48,448
    [InlineData(255, 5000, "[", "]", 246_000L)]     // 12,479 B, measured 153,552 (987,557,944 at 50,000 elements on v2.3.3)
    public void Open_ObjectStreamMembersThatNestAValueAtEveryLevel_AllocateWithinTheCeiling(
        int levels, int elements, string open, string close, long ceiling)
    {
        var input = ObjectStreamWithNestedMembers(levels, elements, open, close);
        var allocated = Measure(input, new PdfReaderOptions { AllowReconstruction = true }, out var outcome);

        Assert.StartsWith("InvalidDataException: Malformed PDF: reconstruction could not find a usable document catalog", outcome);
        Assert.True(allocated <= ceiling, $"{levels} levels of {open}, {elements} elements, {input.Length} bytes: allocated {allocated:N0} (ceiling {ceiling:N0}).");
    }

    [Theory]
    [InlineData("<</a", ">>")]
    [InlineData("[", "]")]
    public void Open_ObjectStreamMembersThatNestAValueAtEveryLevel_StillElectsALaterCatalog(string open, string close)
    {
        using var reader = PdfReader.Open(
            ObjectStreamWithNestedMembers(100, 500, open, close, catalogAfter: true),
            new PdfReaderOptions { AllowReconstruction = true });

        Assert.True(reader.WasReconstructed);
        Assert.Equal(new VellumPdf.Core.PdfIndirectReference(9, 0), reader.Trailer.Get(VellumPdf.Core.PdfName.Root));
    }

    [Fact]
    public void Open_NestedDictionaryMembersThatAreAllCatalogs_ElectsTheOneAtTheLowestOffset()
    {
        // Every level opens with /Type/Catalog, so every member qualifies and one is built: the one at
        // the lowest offset, member 10, which is the member v2.3.3 elects.
        using var reader = PdfReader.Open(
            ObjectStreamWithNestedMembers(60, 500, "<</Type/Catalog/a", ">>"),
            new PdfReaderOptions { AllowReconstruction = true });

        Assert.True(reader.WasReconstructed);
        Assert.Equal(new VellumPdf.Core.PdfIndirectReference(10, 0), reader.Trailer.Get(VellumPdf.Core.PdfName.Root));
        var catalog = Assert.IsType<VellumPdf.Core.PdfDictionary>(reader.Resolve(10));
        Assert.Equal("Catalog", ((VellumPdf.Core.PdfName)catalog.Get(VellumPdf.Core.PdfName.Type)!).Value);
    }
}
