// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #530: what <c>PdfDocument.EnsureCanPrepareForSigning</c> refuses before a layout engine has laid
/// anything out. The size bound comes from the placeholder, <c>&lt;</c> + 2n zeros + <c>&gt;</c>, which
/// has to fit in a string of at most 0x3FFFFFDF characters.
/// </summary>
public sealed class SigningPreCheckTests
{
    private const int MaxBytes = (0x3FFFFFDF - 2) / 2;

    private static PdfDocument OnePage()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        return doc;
    }

    [Fact]
    public void TheBoundIsTheLargestSizeWhosePlaceholderFitsInAString()
    {
        Assert.Equal(536_870_894, PdfSignatureHelper.MaxContentsPlaceholderBytes);
        Assert.Equal(MaxBytes, PdfSignatureHelper.MaxContentsPlaceholderBytes);

        // "<" + 2n zeros + ">" is 2n + 2 characters: the longest string holds the bound, not one more.
        Assert.True((2L * MaxBytes) + 2 <= 0x3FFFFFDF);
        Assert.True((2L * (MaxBytes + 1)) + 2 > 0x3FFFFFDF);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(MaxBytes + 1)]
    [InlineData(1 << 29)]                                           // 536,870,912: used to be OutOfMemory after layout
    [InlineData((1 << 30) - 1)]
    [InlineData(1 << 30)]
    [InlineData(int.MaxValue)]
    public void ASizeWhosePlaceholderCannotBeBuilt_isRefusedWithTheOptionsParameter(int size)
    {
        using var doc = OnePage();
        var options = new SignaturePlaceholderOptions { EstimatedSignatureSizeBytes = size };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => doc.EnsureCanPrepareForSigning(options));
        Assert.Equal("options", ex.ParamName);
        Assert.Equal(size, ex.ActualValue);
        Assert.Contains(MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);

        var viaPublic = Assert.Throws<ArgumentOutOfRangeException>(() => doc.PrepareForSigning(options));
        Assert.Equal("options", viaPublic.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8192)]
    [InlineData(MaxBytes)]
    public void ASizeThatFits_isNotRefused(int size)
    {
        using var doc = OnePage();
        doc.EnsureCanPrepareForSigning(new SignaturePlaceholderOptions { EstimatedSignatureSizeBytes = size });
    }

    /// <summary>
    /// A caller that holds the pages gets the document's own order: no pages is reported before any
    /// option is looked at, as it was before the pre-check existed. A layout engine that has not
    /// laid the pages out yet (<c>pagesKnown: false</c>) goes straight to the option.
    /// </summary>
    [Fact]
    public void ADocumentWithNoPages_reportsThatBeforeALookAtTheOptions()
    {
        using var doc = new PdfDocument();
        var bad = new SignaturePlaceholderOptions { SignaturePage = -1, EstimatedSignatureSizeBytes = -1 };

        var ex = Assert.Throws<InvalidOperationException>(() => doc.PrepareForSigning(bad));
        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => doc.EnsureCanPrepareForSigning(bad));

        Assert.Throws<ArgumentOutOfRangeException>(() => doc.EnsureCanPrepareForSigning(bad, pagesKnown: false));
    }

    [Fact]
    public void ANegativePageIndex_namesThePageCountWhenThePagesAreKnown_andDoesNotWhenTheyAreNot()
    {
        using var doc = OnePage();
        var options = new SignaturePlaceholderOptions { SignaturePage = -1 };

        var known = Assert.Throws<ArgumentOutOfRangeException>(() => doc.EnsureCanPrepareForSigning(options));
        var unknown = Assert.Throws<ArgumentOutOfRangeException>(() => doc.EnsureCanPrepareForSigning(options, pagesKnown: false));

        Assert.Contains("1 page(s)", known.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be negative", unknown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("page(s)", unknown.Message, StringComparison.Ordinal);
    }
}
