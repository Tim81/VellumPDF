// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

public sealed class PdfObjectTests
{
    private static string Serialize(PdfObject obj)
    {
        var ms = new MemoryStream();
        obj.WriteTo(new PdfWriter(ms));
        return System.Text.Encoding.ASCII.GetString(ms.ToArray());
    }

    [Fact] public void Null_serializes()    => Assert.Equal("null",  Serialize(PdfNull.Instance));
    [Fact] public void True_serializes()    => Assert.Equal("true",  Serialize(PdfBoolean.True));
    [Fact] public void False_serializes()   => Assert.Equal("false", Serialize(PdfBoolean.False));

    [Theory]
    [InlineData(0,     "0")]
    [InlineData(42,    "42")]
    [InlineData(-7,    "-7")]
    [InlineData(1000,  "1000")]
    public void Integer_serializes(long value, string expected) =>
        Assert.Equal(expected, Serialize(new PdfInteger(value)));

    [Theory]
    [InlineData(0.0,   "0")]
    [InlineData(1.5,   "1.5")]
    [InlineData(-3.14, "-3.14")]
    [InlineData(595.28,"595.28")]
    public void Real_serializes(double value, string expected) =>
        Assert.Equal(expected, Serialize(new PdfReal(value)));

    [Theory]
    [InlineData("Page",    "/Page")]
    [InlineData("FlateDecode", "/FlateDecode")]
    public void Name_serializes(string value, string expected) =>
        Assert.Equal(expected, Serialize(new PdfName(value)));

    [Fact]
    public void Name_escapesSpecialChars()
    {
        // '#' must be escaped as #23
        var name = new PdfName("A#B");
        Assert.Equal("/A#23B", Serialize(name));
    }

    [Fact]
    public void HexString_serializes()
    {
        var hs = new PdfHexString(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Assert.Equal("<DEADBEEF>", Serialize(hs));
    }

    [Fact]
    public void LiteralString_escapesParens()
    {
        var ls = new PdfLiteralString(System.Text.Encoding.ASCII.GetBytes("hi (there)"));
        Assert.Equal(@"(hi \(there\))", Serialize(ls));
    }

    [Fact]
    public void Array_serializes()
    {
        var arr = new PdfArray([new PdfInteger(1), new PdfInteger(2), new PdfInteger(3)]);
        Assert.Equal("[1 2 3]", Serialize(arr));
    }

    [Fact]
    public void Dictionary_serializes()
    {
        var dict = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Page);
        var result = Serialize(dict);
        Assert.Contains("/Type", result);
        Assert.Contains("/Page", result);
        Assert.StartsWith("<<", result);
        Assert.EndsWith(">>", result);
    }

    [Fact]
    public void IndirectRef_serializes()
    {
        var r = new PdfIndirectReference(5);
        Assert.Equal("5 0 R", Serialize(r));
    }

    [Fact]
    public void IndirectObject_serializes()
    {
        var obj = new PdfIndirectObject(3, new PdfInteger(42));
        var result = Serialize(obj);
        Assert.StartsWith("3 0 obj", result);
        Assert.Contains("42", result);
        Assert.EndsWith("endobj", result);
    }
}
