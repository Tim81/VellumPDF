// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Fonts.Sfnt;
using VellumPdf.Reader;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Shared fixtures for the tests that read an embedded font program or descriptor back out of a
/// saved file. Fonts are the in-tree ones copied beside the test assembly; the variants are built
/// in memory from their bytes, so no font file is added to the repository.
/// </summary>
internal static class EmbeddedFontFixtures
{
    internal static byte[] Load(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", fileName));

    /// <summary>The table tags of an sfnt, in directory order.</summary>
    internal static string[] Tags(byte[] sfnt) =>
        [.. SfntFont.Parse(sfnt).Tables.Keys.Select(t => t.ToString()).OrderBy(t => t, StringComparer.Ordinal)];

    internal static byte[] Table(byte[] sfnt, string tag) =>
        SfntFont.Parse(sfnt).GetTableBytes(new Tag(tag)).ToArray();

    /// <summary> The font with the given tables added (or replaced) and removed, the directory
    /// rebuilt in tag order with each table's checksum. <c>head.checkSumAdjustment</c> is left as
    /// the source has it; nothing under test reads it.
    /// </summary>
    internal static byte[] WithTables(
        byte[] font, IReadOnlyDictionary<string, byte[]>? add = null, IEnumerable<string>? remove = null)
    {
        var sfnt = SfntFont.Parse(font);
        var tables = sfnt.Tables.Keys.ToDictionary(t => t.ToString(), t => sfnt.GetTableBytes(t).ToArray());
        foreach (var tag in remove ?? []) tables.Remove(tag);
        foreach (var (tag, data) in add ?? new Dictionary<string, byte[]>()) tables[tag] = data;

        var ordered = tables.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        U32(sfnt.SfVersion);
        U16(ordered.Count);
        var pow = 1;
        var log = 0;
        while (pow * 2 <= ordered.Count) { pow *= 2; log++; }
        U16(pow * 16);
        U16(log);
        U16(ordered.Count * 16 - pow * 16);
        var offset = 12 + ordered.Count * 16;
        foreach (var (tag, data) in ordered)
        {
            ms.Write(Encoding.ASCII.GetBytes(tag));
            U32(TrueTypeFontEmbedder.Checksum(data));
            U32((uint)offset);
            U32((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        foreach (var (_, data) in ordered)
        {
            ms.Write(data);
            for (var pad = data.Length; (pad & 3) != 0; pad++) ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    /// <summary>The font with <c>head</c> patched in place by <paramref name="patch"/>.</summary>
    internal static byte[] WithHead(byte[] font, Action<byte[]> patch)
    {
        var head = Table(font, "head");
        patch(head);
        return WithTables(font, new Dictionary<string, byte[]> { ["head"] = head });
    }

    internal static void PutI16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    /// <summary>A Kernel document that draws <paramref name="text"/> in the font, saved to
    /// bytes.</summary>
    internal static byte[] SaveKernel(
        byte[] font, string text, PdfConformance conformance = PdfConformance.None)
    {
        using var doc = new PdfDocument
        {
            Conformance = conformance,
            Language = "en-US",
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
        doc.Info.Title = "embedded font";
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(font);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Every font descriptor in the file, with the decoded font program it points
    /// at.</summary>
    internal static List<(PdfDictionary Descriptor, byte[] Program)> Descriptors(byte[] pdf)
    {
        using var reader = PdfReader.Open(pdf);
        var found = new List<(PdfDictionary, byte[])>();
        for (var n = 1; n < reader.Size; n++)
        {
            if (reader.Resolve(n) is not PdfDictionary d
                || d.Get(PdfName.Type) is not PdfName type || type.Value != "FontDescriptor")
                continue;
            var fileRef = d.Get(new PdfName("FontFile2")) ?? d.Get(new PdfName("FontFile3"));
            var stream = reader.ResolveStream((PdfIndirectReference)fileRef!)!;
            found.Add((d, reader.GetDecodedStreamData(stream)!));
        }
        return found;
    }

    /// <summary>The four /FontBBox numbers of a descriptor, each written as an integer or a real.</summary>
    internal static double[] FontBBox(PdfDictionary descriptor)
    {
        var array = (PdfArray)descriptor.Get(new PdfName("FontBBox"))!;
        return [.. Enumerable.Range(0, array.Count).Select(i => array[i] switch
        {
            PdfInteger n => (double)n.Value,
            PdfReal r => r.Value,
            var o => throw new InvalidOperationException($"unexpected {o.GetType().Name} in /FontBBox"),
        })];
    }

    /// <summary>
    /// The bytes of a document's stream bodies, inflated where deflated, concatenated: what the
    /// byte-identity tests digest.
    /// </summary>
    internal static byte[] StreamBodies(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var all = new MemoryStream();
        var pos = 0;
        while (true)
        {
            var s = text.IndexOf("stream\n", pos, StringComparison.Ordinal);
            if (s < 0) break;
            s += "stream\n".Length;
            var e = text.IndexOf("\nendstream", s, StringComparison.Ordinal);
            if (e < 0) break;
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, s, e - s), CompressionMode.Decompress);
                z.CopyTo(all);
            }
            catch (InvalidDataException)
            {
                all.Write(pdf, s, e - s);
            }
            pos = e + "\nendstream".Length;
        }
        return all.ToArray();
    }
}
