// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Property-based invariants for the Standard-14 font metrics (issue #5, CsCheck).
/// </summary>
public sealed class PropertyTests
{
    // Exhaustive over the whole char space: a glyph width is never negative for any font.
    [Fact]
    public void Standard14_glyphWidths_areNeverNegative()
    {
        foreach (var font in Enum.GetValues<Standard14>())
            for (var c = 0; c <= 0xFFFF; c++)
                if (Standard14Metrics.GetWidth(font, (char)c) < 0)
                    Assert.Fail($"{font} U+{c:X4} returned a negative width.");
    }

    // Measuring any string is non-negative and never shrinks when a character is appended.
    [Fact]
    public void Standard14_measureString_isNonNegative_andMonotonicUnderAppend()
    {
        Gen.String.Sample(s =>
        {
            var width = Standard14Metrics.MeasureString(Standard14.Helvetica, s, 12.0);
            Assert.True(width >= 0, $"negative width for {s.Length}-char string");

            var appended = Standard14Metrics.MeasureString(Standard14.Helvetica, s + "M", 12.0);
            Assert.True(appended >= width, "appending a character decreased the measured width");
        });
    }
}
