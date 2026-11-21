// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Document;

/// <summary>
/// A document's named destinations (#262): each name maps to a position on a page, and
/// <see cref="Build"/> turns the table into the <c>/Dests</c> name tree (ISO 32000-2, 12.3.2.4 and
/// 7.9.6). Internal: a <see cref="PdfDocument"/> holds one, filled by
/// <see cref="PdfDocument.AddNamedDestination"/>, and both save paths write its tree into the
/// catalog.
/// </summary>
/// <remarks>
/// <b>What is refused.</b> <see cref="Add"/> throws at the call for a name that breaks the rule of
/// <see cref="DestinationName"/>, a name already in the table, a <see langword="null"/> page, and a
/// coordinate that is not finite or lies beyond ±3.403 × 10^38. <see cref="Build"/> throws
/// <see cref="InvalidOperationException"/> for a page the document does not own. The document
/// refuses such a page at its own call, so <see cref="Build"/> is a backstop there.
/// <para><see cref="Build"/> reads the table and never changes it, so a save that is retried after
/// a precondition failure builds the same objects again.</para>
/// </remarks>
internal sealed class NamedDestinationTable
{
    // ISO 32000-1 Table C.1 (physical p. 657) gives 3.403e38 as the largest real; PdfReal documents
    // that figure for every other coordinate in the library.
    // A whole value outside the 32-bit integer range is written as a real token (PdfNumberFormat),
    // so every value up to this bound is writable. The bound is symmetric. The library applies it
    // by its own choice: PdfReal itself accepts any finite value.
    private const double MaxCoordinate = 3.403e38;

    private readonly Dictionary<string, (PdfPage Page, double Left, double Top)> _entries = new(StringComparer.Ordinal);

    /// <summary>The number of destinations added.</summary>
    internal int Count => _entries.Count;

    /// <summary>Whether a destination with this name was added. A name outside the rule of
    /// <see cref="DestinationName"/> is not found; <paramref name="name"/> must not be
    /// <see langword="null"/>.</summary>
    internal bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>
    /// Adds a destination that shows the point (<paramref name="left"/>, <paramref name="top"/>) of
    /// <paramref name="page"/>, in default user space, at the current zoom.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="page"/>
    /// is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the rule of
    /// <see cref="DestinationName"/>, or is already in the table.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="left"/> or
    /// <paramref name="top"/> is not finite or lies beyond ±3.403 × 10^38.</exception>
    internal void Add(string name, PdfPage page, double left, double top)
    {
        DestinationName.Validate(name, nameof(name));
        ArgumentNullException.ThrowIfNull(page);
        CheckCoordinate(left, nameof(left));
        CheckCoordinate(top, nameof(top));
        // A name tree maps each key to one value and a reader compares keys byte by byte (7.9.6);
        // the writer would refuse the pair too, but only at Build and with a hex preview of the
        // key.
        if (!_entries.TryAdd(name, (page, left, top)))
            throw new ArgumentException($"The destination name '{name}' is already in the table.", nameof(name));
    }

    /// <summary>
    /// Registers one array per destination, in name order, then the tree's nodes, and returns the
    /// tree's root, which is not registered. Returns <see langword="null"/> for an empty table and
    /// leaves the registry alone, so a catalog without destinations keeps its object numbers and
    /// writes no entry.
    /// </summary>
    /// <param name="registry">Where the arrays and the tree's nodes are registered.</param>
    /// <param name="pageRefs">The reference of every page the document owns.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or
    /// <paramref name="pageRefs"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A destination's page is not in
    /// <paramref name="pageRefs"/>. Nothing has been registered when this is thrown.</exception>
    internal PdfDictionary? Build(PdfObjectRegistry registry, IReadOnlyDictionary<PdfPage, PdfIndirectReference> pageRefs)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pageRefs);
        if (_entries.Count == 0)
            return null;

        // Ordinal order is the order the tree sorts into for ASCII names, and fixes the object
        // numbers.
        var names = _entries.Keys.ToArray();
        Array.Sort(names, StringComparer.Ordinal);

        // Check every page before registering anything, so a refusal leaves the registry as it was.
        foreach (var name in names)
        {
            if (!pageRefs.ContainsKey(_entries[name].Page))
                throw new InvalidOperationException($"The page of destination '{name}' does not belong to this document.");
        }

        var tree = new List<KeyValuePair<byte[], PdfObject>>(names.Length);
        foreach (var name in names)
        {
            var (page, left, top) = _entries[name];
            // 7.9.6: dictionary, array and string values "should be specified by indirect object
            // references".
            var value = registry.Add(BuildValue(pageRefs[page], left, top));
            tree.Add(new KeyValuePair<byte[], PdfObject>(DestinationName.Encode(name), value));
        }
        return NameTreeWriter.Write(tree, registry);
    }

    /// <summary>
    /// The value stored under a name: <c>[page /XYZ left top null]</c>, ISO 32000-2 Table 149. A
    /// null zoom leaves the zoom as it is. <see cref="Build"/> stores the array under each name.
    /// </summary>
    private static PdfArray BuildValue(PdfIndirectReference page, double left, double top) =>
        new([page, new PdfName("XYZ"), new PdfReal(left), new PdfReal(top), PdfNull.Instance]);

    private static void CheckCoordinate(double value, string paramName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > MaxCoordinate)
            throw new ArgumentOutOfRangeException(paramName, value, "A destination coordinate must be finite and within ±3.403 × 10^38.");
    }
}
