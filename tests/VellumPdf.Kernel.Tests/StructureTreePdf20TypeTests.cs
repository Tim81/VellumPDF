// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// A structure type that ISO 32000-2, 14.8.4 defines and ISO 32000-1 does not (Aside) is written
/// only when the library asks for it by the internal flag, and only in a document written as
/// PDF 2.0. A caller that names the type through the public constructor is refused as before.
/// </summary>
public sealed class StructureTreePdf20TypeTests
{
    private const string NotStandard = "Structure type 'Aside' is not a standard type and has no role mapping.";

    private static PdfDocument Build(PdfConformance conformance, bool flagged)
    {
        var d = new PdfDocument { Tagged = true, Conformance = conformance };
        var page = d.AddPage();
        var aside = new PdfStructElem("Aside") { Pdf20Type = flagged };
        aside.AddChild(new PdfStructElem("P") { Page = page, Mcid = 0 });
        d.RegisterStructElem(aside);
        return d;
    }

    private static string Text(PdfDocument d)
    {
        using var ms = new MemoryStream();
        d.Save(ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void AnAsideTheCallerNamesIsStillRefused_inAPdf20Document()
    {
        using var d = Build(PdfConformance.None, flagged: false);
        var ex = Assert.Throws<InvalidOperationException>(() => d.Save(new MemoryStream()));
        Assert.Equal(NotStandard, ex.Message);
    }

    [Fact]
    public void AFlaggedAside_isWrittenInAPdf20Document()
    {
        using var d = Build(PdfConformance.None, flagged: true);
        var text = Text(d);
        Assert.StartsWith("%PDF-2.0", text, StringComparison.Ordinal);
        Assert.Contains("/S /Aside", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/RoleMap", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b)]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    public void AFlaggedAside_isRefusedWhereTheFileIsWrittenAsPdf17(PdfConformance conformance)
    {
        using var d = Build(conformance, flagged: true);
        var ex = Assert.Throws<InvalidOperationException>(() => d.Save(new MemoryStream()));
        Assert.Equal(NotStandard, ex.Message);
    }

    [Fact]
    public void TheFlagDoesNotExemptAnotherType()
    {
        using var d = new PdfDocument { Tagged = true };
        var page = d.AddPage();
        var foo = new PdfStructElem("Foo") { Pdf20Type = true };
        foo.AddChild(new PdfStructElem("P") { Page = page, Mcid = 0 });
        d.RegisterStructElem(foo);
        var ex = Assert.Throws<InvalidOperationException>(() => d.Save(new MemoryStream()));
        Assert.Equal("Structure type 'Foo' is not a standard type and has no role mapping.", ex.Message);
    }
}
