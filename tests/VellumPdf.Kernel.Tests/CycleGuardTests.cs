// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #521: three call paths recursed over a caller-built graph with no cycle guard and no depth bound,
/// and a <c>StackOverflowException</c> cannot be caught in .NET. A structure element added to itself
/// reached it from an ordinary <c>Save</c>. Every test here asserts the exception, because a test
/// that only checks the process survives passes on a machine with a larger stack.
/// </summary>
public sealed class CycleGuardTests
{
    // ── Structure elements: refused where the cycle is made ──────────────────

    [Fact]
    public void StructElem_AddChild_itself_isRefused()
    {
        var element = new PdfStructElem("Div");

        var ex = Assert.Throws<ArgumentException>(() => element.AddChild(element));

        Assert.Equal("child", ex.ParamName);
        Assert.Contains("its own child", ex.Message, StringComparison.Ordinal);
        Assert.Empty(element.Children);
    }

    [Fact]
    public void StructElem_AddChild_aDescendantsAncestor_isRefused()
    {
        var a = new PdfStructElem("Div");
        var b = a.AddChild(new PdfStructElem("Sect"));
        var c = b.AddChild(new PdfStructElem("P"));

        // c would then have a, its own great-grandparent, as a child.
        var ex = Assert.Throws<ArgumentException>(() => c.AddChild(a));

        Assert.Equal("child", ex.ParamName);
        Assert.Contains("its own ancestor", ex.Message, StringComparison.Ordinal);
        Assert.Empty(c.Children);
        Assert.Single(a.Children);
    }

    [Fact]
    public void StructElem_AddChild_nullChild_isRefused()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new PdfStructElem("Div").AddChild(null!));

        Assert.Equal("child", ex.ParamName);
    }

    /// <summary>An element reachable by two routes is a DAG, not a cycle, and is not refused.</summary>
    [Fact]
    public void StructElem_AddChild_theSameChildUnderTwoParents_isAllowed()
    {
        var shared = new PdfStructElem("P");
        var left = new PdfStructElem("Div");
        var right = new PdfStructElem("Div");

        left.AddChild(shared);
        right.AddChild(shared);

        Assert.Same(shared, Assert.Single(left.Children));
        Assert.Same(shared, Assert.Single(right.Children));
    }

    [Fact]
    public void StructElem_ordinaryTree_stillSaves()
    {
        using var doc = new PdfDocument { Tagged = true, Language = "en-US" };
        var page = doc.AddPage();
        var root = new PdfStructElem("Div");
        var paragraph = root.AddChild(new PdfStructElem("P"));
        paragraph.Page = page;
        paragraph.Mcid = 0;
        doc.RegisterStructElem(root);

        using var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Contains("/StructTreeRoot", Encoding.Latin1.GetString(ms.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The walks keep a guard of their own for a graph assembled some other way. AddChild refuses a
    /// cycle, so this one is injected by reflection, and Save has to raise rather than overflow.
    /// </summary>
    [Fact]
    public void Save_withACycleInjectedPastAddChild_raisesInsteadOfOverflowingTheStack()
    {
        using var doc = new PdfDocument { Tagged = true, Language = "en-US" };
        doc.AddPage();
        var element = new PdfStructElem("Div");
        var children = (List<PdfStructElem>)typeof(PdfStructElem)
            .GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(element)!;
        children.Add(element);
        doc.RegisterStructElem(element);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Contains("structure tree contains a cycle", ex.Message, StringComparison.Ordinal);
    }

    // ── Arrays and dictionaries: refused when written ────────────────────────

    private static string Write(PdfObject obj)
    {
        using var ms = new MemoryStream();
        obj.WriteTo(new PdfWriter(ms));
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void PdfArray_containingItself_isRefusedWhenWritten()
    {
        var array = new PdfArray();
        array.Add(array);

        var ex = Assert.Throws<InvalidOperationException>(() => Write(array));

        Assert.StartsWith("A PDF array contains itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfDictionary_containingItself_isRefusedWhenWritten()
    {
        var dict = new PdfDictionary();
        dict.Set(new PdfName("Self"), dict);

        var ex = Assert.Throws<InvalidOperationException>(() => Write(dict));

        Assert.StartsWith("A PDF dictionary contains itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleThroughAnArrayAndADictionary_isRefusedWhenWritten()
    {
        var array = new PdfArray();
        var dict = new PdfDictionary();
        array.Add(dict);
        dict.Set(new PdfName("Back"), array);

        Assert.Throws<InvalidOperationException>(() => Write(array));
        Assert.Throws<InvalidOperationException>(() => Write(dict));
    }

    /// <summary>One object placed twice, side by side, is not nested in itself and writes normally.</summary>
    [Fact]
    public void ASharedContainerPlacedTwice_isNotACycle()
    {
        var shared = new PdfArray().Add(new PdfInteger(1)).Add(new PdfInteger(2));
        var outer = new PdfArray().Add(shared).Add(shared);

        Assert.Equal("[[1 2] [1 2]]", Write(outer));
    }

    [Fact]
    public void WritingTheSameWriterAgainAfterARefusal_isNotPoisoned()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        var cyclic = new PdfArray();
        cyclic.Add(cyclic);
        Assert.Throws<InvalidOperationException>(() => { cyclic.WriteTo(writer); });

        new PdfArray().Add(new PdfInteger(7)).WriteTo(writer);

        Assert.EndsWith("[7]", Encoding.Latin1.GetString(ms.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryNestedObjects_writeExactlyAsBefore()
    {
        var dict = new PdfDictionary()
            .Set(new PdfName("Kids"), new PdfArray().Add(new PdfInteger(1)).Add(new PdfDictionary().Set(new PdfName("A"), new PdfInteger(2))));

        Assert.Equal("<<\n/Kids [1 <<\n/A 2\n>>]\n>>", Write(dict));
    }
}
