// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF dictionary object (ISO 32000-2 §7.3.7).</summary>
public sealed class PdfDictionary : PdfObject
{
    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = [];

    public PdfDictionary Set(PdfName key, PdfObject value)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Key.Equals(key))
            {
                _entries[i] = new(key, value);
                return this;
            }
        }
        _entries.Add(new(key, value));
        return this;
    }

    public PdfDictionary Set(PdfName key, long value) => Set(key, new PdfInteger(value));
    public PdfDictionary Set(PdfName key, string nameValue) => Set(key, new PdfName(nameValue));

    public bool TryGet(PdfName key, out PdfObject? value)
    {
        foreach (var kv in _entries)
        {
            if (kv.Key.Equals(key)) { value = kv.Value; return true; }
        }
        value = null;
        return false;
    }

    public PdfObject? Get(PdfName key) => TryGet(key, out var v) ? v : null;

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteAscii("<<"u8);
        foreach (var kv in _entries)
        {
            writer.WriteByte((byte)'\n');
            kv.Key.WriteTo(writer);
            writer.WriteByte((byte)' ');
            kv.Value.WriteTo(writer);
        }
        writer.WriteAscii("\n>>"u8);
    }
}
