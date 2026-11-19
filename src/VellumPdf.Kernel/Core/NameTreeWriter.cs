// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// Builds a name tree (ISO 32000-2:2020, 7.9.6): keys are byte strings, ordered byte by byte.
/// Internal, so there is no public surface to lock. Nothing calls it yet.
/// </summary>
/// <remarks>
/// The layout rules and the node shape are documented on <see cref="PdfTreeNodes"/>.
/// <para><b>Order.</b> 7.9.6 says keys "shall be sorted lexically in ascending order by key. Shorter keys
/// shall appear before longer ones beginning with the same byte sequence. Any encoding of the keys may be
/// used as long as it is self-consistent; keys shall be compared for equality on a simple
/// byte-by-byte basis." That is an ordinal comparison of unsigned bytes, which is what
/// <see cref="MemoryExtensions.SequenceCompareTo{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/> does for
/// <see cref="byte"/>. A culture-aware comparison, or an ordinal comparison of UTF-16 code units, orders
/// some keys differently: U+FF5E encodes to EF BD 9E in UTF-8 and U+10000 to F0 90 80 80, so the UTF-8
/// bytes put U+FF5E first, while UTF-16 code units (FF5E against D800) put U+10000 first. A reader may
/// binary-search the array, so a wrong order loses keys silently.</para>
/// <para>The empty key is accepted: 7.9.6 does not exclude a zero-length string, and it sorts before every other key.</para>
/// </remarks>
internal static class NameTreeWriter
{
    /// <summary>
    /// The longest key written, in bytes. ISO 19005-2, 6.1.13 limits a string in a PDF/A-2 file to 32767
    /// bytes; the repository does not hold that part, but its own <c>NumericLimitsRule</c> enforces the same
    /// limit (<c>MaxStringBytes</c> = 32767). ISO 32000-1 Table C.1 gives 32,767 as the maximum length of a
    /// string in a content stream and adds "There is no effective restriction on other strings in PDF
    /// files". ISO 32000-2 Table C.1 has a row, "Length of a string object in a content stream", that
    /// gives 32,767 as the recommendation of previous versions and says it applied only to strings in
    /// content streams, so neither edition limits the length of a key. A longer key is valid PDF and
    /// invalid PDF/A-2, and the cap is applied to every file so a tree never needs rewriting when a
    /// conformance claim is added.
    /// </summary>
    internal const int MaxKeyBytes = 32767;

    private static readonly PdfName NamesName = new("Names");

    /// <summary>
    /// Writes a name tree for <paramref name="entries"/> and returns its root node, which is not
    /// registered. Every other node is added to <paramref name="registry"/>. Entries may arrive in any
    /// order; they are sorted by key bytes here. Key bytes are copied, so the caller may reuse its arrays.
    /// </summary>
    /// <remarks>
    /// <b>No entries.</b> The result is a root holding an empty <c>/Names</c> array, which is a
    /// well-formed tree by 7.9.6 (the root holds <c>/Names</c>, and no other node exists). A caller
    /// that has nothing to map should leave the catalog entry out instead.
    /// <para>A value that is a <see cref="PdfIndirectReference"/> stays a reference; a direct value is
    /// written inline. 7.9.6 says dictionaries, arrays and strings "should be" indirect and streams "shall be",
    /// so a direct dictionary is accepted, and a direct <see cref="PdfStream"/> is refused, whether it is the value or nested inside a direct array or dictionary value.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="registry"/>
    /// is <see langword="null"/>, or an entry's key array or value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A key is longer than <see cref="MaxKeyBytes"/> bytes; two entries
    /// have the same key bytes (7.9.6: keys "shall not overlap"); or a value is, or contains
    /// at any depth of direct nesting, a <see cref="PdfStream"/> or a <see cref="PdfIndirectObject"/>.</exception>
    internal static PdfDictionary Write(IEnumerable<KeyValuePair<byte[], PdfObject>> entries, PdfObjectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(registry);

        var list = new List<(byte[] Key, PdfObject Value)>();
        foreach (var (key, value) in entries)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(entries));
            if (key.Length > MaxKeyBytes)
                throw new ArgumentException(
                    $"A name-tree key is {key.Length} bytes; the limit is {MaxKeyBytes} (ISO 19005-2, 6.1.13).",
                    nameof(entries));
            PdfTreeNodes.ValidateValue(value, nameof(entries));
            list.Add((key.ToArray(), value));
        }

        var sorted = list.ToArray();
        Array.Sort(sorted, static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        var keys = new PdfObject[sorted.Length];
        var values = new PdfObject[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            if (i > 0 && sorted[i - 1].Key.AsSpan().SequenceEqual(sorted[i].Key))
                throw new ArgumentException(
                    $"Duplicate name-tree key (bytes {Preview(sorted[i].Key)}); 7.9.6 requires each key to appear once.",
                    nameof(entries));
            keys[i] = new PdfLiteralString(sorted[i].Key);
            values[i] = sorted[i].Value;
        }

        return PdfTreeNodes.Build(keys, values, NamesName, registry);
    }

    private static string Preview(byte[] key)
    {
        const int shown = 16;
        if (key.Length == 0)
            return "(empty key)";
        var hex = Convert.ToHexString(key.AsSpan(0, Math.Min(key.Length, shown)));
        return key.Length > shown ? $"{hex}... ({key.Length} bytes)" : hex;
    }
}
