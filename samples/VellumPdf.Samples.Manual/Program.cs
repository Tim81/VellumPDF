// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Samples.Manual;

// Usage: VellumPdf.Samples.Manual [output.pdf]
// Writes the Aerolith AL-5 sample manual. The default file name is aerolith-al5-manual.pdf in the
// current directory.
var path = args.Length > 0 ? args[0] : "aerolith-al5-manual.pdf";

using var manual = ManualDocument.Build();
manual.Save(path);

Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
foreach (var d in manual.Diagnostics)
    Console.WriteLine($"{d.Severity}: {d.Code}: {d.Message}");
