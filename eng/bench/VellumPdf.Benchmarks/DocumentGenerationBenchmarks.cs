// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Benchmarks;

/// <summary>
/// Measures document-generation throughput for both a rich layout document
/// (paragraphs + table) and a minimal single-page document.
/// </summary>
[MemoryDiagnoser]
public class DocumentGenerationBenchmarks
{
    /// <summary>
    /// Builds a representative multi-element Layout document (paragraphs, a line
    /// separator, and a table), paginates it, and saves to a MemoryStream.
    /// Returns the byte length so the JIT cannot optimise the work away.
    /// </summary>
    [Benchmark]
    public int RichDocument()
    {
        using var doc = new LayoutDocument();
        doc.Info.Title = "Benchmark Document";

        doc.Add(new Paragraph("VellumPdf Benchmark — document generation throughput"));
        doc.Add(new LineSeparator());

        for (var i = 0; i < 30; i++)
            doc.Add(new Paragraph($"Paragraph {i + 1}: the quick brown fox jumps over the lazy dog."));

        var table = new TableElement();
        table.SetColumnWidths(150, 150, 150);

        var header = table.AddHeaderRow();
        header.AddCell("Name").AddCell("Role").AddCell("Department");

        for (var i = 0; i < 10; i++)
        {
            var row = table.AddRow();
            row.AddCell($"Person {i}").AddCell("Engineer").AddCell("Platform");
        }

        doc.Add(table);

        using var ms = new MemoryStream();
        doc.Save(ms);
        return (int)ms.Length;
    }

    /// <summary>
    /// Builds the smallest useful document: a single Standard-14 paragraph saved
    /// to a MemoryStream. Isolates the baseline PDF-writer overhead.
    /// Returns the byte length so the JIT cannot optimise the work away.
    /// </summary>
    [Benchmark]
    public int MinimalDocument()
    {
        using var doc = new LayoutDocument();
        doc.Add(new Paragraph("Hello, VellumPdf!"));

        using var ms = new MemoryStream();
        doc.Save(ms);
        return (int)ms.Length;
    }
}
