// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

internal enum XrefEntryKind
{
    Uncompressed,
    InObjectStream,
}

internal readonly struct XrefEntry
{
    public XrefEntryKind Kind { get; }
    public long Offset { get; }

    /// <summary>
    /// The generation recorded for this object in the cross-reference table (the middle field of a
    /// classic 20-byte entry, or the third <c>/W</c> field of a type-1 xref-stream row). A reference
    /// whose generation does not match this must resolve to nothing (ISO 32000-2 §7.3.10). Objects
    /// compressed into an <c>/ObjStm</c> are always generation 0 (§7.5.7).
    /// </summary>
    public int Generation { get; }

    public int ObjStmObjectNumber { get; }
    public int IndexInObjStm { get; }

    private XrefEntry(XrefEntryKind kind, long offset, int generation, int objStmObjectNumber, int indexInObjStm)
    {
        Kind = kind;
        Offset = offset;
        Generation = generation;
        ObjStmObjectNumber = objStmObjectNumber;
        IndexInObjStm = indexInObjStm;
    }

    public static XrefEntry Uncompressed(long offset, int generation) =>
        new(XrefEntryKind.Uncompressed, offset, generation, 0, 0);

    public static XrefEntry InObjStm(int objStmObjNum, int indexInObjStm) =>
        new(XrefEntryKind.InObjectStream, 0, 0, objStmObjNum, indexInObjStm);
}
