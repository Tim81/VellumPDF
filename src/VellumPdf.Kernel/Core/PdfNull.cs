// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF null object. Singleton.</summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>The shared singleton instance of the PDF null object.</summary>
    public static readonly PdfNull Instance = new();
    private PdfNull() { }

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer) => writer.WriteAscii("null"u8);
}
