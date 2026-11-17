// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Conformance.Tests.StructureDump;

/// <summary>
/// Builds a PDF from object bodies written as text, so a test can state a structure the writer does
/// not produce yet (an MCR kid, an OBJR kid, a wrong parent tree). The cross-reference table is
/// correct and plain; object 1 is <c>/Root</c>.
/// </summary>
internal static class RawPdf
{
    /// <summary>
    /// A one-page document: object 1 the catalog (with <c>/StructTreeRoot 5 0 R</c>), 2 the page tree,
    /// 3 the page, 4 its content stream, 5 the structure tree root. <paramref name="more"/> adds
    /// object 6 and up. A stream's body is the dictionary text without <c>/Length</c>.
    /// </summary>
    /// <param name="content">The page's content stream.</param>
    /// <param name="pageExtra">Extra page-dictionary entries, such as <c>/Resources</c> or <c>/StructParents</c>.</param>
    /// <param name="rootExtra">Extra structure-tree-root entries, such as <c>/K</c> or <c>/ParentTree</c>.</param>
    /// <param name="more">Further objects as (number, dictionary or value text, stream body or null).</param>
    internal static byte[] Doc(string content, string pageExtra, string rootExtra, params (int Num, string Body, string? Stream)[] more)
    {
        var objs = new List<(int Num, string Body, string? Stream)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R {pageExtra} >>", null),
            (4, "<< >>", content),
            (5, $"<< /Type /StructTreeRoot {rootExtra} >>", null),
        };
        objs.AddRange(more);
        return Build(objs);
    }

    /// <summary>Writes <paramref name="objs"/> with a cross-reference table; object 1 is <c>/Root</c>.</summary>
    internal static byte[] Build(IReadOnlyList<(int Num, string Body, string? Stream)> objs)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var max = objs.Max(o => o.Num);
        var offsets = new int?[max + 1];
        foreach (var o in objs.OrderBy(o => o.Num))
        {
            offsets[o.Num] = (int)ms.Position;
            if (o.Stream is null)
            {
                W($"{o.Num} 0 obj\n{o.Body}\nendobj\n");
            }
            else
            {
                var data = Encoding.Latin1.GetBytes(o.Stream);
                var dict = o.Body.TrimEnd();
                dict = dict[..^2].TrimEnd() + $" /Length {data.Length} >>";
                W($"{o.Num} 0 obj\n{dict}\nstream\n");
                ms.Write(data);
                W("\nendstream\nendobj\n");
            }
        }

        var xref = (int)ms.Position;
        W($"xref\n0 {max + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= max; i++)
            W(offsets[i] is { } off ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        W($"trailer\n<< /Size {max + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
