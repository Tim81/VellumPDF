// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Graphics;

namespace VellumPdf.Canvas;

/// <summary>
/// Low-level PDF content stream builder. Emits PDF operators for graphics
/// state, text, paths, and marked content (PDF/UA tagging seam).
///
/// Coordinates are in PDF user-space (points, Y-up). The layout engine
/// performs the Y-down→Y-up flip at a single boundary before calling here.
/// </summary>
public sealed class PdfCanvas
{
    private readonly MemoryStream _ops = new();
    private readonly PdfPage _page;
    private readonly Dictionary<string, PdfFontResource> _usedFonts = new();
    private int _mcidCounter;

    // ExtGState dedup: key encodes the parameters, value is the resource name.
    // Keys use format "ca:0.5" or "CA:0.5" or "ca:0.5;CA:0.75".
    private readonly Dictionary<string, string> _extGStateIndex = new(StringComparer.Ordinal);
    private int _extGStateCounter;

    // Shading dedup: key is the canonical descriptor string, value is resource name.
    private readonly Dictionary<string, string> _shadingIndex = new(StringComparer.Ordinal);
    private int _shadingCounter;

    /// <summary>Creates a content stream builder targeting the given page.</summary>
    public PdfCanvas(PdfPage page) => _page = page;

    // ── Graphics state ──────────────────────────────────────────────────────

    /// <summary>Pushes the current graphics state onto the stack. Emits <c>q</c>.</summary>
    public PdfCanvas SaveState() { WriteOp("q"u8); return this; }
    /// <summary>Pops the graphics state off the stack, restoring the previous state. Emits <c>Q</c>.</summary>
    public PdfCanvas RestoreState() { WriteOp("Q"u8); return this; }

    /// <summary>Concatenates the matrix [a b c d e f] onto the current transformation matrix. Emits <c>cm</c>.</summary>
    public PdfCanvas Concat(double a, double b, double c, double d, double e, double f)
    { WriteOpAscii($"{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm"); return this; }

    /// <summary>Sets the line width for stroking. Emits <c>w</c>.</summary>
    public PdfCanvas SetLineWidth(double w) { WriteOpAscii($"{N(w)} w"); return this; }
    /// <summary>Sets the line cap style (0 butt, 1 round, 2 square). Emits <c>J</c>.</summary>
    public PdfCanvas SetLineCap(int cap) { WriteOpAscii($"{cap} J"); return this; }
    /// <summary>Sets the line join style (0 miter, 1 round, 2 bevel). Emits <c>j</c>.</summary>
    public PdfCanvas SetLineJoin(int join) { WriteOpAscii($"{join} j"); return this; }
    /// <summary>Sets the miter limit for mitered line joins. Emits <c>M</c>.</summary>
    public PdfCanvas SetMiterLimit(double m) { WriteOpAscii($"{N(m)} M"); return this; }

    /// <summary>Sets the stroke colour in DeviceRGB (each component in [0, 1]). Emits <c>RG</c>.</summary>
    public PdfCanvas SetStrokeColorRgb(double r, double g, double b)
    { WriteOpAscii($"{N(r)} {N(g)} {N(b)} RG"); return this; }

    /// <summary>Sets the fill colour in DeviceRGB (each component in [0, 1]). Emits <c>rg</c>.</summary>
    public PdfCanvas SetFillColorRgb(double r, double g, double b)
    { WriteOpAscii($"{N(r)} {N(g)} {N(b)} rg"); return this; }

    /// <summary>Sets the stroke colour in DeviceGray (in [0, 1]). Emits <c>G</c>.</summary>
    public PdfCanvas SetStrokeColorGray(double g) { WriteOpAscii($"{N(g)} G"); return this; }
    /// <summary>Sets the fill colour in DeviceGray (in [0, 1]). Emits <c>g</c>.</summary>
    public PdfCanvas SetFillColorGray(double g) { WriteOpAscii($"{N(g)} g"); return this; }

    // ── Feature 4: CMYK colour ──────────────────────────────────────────────

