// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Layout;
using VellumPdf.Layout.Elements;

// Exercises the public generation path under Native AOT: layout engine,
// pagination, Standard-14 fonts, FlateDecode, and the PDF writer.
using var doc = new Document();
doc.Info.Title = "VellumPdf AOT Smoke";
doc.Add(new Paragraph("VellumPdf AOT smoke test — Hello, world!"));
doc.Add(new LineSeparator());
for (var i = 0; i < 80; i++)
    doc.Add(new Paragraph($"Paragraph {i + 1}: the quick brown fox jumps over the lazy dog."));

using var ms = new MemoryStream();
doc.Save(ms);
var bytes = ms.ToArray();

if (bytes.Length < 100)
{
    Console.Error.WriteLine($"FAIL: PDF too small ({bytes.Length} bytes)");
    return 1;
}

var header = Encoding.ASCII.GetString(bytes, 0, 8);
if (header != "%PDF-2.0")
{
    Console.Error.WriteLine($"FAIL: unexpected header '{header}'");
    return 1;
}

Console.WriteLine($"OK: Native AOT generated a {bytes.Length}-byte PDF.");
return 0;
