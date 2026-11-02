// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #532: a list indent of <c>NaN</c> or negative infinity reached an item's text matrix as a token
/// that is not a PDF number, on a nested child always and on a flat item whose longest word will not
/// fit beside its marker. Each value is listed alone, on each path, and every case asserts the
/// text-matrix operand rather than that a file came back.
/// </summary>
public sealed class ListIndentNonFiniteTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private static Document Page() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 300),
        Margins = new EdgeInsets(10),
    };

    private static string Stream(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    private static ListElement Build(string shape, double indent)
    {
        var list = new ListElement(ListStyle.Unordered) { Indent = indent, DefaultStyle = Style() };
        switch (shape)
        {
            case "flat-short": list.Add("short"); break;
            case "flat-longword": list.Add(new string('W', 40)); break;
            default: list.Add(new ListItem("parent").AddChild("child")); break;
        }
        return list;
    }

    public static TheoryData<string, double, string> Rows()
    {
        var rows = new TheoryData<string, double, string>();
        foreach (var shape in new[] { "flat-short", "flat-longword", "nested" })
        {
            rows.Add(shape, double.NaN, "NaN");
            rows.Add(shape, double.NegativeInfinity, "negative infinity");
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Indent_NaNOrNegativeInfinity_isRefusedOnEveryShape(string shape, double indent, string rendered)
    {
        using var doc = Page();
        doc.Add(Build(shape, indent));

        var ex = Assert.Throws<InvalidOperationException>(() => Stream(doc));

        Assert.Equal(
            $"A list has an indent of {rendered}, which cannot be laid out. An indent must be a finite number.",
            ex.Message);
    }

    /// <summary>The finite neighbours keep rendering, and no text matrix ever carries a bad token.</summary>
    [Theory]
    [InlineData("flat-short", -50.0)]
    [InlineData("flat-longword", -50.0)]
    [InlineData("nested", -50.0)]
    [InlineData("nested", 20.0)]
    public void Indent_finite_stillWritesNumericTextMatrices(string shape, double indent)
    {
        using var doc = Page();
        doc.Add(Build(shape, indent));

        var stream = Stream(doc);

        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"1 0 0 1 -?\d+(\.\d+)? -?\d+(\.\d+)? Tm"), stream);
    }

    /// <summary>
    /// Positive infinity is not part of this fix: it writes no invalid token, and a nested list
    /// already throws at the content-width boundary. It is pinned so the boundary stays visible.
    /// </summary>
    [Fact]
    public void Indent_positiveInfinity_nested_stillThrowsTheTooTallMessage()
    {
        using var doc = Page();
        doc.Add(Build("nested", double.PositiveInfinity));

        var ex = Assert.Throws<InvalidOperationException>(() => Stream(doc));

        Assert.StartsWith("An element is too tall to fit on a single page", ex.Message, StringComparison.Ordinal);
    }
}
