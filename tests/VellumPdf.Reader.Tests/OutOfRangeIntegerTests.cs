// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Author-controlled integers that exceed <see cref="int.MaxValue"/> must be rejected, not narrowed.
/// </summary>
/// <remarks>
/// <para>
/// The reader already had <c>CheckedOffset</c> and <c>ParseObjectNumber</c> for exactly this, both
/// documented as throwing "rather than wrapping silently". Two sites did not use them, and both
/// wrapped a hostile value onto a legitimate one instead of failing:
/// </para>
/// <list type="bullet">
///   <item>An indirect reference's object number, so <c>4294967297 0 R</c> resolved to object 1 —
///   letting a crafted document steer this library to a different object graph than any other
///   reader, which for a conformance validator means a verdict describing content the document does
///   not reference.</item>
///   <item>The classic trailer's <c>/Size</c>, which <c>DssBuilder</c> and
///   <c>ArchiveTimestampBuilder</c> use as the first object number for objects they append, so a
///   wrapped value makes an LTV revision overwrite base-revision objects.</item>
/// </list>
/// </remarks>
public sealed class OutOfRangeIntegerTests
{
    [Fact]
    public void IndirectReference_objectNumberBeyondIntMaxValue_isRejected()
    {
        // 4294967297 = 2^32 + 1, which narrows to 1 — the object this document really has.
        var pdf = BuildPdf(rootRef: "4294967297 0 R", size: "6");

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(pdf));

        Assert.Contains("4294967297", ex.Message, StringComparison.Ordinal);
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndirectReference_objectNumberWithinRange_stillResolves()
    {
        // FP-safety: the ordinary case must be untouched.
        using var reader = PdfReader.Open(BuildPdf(rootRef: "1 0 R", size: "6"));

        Assert.Equal("Catalog", ((PdfName)reader.Catalog.Get(PdfName.Type)!).Value);
    }

    [Fact]
    public void Trailer_sizeBeyondIntMaxValue_isRejected()
    {
        // 4294967300 narrows to 4, which is a real object number in this document, so the LTV
        // append would start numbering on top of existing objects.
        using var reader = PdfReader.Open(BuildPdf(rootRef: "1 0 R", size: "4294967300"));

        var ex = Assert.Throws<InvalidDataException>(() => _ = ReaderSize(reader));
        Assert.Contains("4294967300", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Trailer_sizeWithinRange_isReturned()
    {
        using var reader = PdfReader.Open(BuildPdf(rootRef: "1 0 R", size: "6"));

        Assert.Equal(6, ReaderSize(reader));
    }

    /// <summary>
    /// <c>PdfDocumentReader.Size</c> is internal; the test assembly reaches it through
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    private static int ReaderSize(PdfDocumentReader reader) => reader.Size;

    /// <summary>
    /// A minimal single-revision PDF whose trailer <c>/Root</c> reference and <c>/Size</c> are
    /// caller-supplied verbatim, so either can be made out of range.
    /// </summary>
    private static byte[] BuildPdf(string rootRef, string size)
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
        Write($"trailer\n<< /Size {size} /Root {rootRef} >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }
}
