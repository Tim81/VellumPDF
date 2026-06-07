// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF array object (ISO 32000-2 §7.3.6).</summary>
public sealed class PdfArray : PdfObject
{
    private readonly List<PdfObject> _items;

    public PdfArray() => _items = [];
    public PdfArray(IEnumerable<PdfObject> items) => _items = [..items];

    public int Count => _items.Count;
    public PdfObject this[int i] => _items[i];

    public PdfArray Add(PdfObject obj) { _items.Add(obj); return this; }

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
