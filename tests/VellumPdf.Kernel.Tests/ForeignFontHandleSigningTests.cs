// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Signing;
using VellumPdf.TestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>A handle from another document is resolved to the saving document's own embedding of the same font program under the same name, and its registered glyphs are added to that subset when the fonts are written, in both <c>Save</c> and <c>PrepareForSigning</c>.</summary>
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
        if (!File.Exists(FontPath)) OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is copied beside the test assembly by the project file.");

        var own = WidthsOf(Build(viaForeign: false, signing));
        var viaForeign = WidthsOf(Build(viaForeign: true, signing));

        Assert.NotEqual(string.Empty, own);
        Assert.Equal(own, viaForeign);
    }

    private static byte[] SaveOrPrepare(PdfDocument doc, bool signing)
    {
        if (!signing)
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        return doc.PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });
    }

    /// <summary>A foreign handle registered before the saving document embeds its own font saves, with the same widths as the other order: whether the name is defined is decided at save, as in 2.3.3.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignHandleRegisteredBeforeTheOwnFont_savesLikeTheOtherOrder(bool signing)
    {
        if (!File.Exists(FontPath)) OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is copied beside the test assembly by the project file.");
        var font = File.ReadAllBytes(FontPath);

        using var owner = new PdfDocument();
        var foreign = owner.UseTrueTypeFont(font);
        using var early = new PdfDocument();
        var page = early.AddPage();
        foreign.GetGlyphIds("Hello", new ushort[5]);
        early.RegisterEmbeddedFontUsage(page, foreign);       // before early.UseTrueTypeFont
        _ = early.UseTrueTypeFont(font);

        var late = WidthsOf(SaveOrPrepare(early, signing));

        Assert.NotEqual(string.Empty, late);
        Assert.Equal(WidthsOf(Build(viaForeign: true, signing)), late);
    }

    /// <summary>
    /// A foreign handle whose name this document never defines is refused by the save, not by the
    /// registration, with <see cref="ArgumentException"/> naming <c>handle</c> and before the document
    /// is marked written: defining the font afterwards lets the same document save.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignHandleWithNoOwnFont_isRefusedBySaveNotByTheRegistration(bool signing)
    {
        if (!File.Exists(FontPath)) OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is copied beside the test assembly by the project file.");
        var font = File.ReadAllBytes(FontPath);

        using var owner = new PdfDocument();
        var foreign = owner.UseTrueTypeFont(font);
        using var other = new PdfDocument();
        var page = other.AddPage();
        other.RegisterEmbeddedFontUsage(page, foreign);       // accepted

        var ex = Assert.Throws<ArgumentException>(() => SaveOrPrepare(other, signing));
        Assert.Equal("handle", ex.ParamName);
        Assert.Contains("belongs to a different document", ex.Message, StringComparison.Ordinal);

        _ = other.UseTrueTypeFont(font);                       // now the name is defined: the same document saves
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(SaveOrPrepare(other, signing)), StringComparison.Ordinal);
    }
}
