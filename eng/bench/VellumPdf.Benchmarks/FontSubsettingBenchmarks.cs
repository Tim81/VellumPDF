// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Benchmarks;

/// <summary>
/// Measures allocation and throughput of the TrueType font subsetter.
/// Requires a system TrueType font (Arial on Windows, DejaVuSans on Linux).
/// </summary>
[MemoryDiagnoser]
public class FontSubsettingBenchmarks
{
    private byte[] _fontData = [];

    /// <summary>
    /// Locates a platform TrueType font and reads it into memory once.
    /// Throws <see cref="InvalidOperationException"/> if no supported font is found —
    /// a system font is required to run this benchmark.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        string[] candidates =
        [
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _fontData = File.ReadAllBytes(candidate);
                return;
            }
        }

        throw new InvalidOperationException(
            "FontSubsettingBenchmarks requires a system TrueType font. " +
            "Expected one of: " + string.Join(", ", candidates) + ". " +
            "Install a TrueType font at one of these paths before running this benchmark.");
    }

    /// <summary>
    /// Registers the preloaded TrueType font, renders a paragraph of text
    /// (exercising glyph selection and the sfnt subsetter), and saves to a
    /// MemoryStream. <see cref="BenchmarkDotNet.Attributes.MemoryDiagnoserAttribute"/>
    /// reports the allocations attributable to subsetting per invocation.
    /// Returns the byte length so the JIT cannot optimise the work away.
    /// </summary>
    [Benchmark]
    public int EmbedAndSubset()
    {
        using var doc = new LayoutDocument();
        var handle = doc.UseTrueTypeFont(_fontData);

        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("The quick brown fox jumps over the lazy dog.", style));
        doc.Add(new Paragraph("Cafe resume — Unicode glyphs: é à ü ö.", style));

        using var ms = new MemoryStream();
        doc.Save(ms);
        return (int)ms.Length;
    }
}
