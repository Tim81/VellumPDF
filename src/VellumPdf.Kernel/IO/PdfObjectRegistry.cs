// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.IO;

/// <summary>
/// General-purpose indirect-object allocator for a single PDF revision.
///
/// Usage:
///   1. Call Reserve to obtain a PdfIndirectReference with an assigned object number.
///   2. Call SetValue to attach the PdfObject payload.
///   3. At serialisation time call WriteAll which walks entries in object-number order,
///      records each byte offset into the supplied CrossReferenceBuilder, and writes
///      the indirect-object wrappers.
/// </summary>
public sealed class PdfObjectRegistry
{
    private readonly List<PdfObject?> _values = []; // index = objectNumber - 1

    /// <summary>
    /// Reserves the next object number and returns its reference.
    /// The payload must be set via SetValue before WriteAll is called.
    /// </summary>
    public PdfIndirectReference Reserve()
    {
        _values.Add(null);
        return new PdfIndirectReference(_values.Count);
    }

    /// <summary>
    /// Reserves an object number and immediately assigns its value.
    /// Equivalent to <c>Reserve()</c> followed by <c>SetValue(ref, value)</c>.
    /// </summary>
    public PdfIndirectReference Add(PdfObject value)
    {
        _values.Add(value);
        return new PdfIndirectReference(_values.Count);
    }

    /// <summary>Assigns (or replaces) the value for a previously reserved reference.</summary>
    public void SetValue(PdfIndirectReference reference, PdfObject value)
    {
        var idx = reference.ObjectNumber - 1;
        if (idx < 0 || idx >= _values.Count)
            throw new ArgumentOutOfRangeException(nameof(reference), "Reference was not allocated by this registry.");
        _values[idx] = value;
    }

    /// <summary>
    /// Writes all registered indirect objects to <paramref name="writer"/> in object-number order,
    /// recording each byte offset into <paramref name="xref"/>.
    /// </summary>
    public void WriteAll(PdfWriter writer, CrossReferenceBuilder xref)
        => WriteAll(writer, xref, preWrite: null);

    /// <summary>
    /// Writes all registered indirect objects with an optional per-object pre-write hook.
    /// <paramref name="preWrite"/> receives the object number and may return a cleanup action
    /// invoked after the object is written (e.g. to restore writer state).
    /// Returning null from the delegate means no cleanup is needed.
    /// </summary>
    public void WriteAll(PdfWriter writer, CrossReferenceBuilder xref, Func<int, Action?>? preWrite)
    {
        for (var i = 0; i < _values.Count; i++)
        {
            var value = _values[i];
            if (value is null)
                throw new InvalidOperationException($"Object {i + 1} was reserved but never assigned a value.");

            var objNum = i + 1;
            var cleanup = preWrite?.Invoke(objNum);
            xref.ReserveObjectNumber(writer.Position);
            new PdfIndirectObject(objNum, value).WriteTo(writer);
            writer.WriteByte((byte)'\n');
            cleanup?.Invoke();
        }
    }
}
