// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.IO.Linearization;

/// <summary>
/// Builds the primary linearization hint stream body (ISO 32000-2 §F.3): a page-offset
/// hint table (§F.3.1) followed by a shared-object hint table (§F.3.2), both bit-packed
/// MSB-first and column-major with a byte boundary after every column.
///
/// The layout, field order, and bit-width choices match qpdf's encoder (which the CI
/// oracle validates): each numeric field is delta-encoded against a per-table minimum,
/// and the number of bits for a delta is its bit length. Following Acrobat implementation
/// note 126, the content-stream offset is always 0 and the content-stream length mirrors
/// the page length.
///
/// All file offsets passed in must already be in the hint stream's coordinate system
/// (see the H-relative convention in the caller): offsets at or past the hint stream are
/// measured as if the hint stream had zero length.
/// </summary>
internal static class HintStreamBuilder
{
    /// <summary>A page's contribution to the page-offset hint table.</summary>
    /// <param name="ObjectCount">Number of objects that belong to the page.</param>
    /// <param name="Length">Total byte length of the page's objects in the file.</param>
    /// <param name="SharedIds">Indices into the shared-object table that this page references.</param>
    internal sealed record PageHint(int ObjectCount, int Length, IReadOnlyList<int> SharedIds);

    /// <summary>A shared object's contribution to the shared-object hint table.</summary>
    /// <param name="GroupLength">Byte length of the shared object (group) in the file.</param>
    internal sealed record SharedHint(int GroupLength);

    /// <summary>The outline hint table entry (ISO 32000-2 §F.3.4). Present only when outlines exist.</summary>
    /// <param name="FirstObjNum">Object number of the outlines root.</param>
    /// <param name="FirstObjOffset">Hint-relative byte offset of the outlines root.</param>
    /// <param name="ObjectCount">Number of objects in the outline group.</param>
    /// <param name="GroupLength">Total byte length of the outline group in the file.</param>
    internal sealed record OutlineHint(int FirstObjNum, int FirstObjOffset, int ObjectCount, int GroupLength);

    /// <summary>
    /// Builds the hint stream body. Returns the raw (uncompressed) bytes, the byte offset of the
    /// shared-object table within them (the <c>/S</c> value for the hint stream dict), and the byte
    /// offset of the outline hint table (the <c>/O</c> value; equals the body length when no outlines).
    /// </summary>
    /// <param name="pages">Per-page hints, first page first.</param>
    /// <param name="firstPageOffset">
    /// The hint-relative byte offset of the first page's first object (item 2 of the header).
    /// </param>
    /// <param name="shared">Shared-object hints, in table order.</param>
    /// <param name="nsharedFirstPage">Number of shared objects that belong to the first page.</param>
    /// <param name="firstSharedObj">
    /// Object number of the first shared object not on the first page (meaningless, and passed as 0,
    /// when every shared object is on the first page).
    /// </param>
    /// <param name="firstSharedOffset">Hint-relative offset of that object (0 when meaningless).</param>
    /// <param name="outline">Outline hint table data; null when the document has no outlines.</param>
    public static (byte[] Body, int SharedOffset, int OutlineOffset) Build(
        IReadOnlyList<PageHint> pages,
        int firstPageOffset,
        IReadOnlyList<SharedHint> shared,
        int nsharedFirstPage,
        int firstSharedObj,
        int firstSharedOffset,
        OutlineHint? outline = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pages.Count, nameof(pages));

        var w = new BitWriter();

        // ── Page offset hint table header (§F.3.1, 13 fields) ────────────────────
        var minNobjects = pages.Min(p => p.ObjectCount);
        var nbitsDeltaNobjects = NBits(pages.Max(p => p.ObjectCount) - minNobjects);
        var minPageLength = pages.Min(p => p.Length);
        var nbitsDeltaPageLength = NBits(pages.Max(p => p.Length) - minPageLength);
        var nbitsNshared = NBits(pages.Max(p => p.SharedIds.Count));
        var nsharedTotal = shared.Count;
        var nbitsSharedId = NBits(nsharedTotal);

