// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// Builds a number tree (ISO 32000-2:2020, 7.9.7): keys are integers in ascending numerical order, and the
/// entries array is named <c>/Nums</c>. Internal. Nothing calls it yet.
/// </summary>
/// <remarks>
/// Node layout, <c>/Limits</c> and the no-limits-on-the-root rule are as for <see cref="NameTreeWriter"/>
/// and are documented on <see cref="PdfTreeNodes"/>. 7.9.7 says the keys are "sorted in numerical order,
/// analogously to the arrangement of keys in a name tree", and a negative key is an integer like any
/// other, so it is accepted and sorts first.
/// </remarks>
internal static class NumberTreeWriter
{
    private static readonly PdfName NumsName = new("Nums");

    /// <summary>
    /// Writes a number tree for <paramref name="entries"/> and returns its root node, which is not
    /// registered. Every other node is added to <paramref name="registry"/>. Entries may arrive in any
    /// order.
    /// </summary>
    /// <remarks>
    /// <b>No entries.</b> The result is a root holding an empty <c>/Nums</c> array; a caller with
    /// nothing to map should leave the catalog entry out instead.
    /// <para>The key range is that of a 32-bit signed integer: ISO 32000-1 Table C.1 gives -2,147,483,648 to
    /// 2,147,483,647, and ISO 19005-2, 6.1.13 holds a PDF/A-2 file to it. The repository does not hold that
    /// part, but its own <c>NumericLimitsRule</c> enforces the same range. ISO 32000-2, C.2 no longer states
    /// the figure; its Table C.1 says only that integer values "can often be expressed within 32 bits".
    /// Value rules are those of <see cref="NameTreeWriter"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="registry"/>
    /// is <see langword="null"/>, or an entry's value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A key is below -2,147,483,648 or above
    /// 2,147,483,647.</exception>
    /// <exception cref="ArgumentException">Two entries have the same key, or a value is, or contains
    /// at any depth of direct nesting, a <see cref="PdfStream"/> or a <see cref="PdfIndirectObject"/>.</exception>
    internal static PdfDictionary Write(IEnumerable<KeyValuePair<long, PdfObject>> entries, PdfObjectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(registry);

        var list = new List<KeyValuePair<long, PdfObject>>();
        foreach (var (key, value) in entries)
        {
            if (key < int.MinValue || key > int.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(entries), key,
                    "A number-tree key must fit a 32-bit signed integer (ISO 32000-1 Table C.1; ISO 19005-2, 6.1.13).");
            PdfTreeNodes.ValidateValue(value, nameof(entries));
            list.Add(new(key, value));
        }

        var sorted = list.ToArray();
        Array.Sort(sorted, static (a, b) => a.Key.CompareTo(b.Key));

        var keys = new PdfObject[sorted.Length];
        var values = new PdfObject[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            if (i > 0 && sorted[i - 1].Key == sorted[i].Key)
                throw new ArgumentException(
                    $"Duplicate number-tree key {sorted[i].Key}; 7.9.7 requires each key to appear once.",
                    nameof(entries));
            keys[i] = new PdfInteger(sorted[i].Key);
            values[i] = sorted[i].Value;
        }

        return PdfTreeNodes.Build(keys, values, NumsName, registry);
    }
}
