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
    public int ObjStmObjectNumber { get; }
    public int IndexInObjStm { get; }

    private XrefEntry(XrefEntryKind kind, long offset, int objStmObjectNumber, int indexInObjStm)
    {
        Kind = kind;
        Offset = offset;
        ObjStmObjectNumber = objStmObjectNumber;
        IndexInObjStm = indexInObjStm;
    }

    public static XrefEntry Uncompressed(long offset) =>
        new(XrefEntryKind.Uncompressed, offset, 0, 0);

    public static XrefEntry InObjStm(int objStmObjNum, int indexInObjStm) =>
        new(XrefEntryKind.InObjectStream, 0, objStmObjNum, indexInObjStm);
}
