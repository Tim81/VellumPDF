// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Gaps in what #521's cycle guards pin: that the guards leave a path when they leave an element,
/// so sharing is not mistaken for a cycle, and that a structure element nested more than one level
/// deep is still found for the parent tree.
/// </summary>
public sealed class StructureTreeAndWriterGapTests
{
    private static string Write(PdfObject obj)
    {
        using var ms = new MemoryStream();
        obj.WriteTo(new PdfWriter(ms));
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    // ── Writer: a dictionary used twice is not nested in itself ──────────────

    [Fact]
    public void ASharedDictionaryUnderTwoKeys_isNotACycle()
    {
        var shared = new PdfDictionary().Set(new PdfName("A"), new PdfInteger(1));
        var outer = new PdfDictionary().Set(new PdfName("X"), shared).Set(new PdfName("Y"), shared);

        Assert.Equal("<<\n/X <<\n/A 1\n>>\n/Y <<\n/A 1\n>>\n>>", Write(outer));
    }

    [Fact]
    public void ASharedDictionaryAsTwoArrayEntries_isNotACycle()
    {
        var shared = new PdfDictionary().Set(new PdfName("A"), new PdfInteger(1));
        var outer = new PdfArray().Add(shared).Add(shared);

        Assert.Equal("[<<\n/A 1\n>> <<\n/A 1\n>>]", Write(outer));
    }

    [Fact]
    public void ASharedArrayUnderTwoDictionaryKeys_isNotACycle()
    {
        var shared = new PdfArray().Add(new PdfInteger(1));
        var outer = new PdfDictionary().Set(new PdfName("X"), shared).Set(new PdfName("Y"), shared);

        Assert.Equal("<<\n/X [1]\n/Y [1]\n>>", Write(outer));
    }

    // ── Structure tree ───────────────────────────────────────────────────────

    /// <summary>
    /// One element under two parents is a DAG, and AddChild allows it. Saving it is not supported:
    /// the element is walked twice and the second reservation leaves the first unassigned. What
    /// matters here is that the failure is not reported as a cycle, which it is not: the walk guard
    /// forgets an element when it leaves it, so only an element that is its own ancestor is refused.
    /// </summary>
    [Fact]
    public void Save_withOneElementUnderTwoParents_isNotReportedAsACycle()
    {
        using var doc = new PdfDocument { Tagged = true, Language = "en-US" };
        var page = doc.AddPage();
        var root = new PdfStructElem("Document");
        var left = root.AddChild(new PdfStructElem("Sect"));
        var right = root.AddChild(new PdfStructElem("Sect"));
        var shared = new PdfStructElem("Div") { Page = page };
        left.AddChild(shared);
        right.AddChild(shared);
        doc.RegisterStructElem(root);

        using var ms = new MemoryStream();
        var ex = Record.Exception(() => doc.Save(ms));

        if (ex is not null)
            Assert.DoesNotContain("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Document &gt; Sect &gt; P, with the marked content on the page owned by P, two levels below the
    /// root. The page's parent tree must hold P's reference at index 0, so the walk that gathers the
    /// elements on a page has to go down through Sect, not stop at the root's children.
    /// </summary>
    [Fact]
    public void Save_anElementTwoLevelsDownWithAnMcid_isInThePagesParentTree()
    {
        using var doc = new PdfDocument { Tagged = true, Language = "en-US" };
        var page = doc.AddPage();
        var root = new PdfStructElem("Document");
        var sect = root.AddChild(new PdfStructElem("Sect"));
        var p = sect.AddChild(new PdfStructElem("P"));
        p.Page = page;
        p.Mcid = 0;
        doc.RegisterStructElem(root);

        using var ms = new MemoryStream();
        doc.Save(ms);
        var text = Encoding.Latin1.GetString(ms.ToArray());

        var pObject = Regex.Match(text, @"(?m)^(\d+) 0 obj\r?\n<<(?:(?!endobj)[\s\S])*?/S /P\r?\n");
        Assert.True(pObject.Success, "no /S /P structure element object was written");
        var number = pObject.Groups[1].Value;

        // The parent tree's array for the page: [ <P> 0 R ] and nothing else.
        Assert.Matches(new Regex($@"(?m)^\[{number} 0 R\]\s*$"), text);
    }
}
