// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using VellumPdf.Canvas;
using VellumPdf.Document;
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

    /// <summary>
    /// For any finite double coordinates, constructing a page and issuing a
    /// PdfCanvas.Rectangle/Fill, then saving, must produce a well-formed PDF
    /// (starts with %PDF, non-trivial length) without throwing. NaN/Infinity are
    /// excluded — they are not representable as PDF reals.
    /// </summary>
    [Fact]
    public void FiniteCoordinates_produceWellFormedPdf()
    {
        var finiteCoord = Gen.Double[-1e6, 1e6];
        Gen.Select(finiteCoord, finiteCoord, finiteCoord, finiteCoord)
           .Sample((x, y, w, h) =>
           {
               using var doc = new PdfDocument();
               var page = doc.AddPage();
               var canvas = new PdfCanvas(page);
               canvas.Rectangle(x, y, w, h).Fill();
               canvas.Finish();

               var ms = new MemoryStream();
               doc.Save(ms);
               var bytes = ms.ToArray();

               Assert.True(bytes.Length >= 100, $"PDF too short ({bytes.Length} bytes) for coords ({x},{y},{w},{h})");
               Assert.Equal((byte)'%', bytes[0]);
               Assert.Equal((byte)'P', bytes[1]);
               Assert.Equal((byte)'D', bytes[2]);
               Assert.Equal((byte)'F', bytes[3]);
           });
    }
}
