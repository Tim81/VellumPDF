// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF dictionary object (ISO 32000-2 §7.3.7).</summary>
public sealed class PdfDictionary : PdfObject
{
    // Invariant: while _index is non-null, it maps every key to that key's exact position in
    // _entries. Nothing may append to, remove from, or reorder _entries without updating _index in
    // the same step. Break that and the failure is silent and asymmetric: WriteTo iterates _entries
    // and emits whatever is there, while TryGet consults _index and trusts it, so the two disagree
    // with no exception to catch it.
    //
    // This type is not thread-safe, and the index makes a race worse than the old linear scan did:
    // two Set calls running concurrently can leave _index pointing one key at another key's slot in
    // _entries, so a later TryGet returns the WRONG value with no exception. The linear scan a small
    // dictionary still uses could only return a stale value that still matched the key it was stored
    // under. Callers must serialise their own access, the way PdfDocumentReader documents itself as
    // not thread-safe.
    //
    // A real PDF dictionary carries a handful of keys — a /CF sub-dictionary names one or two crypt
    // filters, for instance — and below roughly this count a linear scan over a contiguous list
    // outperforms a hash lookup: no second allocation, no hashing, good cache behaviour. Past it,
    // Set/TryGet switch to _index instead. The threshold is a tuning choice, not a correctness one:
    // an /Encrypt dictionary is parsed and copied before any password is checked (#208), so a
    // hostile file that declares thousands of keys must not cost time quadratic in the key count
    // either way.
    private const int IndexThreshold = 16;

    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = [];
    private Dictionary<PdfName, int>? _index;

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, replacing any existing entry, and returns this dictionary.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public PdfDictionary Set(PdfName key, PdfObject value)
    {
        // Without this, a null key would behave differently depending on which side of
        // IndexThreshold the dictionary sits: below it, the linear scan's Equals check quietly
        // answers "not found" and appends a null-key entry that only fails later, in WriteTo; above
        // it, Dictionary<PdfName, int> itself throws ArgumentNullException on the lookup. Rejecting
        // it here up front keeps Set, TryGet and Get from depending on the threshold — the exact
        // property that comment above promises and DictionaryAtExactlyTheThreshold_isStillCorrect
        // pins.
        ArgumentNullException.ThrowIfNull(key);

        if (_index is not null)
        {
            if (_index.TryGetValue(key, out var i))
            {
                _entries[i] = new(key, value);
            }
            else
            {
                // Add before recording the position: if Add throws — realistically OutOfMemoryException
                // while a hostile file's key count forces the backing list to double its capacity —
                // _index must not end up naming a slot that was never written. The other order would
                // leave a phantom entry: a later TryGet would resolve it to _entries[i] with
                // i == _entries.Count and throw ArgumentOutOfRangeException instead of returning false.
                _entries.Add(new(key, value));
                _index[key] = _entries.Count - 1;
            }
            return this;
        }

        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Key.Equals(key))
            {
                _entries[i] = new(key, value);
                return this;
            }
        }
        _entries.Add(new(key, value));
        if (_entries.Count > IndexThreshold)
            BuildIndex();
        return this;
    }

    private void BuildIndex()
    {
        var index = new Dictionary<PdfName, int>(_entries.Count);
        for (var i = 0; i < _entries.Count; i++)
            index[_entries[i].Key] = i;
        _index = index;
    }

    /// <summary>Sets <paramref name="key"/> to an integer <paramref name="value"/> and returns this dictionary.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public PdfDictionary Set(PdfName key, long value) => Set(key, new PdfInteger(value));
    /// <summary>Sets <paramref name="key"/> to a name built from <paramref name="nameValue"/> and returns this dictionary.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public PdfDictionary Set(PdfName key, string nameValue) => Set(key, new PdfName(nameValue));

    /// <summary>Gets the value for <paramref name="key"/>; returns <see langword="true"/> when present.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool TryGet(PdfName key, out PdfObject? value)
    {
        // See the comment in Set: rejecting a null key here, rather than at one or the other lookup
        // path, is what keeps the answer independent of IndexThreshold.
        ArgumentNullException.ThrowIfNull(key);

        if (_index is not null)
        {
            if (_index.TryGetValue(key, out var i))
            {
                value = _entries[i].Value;
                return true;
            }
            value = null;
            return false;
        }

        foreach (var kv in _entries)
        {
            if (kv.Key.Equals(key)) { value = kv.Value; return true; }
        }
        value = null;
        return false;
    }

    /// <summary>Returns the value for <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public PdfObject? Get(PdfName key) => TryGet(key, out var v) ? v : null;

    /// <summary>
    /// All entries in insertion order. Exposed to sibling assemblies (e.g. the conformance
    /// validator) that must iterate dictionaries whose keys are not known ahead of time, such
    /// as a resource sub-dictionary. This is the backing <see cref="List{T}"/> itself, not a
    /// snapshot — every current caller only reads it, but a caller that downcast it and mutated it
    /// would desync <c>_index</c>, triggering the asymmetric failure the invariant above warns about.
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
        // Positions carry over unchanged (same entries, same order), so the index just needs its own
        // copy of the map — sharing the source's Dictionary instance would let a later Set on either
        // dictionary corrupt the other's lookup.
        if (_index is not null)
            copy._index = new Dictionary<PdfName, int>(_index);
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
