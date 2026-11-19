// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// In an encrypted file the bytes between a literal string's parentheses are ciphertext, so the
/// 7.3.4.2 end-of-line rule (#511) must not run on them: folding a 0Dh into 0Ah, or dropping the 0Ah
/// of a CRLF, changes the bytes the cipher sees. 2.3.3 kept them, and a writer that escapes only
/// parentheses and the backslash produces such files.
///
/// The writer produces AES-256 (R6) files only, and escapes a 0Dh as <c>\r</c>. Each fixture turns every such escape in a string
/// outside a stream body into the raw byte, and adds one space after the closing parenthesis for
/// each, so no offset in the file moves and the cross-reference table stays valid.
/// </summary>
public sealed class EncryptedLiteralStringEndOfLineTests
{
    private static byte[] Build(PdfEncryptionSettings settings, int salt)
    {
        using var doc = new PdfDocument();
        doc.Language = "en-US";
        doc.Info.Title = "T" + salt;
        doc.Encrypt(settings);
        new PdfCanvas(doc.AddPage()).Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static bool At(byte[] a, int i, string w)
    {
        if (i + w.Length > a.Length)
            return false;
        for (var j = 0; j < w.Length; j++)
        {
            if (a[i + j] != w[j])
                return false;
        }
        return true;
    }

    /// <summary>Returns the file with each <c>\r</c> escape in a non-stream string made raw, and the count replaced.</summary>
    private static (byte[] Pdf, int Replaced) WithRawCarriageReturns(byte[] src)
    {
        var o = new List<byte>(src.Length);
        var replaced = 0;
        var i = 0;
        while (i < src.Length)
        {
            if (At(src, i, "stream") && i + 6 < src.Length && (src[i + 6] == '\r' || src[i + 6] == '\n') && !At(src, i - 3, "end"))
            {
                var end = src.AsSpan(i).IndexOf("endstream"u8);
                var stop = end < 0 ? src.Length : i + end + 9;
                for (; i < stop; i++)
                    o.Add(src[i]);
                continue;
            }

            if (src[i] != '(')
            {
                o.Add(src[i++]);
                continue;
            }

            var depth = 0;
            var here = 0;
            do
            {
                var b = src[i];
                if (b == '\\')
                {
                    if (src[i + 1] == 'r')
                    {
                        o.Add(0x0D);
                        here++;
                    }
                    else
                    {
                        o.Add(b);
                        o.Add(src[i + 1]);
                    }
                    i += 2;
                    continue;
                }
                if (b == '(')
                    depth++;
                if (b == ')')
                    depth--;
                o.Add(b);
                i++;
            } while (depth > 0);

            for (var k = 0; k < here; k++)
                o.Add((byte)' ');
            replaced += here;
        }

        return ([.. o], replaced);
    }

    [Fact]
    public void RawCarriageReturnInCiphertext_opensAsItDidOnTheUnmodifiedFile()
    {
        var filesWithARawCr = 0;
        for (var n = 0; n < 400; n++)
        {
            var original = Build(new PdfEncryptionSettings { UserPassword = "", OwnerPassword = "o" }, n);
            var (modified, replaced) = WithRawCarriageReturns(original);
            if (replaced == 0)
                continue;
            filesWithARawCr++;
            Assert.Equal(original.Length, modified.Length);

            using var want = PdfReader.Open(original);
            using var got = PdfReader.Open(modified);

            var lang = new PdfName("Lang");
            Assert.Equal(
                Convert.ToHexString(((PdfLiteralString)want.Catalog.Get(lang)!).Bytes.Span),
                Convert.ToHexString(((PdfLiteralString)got.Catalog.Get(lang)!).Bytes.Span));
            Assert.Equal(
                [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("en-US")],
                ((PdfLiteralString)got.Catalog.Get(lang)!).Bytes.ToArray());
        }

        // Without a file that actually carries a raw CR in a string, the loop proves nothing.
        Assert.True(filesWithARawCr > 0, "no generated file carried a 0Dh in an encrypted string");
    }
}