    /// <summary>
    /// Sets the fill colour to the given CMYK values (each in [0, 1]).
    /// Emits the <c>k</c> operator.
    /// </summary>
    public PdfCanvas SetFillColorCmyk(double c, double m, double y, double k)
    { WriteOpAscii($"{N(c)} {N(m)} {N(y)} {N(k)} k"); return this; }

    /// <summary>
    /// Sets the stroke colour to the given CMYK values (each in [0, 1]).
    /// Emits the <c>K</c> operator.
    /// </summary>
    public PdfCanvas SetStrokeColorCmyk(double c, double m, double y, double k)
    { WriteOpAscii($"{N(c)} {N(m)} {N(y)} {N(k)} K"); return this; }

    // ── Feature 3: Dash patterns ────────────────────────────────────────────

    /// <summary>
    /// Sets the line dash pattern.
    /// Emits <c>[a b …] phase d</c>.
    /// Pass an empty span to set a solid line.
    /// </summary>
    public PdfCanvas SetLineDash(ReadOnlySpan<double> pattern, double phase)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(N(pattern[i]));
        }
        sb.Append("] ");
        sb.Append(N(phase));
        sb.Append(" d");
        WriteOpAscii(sb.ToString());
        return this;
    }

    /// <summary>
    /// Resets the line to solid (no dash).
    /// Emits <c>[] 0 d</c>.
    /// </summary>
    public PdfCanvas SetSolidLine() { WriteOp("[] 0 d"u8); return this; }

    // ── Feature 1: Transparency / ExtGState ────────────────────────────────

    /// <summary>
    /// Sets the fill (non-stroking) opacity.
    /// Registers an inline <c>/ExtGState</c> resource entry
    /// <c>&lt;&lt; /ca <paramref name="alpha"/> &gt;&gt;</c> and emits
    /// <c>/GSn gs</c>. Values are deduplicated by alpha value so repeated
    /// calls with the same alpha reuse a single resource entry.
    /// </summary>
    public PdfCanvas SetFillAlpha(double alpha)
    {
        var gsName = GetOrRegisterExtGState($"ca:{N(alpha)}", fillAlpha: alpha, strokeAlpha: null);
        WriteOpAscii($"/{gsName} gs");
        return this;
    }

    /// <summary>
    /// Sets the stroke opacity.
    /// Registers an inline <c>/ExtGState</c> resource entry
    /// <c>&lt;&lt; /CA <paramref name="alpha"/> &gt;&gt;</c> and emits
    /// <c>/GSn gs</c>.
    /// </summary>
    public PdfCanvas SetStrokeAlpha(double alpha)
    {
        var gsName = GetOrRegisterExtGState($"CA:{N(alpha)}", fillAlpha: null, strokeAlpha: alpha);
        WriteOpAscii($"/{gsName} gs");
        return this;
    }

    private string GetOrRegisterExtGState(string key, double? fillAlpha, double? strokeAlpha)
    {
        if (_extGStateIndex.TryGetValue(key, out var existing))
            return existing;

        var name = $"GS{++_extGStateCounter}";
        _extGStateIndex[key] = name;

        var dict = new PdfDictionary();
        if (fillAlpha.HasValue)
            dict.Set(new PdfName("ca"), new PdfReal(fillAlpha.Value));
        if (strokeAlpha.HasValue)
            dict.Set(new PdfName("CA"), new PdfReal(strokeAlpha.Value));

        _page.RegisterExtGState(name, dict);
        return name;
    }

    // ── Feature 2: Clipping ─────────────────────────────────────────────────

    /// <summary>
    /// Intersects the current path with the clipping path using the non-zero
    /// winding rule. Emits the <c>W</c> operator.
    ///
    /// <para>
    /// Per PDF spec (ISO 32000-2 §8.5.4), <c>W</c> must be paired with a path-painting
    /// or <c>n</c> operator on the same path. Typical usage:
    /// <code>
    /// canvas.Rectangle(...).Clip().EndPath(); // establish clip, discard path
    /// canvas.PaintAxialGradient(...);          // paint within clip
    /// </code>
    /// </para>
    /// </summary>
    public PdfCanvas Clip() { WriteOp("W"u8); return this; }

    /// <summary>
    /// Intersects the current path with the clipping path using the even-odd rule.
    /// Emits the <c>W*</c> operator.
    ///
    /// <para>See <see cref="Clip"/> for usage notes.</para>
    /// </summary>
    public PdfCanvas ClipEvenOdd() { WriteOp("W*"u8); return this; }

    // ── Path construction ───────────────────────────────────────────────────

    /// <summary>Begins a new subpath at (<paramref name="x"/>, <paramref name="y"/>). Emits <c>m</c>.</summary>
    public PdfCanvas MoveTo(double x, double y) { WriteOpAscii($"{N(x)} {N(y)} m"); return this; }
    /// <summary>Appends a straight line segment to (<paramref name="x"/>, <paramref name="y"/>). Emits <c>l</c>.</summary>
    public PdfCanvas LineTo(double x, double y) { WriteOpAscii($"{N(x)} {N(y)} l"); return this; }

    /// <summary>Appends a cubic Bézier curve with control points (x1,y1) and (x2,y2) ending at (x3,y3). Emits <c>c</c>.</summary>
    public PdfCanvas CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    { WriteOpAscii($"{N(x1)} {N(y1)} {N(x2)} {N(y2)} {N(x3)} {N(y3)} c"); return this; }

    /// <summary>Appends a rectangle with lower-left corner (<paramref name="x"/>, <paramref name="y"/>), width <paramref name="w"/> and height <paramref name="h"/> as a complete subpath. Emits <c>re</c>.</summary>
    public PdfCanvas Rectangle(double x, double y, double w, double h)
    { WriteOpAscii($"{N(x)} {N(y)} {N(w)} {N(h)} re"); return this; }

    /// <summary>Closes the current subpath with a line back to its start point. Emits <c>h</c>.</summary>
    public PdfCanvas ClosePath() { WriteOp("h"u8); return this; }
    /// <summary>Strokes the current path. Emits <c>S</c>.</summary>
    public PdfCanvas Stroke() { WriteOp("S"u8); return this; }
    /// <summary>Fills the current path using the non-zero winding rule. Emits <c>f</c>.</summary>
    public PdfCanvas Fill() { WriteOp("f"u8); return this; }
    /// <summary>Fills the current path using the even-odd rule. Emits <c>f*</c>.</summary>
    public PdfCanvas FillEvenOdd() { WriteOp("f*"u8); return this; }
    /// <summary>Fills (non-zero winding) then strokes the current path. Emits <c>B</c>.</summary>
    public PdfCanvas FillAndStroke() { WriteOp("B"u8); return this; }
    /// <summary>Closes then strokes the current subpath. Emits <c>s</c>.</summary>
    public PdfCanvas CloseAndStroke() { WriteOp("s"u8); return this; }
    /// <summary>Ends the current path without filling or stroking (no-op painting). Emits <c>n</c>.</summary>
    public PdfCanvas EndPath() { WriteOp("n"u8); return this; }

    // ── Feature 5: Axial and radial shadings ────────────────────────────────

    /// <summary>
    /// Paints an axial (linear) gradient from point (<paramref name="x0"/>, <paramref name="y0"/>)
    /// to (<paramref name="x1"/>, <paramref name="y1"/>), blending from colour <paramref name="c0"/>
    /// to <paramref name="c1"/>. Registers an inline <c>/Shading</c> resource and emits <c>sh</c>.
    ///
    /// <para>
    /// Typical usage to paint within a clipped region:
    /// <code>
    /// canvas.SaveState();
    /// canvas.Rectangle(x, y, w, h).Clip().EndPath();
    /// canvas.PaintAxialGradient(x, y, x + w, y, c0, c1);
    /// canvas.RestoreState();
    /// </code>
    /// </para>
    /// </summary>
    public PdfCanvas PaintAxialGradient(
        double x0, double y0, double x1, double y1,
        KernelColor c0, KernelColor c1)
    {
        var key = $"axial:{N(x0)},{N(y0)},{N(x1)},{N(y1)};c0:{N(c0.R)},{N(c0.G)},{N(c0.B)};c1:{N(c1.R)},{N(c1.G)},{N(c1.B)}";
        var shName = GetOrRegisterAxialShading(key, x0, y0, x1, y1, c0, c1);
        WriteOpAscii($"/{shName} sh");
        return this;
    }

    /// <summary>
    /// Paints a radial (circular) gradient between two circles.
    /// Circle 0: centre (<paramref name="x0"/>, <paramref name="y0"/>), radius <paramref name="r0"/>.
    /// Circle 1: centre (<paramref name="x1"/>, <paramref name="y1"/>), radius <paramref name="r1"/>.
    /// Colour blends from <paramref name="c0"/> to <paramref name="c1"/>.
    /// Registers an inline <c>/Shading</c> resource and emits <c>sh</c>.
    /// </summary>
    public PdfCanvas PaintRadialGradient(
        double x0, double y0, double r0,
        double x1, double y1, double r1,
        KernelColor c0, KernelColor c1)
    {
        var key = $"radial:{N(x0)},{N(y0)},{N(r0)},{N(x1)},{N(y1)},{N(r1)};c0:{N(c0.R)},{N(c0.G)},{N(c0.B)};c1:{N(c1.R)},{N(c1.G)},{N(c1.B)}";
        var shName = GetOrRegisterRadialShading(key, x0, y0, r0, x1, y1, r1, c0, c1);
        WriteOpAscii($"/{shName} sh");
        return this;
    }

    private string GetOrRegisterAxialShading(
        string key, double x0, double y0, double x1, double y1,
        KernelColor c0, KernelColor c1)
    {
        if (_shadingIndex.TryGetValue(key, out var existing))
            return existing;

        var name = $"Sh{++_shadingCounter}";
        _shadingIndex[key] = name;

        var fn = BuildType2Function(c0, c1);
        var coords = new PdfArray([
            new PdfReal(x0), new PdfReal(y0),
            new PdfReal(x1), new PdfReal(y1),
        ]);
        var shading = new PdfDictionary()
            .Set(PdfName.ShadingType, new PdfInteger(2))
            .Set(PdfName.ColorSpace, PdfName.DeviceRGB)
            .Set(PdfName.Coords, coords)
            .Set(PdfName.Function, fn)
            .Set(PdfName.Extend, new PdfArray([PdfBoolean.True, PdfBoolean.True]));

        _page.RegisterShading(name, shading);
        return name;
    }

    private string GetOrRegisterRadialShading(
        string key, double x0, double y0, double r0,
        double x1, double y1, double r1,
        KernelColor c0, KernelColor c1)
    {
        if (_shadingIndex.TryGetValue(key, out var existing))
            return existing;

        var name = $"Sh{++_shadingCounter}";
        _shadingIndex[key] = name;

        var fn = BuildType2Function(c0, c1);
        var coords = new PdfArray([
            new PdfReal(x0), new PdfReal(y0), new PdfReal(r0),
            new PdfReal(x1), new PdfReal(y1), new PdfReal(r1),
        ]);
        var shading = new PdfDictionary()
            .Set(PdfName.ShadingType, new PdfInteger(3))
            .Set(PdfName.ColorSpace, PdfName.DeviceRGB)
            .Set(PdfName.Coords, coords)
            .Set(PdfName.Function, fn)
            .Set(PdfName.Extend, new PdfArray([PdfBoolean.True, PdfBoolean.True]));

        _page.RegisterShading(name, shading);
        return name;
    }

    /// <summary>
    /// Builds a Type 2 (exponential interpolation) function with
    /// /Domain [0 1], /N 1, /C0 and /C1 from the two colours.
    /// </summary>
    private static PdfDictionary BuildType2Function(KernelColor c0, KernelColor c1)
    {
        var domain = new PdfArray([new PdfReal(0), new PdfReal(1)]);
        var c0Array = new PdfArray([new PdfReal(c0.R), new PdfReal(c0.G), new PdfReal(c0.B)]);
        var c1Array = new PdfArray([new PdfReal(c1.R), new PdfReal(c1.G), new PdfReal(c1.B)]);
        return new PdfDictionary()
            .Set(PdfName.FunctionType, new PdfInteger(2))
            .Set(PdfName.Domain, domain)
            .Set(PdfName.C0, c0Array)
            .Set(PdfName.C1, c1Array)
            .Set(PdfName.N, new PdfInteger(1));
    }

    // ── Text operators ──────────────────────────────────────────────────────

    /// <summary>Begins a text object. Emits <c>BT</c>.</summary>
    public PdfCanvas BeginText() { WriteOp("BT"u8); return this; }
    /// <summary>Ends the current text object. Emits <c>ET</c>.</summary>
    public PdfCanvas EndText() { WriteOp("ET"u8); return this; }

    /// <summary>Selects the given font resource at the specified size, registering it for inclusion in the page resources. Emits <c>Tf</c>.</summary>
    public PdfCanvas SetFont(PdfFontResource font, double size)
    {
        _usedFonts[font.ResourceName] = font;
        WriteOpAscii($"/{font.ResourceName} {N(size)} Tf");
        return this;
    }

    /// <summary>
    /// Selects an embedded TrueType font by resource name and size.
    /// The resource must already be registered on the page (via <c>RegisterFontRef</c>)
    /// before the content stream is consumed.
    /// </summary>
    public PdfCanvas SetFontByName(string resourceName, double size)
    {
        WriteOpAscii($"/{resourceName} {N(size)} Tf");
        return this;
    }

    /// <summary>
    /// Emits a hex-encoded glyph-id run using the PDF <c>Tj</c> operator.
    /// Each glyph id is encoded as 2 bytes big-endian (Identity-H CIDFont encoding).
    /// </summary>
    public PdfCanvas ShowGlyphs(ReadOnlySpan<ushort> glyphIds)
    {
        _ops.WriteByte((byte)'<');
        foreach (var gid in glyphIds)
        {
            _ops.WriteByte(HexNibble(gid >> 12));
            _ops.WriteByte(HexNibble((gid >> 8) & 0xF));
            _ops.WriteByte(HexNibble((gid >> 4) & 0xF));
            _ops.WriteByte(HexNibble(gid & 0xF));
        }
        _ops.Write("> Tj\n"u8);
        return this;
    }

    private static byte HexNibble(int v) => (byte)(v < 10 ? '0' + v : 'A' + v - 10);

    /// <summary>Sets the text matrix and text line matrix to [a b c d e f]. Emits <c>Tm</c>.</summary>
    public PdfCanvas SetTextMatrix(double a, double b, double c, double d, double e, double f)
    { WriteOpAscii($"{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} Tm"); return this; }

    /// <summary>Moves to the start of the next line, offset by (<paramref name="tx"/>, <paramref name="ty"/>) from the current line start. Emits <c>Td</c>.</summary>
    public PdfCanvas MoveTextPosition(double tx, double ty)
    { WriteOpAscii($"{N(tx)} {N(ty)} Td"); return this; }

    /// <summary>Sets the character spacing. Emits <c>Tc</c>.</summary>
    public PdfCanvas SetCharSpacing(double cs) { WriteOpAscii($"{N(cs)} Tc"); return this; }
    /// <summary>Sets the word spacing. Emits <c>Tw</c>.</summary>
    public PdfCanvas SetWordSpacing(double ws) { WriteOpAscii($"{N(ws)} Tw"); return this; }
    /// <summary>Sets the text rise (vertical baseline shift). Emits <c>Ts</c>.</summary>
    public PdfCanvas SetTextRise(double tr) { WriteOpAscii($"{N(tr)} Ts"); return this; }
    /// <summary>Sets the horizontal text scaling as a percentage. Emits <c>Tz</c>.</summary>
    public PdfCanvas SetHorizScaling(double s) { WriteOpAscii($"{N(s)} Tz"); return this; }
    /// <summary>Sets the text leading (line spacing) used by <see cref="NextLine"/>. Emits <c>TL</c>.</summary>
    public PdfCanvas SetTextLeading(double tl) { WriteOpAscii($"{N(tl)} TL"); return this; }
    /// <summary>Moves to the start of the next text line using the current leading. Emits <c>T*</c>.</summary>
    public PdfCanvas NextLine() { WriteOp("T*"u8); return this; }

    /// <summary>Renders a Latin-1 string using the standard PDF string operator (Tj).</summary>
    public PdfCanvas ShowText(string text)
    {
        WritePdfString(Encoding.Latin1.GetBytes(text));
        _ops.Write(" Tj\n"u8);
        return this;
    }

    // ── Marked content (PDF/UA tagging seam) ───────────────────────────────

    /// <summary>
    /// Opens a BDC marked-content sequence with an inline MCID property dict.
    /// Returns the MCID so callers can register a structure element.
    /// When tagging is disabled this can still be called harmlessly.
    /// </summary>
    public int BeginMarkedContent(string tag)
    {
        var mcid = _mcidCounter++;
        // /<tag> <</MCID mcid>> BDC
        _ops.Write(Encoding.ASCII.GetBytes($"/{tag} <</MCID {mcid}>> BDC\n"));
        return mcid;
    }

    /// <summary>Closes the current marked-content sequence. Emits <c>EMC</c>.</summary>
    public PdfCanvas EndMarkedContent() { WriteOp("EMC"u8); return this; }

    /// <summary>Opens an /Artifact marked-content sequence (no MCID; not part of the
    /// logical structure). Close it with <see cref="EndMarkedContent"/>. Use for purely
    /// decorative content (rules, table borders/fills) so tagged/PDF-UA output has no
    /// untagged "real" content.</summary>
    public void BeginArtifactMarkedContent()
    {
        WriteOp("/Artifact BMC"u8);
    }

    // ── XObject ─────────────────────────────────────────────────────────────

    /// <summary>Paints the named XObject (image or form) from the page resources. Emits <c>Do</c>.</summary>
    public PdfCanvas DoXObject(string resourceName)
    { WriteOpAscii($"/{resourceName} Do"); return this; }

    // ── Finalise ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers used fonts inline into the page's resource dictionary and
    /// stores the accumulated operator bytes as the page content.
    /// Call exactly once when the page is complete.
    /// </summary>
    public void Finish()
    {
        foreach (var (name, font) in _usedFonts)
            _page.RegisterFont(name, font.BuildDictionary());
        _page.ContentBytes = _ops.ToArray();
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private void WriteOp(ReadOnlySpan<byte> op)
    {
        _ops.Write(op);
        _ops.WriteByte((byte)'\n');
    }

    private void WriteOpAscii(string line)
    {
        _ops.Write(Encoding.ASCII.GetBytes(line));
        _ops.WriteByte((byte)'\n');
    }

    private void WritePdfString(byte[] bytes)
    {
        _ops.WriteByte((byte)'(');
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(': _ops.WriteByte((byte)'\\'); _ops.WriteByte((byte)'('); break;
                case (byte)')': _ops.WriteByte((byte)'\\'); _ops.WriteByte((byte)')'); break;
                case (byte)'\\': _ops.WriteByte((byte)'\\'); _ops.WriteByte((byte)'\\'); break;
                case 0x0A: _ops.WriteByte((byte)'\\'); _ops.WriteByte((byte)'n'); break;
                case 0x0D: _ops.WriteByte((byte)'\\'); _ops.WriteByte((byte)'r'); break;
                default: _ops.WriteByte(b); break;
            }
        }
        _ops.WriteByte((byte)')');
    }
}
