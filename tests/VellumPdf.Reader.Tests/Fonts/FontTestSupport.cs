// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>
/// Shared fixture builders for the <c>Fonts/</c> test classes: a minimal, hand-built PDF byte
/// stream (the <c>ContentInterpreterTests</c> / <c>PageTreeTests</c> style: a raw text template
/// per object, not <c>VellumPdf.Document.PdfDocument</c>) that gives
/// <see cref="VellumPdf.Reader.Fonts.SimpleFontReader.Create"/> a live
/// <see cref="PdfDocumentReader"/> to resolve indirect references through, with exact control over
/// object shapes a document writer would never produce.
/// </summary>
internal static class FontTestSupport
{
    internal readonly record struct Obj(int Num, string Dict, byte[]? Stream = null);

    /// <summary>A one-page document with no fonts of its own; enough for tests that only need a
    /// live <see cref="PdfDocumentReader"/> to resolve direct (non-reference) objects
    /// through.</summary>
    internal static PdfDocumentReader OpenMinimal() => Open();

    /// <summary>Opens a document built from <paramref name="objects"/>, always including a minimal
    /// catalog/page tree at objects 1 and 2 so <c>startxref</c>/<c>/Root</c> resolve.</summary>
    internal static PdfDocumentReader Open(params Obj[] objects)
    {
        var all = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
        };
        all.AddRange(objects);
        return PdfReader.Open(BuildPdf(1, [.. all]));
    }

    /// <summary>
    /// A one-page document whose object <paramref name="firstChainObject"/> is a stream whose
    /// <c>/Length</c> is an indirect reference to the next object, itself a stream with the same
    /// shape, <paramref name="chainLen"/> objects deep; the final object is a plain integer
    /// terminus. Resolving <paramref name="firstChainObject"/> re-enters
    /// <c>PdfDocumentReader.Resolve</c> once per link (parsing a stream's own structure calls back
    /// into resolution for its <c>/Length</c>), so a long enough chain throws
    /// <see cref="InvalidDataException"/> past <c>MaxResolveDepth</c> before the caller's own
    /// dictionary-type check ever runs, the same shape <c>XrefStreamTests</c> uses to pin this
    /// guard against a stack overflow.
    /// </summary>
    internal static byte[] BuildDeepIndirectLengthChain(int firstChainObject, int chainLen)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        var last = firstChainObject + chainLen;
        var offsets = new int[last + 1];

        offsets[1] = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        for (var k = firstChainObject; k < last; k++)
        {
            offsets[k] = (int)ms.Position;
            W($"{k} 0 obj\n<< /Length {k + 1} 0 R >>\nstream\nx\nendstream\nendobj\n");
        }
        offsets[last] = (int)ms.Position;
        W($"{last} 0 obj\n1\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {last + 1}\n");
        W("0000000000 65535 f \n");
        for (var k = 1; k <= last; k++)
            W($"{offsets[k]:D10} 00000 n \n");
        W($"trailer\n<< /Size {last + 1} /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] BuildPdf(int rootObjectNumber, Obj[] objects)
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
}
