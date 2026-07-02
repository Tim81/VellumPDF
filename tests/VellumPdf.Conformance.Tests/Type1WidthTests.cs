// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Conformance.Tests.Oracle;
using Xunit;

namespace VellumPdf.Conformance.Tests;

public sealed class Type1WidthTests
{
    // The advance width of uni00A0 in the embedded Noto Sans Shavian program, as verified by
    // veraPDF 1.30.2. The charstring encodes 259 glyph-space units (matched against /Widths).
    private const int TrueWidth = 259;

    [Fact]
    public void TryGetWidths_NotoSansShavian_uni00A0_IsNearTrueWidth()
    {
        var (fontFile, length1, _, _) = Type1FontAsset.ToFontFile();

        var widths = Type1Glyphs.TryGetWidths(fontFile, length1);

        Assert.NotNull(widths);
        Assert.True(widths!.TryGetValue("uni00A0", out var w),
            "uni00A0 must be present in the width map");
        Assert.True(Math.Abs(w - TrueWidth) <= 1,
            $"Expected uni00A0 width ≈ {TrueWidth}, got {w}");
    }
}
