// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF null object. Singleton.</summary>
public sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    private PdfNull() { }

    public override void WriteTo(PdfWriter writer) => writer.WriteAscii("null"u8);
}
