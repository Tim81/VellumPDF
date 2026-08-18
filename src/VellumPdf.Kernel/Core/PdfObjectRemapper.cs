// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>Rewrites indirect references in an object graph according to an old→new number map.</summary>
internal static class PdfObjectRemapper
{
    /// <summary>
    /// Returns a structurally equal copy of <paramref name="obj"/> with every
    /// <see cref="PdfIndirectReference"/> whose object number appears in
    /// <paramref name="oldToNew"/> replaced by a reference to the mapped number.
    /// References not in the map are left unchanged.
    /// Scalars (integers, names, booleans, strings, null) are returned as-is.
    /// Streams: the dictionary entries are remapped; the compressed byte body is untouched.
    /// Stream dictionary entries are remapped in place (the caller owns the object graph
    /// at this point and the mutation is safe for a single-use linearization pass).
    /// </summary>
    public static PdfObject Remap(PdfObject obj, IReadOnlyDictionary<int, int> oldToNew)
    {
        return obj switch
        {
            // The one-arg ctor drops r.Generation, silently rewriting a remapped reference to
            // generation 0. Harmless here specifically: LinearizedLayoutPlanner, this method's only
            // caller, only ever remaps freshly-registered objects from PdfObjectRegistry, which are
            // always generation 0 — a read → remap → write path already exists (AppendRevision,
            // fixed for exactly this in #121), but linearization is not it, and never has been handed
            // a reference parsed from a source document. It stops being harmless the day that changes.
            PdfIndirectReference r => oldToNew.TryGetValue(r.ObjectNumber, out var n)
                ? new PdfIndirectReference(n)
                : r,
            PdfDictionary d => RemapDictionary(d, oldToNew),
            PdfArray a => RemapArray(a, oldToNew),
            PdfStream s => RemapStreamInPlace(s, oldToNew),
            _ => obj,
        };
    }

    private static PdfDictionary RemapDictionary(PdfDictionary d, IReadOnlyDictionary<int, int> map)
    {
        var result = new PdfDictionary();
        foreach (var kv in d.Entries)
            result.Set(kv.Key, Remap(kv.Value, map));
        return result;
    }

    private static PdfArray RemapArray(PdfArray a, IReadOnlyDictionary<int, int> map)
    {
        var result = new PdfArray();
        for (var i = 0; i < a.Count; i++)
            result.Add(Remap(a[i], map));
        return result;
    }

    // Remaps dictionary entries on the stream's own Dictionary in place.
    // Safe because linearization owns the object graph at this point.
    private static PdfStream RemapStreamInPlace(PdfStream s, IReadOnlyDictionary<int, int> map)
    {
        // Snapshot first — Set() modifies the underlying list and would break the enumerator.
        var snapshot = s.Dictionary.Entries.ToList();
        foreach (var kv in snapshot)
        {
            var remapped = Remap(kv.Value, map);
            if (!ReferenceEquals(remapped, kv.Value))
                s.Dictionary.Set(kv.Key, remapped);
        }
        return s;
    }
}
