// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF array object (ISO 32000-2 §7.3.6).</summary>
public sealed class PdfArray : PdfObject
{
    private readonly List<PdfObject> _items;

    /// <summary>Creates an empty array.</summary>
    public PdfArray() => _items = [];
    /// <summary>Creates an array populated with <paramref name="items"/>.</summary>
    public PdfArray(IEnumerable<PdfObject> items) => _items = [.. items];

    /// <summary>The number of items in the array.</summary>
    public int Count => _items.Count;
    /// <summary>Gets the item at index <paramref name="i"/>.</summary>
    public PdfObject this[int i] => _items[i];

    /// <summary>Appends <paramref name="obj"/> to the array and returns this array.</summary>
    public PdfArray Add(PdfObject obj) { _items.Add(obj); return this; }

    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteAscii("["u8);
        for (var i = 0; i < _items.Count; i++)
        {
            if (i > 0) writer.WriteByte((byte)' ');
            _items[i].WriteTo(writer);
        }
        writer.WriteAscii("]"u8);
    }
}
