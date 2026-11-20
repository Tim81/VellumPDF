// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// An empty <see cref="PdfStream"/> is written as the eight-byte empty zlib stream of RFC 1950 on
/// every runtime. .NET 10's ZLibStream writes no bytes for empty input, which left an empty
/// FlateDecode body with no header and no Adler-32; the assertions are on bytes because
/// the common readers accept that body.
/// </summary>
public sealed class EmptyStreamTests
{
    private static readonly byte[] EmptyZlib = [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];
    private static readonly DateTimeOffset PinnedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    // SHA-256 of EmptyPageDocument(), recorded from a net11.0 run of the commit before the fix, so
    // it is the bytes the fix must reproduce on net10.0. It is the raw file, not a normalised one.
    private const string EmptyPageSha256 = "D02BE673A9F71215EC66EF3A875BAE8601AAD116CBC3BCEDADD56F0C33D5056F";

    private static byte[] EmptyPageDocument()
    {
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId };
        doc.AddPage(PageSize.A4);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] WriteStream(byte[] data)
    {
        using var ms = new MemoryStream();
        new PdfStream(data).WriteTo(new PdfWriter(ms));
        return ms.ToArray();
    }

    // The bytes between "stream\n" and "\nendstream" of the first stream in the output.
    private static byte[] Body(byte[] output)
    {
        string text = Encoding.Latin1.GetString(output);
        int start = text.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length;
        int end = text.IndexOf("\nendstream", start, StringComparison.Ordinal);
        return output[start..end];
    }

    [Fact]
    public void PdfStream_withNoData_writesTheEmptyZlibStream()
    {
        byte[] output = WriteStream([]);

        Assert.Contains("/Length 8", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.Equal(EmptyZlib, Body(output));
    }

    [Fact]
    public void PdfStream_withOneByte_isUnchanged()
    {
        byte[] output = WriteStream([0x00]);

        Assert.Contains("/Length 9", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.Equal(Convert.FromHexString("789C63000000010001"), Body(output));
    }

    [Fact]
    public void EmptyPage_savesTheEmptyZlibStreamAsItsContents()
    {
        byte[] pdf = EmptyPageDocument();
        string text = Encoding.Latin1.GetString(pdf);

        Assert.Contains("/Filter /FlateDecode\n/Length 8\n>>\nstream\n", text, StringComparison.Ordinal);
        Assert.Contains("\nstream\nx\u009c\u0003\0\0\0\0\u0001\nendstream", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Length 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPage_savesTheSameBytesOnEveryRuntime()
    {
        Assert.Equal(EmptyPageSha256, Convert.ToHexString(SHA256.HashData(EmptyPageDocument())));
    }

    [Fact]
    public void TheEmptyZlibStream_inflatesToNothing()
    {
        using var input = new MemoryStream(EmptyZlib);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        Assert.Equal(0, output.Length);
    }
}
