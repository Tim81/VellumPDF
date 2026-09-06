// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;

namespace VellumPdf.Reader.Tests.Text;

/// <summary>
/// Shared fixture builders for the <c>Text/</c> test classes, the same hand-built-byte-string style
/// <c>ContentInterpreterTests</c> and <c>FontTestSupport</c> already use, so a fixture can name an
/// exact malformation or an exact arithmetic case a document writer would never produce.
/// </summary>
internal static class TextTestSupport
{
    internal sealed record Obj(int Num, string Dict, byte[]? Stream = null);

    /// <summary>Builds a document from <paramref name="objects"/>, writing each object's own dict
    /// (and, if given, stream body with <c>/Length</c> computed and inserted) at increasing file
    /// offsets, with a plain, non-hybrid cross-reference table naming <paramref
    /// name="rootObjectNumber"/> as <c>/Root</c>.</summary>
    internal static byte[] BuildPdf(int rootObjectNumber, params Obj[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");

        var maxNum = objects.Max(o => o.Num);
        var offsets = new int?[maxNum + 1];
        foreach (var obj in objects.OrderBy(o => o.Num))
        {
            offsets[obj.Num] = (int)ms.Position;
            if (obj.Stream is null)
            {
                W($"{obj.Num} 0 obj\n{obj.Dict}\nendobj\n");
            }
            else
            {
                var trimmed = obj.Dict.TrimEnd();
                var withLength = trimmed[..^2].TrimEnd() + $" /Length {obj.Stream.Length} >>";
                W($"{obj.Num} 0 obj\n{withLength}\nstream\n");
                ms.Write(obj.Stream);
                W("\nendstream\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {maxNum + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= maxNum; i++)
        {
            W(offsets[i] is { } offset
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        }
        W($"trailer\n<< /Size {maxNum + 1} /Root {rootObjectNumber} 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    internal static byte[] Flate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Builds a one-page document whose page's own content is <paramref name="content"/>.</summary>
    internal static byte[] BuildPageDoc(
        string content, string resourcesDict = "<< >>", params Obj[] extraObjects)
    {
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources {resourcesDict} /Contents 4 0 R >>"),
            new(4, "<< >>", Encoding.ASCII.GetBytes(content)),
        };
        objs.AddRange(extraObjects);
        return BuildPdf(1, [.. objs]);
    }

    /// <summary>Builds a document with one page per entry in <paramref name="pageContents"/>, all
    /// sharing <paramref name="resourcesDict"/> and <paramref name="extraObjects"/>: enough for the
    /// page-range and page-separator contract tests, which need more than one page but no per-page
    /// resource variation.</summary>
    internal static byte[] BuildMultiPageDoc(
        IReadOnlyList<string> pageContents, string resourcesDict = "<< >>", params Obj[] extraObjects)
    {
        var objs = new List<Obj> { new(1, "<< /Type /Catalog /Pages 2 0 R >>") };

        // Page objects start right after the content-stream objects, one per page, so object
        // numbers never collide with extraObjects (conventionally numbered from 100 up in callers).
        var firstPageObj = 3 + pageContents.Count;
        var kids = string.Join(' ', Enumerable.Range(0, pageContents.Count).Select(i => $"{firstPageObj + i} 0 R"));
        objs.Add(new(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageContents.Count} >>"));

        for (var i = 0; i < pageContents.Count; i++)
        {
            var contentObj = 3 + i;
            var pageObj = firstPageObj + i;
            objs.Add(new(
                pageObj,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources {resourcesDict} /Contents {contentObj} 0 R >>"));
            objs.Add(new(contentObj, "<< >>", Encoding.ASCII.GetBytes(pageContents[i])));
        }

        objs.AddRange(extraObjects);
        return BuildPdf(1, [.. objs]);
    }

    /// <summary>
    /// A Type1 font dictionary with an EXPLICIT <c>/Widths</c> array (every code in
    /// <paramref name="firstChar"/>..<paramref name="lastChar"/> set to <paramref name="width"/>, in
    /// the thousandths-of-text-space units Table 109 itself uses), so no expected value in an
    /// arithmetic fixture ever depends on the standard-14 AFM metrics <c>SymbolFontMetrics</c>
    /// supplies when a font has none of its own. <c>/Encoding /WinAnsiEncoding</c> gives ASCII
    /// letters and space their familiar codes without needing a <c>/FontDescriptor</c>.
    /// </summary>
    internal static string SimpleFontDict(int firstChar = 32, int lastChar = 126, int width = 600) =>
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding "
        + $"/FirstChar {firstChar} /LastChar {lastChar} /Widths ["
        + string.Join(' ', Enumerable.Repeat(width, lastChar - firstChar + 1)) + "] >>";
}