        w.WriteBits((uint)minNobjects, 32);                 // 1  min objects per page
        w.WriteBits((uint)firstPageOffset, 32);             // 2  first page's first object offset
        w.WriteBits((uint)nbitsDeltaNobjects, 16);          // 3
        w.WriteBits((uint)minPageLength, 32);               // 4  min page length
        w.WriteBits((uint)nbitsDeltaPageLength, 16);        // 5
        w.WriteBits(0, 32);                                 // 6  min content offset (always 0)
        w.WriteBits(0, 16);                                 // 7  nbits delta content offset (always 0)
        w.WriteBits((uint)minPageLength, 32);               // 8  min content length mirrors page length
        w.WriteBits((uint)nbitsDeltaPageLength, 16);        // 9  nbits delta content length mirrors 5
        w.WriteBits((uint)nbitsNshared, 16);                // 10
        w.WriteBits((uint)nbitsSharedId, 16);               // 11
        w.WriteBits(0, 16);                                 // 12 nbits shared numerator (unused)
        w.WriteBits(4, 16);                                 // 13 shared denominator (qpdf: value is unused)

        // ── Per-page columns (column-major; byte boundary after each column) ─────
        foreach (var p in pages) w.WriteBits((uint)(p.ObjectCount - minNobjects), nbitsDeltaNobjects);
        w.SkipToNextByte();
        foreach (var p in pages) w.WriteBits((uint)(p.Length - minPageLength), nbitsDeltaPageLength);
        w.SkipToNextByte();
        foreach (var p in pages) w.WriteBits((uint)p.SharedIds.Count, nbitsNshared);
        w.SkipToNextByte();
        foreach (var p in pages)
            foreach (var id in p.SharedIds)
                w.WriteBits((uint)id, nbitsSharedId);
        w.SkipToNextByte();
        // shared numerators: 0 bits each — nothing to write, but the column still aligns.
        w.SkipToNextByte();
        // content offset deltas: 0 bits each.
        w.SkipToNextByte();
        foreach (var p in pages) w.WriteBits((uint)(p.Length - minPageLength), nbitsDeltaPageLength);
        w.SkipToNextByte();

        var sharedOffset = w.ByteCount;

        // ── Shared object hint table (§F.3.2) ────────────────────────────────────
        var minGroupLength = shared.Count > 0 ? shared.Min(s => s.GroupLength) : 0;
        var nbitsDeltaGroup = shared.Count > 0 ? NBits(shared.Max(s => s.GroupLength) - minGroupLength) : 0;

        // Fields 1-2: 32-bit per spec; casts assume sub-2 GB offsets (qpdf's own limit).
        w.WriteBits((uint)firstSharedObj, 32);              // 1
        w.WriteBits((uint)firstSharedOffset, 32);           // 2
        w.WriteBits((uint)nsharedFirstPage, 32);            // 3
        w.WriteBits((uint)nsharedTotal, 32);                // 4
        w.WriteBits(0, 16);                                 // 5  nbits nobjects (each shared is one object)
        w.WriteBits((uint)minGroupLength, 32);              // 6
        w.WriteBits((uint)nbitsDeltaGroup, 16);             // 7

        foreach (var s in shared) w.WriteBits((uint)(s.GroupLength - minGroupLength), nbitsDeltaGroup);
        w.SkipToNextByte();
        foreach (var _ in shared) w.WriteBits(0, 1); // signature_present — never set
        w.SkipToNextByte();
        // nobjects_minus_one: 0 bits each.
        w.SkipToNextByte();

        var outlineOffset = w.ByteCount;

        if (outline is not null)
        {
            // Outline hint table (§F.3.4): four 32-bit big-endian fields.
            // Casts to uint are intentional: the format stores 32-bit values and qpdf's
            // own hint-table parser uses uint throughout, implying a sub-2 GB file limit.
            w.WriteBits((uint)outline.FirstObjNum, 32);
            w.WriteBits((uint)outline.FirstObjOffset, 32);
            w.WriteBits((uint)outline.ObjectCount, 32);
            w.WriteBits((uint)outline.GroupLength, 32);
            w.SkipToNextByte();
        }

        return (w.ToArray(), sharedOffset, outlineOffset);
    }

    /// <summary>The number of bits needed to represent <paramref name="value"/> (0 needs 0 bits).</summary>
    private static int NBits(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Hint-table values cannot be negative.");
        var bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }
        return bits;
    }
}
