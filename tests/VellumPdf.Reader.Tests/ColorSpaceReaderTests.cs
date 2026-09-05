// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <see cref="ColorSpaceReader"/> (#98): turning a raw <c>/ColorSpace</c> value into a
/// <see cref="PdfImageColorSpace"/>, one family at a time.
/// </summary>
public sealed class ColorSpaceReaderTests
{
    private sealed record Obj(int Num, string Dict, byte[]? Stream = null);

    private static byte[] BuildPdf(int rootObjectNumber, params Obj[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.7\n");

        var maxNum = objects.Max(o => o.Num);
        var offsets = new int?[maxNum + 1];
        foreach (var obj in objects.OrderBy(o => o.Num))
        {
            offsets[obj.Num] = (int)ms.Position;
            if (obj.Stream is null)
            {
                W($"{obj.Num} 0 obj\n{obj.Dict}\nendobj\n");
            }
            else
            {
                var trimmed = obj.Dict.TrimEnd();
                var withLength = trimmed[..^2].TrimEnd() + $" /Length {obj.Stream.Length} >>";
                W($"{obj.Num} 0 obj\n{withLength}\nstream\n");
                ms.Write(obj.Stream);
                W("\nendstream\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {maxNum + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= maxNum; i++)
        {
            W(offsets[i] is { } offset
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        }
        W($"trailer\n<< /Size {maxNum + 1} /Root {rootObjectNumber} 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static (PdfDocumentReader Reader, ColorSpaceReader ColorSpaceReader, DiagnosticSink Sink) Setup(
        byte[] pdfBytes)
    {
        var reader = PdfReader.Open(pdfBytes);
        var sink = new DiagnosticSink(cap: 100);
        var budget = new ImageCallBudget(reader.Limits.MaxDecodedBytes, sink);
        var ancillary = new AncillaryStreamCache();
        var csReader = new ColorSpaceReader(reader, ancillary, budget, reader.Limits);
        return (reader, csReader, sink);
    }

    private static PdfObject ParseValue(PdfDocumentReader reader, string text)
    {
        // A single-object indirect reference wraps arbitrary source text so the same lexer/parser
        // this reader already trusts, not a second hand-rolled one, produces the PdfObject.
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes(text));
        var parser = new PdfObjectParser(lexer);
        return parser.ParseObject();
    }

    [Fact]
    public void DeviceGray_direct()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "/DeviceGray"), null, sink, null, null, 0);
        Assert.NotNull(cs);
        Assert.Equal(PdfImageColorSpaceFamily.DeviceGray, cs!.Family);
        Assert.Equal(1, cs.ComponentCount);
    }

    [Fact]
    public void DeviceRgb_direct()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "/DeviceRGB"), null, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.DeviceRgb, cs!.Family);
        Assert.Equal(3, cs.ComponentCount);
    }

    [Fact]
    public void DeviceCmyk_direct()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "/DeviceCMYK"), null, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.DeviceCmyk, cs!.Family);
        Assert.Equal(4, cs.ComponentCount);
    }

    [Fact]
    public void Pattern_isRejected()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "/Pattern"), null, sink, null, null, 0);
        Assert.Null(cs);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
    }

    [Fact]
    public void CalGray_CalRgb_Lab_Separation()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);

        var calGray = csReader.Read(ParseValue(reader, "[/CalGray << /WhitePoint [1 1 1] >>]"), null, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.CalGray, calGray!.Family);
        Assert.Equal(1, calGray.ComponentCount);

        var calRgb = csReader.Read(ParseValue(reader, "[/CalRGB << /WhitePoint [1 1 1] >>]"), null, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.CalRgb, calRgb!.Family);
        Assert.Equal(3, calRgb.ComponentCount);

        var lab = csReader.Read(ParseValue(reader, "[/Lab << /WhitePoint [1 1 1] >>]"), null, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.Lab, lab!.Family);
        Assert.Equal(3, lab.ComponentCount);

        var separation = csReader.Read(ParseValue(reader, "[/Separation /Spot /DeviceGray 5 0 R]"), null, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.Separation, separation!.Family);
        Assert.Equal(1, separation.ComponentCount);
    }

    [Fact]
    public void DeviceN_64Names_succeeds_65Names_rejected()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);

        var names64 = string.Join(" ", Enumerable.Range(0, 64).Select(i => $"/C{i}"));
        var cs64 = csReader.Read(ParseValue(reader, $"[/DeviceN [{names64}] /DeviceGray 5 0 R]"), null, sink, null, null, 0);
        // §8.6.6.5 allows an arbitrary number; this reader's own cap matches
        // ContentInterpreter.MaxOperandsPerOperator (64), not Annex C.2's informative 32.
        Assert.NotNull(cs64);
        Assert.Equal(PdfImageColorSpaceFamily.DeviceN, cs64!.Family);
        Assert.Equal(64, cs64.ComponentCount);

        var names65 = string.Join(" ", Enumerable.Range(0, 65).Select(i => $"/C{i}"));
        var cs65 = csReader.Read(ParseValue(reader, $"[/DeviceN [{names65}] /DeviceGray 5 0 R]"), null, sink, null, null, 0);
        Assert.Null(cs65);
    }

    [Fact]
    public void IccBased_N1_N3_N4_succeed_N2_rejected()
    {
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new(10, "<< /N 1 >>", "x"u8.ToArray()),
            new(11, "<< /N 3 >>", "xyz"u8.ToArray()),
            new(12, "<< /N 4 >>", "wxyz"u8.ToArray()),
            new(13, "<< /N 2 >>", "xy"u8.ToArray()),
        };
        var pdf = BuildPdf(1, [.. objs]);
        var (reader, csReader, sink) = Setup(pdf);

        var n1 = csReader.Read(ParseValue(reader, "[/ICCBased 10 0 R]"), null, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.IccBased, n1!.Family);
        Assert.Equal(1, n1.ComponentCount);
        Assert.Equal("x"u8.ToArray(), n1.IccProfile.ToArray());

        var n3 = csReader.Read(ParseValue(reader, "[/ICCBased 11 0 R]"), null, sink, null, null, 0);
        Assert.Equal(3, n3!.ComponentCount);

        var n4 = csReader.Read(ParseValue(reader, "[/ICCBased 12 0 R]"), null, sink, null, null, 0);
        Assert.Equal(4, n4!.ComponentCount);

        var n2 = csReader.Read(ParseValue(reader, "[/ICCBased 13 0 R]"), null, sink, null, null, 0);
        Assert.Null(n2);
    }

    [Fact]
    public void Indexed_stringLookup()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(
            ParseValue(reader, "[/Indexed /DeviceRGB 1 <FF0000FFFFFF>]"), null, sink, null, null, 0);
        Assert.NotNull(cs);
        Assert.Equal(PdfImageColorSpaceFamily.Indexed, cs!.Family);
        Assert.Equal(1, cs.HighValue);
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF, 0xFF, 0xFF }, cs.Lookup.ToArray());
    }

    [Fact]
    public void Indexed_streamLookup()
    {
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new(10, "<< >>", new byte[] { 1, 2, 3, 4, 5, 6 }),
        };
        var pdf = BuildPdf(1, [.. objs]);
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "[/Indexed /DeviceRGB 1 10 0 R]"), null, sink, null, null, 0);
        Assert.NotNull(cs);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, cs!.Lookup.ToArray());
    }

    [Fact]
    public void Indexed_shortLookup_isRejected()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        // hival 1 over DeviceRGB needs (1+1)*3 = 6 bytes; only 3 are given.
        var cs = csReader.Read(ParseValue(reader, "[/Indexed /DeviceRGB 1 <FF0000>]"), null, sink, null, null, 0);
        Assert.Null(cs);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
    }

    [Fact]
    public void Indexed_longLookup_isTruncatedSilently()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "[/Indexed /DeviceRGB 0 <FF0000AABBCC>]"), null, sink, null, null, 0);
        Assert.NotNull(cs);
        Assert.Equal(new byte[] { 0xFF, 0, 0 }, cs!.Lookup.ToArray()); // hival 0 -> 1*3 = 3 bytes only.
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Indexed_baseIsIndexed_isRejected()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(
            ParseValue(reader, "[/Indexed [/Indexed /DeviceGray 1 <0001>] 1 <0001>]"), null, sink, null, null, 0);
        Assert.Null(cs);
    }

    [Fact]
    public void Indexed_baseIsPattern_isRejected()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "[/Indexed /Pattern 1 <0001>]"), null, sink, null, null, 0);
        Assert.Null(cs);
    }

    [Fact]
    public void NamedResource_resolvedFromPageColorSpaceDictionary()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var resources = (PdfDictionary)ParseValue(reader, "<< /ColorSpace << /CS0 /DeviceRGB >> >>");
        var cs = csReader.Read(ParseValue(reader, "/CS0"), resources, sink, null, null, 0);
        Assert.Equal(PdfImageColorSpaceFamily.DeviceRgb, cs!.Family);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void NamedResource_selfReferential_terminatesAt501_ratherThanLooping()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var resources = (PdfDictionary)ParseValue(reader, "<< /ColorSpace << /CS0 /CS0 >> >>");
        var cs = csReader.Read(ParseValue(reader, "/CS0"), resources, sink, null, null, 0);
        Assert.Null(cs);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
    }

    /// <summary>
    /// An Indexed space whose base is a resource name resolving back to the same Indexed array
    /// (<c>/CS0 = [/Indexed /CS0 1 &lt;...&gt;]</c>) would recurse
    /// <c>ReadCore(/CS0, true) -&gt; ReadIndexed -&gt; ReadCore(/CS0, true) -&gt; ...</c> forever
    /// without the two guards below, crashing the process with an uncatchable
    /// <see cref="StackOverflowException"/>. Terminates at 501 instead, both because the base is
    /// read with <c>allowResourceLookup: false</c> and independently through
    /// <c>MaxColorSpaceNesting</c>.
    /// </summary>
    [Fact]
    public void Indexed_baseIsResourceNameCyclingToSelf_terminatesAt501_ratherThanLooping()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var resources = (PdfDictionary)ParseValue(
            reader, "<< /ColorSpace << /CS0 [/Indexed /CS0 1 <FF00FF00FF00>] >> >>");

        var cs = csReader.Read(ParseValue(reader, "/CS0"), resources, sink, null, null, 0);

        Assert.Null(cs);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
    }

    /// <summary>
    /// The two guards on that recursion are independent, and the cycle tests around this one pass
    /// with either alone, so neither is pinned by them. This one isolates the resource-name half:
    /// an Indexed base that names a resource entry resolving straight to a device space, in two
    /// levels, so <c>MaxColorSpaceNesting</c> cannot fire and only
    /// <c>allowResourceLookup: false</c> decides the outcome. The nesting cap has no such test and
    /// can have none while this guard stands: both recursive arms pass
    /// <c>allowResourceLookup: false</c>, so the deepest value a document can drive is 2 against a
    /// check that fires above 3. It is headroom for a future recursive arm, which is what its own
    /// comment says it is. ISO 32000-2 §8.6.3 is why it is refused rather than followed: a colour
    /// space named by an image "shall always be defined directly as a PDF object, not by an entry
    /// in the ColorSpace resource subdictionary", and the clause extends that to spaces "defined in
    /// terms of other colour spaces", which is what an Indexed base is.
    /// </summary>
    [Fact]
    public void Indexed_baseIsAResourceNameResolvingToADeviceSpace_isRefusedAt501()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var resources = (PdfDictionary)ParseValue(
            reader, "<< /ColorSpace << /CS0 [/Indexed /CS1 1 <FF00FF00FF00>] /CS1 /DeviceRGB >> >>");

        var cs = csReader.Read(ParseValue(reader, "/CS0"), resources, sink, null, null, 0);

        Assert.Null(cs);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
    }

    /// <summary>
    /// The two-name variant of the same cycle: <c>/CS0</c>'s base names <c>/CS1</c>, whose own base
    /// names <c>/CS0</c> back. A fix that only special-cased a base naming ITSELF (rather than
    /// disabling the resource-name hop for an Indexed base entirely, or capping recursion depth)
    /// would still loop forever on this shape.
    /// </summary>
    [Fact]
    public void Indexed_baseIsResourceNameCyclingThroughTwoNames_terminatesAt501_ratherThanLooping()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var resources = (PdfDictionary)ParseValue(
            reader,
            "<< /ColorSpace << /CS0 [/Indexed /CS1 1 <FF00FF00FF00>] "
            + "/CS1 [/Indexed /CS0 1 <FF00FF00FF00>] >> >>");

        var cs = csReader.Read(ParseValue(reader, "/CS0"), resources, sink, null, null, 0);

        Assert.Null(cs);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
    }

    [Fact]
    public void UnresolvableName_reports501()
    {
        var pdf = BuildPdf(1, new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"), new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        var (reader, csReader, sink) = Setup(pdf);
        var cs = csReader.Read(ParseValue(reader, "/NotAThing"), null, sink, null, null, 0);
        Assert.Null(cs);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageColorSpaceUnsupported);
    }
}
