// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Boundary coverage for <c>XrefParser.ReadInt</c>'s "could not parse integer" throw (#406 round 2):
/// a classic xref subsection header (<c>firstObjNum count</c>) whose digit run overflows
/// <see cref="int"/> reaches this with the whole run, unbounded by the digit-scan loop above it.
/// Driven directly through <see cref="XrefParser.Parse"/> rather than <see cref="PdfReader.Open"/>,
/// since this throw happens before reconstruction or any caller of <c>Parse</c> gets a chance to
/// wrap it, and the direct call avoids building a whole otherwise-valid document around one
/// subsection header.
/// </summary>
public sealed class XrefParserTests
{
    // A minimal classic xref table whose subsection header names a digit run big enough to overflow
    // int.TryParse: "0 <count>" as the (firstObjNum count) pair, followed by a trailer so the parser
    // reaches ReadInt's second call (the count) before anything else could fail first.
    private static byte[] Build(string count)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {count}\n");
        W("trailer\n<< /Size 1 >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void SubsectionHeaderCount_withAnOversizedDigitRun_throwsOnlyAFixedExcerpt()
    {
        // int.MaxValue is 10 digits, so a run this long overflows int.TryParse regardless of its
        // actual digits — no need for the run to encode a real, near-overflow value.
        var count = new string('9', 1 << 20);

        var ex = Assert.Throws<InvalidDataException>(
            () => XrefParser.Parse(Build(count), allowReconstruction: false, ReaderLimits.Defaults));

        Assert.Equal(
            "Malformed PDF: could not parse integer '" + new string('9', 32) + "... (1048576 bytes)'.",
            ex.Message);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void SubsectionHeaderCount_atTheExcerptBoundary_quotesThirtyTwoWhole_andExcerptsThirtyThree(
        int digitCount, bool expectExcerpt)
    {
        var count = new string('9', digitCount);

        var ex = Assert.Throws<InvalidDataException>(
            () => XrefParser.Parse(Build(count), allowReconstruction: false, ReaderLimits.Defaults));

        var expected = expectExcerpt
            ? "Malformed PDF: could not parse integer '" + new string('9', 32) + $"... ({digitCount} bytes)'."
            : $"Malformed PDF: could not parse integer '{count}'.";
        Assert.Equal(expected, ex.Message);
    }
}
