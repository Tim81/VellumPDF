// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

public sealed class TableTests
{
    [Fact]
    public void Table_simpleGrid_producesPdf()
    {
        using var doc = new Document();
        var table = new TableElement();
        table.SetColumnWidths(180, 180, 180);

        var header = table.AddHeaderRow();
        header.AddCell("Name").AddCell("Role").AddCell("Department");

        var r1 = table.AddRow();
        r1.AddCell("Alice").AddCell("Engineer").AddCell("Platform");

        var r2 = table.AddRow();
        r2.AddCell("Bob").AddCell("Designer").AddCell("Product");

        doc.Add(table);

        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 100);
    }

    [Fact]
    public void Table_manyRows_paginates()
    {
        using var doc = new Document();
        var table = new TableElement();
        table.SetColumnWidths(250, 250);

        var header = table.AddHeaderRow();
        header.AddCell("Key").AddCell("Value");

        for (var i = 0; i < 60; i++)
        {
            var row = table.AddRow();
            row.AddCell($"Key {i}").AddCell($"Value {i}");
        }

        doc.Add(table);

        var ms = new MemoryStream();
        doc.Save(ms); // must not throw
        Assert.True(ms.Length > 100);
    }

    [Fact]
    public void Table_withAutoWidths_producesPdf()
    {
        using var doc = new Document();
        var table = new TableElement();

        var row = table.AddRow();
        row.AddCell("Short").AddCell("This is a longer cell value").AddCell("Medium content");

        doc.Add(table);

        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 100);
    }
}
