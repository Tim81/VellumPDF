// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// An incremental update must not number its new objects on top of objects the document already
/// defines.
/// </summary>
/// <remarks>
/// <c>DssBuilder</c> and <c>ArchiveTimestampBuilder</c> numbered from the trailer's <c>/Size</c>,
/// which is author-controlled and only advisory. Every malformed or dishonest form of it yields a
/// starting number that collides, and the appended /DSS or document-timestamp revision then
/// replaces base-revision objects — potentially invalidating the signature it was added to
/// augment. Range-checking <c>/Size</c> caught only the unrepresentable case.
/// </remarks>
public sealed class NextFreeObjectNumberTests
{
    // The fixture defines objects 1-5, so 6 is the first free number regardless of what /Size says.
    private const int FirstFree = 6;

    [Theory]
    [InlineData("6", "honest")]
    [InlineData("4", "understated — the case that silently overwrote objects 4 and 5")]
    [InlineData("0", "zero")]
    [InlineData("1 0 R", "indirect, which the reader cannot resolve here")]
    [InlineData("6.0", "a real rather than an integer")]
    public void NextFreeObjectNumber_neverCollides_whateverSizeSays(string size, string why)
    {
        using var reader = PdfReader.Open(BuildPdf(size));

        Assert.True(
            reader.NextFreeObjectNumber >= FirstFree,
            $"/Size {size} ({why}) produced {reader.NextFreeObjectNumber}, which collides with an existing object.");
    }

    [Fact]
    public void NextFreeObjectNumber_honoursALargerSize()
    {
        // A conformant /Size exceeds every object number in the file, so it must win when larger —
        // otherwise an update could reuse a number the document reserved.
        using var reader = PdfReader.Open(BuildPdf("50"));

        Assert.Equal(50, reader.NextFreeObjectNumber);
    }

    private static byte[] BuildPdf(string size)
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n");
        var offsets = new int[6];

        offsets[1] = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = (int)ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        offsets[4] = (int)ms.Position;
        Write("4 0 obj\n<< /Filler true >>\nendobj\n");
        offsets[5] = (int)ms.Position;
        Write("5 0 obj\n<< /Filler true >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n0 6\n");
        Write($"{0:D10} 65535 f \n");
        for (var i = 1; i <= 5; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }
}
