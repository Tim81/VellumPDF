// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF array object (ISO 32000-2 §7.3.6).</summary>
public sealed class PdfArray : PdfObject
{
    private readonly List<PdfObject> _items;

    /// <summary>Creates an empty array.</summary>
    public PdfArray() => _items = [];
    /// <summary>Creates an array populated with <paramref name="items"/>.</summary>
    /// <remarks>
    /// A null element is refused, as it is by <see cref="Add"/> (#523). The message names its index.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="items"/> is <see langword="null"/> or contains <see langword="null"/>.
    /// </exception>
    public PdfArray(IEnumerable<PdfObject> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i] is null)
                throw new ArgumentNullException(nameof(items), $"The element at index {i} is null.");
        }
    }

    /// <summary>The number of items in the array.</summary>
    public int Count => _items.Count;
    /// <summary>Gets the item at index <paramref name="i"/>.</summary>
    public PdfObject this[int i] => _items[i];

    /// <summary>Appends <paramref name="obj"/> to the array and returns this array.</summary>
    /// <remarks>
    /// A null <paramref name="obj"/> is refused (#523). It used to be added, <see cref="Count"/>
    /// reported it, and <c>WriteTo</c> threw <see cref="NullReferenceException"/> later, far from
    /// this call. A PDF null is <see cref="PdfNull"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="obj"/> is <see langword="null"/>.</exception>
    public PdfArray Add(PdfObject obj)
    {
        // A null item reported Count 1 and failed later, in WriteTo, far from this call (#523).
        ArgumentNullException.ThrowIfNull(obj);
        _items.Add(obj);
        return this;
    }

    /// <summary>
    /// Replaces the item at <paramref name="index"/> in place. Internal: existing callers rebuild a
    /// new array (see <c>PdfObjectRemapper</c>) rather than mutate a shared one, but the reader's
    /// decrypt walk owns a just-parsed, not-yet-cached object graph and mutates it in place for the
    /// same reason <c>PdfObjectRemapper.RemapStreamInPlace</c> does for a stream's dictionary.
    /// </summary>
    internal void SetAt(int index, PdfObject obj) => _items[index] = obj;

    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
    /// <remarks>
    /// An array that holds itself, directly or through other arrays and dictionaries, is refused
    /// (#521). Writing it would recurse until the stack overflowed, which cannot be caught. A
    /// member that is an indirect reference is written as a reference and not followed, so a cycle
    /// through indirect objects, which PDF allows, is not affected. The same object placed twice,
    /// side by side, is not a cycle.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// This array contains itself.
    /// </exception>
    public override void WriteTo(PdfWriter writer)
    {
        writer.EnterContainer(this);
        try
        {
            writer.WriteAscii("["u8);
            for (var i = 0; i < _items.Count; i++)
            {
                if (i > 0) writer.WriteByte((byte)' ');
                _items[i].WriteTo(writer);
            }
            writer.WriteAscii("]"u8);
        }
        finally
        {
            writer.LeaveContainer(this);
        }
    }
}
