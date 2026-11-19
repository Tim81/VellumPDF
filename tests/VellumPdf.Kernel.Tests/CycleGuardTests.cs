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
/// reached it from an ordinary <c>Save</c>. Every test here that refuses asserts the exception,
/// because a test that only checks the process survives passes on a machine with a larger stack.
/// </summary>
public sealed class CycleGuardTests
{
    // ── Structure elements: accepted where the cycle is made, refused where the tree is built ──

    /// <summary>
    /// 2.3.3 stored a cycle: <c>AddChild</c> checked nothing. The call keeps accepting it, since the
    /// cycle only does harm where a save walks the tree.
    /// </summary>
    [Fact]
    public void StructElem_AddChild_aCycle_isStored()
    {
        var self = new PdfStructElem("Div");
        self.AddChild(self);
        Assert.Same(self, Assert.Single(self.Children));

        var a = new PdfStructElem("Div");
        var b = a.AddChild(new PdfStructElem("Sect"));
        var c = b.AddChild(new PdfStructElem("P"));
        c.AddChild(a);
        Assert.Same(a, Assert.Single(c.Children));
    }

    /// <summary>
    /// The shapes 2.3.3 saved: a cyclic element that a save never walks, because the document is not
    /// tagged when it is saved. Measured on 2.3.3 (1c61fa6): all four save, the last with a
    /// <c>/StructTreeRoot</c>, since the tree is built only for a tagged document and an element
    /// registered while the document was untagged is not part of it.
    /// </summary>
    [Theory]
    [InlineData("self-cycle registered")]
    [InlineData("two-element cycle registered")]
    [InlineData("cycle built, not registered")]
    [InlineData("tagged later, cycle registered while untagged")]
    public void Save_withACyclicElementTheSaveNeverWalks_saves_asOn1c61fa6(string shape)
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var e = new PdfStructElem("P");
        switch (shape)
        {
            case "two-element cycle registered":
                var other = new PdfStructElem("Div");
                other.AddChild(e);
                e.AddChild(other);
                doc.RegisterStructElem(other);
                break;
            case "cycle built, not registered":
                e.AddChild(e);
                break;
            default:
                e.AddChild(e);
                doc.RegisterStructElem(e);
                break;
        }

        if (shape.StartsWith("tagged later", StringComparison.Ordinal))
            doc.Tagged = true;

        using var ms = new MemoryStream();
        doc.Save(ms);

        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(ms.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A tagged document whose tree reaches a cycle made through <c>AddChild</c> is refused by the
    /// save with <see cref="InvalidOperationException"/>, for a self-cycle and for a longer one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_taggedDocumentReachingACycleBuiltWithAddChild_raisesInsteadOfOverflowingTheStack(bool longer)
    {
        using var doc = new PdfDocument { Tagged = true, Language = "en-US" };
        doc.AddPage();
        var root = new PdfStructElem("Div");
        if (longer)
        {
            var child = root.AddChild(new PdfStructElem("Sect"));
            child.AddChild(root);
        }
        else
        {
            root.AddChild(root);
        }

        doc.RegisterStructElem(root);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        Assert.Contains("structure tree contains a cycle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StructElem_AddChild_nullChild_isStored_andRefusedWhereTheTreeIsBuilt()
    {
        // 1c61fa6 stored it: an untagged document never builds the tree and saved.
        using var untagged = new PdfDocument();
        untagged.AddPage();
        var element = new PdfStructElem("Div");
        element.AddChild(null!);
        untagged.RegisterStructElem(element);
        using (var ms = new MemoryStream()) untagged.Save(ms);

        using var tagged = new PdfDocument { Tagged = true };
        tagged.AddPage();
        var tree = new PdfStructElem("Div");
        tree.AddChild(null!);
        tagged.RegisterStructElem(tree);

        var ex = Assert.Throws<ArgumentNullException>(() => tagged.Save(new MemoryStream()));
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
    /// The same cycle injected into the child list by reflection, which no member of the public surface
    /// does: Save has to raise rather than overflow.
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
    public void PdfStream_whoseDictionaryHoldsTheStream_isRefusedWhenWritten()
    {
        // WriteTo serialises a fresh copy of the dictionary each time, so the dictionary guard alone
        // never sees the same container twice here and the recursion ran until the stack was gone.
        var stream = new PdfStream([1, 2, 3]);
        stream.Dictionary.Set(new PdfName("Self"), stream);

        var ex = Assert.Throws<InvalidOperationException>(() => Write(stream));

        Assert.StartsWith("A PDF stream contains itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RawPdfStream_whoseDictionaryHoldsTheStream_isRefusedWhenWritten()
    {
        var stream = new RawPdfStream([1, 2, 3], PdfName.FlateDecode);
        stream.Dictionary.Set(new PdfName("Self"), stream);

        var ex = Assert.Throws<InvalidOperationException>(() => Write(stream));

        Assert.StartsWith("A PDF stream contains itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UncompressedPdfStream_whoseDictionaryHoldsTheStream_isRefusedWhenWritten()
    {
        var stream = new UncompressedPdfStream([1, 2, 3]);
        stream.Dictionary.Set(new PdfName("Self"), stream);

        var ex = Assert.Throws<InvalidOperationException>(() => Write(stream));

        Assert.StartsWith("A PDF stream contains itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleThroughAStreamAndAnArray_isRefusedWhenWritten()
    {
        var stream = new PdfStream([1, 2, 3]);
        var array = new PdfArray();
        array.Add(stream);
        stream.Dictionary.Set(new PdfName("Kids"), array);

        Assert.Throws<InvalidOperationException>(() => Write(array));
    }

    [Fact]
    public void ThePdfStreamPlacedTwiceSideBySide_isNotACycle()
    {
        var stream = new PdfStream([1, 2, 3]);
        var array = new PdfArray();
        array.Add(stream);
        array.Add(stream);

        var text = Write(array);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "endstream").Count);
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
