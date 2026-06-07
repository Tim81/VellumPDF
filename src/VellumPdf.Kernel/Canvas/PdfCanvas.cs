// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;

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

    public PdfCanvas(PdfPage page) => _page = page;

    // ── Graphics state ──────────────────────────────────────────────────────

    public PdfCanvas SaveState() { WriteOp("q"u8); return this; }
    public PdfCanvas RestoreState() { WriteOp("Q"u8); return this; }

    public PdfCanvas Concat(double a, double b, double c, double d, double e, double f)
    { WriteOpAscii($"{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm"); return this; }

    public PdfCanvas SetLineWidth(double w) { WriteOpAscii($"{N(w)} w"); return this; }
    public PdfCanvas SetLineCap(int cap) { WriteOpAscii($"{cap} J"); return this; }
    public PdfCanvas SetLineJoin(int join) { WriteOpAscii($"{join} j"); return this; }
    public PdfCanvas SetMiterLimit(double m) { WriteOpAscii($"{N(m)} M"); return this; }

    public PdfCanvas SetStrokeColorRgb(double r, double g, double b)
    { WriteOpAscii($"{N(r)} {N(g)} {N(b)} RG"); return this; }

    public PdfCanvas SetFillColorRgb(double r, double g, double b)
    { WriteOpAscii($"{N(r)} {N(g)} {N(b)} rg"); return this; }

    public PdfCanvas SetStrokeColorGray(double g) { WriteOpAscii($"{N(g)} G"); return this; }
    public PdfCanvas SetFillColorGray(double g) { WriteOpAscii($"{N(g)} g"); return this; }

    // ── Path construction ───────────────────────────────────────────────────

    public PdfCanvas MoveTo(double x, double y) { WriteOpAscii($"{N(x)} {N(y)} m"); return this; }
    public PdfCanvas LineTo(double x, double y) { WriteOpAscii($"{N(x)} {N(y)} l"); return this; }

    public PdfCanvas CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    { WriteOpAscii($"{N(x1)} {N(y1)} {N(x2)} {N(y2)} {N(x3)} {N(y3)} c"); return this; }

    public PdfCanvas Rectangle(double x, double y, double w, double h)
    { WriteOpAscii($"{N(x)} {N(y)} {N(w)} {N(h)} re"); return this; }

    public PdfCanvas ClosePath() { WriteOp("h"u8); return this; }
    public PdfCanvas Stroke() { WriteOp("S"u8); return this; }
    public PdfCanvas Fill() { WriteOp("f"u8); return this; }
    public PdfCanvas FillEvenOdd() { WriteOp("f*"u8); return this; }
    public PdfCanvas FillAndStroke() { WriteOp("B"u8); return this; }
    public PdfCanvas CloseAndStroke() { WriteOp("s"u8); return this; }
    public PdfCanvas EndPath() { WriteOp("n"u8); return this; }

    // ── Text operators ──────────────────────────────────────────────────────

    public PdfCanvas BeginText() { WriteOp("BT"u8); return this; }
    public PdfCanvas EndText() { WriteOp("ET"u8); return this; }

    public PdfCanvas SetFont(PdfFontResource font, double size)
    {
        _usedFonts[font.ResourceName] = font;
        WriteOpAscii($"/{font.ResourceName} {N(size)} Tf");
        return this;
    }

    public PdfCanvas SetTextMatrix(double a, double b, double c, double d, double e, double f)
    { WriteOpAscii($"{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} Tm"); return this; }

    public PdfCanvas MoveTextPosition(double tx, double ty)
    { WriteOpAscii($"{N(tx)} {N(ty)} Td"); return this; }

    public PdfCanvas SetCharSpacing(double cs) { WriteOpAscii($"{N(cs)} Tc"); return this; }
    public PdfCanvas SetWordSpacing(double ws) { WriteOpAscii($"{N(ws)} Tw"); return this; }
    public PdfCanvas SetTextRise(double tr) { WriteOpAscii($"{N(tr)} Ts"); return this; }
    public PdfCanvas SetHorizScaling(double s) { WriteOpAscii($"{N(s)} Tz"); return this; }
    public PdfCanvas SetTextLeading(double tl) { WriteOpAscii($"{N(tl)} TL"); return this; }
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

    public PdfCanvas EndMarkedContent() { WriteOp("EMC"u8); return this; }

    // ── XObject ─────────────────────────────────────────────────────────────

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
