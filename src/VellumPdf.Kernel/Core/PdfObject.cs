// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>Abstract base for all eight PDF basic object types plus indirect references.</summary>
public abstract class PdfObject
{
    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
    /// <exception cref="ArgumentNullException">
    /// A <see cref="PdfDictionary"/> being written, one that is a stream's dictionary or holds the
    /// object being written included, has an entry whose value is <see langword="null"/>:
    /// <c>ParamName</c> is <c>value</c>. The null was stored without a check, so this is raised here
    /// and not from <see cref="PdfDictionary.Set(PdfName, PdfObject)"/>.
    /// </exception>
    public abstract void WriteTo(PdfWriter writer);
}
