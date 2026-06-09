// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

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

    /// <summary>The number of objects currently registered (= highest object number).</summary>
    public int ObjectCount => _values.Count;

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

    /// <summary>
    /// Compressed write path (PDF 1.5+, ISO 32000-2 §7.5.7–7.5.8): packs non-stream
    /// indirect objects into a single ObjStm; stream objects stay as type-1 entries.
    ///
    /// Object numbers are allocated as follows (caller must have pre-reserved them
    /// and must NOT have added them to this registry):
    ///   <paramref name="objStmObjectNumber"/>: the object stream itself.
    ///   <paramref name="xrefObjNumber"/>:      the XRef stream (written by caller).
    ///
    /// Returns a <see cref="CrossReferenceStreamBuilder"/> with all type-1 and type-2
    /// xref entries recorded. The caller is responsible for writing the XRef stream
    /// object (which becomes a type-1 entry that the caller records after this returns).
    ///
    /// The xref entries are stored by object number so they appear in the correct
    /// order in the XRef stream regardless of physical write order.
    /// </summary>
    internal CrossReferenceStreamBuilder WriteAllCompressed(
        PdfWriter writer,
        int objStmObjectNumber,
        int xrefObjNumber)
    {
        // Validate that the two reserved object numbers don't collide with existing objects.
        // All existing objects use numbers 1.._values.Count; the two new objects must be
        // higher (caller is expected to have allocated them at _values.Count+1 and _values.Count+2).
        if (objStmObjectNumber <= _values.Count || xrefObjNumber <= _values.Count)
            throw new ArgumentException("objStmObjectNumber and xrefObjNumber must be greater than all registered object numbers.");
        if (objStmObjectNumber == xrefObjNumber)
            throw new ArgumentException("objStmObjectNumber and xrefObjNumber must be distinct.");

        // Classify objects:
        //   - PdfStream (or subclass): type-1 (uncompressed indirect object)
        //   - everything else:          type-2 (packed into ObjStm)
        var packable = new ObjectStreamBuilder();
        // packableObjNums[i] = the object number of the i-th object added to packable,
        // so we can set the right type-2 xref entries after Build().
        var packableObjNums = new List<int>();
        var streamObjNums = new List<int>();
        var streamObjValues = new List<PdfObject>();

        for (var i = 0; i < _values.Count; i++)
        {
            var value = _values[i];
            if (value is null)
                throw new InvalidOperationException($"Object {i + 1} was reserved but never assigned a value.");

            var objNum = i + 1;
            if (value is PdfStream)
            {
                streamObjNums.Add(objNum);
                streamObjValues.Add(value);
            }
            else
            {
                packable.Add(objNum, value);
                packableObjNums.Add(objNum);
            }
        }

        var xref = new CrossReferenceStreamBuilder();

        // ── Write stream objects (type-1 entries) in object-number order ────
        for (var i = 0; i < streamObjNums.Count; i++)
        {
            var objNum = streamObjNums[i];
            xref.SetUncompressed(objNum, writer.Position);
            new PdfIndirectObject(objNum, streamObjValues[i]).WriteTo(writer);
            writer.WriteByte((byte)'\n');
        }

        // ── Build and write the ObjStm (type-1 entry) ───────────────────────
        if (packable.Count > 0)
        {
            var (compBody, first, n) = packable.Build();

            xref.SetUncompressed(objStmObjectNumber, writer.Position);

            var objStmDict = new PdfDictionary()
                .Set(PdfName.Type, new PdfName("ObjStm"))
                .Set(new PdfName("N"), new PdfInteger(n))
                .Set(new PdfName("First"), new PdfInteger(first))
                .Set(PdfName.Filter, PdfName.FlateDecode)
                .Set(PdfName.Length, new PdfInteger(compBody.Length));

            WriteInt(writer, objStmObjectNumber);
            writer.WriteAscii(" 0 obj\n"u8);
            objStmDict.WriteTo(writer);
            writer.WriteAscii("\nstream\n"u8);
            writer.WriteRaw(compBody);
            writer.WriteAscii("\nendstream\nendobj\n"u8);

            // Register type-2 entries: packable objects are in the ObjStm in the
            // order they were added to the ObjectStreamBuilder (same as packableObjNums).
            for (var i = 0; i < packableObjNums.Count; i++)
                xref.SetInObjectStream(packableObjNums[i], objStmObjectNumber, i);
        }

        // The XRef stream itself will be written by the caller; its type-1 entry
        // is NOT recorded here (caller sets it after this returns, using the offset
        // returned from CrossReferenceStreamBuilder.WriteXRefStream).

        return xref;
    }

    private static void WriteInt(PdfWriter w, int n)
    {
        Span<byte> buf = stackalloc byte[12];
        System.Buffers.Text.Utf8Formatter.TryFormat(n, buf, out var len);
        w.WriteAscii(buf[..len]);
    }
}
