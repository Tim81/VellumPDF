// Copyright © Timothy van der Ham (@Tim81) SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>What a saved file says about its links, read back from the bytes.</summary>
internal static class LinkSupport
{
    internal static readonly FontReference Helv = new(Standard14.Helvetica);

    /// <summary>One link annotation: its target and rectangle.</summary>
    internal sealed record Link(string? Uri, string? Dest, double X0, double Y0, double X1, double Y1);

    private static readonly Regex RectOf = new(
        @"/Rect \[(?<x0>-?[\d.]+) (?<y0>-?[\d.]+) (?<x1>-?[\d.]+) (?<y1>-?[\d.]+)\]", RegexOptions.CultureInvariant);

    /// <summary>Every link annotation, in file order.</summary>
    internal static List<Link> Links(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var found = new List<Link>();
        foreach (var obj in Regex.Split(text, @"\d+ 0 obj"))
        {
            if (!obj.Contains("/Subtype /Link", StringComparison.Ordinal)) continue;
            var rect = RectOf.Match(obj);
            if (!rect.Success) continue;
            var uri = Regex.Match(obj, @"/URI \((?<u>[^)]*)\)");
            var dest = Regex.Match(obj, @"/Dest \((?<d>[^)]*)\)");
            double D(string g) => double.Parse(rect.Groups[g].Value, CultureInfo.InvariantCulture);
            found.Add(new Link(
                uri.Success ? uri.Groups["u"].Value : null,
                dest.Success ? dest.Groups["d"].Value : null,
                D("x0"), D("y0"), D("x1"), D("y1")));
        }

        return found;
    }

    internal static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    internal static string Content(byte[] pdf) => PdfTestUtil.DecompressAllFlatStreams(pdf);

    /// <summary>The text shown by every simple-font <c>Tj</c>, in order.</summary>
    internal static List<string> Shown(byte[] pdf) =>
        [.. ContentStreamReadback.TextPlacements(Content(pdf)).Where(p => p.Text.Length > 0).Select(p => p.Text)];

    internal static Document ShortPage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = new EdgeInsets(10),
    };

    internal static TextStyle Style(string? uri = null, string? anchor = null, double size = 12) =>
        new() { FontRef = Helv, FontSize = size, LinkUri = uri, LinkAnchor = anchor };
}
