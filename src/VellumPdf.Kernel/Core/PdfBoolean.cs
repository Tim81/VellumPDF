// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

public sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    public bool Value { get; }
    private PdfBoolean(bool value) => Value = value;

    public static PdfBoolean Of(bool value) => value ? True : False;

    public override void WriteTo(PdfWriter writer) =>
        writer.WriteAscii(Value ? "true"u8 : "false"u8);
}
