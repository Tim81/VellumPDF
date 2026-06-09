// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>Abstract base for all eight PDF basic object types plus indirect references.</summary>
public abstract class PdfObject
{
    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
    public abstract void WriteTo(PdfWriter writer);
}
