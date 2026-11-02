// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

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

    [Fact]
    public void PdfDictionary_Set_nullValue_isRefused_andStoresNothing()
    {
        var dict = new PdfDictionary();

        var ex = Assert.Throws<ArgumentNullException>(() => dict.Set(new PdfName("A"), (PdfObject)null!));

        Assert.Equal("value", ex.ParamName);
        Assert.False(dict.TryGet(new PdfName("A"), out _));
    }

    [Fact]
    public void PdfDictionary_Set_nullValue_isRefusedAboveTheIndexThresholdToo()
    {
        // The dictionary switches to an index past a size threshold, and the null key it already
        // refused had to behave the same on both sides. The value does as well.
        var dict = new PdfDictionary();
        for (var i = 0; i < 64; i++) dict.Set(new PdfName($"K{i}"), new PdfInteger(i));

        Assert.Throws<ArgumentNullException>(() => dict.Set(new PdfName("K3"), (PdfObject)null!));
        Assert.Throws<ArgumentNullException>(() => dict.Set(new PdfName("New"), (PdfObject)null!));
        Assert.False(dict.TryGet(new PdfName("New"), out _));
        Assert.Equal(3, ((PdfInteger)dict.Get(new PdfName("K3"))!).Value);
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
