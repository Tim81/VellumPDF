// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Regression tests for the <c>/ByteRange</c> truncation: the reader narrowed each value to
/// <see cref="int"/>, so a signature in a PDF larger than 2 GB reported wrapped offsets and its
/// verification would read the wrong bytes, with nothing reported.
/// </summary>
/// <remarks>
/// The document here is a small hand-built PDF whose signature dictionary simply <em>claims</em>
/// offsets past <see cref="int.MaxValue"/>. That is enough, and much better than a 2 GB fixture:
/// the defect was in parsing the array, not in reading a large file, so the parse is what needs
/// exercising. Building a real multi-gigabyte PDF would move the test's cost from milliseconds to
/// minutes without testing anything more.
/// </remarks>
public sealed class LargeByteRangeTests
{
    [Fact]
    public void ByteRange_valuesBeyondIntMaxValue_areNotTruncated()
    {
        // 0x1_0000_0000 = 4 GiB, and 3_000_000_000 both exceed int.MaxValue (2_147_483_647).
        const long segment0Length = 3_000_000_000L;
        const long segment1Start = 4_294_967_296L;

        using var reader = PdfReader.Open(BuildPdfWithByteRange(0, segment0Length, segment1Start, 1024));

        var sig = Assert.Single(reader.Signatures);
        var br = sig.ByteRange.Span;

        Assert.Equal(4, br.Length);
        Assert.Equal(0L, br[0]);
        Assert.Equal(segment0Length, br[1]);
        Assert.Equal(segment1Start, br[2]);
        Assert.Equal(1024L, br[3]);

        // What the old int[] produced instead: 3_000_000_000 wraps to -1_294_967_296 and
        // 4_294_967_296 wraps to 0. Asserting the absence of those specific values documents the
        // failure mode rather than just the fix.
        Assert.DoesNotContain(-1_294_967_296L, br.ToArray());
        Assert.NotEqual(0L, br[2]);
    }

    /// <summary>
    /// A minimal single-revision PDF with an AcroForm signature field whose signature dictionary
    /// carries the given <c>/ByteRange</c>.
    /// </summary>
    private static byte[] BuildPdfWithByteRange(long a, long b, long c, long d)
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n");

        var offsets = new int[6];

        offsets[1] = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>\nendobj\n");

        offsets[2] = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = (int)ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        offsets[4] = (int)ms.Position;
        Write("4 0 obj\n<< /FT /Sig /T (Signature1) /V 5 0 R >>\nendobj\n");

        offsets[5] = (int)ms.Position;
        Write("5 0 obj\n<< /Type /Sig /SubFilter /ETSI.CAdES.detached "
            + $"/ByteRange [{a} {b} {c} {d}] /Contents <00> >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n0 6\n");
        Write($"{0:D10} 65535 f \n");
        for (var i = 1; i <= 5; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }
}
