// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #523: a null object added to a <see cref="PdfArray"/> or set on a <see cref="PdfDictionary"/> was
/// accepted, reported in <c>Count</c>, and failed with <see cref="NullReferenceException"/> later, in
/// <c>WriteTo</c>. <see cref="PdfDictionary.Set(PdfName, PdfObject)"/> already refused a null key and
/// its own comment described this failure mode for the value.
/// </summary>
public sealed class NullPdfObjectTests
{
    [Fact]
    public void PdfArray_Add_null_isRefused_andLeavesTheArrayUnchanged()
    {
        var array = new PdfArray();

        var ex = Assert.Throws<ArgumentNullException>(() => array.Add(null!));

        Assert.Equal("obj", ex.ParamName);
        Assert.Equal(0, array.Count);
    }

    [Fact]
    public void PdfArray_constructor_refusesANullSequenceAndANullElement()
    {
        Assert.Equal("items", Assert.Throws<ArgumentNullException>(() => new PdfArray((IEnumerable<PdfObject>)null!)).ParamName);

        var ex = Assert.Throws<ArgumentNullException>(() => new PdfArray(new PdfObject[] { new PdfInteger(1), null! }));
        Assert.Equal("items", ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
    }

    private static string Write(PdfObject obj)
    {
        using var ms = new MemoryStream();
        obj.WriteTo(new PdfWriter(ms));
        return System.Text.Encoding.ASCII.GetString(ms.ToArray());
    }

    [Fact]
    public void PdfDictionary_Set_nullValue_isStored_andRefusedByWriteToNamingTheKey()
    {
        var dict = new PdfDictionary();
        dict.Set(new PdfName("A"), (PdfObject)null!);

        Assert.True(dict.TryGet(new PdfName("A"), out var stored));
        Assert.Null(stored);
        var ex = Assert.Throws<ArgumentNullException>(() => Write(dict));
        Assert.Equal("value", ex.ParamName);
        Assert.Contains("A", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfDictionary_Set_nullValueThenARealOne_writesTheRealOne_asOn1c61fa6()
    {
        var dict = new PdfDictionary();
        dict.Set(new PdfName("K"), (PdfObject)null!);
        dict.Set(new PdfName("K"), new PdfInteger(1));

        Assert.Contains("/K 1", Write(dict), StringComparison.Ordinal);
    }

    [Fact]
    public void PdfDictionary_Set_nullValue_aboveTheIndexThreshold_behavesTheSame()
    {
        var dict = new PdfDictionary();
        for (var i = 0; i < 64; i++) dict.Set(new PdfName($"K{i}"), new PdfInteger(i));

        dict.Set(new PdfName("New"), (PdfObject)null!);
        dict.Set(new PdfName("K3"), (PdfObject)null!);

        Assert.True(dict.TryGet(new PdfName("New"), out var stored));
        Assert.Null(stored);
        Assert.Throws<ArgumentNullException>(() => Write(dict));
    }

    [Fact]
    public void RealObjects_stillAdd()
    {
        var array = new PdfArray().Add(new PdfInteger(1));
        var dict = new PdfDictionary().Set(new PdfName("A"), new PdfInteger(2));

        Assert.Equal(1, array.Count);
        Assert.Equal(2, ((PdfInteger)dict.Get(new PdfName("A"))!).Value);
    }
}
