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
/// the measured value, so a reintroduced per-level cost fails them by a wide margin: every ceiling
/// below is below the figure the 2.3.3 code allocates, which the PR body tabulates. Allocation is
/// per thread (<see cref="GC.GetAllocatedBytesForCurrentThread"/>), deterministic for a given input,
/// and no assertion here reads a clock.
/// </para>
/// <para>
/// The ceilings are not all linear in input length, on purpose. For the shapes that carry a leaf at
/// every level (<c>[1[1</c>, <c>&lt;&lt;/a&lt;&lt;/a</c>) each failed start still allocates the
/// leaves it passed before failing, so allocation scales with starts times depth until the
/// reconstruction budget stops the walk; the ceiling there is the measured plateau, not a multiple of
/// the length. The linearity pin below covers the shapes for which linear holds.
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
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // Ceiling in bytes for each of _sizes, in order: 1.6 x the figure measured on the #610 build.
    private static readonly Dictionary<string, long[]> _ceilings = new()
    {
        ["["] = [122_000, 948_000, 946_000, 1_886_000],
        ["<<"] = [63_000, 476_000, 7_554_000, 30_204_000],
        ["trailer+["] = [122_000, 948_000, 946_000, 1_886_000],
        ["trailer+<<"] = [63_000, 476_000, 7_554_000, 30_204_000],
        ["[1[1"] = [7_222_000, 107_459_000, 114_395_000, 228_729_000],
        ["<</a<</a"] = [1_090_000, 29_617_000, 33_764_000, 67_488_000],
        ["<</a[<</a["] = [1_383_000, 25_361_000, 27_171_000, 54_335_000],
        ["balanced"] = [76_000, 489_000, 946_000, 1_886_000],
        ["embedded"] = [336_000, 1_161_000, 953_000, 1_895_000],
        ["hdr+[x300"] = [390_000, 4_391_000, 7_479_000, 15_120_000],
    };

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var shape in _ceilings.Keys)
            foreach (var size in _sizes)
                data.Add(shape, size);
        return data;
    }

    private static long Measure(byte[] input, PdfReaderOptions options, out string outcome)
    {
        // One throwaway run so one-time JIT and static-initialiser allocations are not charged to
        // the measured one (the convention EncryptedReconstructionTests uses).
        try { PdfReader.Open(input, options).Dispose(); } catch (InvalidDataException) { }

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
        var off = new PdfReaderOptions { AllowReconstruction = false };
        var a = Measure(Shape("[", 512), off, out _);
        var b = Measure(Shape("[", 262144), off, out _);
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

    // 1.6 x the figures measured on the #610 build: 480,368 / 1,411,192 / 5,811,016 bytes. The same
    // files allocated 74.9 MB / 299 MB / 1.2 GB on v2.3.3.
    [Theory]
    [InlineData(1000, 770_000L)]
    [InlineData(4000, 2_260_000L)]
    [InlineData(16000, 9_300_000L)]
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
}
