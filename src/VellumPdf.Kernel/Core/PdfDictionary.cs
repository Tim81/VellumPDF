// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF dictionary object (ISO 32000-2 §7.3.7).</summary>
public sealed class PdfDictionary : PdfObject
{
    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = [];

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, replacing any existing entry, and returns this dictionary.</summary>
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

    /// <summary>Sets <paramref name="key"/> to an integer <paramref name="value"/> and returns this dictionary.</summary>
    public PdfDictionary Set(PdfName key, long value) => Set(key, new PdfInteger(value));
    /// <summary>Sets <paramref name="key"/> to a name built from <paramref name="nameValue"/> and returns this dictionary.</summary>
    public PdfDictionary Set(PdfName key, string nameValue) => Set(key, new PdfName(nameValue));

    /// <summary>Gets the value for <paramref name="key"/>; returns <see langword="true"/> when present.</summary>
    public bool TryGet(PdfName key, out PdfObject? value)
    {
        foreach (var kv in _entries)
        {
            if (kv.Key.Equals(key)) { value = kv.Value; return true; }
        }
        value = null;
        return false;
    }

    /// <summary>Returns the value for <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    public PdfObject? Get(PdfName key) => TryGet(key, out var v) ? v : null;

    /// <summary>
    /// All entries in insertion order. Exposed to sibling assemblies (e.g. the conformance
    /// validator) that must iterate dictionaries whose keys are not known ahead of time, such
    /// as a resource sub-dictionary.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<PdfName, PdfObject>> Entries => _entries;

    /// <summary>
    /// Returns a new <see cref="PdfDictionary"/> with a shallow copy of all entries.
    /// Used by stream <c>WriteTo</c> overrides to add serialisation-only entries
    /// (e.g. <c>/Length</c>, <c>/Filter</c>) without mutating the shared dictionary.
    /// </summary>
    internal PdfDictionary ShallowCopy()
    {
        var copy = new PdfDictionary();
        foreach (var kv in _entries)
            copy._entries.Add(kv);
        return copy;
    }

    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
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
