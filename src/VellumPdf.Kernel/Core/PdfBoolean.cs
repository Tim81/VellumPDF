// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF boolean object (ISO 32000-2 §7.3.2).</summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>The shared <c>true</c> instance.</summary>
    public static readonly PdfBoolean True = new(true);
    /// <summary>The shared <c>false</c> instance.</summary>
    public static readonly PdfBoolean False = new(false);

    /// <summary>The underlying boolean value.</summary>
    public bool Value { get; }
    private PdfBoolean(bool value) => Value = value;

    /// <summary>Returns the shared <see cref="True"/> or <see cref="False"/> instance for <paramref name="value"/>.</summary>
    public static PdfBoolean Of(bool value) => value ? True : False;

    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
    public override void WriteTo(PdfWriter writer) =>
        writer.WriteAscii(Value ? "true"u8 : "false"u8);
}
