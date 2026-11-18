// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #544: a handle from another document is resolved to the saving document's own embedding of the
/// same font program under the same name, and the glyphs the handle registered are added to that
/// subset when the fonts are written. <c>Save</c> and <c>PrepareForSigning</c> write fonts in
/// separate places, so each needs the merge.
/// </summary>
public sealed class ForeignFontHandleSigningTests
{
    private static readonly string FontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    private static string WidthsOf(byte[] pdf) =>
        Regex.Match(Encoding.Latin1.GetString(pdf), @"/W\s*(\[.*?\])\s*(?=/|>>)", RegexOptions.Singleline).Groups[1].Value;

    private static byte[] Build(bool viaForeign, bool signing)
    {
        var font = File.ReadAllBytes(FontPath);
        using var owner = new PdfDocument();
        var foreign = owner.UseTrueTypeFont(font);
        using var doc = new PdfDocument();
        var own = doc.UseTrueTypeFont(font);
        var page = doc.AddPage();
        var handle = viaForeign ? foreign : own;

        // The glyphs are registered through whichever handle draws them, as layout does.
        var gids = new ushort[5];
        handle.GetGlyphIds("Hello", gids);
        doc.RegisterEmbeddedFontUsage(page, handle);

        if (!signing)
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        return doc.PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignHandle_drawnText_getsTheSameWidthsAsTheOwnHandleWould(bool signing)
    {
        if (!File.Exists(FontPath)) Assert.Skip("DejaVu Sans is copied beside the test assembly by the project file.");

        var own = WidthsOf(Build(viaForeign: false, signing));
        var viaForeign = WidthsOf(Build(viaForeign: true, signing));

        Assert.NotEqual(string.Empty, own);
        Assert.Equal(own, viaForeign);
    }
}
