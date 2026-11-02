// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #519: every number <see cref="PdfCanvas"/> writes goes through one formatter, and that formatter
/// refuses NaN and both infinities, as <c>PdfReal</c> does. A token such as <c>NaN</c> is not a PDF
/// number (ISO 32000-2 7.3.3), so a stream carrying one is not a valid content stream.
/// </summary>
public sealed class PdfCanvasNonFiniteTests
{
    private delegate void Invoke(PdfCanvas canvas, double[] a);

    private sealed record OperatorCase(string Operator, int Operands, Invoke Call);

    private static readonly OperatorCase[] _cases =
    [
        new("cm", 6, (c, a) => c.Concat(a[0], a[1], a[2], a[3], a[4], a[5])),
        new("w", 1, (c, a) => c.SetLineWidth(a[0])),
        new("M", 1, (c, a) => c.SetMiterLimit(a[0])),
        new("RG", 3, (c, a) => c.SetStrokeColorRgb(a[0], a[1], a[2])),
        new("rg", 3, (c, a) => c.SetFillColorRgb(a[0], a[1], a[2])),
        new("G", 1, (c, a) => c.SetStrokeColorGray(a[0])),
        new("g", 1, (c, a) => c.SetFillColorGray(a[0])),
        new("k", 4, (c, a) => c.SetFillColorCmyk(a[0], a[1], a[2], a[3])),
        new("K", 4, (c, a) => c.SetStrokeColorCmyk(a[0], a[1], a[2], a[3])),
        new("sc", 3, (c, a) => c.SetFillColor(a[0], a[1], a[2])),
        new("SC", 3, (c, a) => c.SetStrokeColor(a[0], a[1], a[2])),
        new("d", 3, (c, a) => c.SetLineDash([a[0], a[1]], a[2])),
        new("m", 2, (c, a) => c.MoveTo(a[0], a[1])),
        new("l", 2, (c, a) => c.LineTo(a[0], a[1])),
        new("c", 6, (c, a) => c.CurveTo(a[0], a[1], a[2], a[3], a[4], a[5])),
        new("re", 4, (c, a) => c.Rectangle(a[0], a[1], a[2], a[3])),
        new("Tf", 1, (c, a) => c.SetFontByName("F1", a[0])),
        new("Tm", 6, (c, a) => c.SetTextMatrix(a[0], a[1], a[2], a[3], a[4], a[5])),
        new("Td", 2, (c, a) => c.MoveTextPosition(a[0], a[1])),
        new("Tc", 1, (c, a) => c.SetCharSpacing(a[0])),
        new("Tw", 1, (c, a) => c.SetWordSpacing(a[0])),
        new("Ts", 1, (c, a) => c.SetTextRise(a[0])),
        new("Tz", 1, (c, a) => c.SetHorizScaling(a[0])),
        new("TL", 1, (c, a) => c.SetTextLeading(a[0])),
        new("gs fill", 1, (c, a) => c.SetFillAlpha(a[0])),
        new("gs stroke", 1, (c, a) => c.SetStrokeAlpha(a[0])),
        // AppendArc refuses a non-finite centre. A non-finite radius or angle is a sweep it
        // already skips, so those operands are not part of this theory.
        new("c (arc centre x)", 1, (c, a) => c.AppendArc(a[0], 0, 10, 0, 1.5)),
        new("c (arc centre y)", 1, (c, a) => c.AppendArc(0, a[0], 10, 0, 1.5)),
    ];

    public static TheoryData<string, int, string> Rows()
    {
        var rows = new TheoryData<string, int, string>();
        foreach (var c in _cases)
        {
            for (var position = 0; position < c.Operands; position++)
            {
                foreach (var value in new[] { "NaN", "+Infinity", "-Infinity" })
                    rows.Add(c.Operator, position, value);
            }
        }
        return rows;
    }

    private static double Parse(string value) => value switch
    {
        "NaN" => double.NaN,
        "+Infinity" => double.PositiveInfinity,
        _ => double.NegativeInfinity,
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Operator_with_a_non_finite_operand_is_refused_and_writes_nothing(string op, int position, string value)
    {
        var c = _cases.Single(x => x.Operator == op);
        var args = new double[c.Operands];
        Array.Fill(args, 1.0);
        args[position] = Parse(value);

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        var ex = Assert.Throws<ArgumentException>(() => c.Call(canvas, args));
        Assert.Contains("NaN or Infinity", ex.Message, StringComparison.Ordinal);

        canvas.Finish();
        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        Assert.DoesNotContain("NaN", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_operand_leaves_no_partial_operator_behind()
    {
        // SetFont writes the font name and then the size. The size has to be formatted first,
        // otherwise a refused size leaves "/F1" in the stream with no operator after it.
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);

        Assert.Throws<ArgumentException>(() => canvas.SetFont(font, double.NaN));
        Assert.Throws<ArgumentException>(() => canvas.SetFontByName("F1", double.PositiveInfinity));

        canvas.Finish();
        Assert.Empty(page.ContentBytes!);
    }

    [Fact]
    public void Finite_operands_are_still_written()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        canvas.SetFillColorRgb(1, 0.5, 0).SetLineWidth(2.5).MoveTo(10, 20);
        canvas.Finish();

        var content = Encoding.Latin1.GetString(page.ContentBytes!);
        Assert.Contains("1 0.5 0 rg", content, StringComparison.Ordinal);
        Assert.Contains("2.5 w", content, StringComparison.Ordinal);
        Assert.Contains("10 20 m", content, StringComparison.Ordinal);
    }
}
