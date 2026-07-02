// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.9 (Indirect objects). In an <c>N G obj … endobj</c> indirect object the object
/// number and generation number shall be separated by a single white-space character, the generation
/// number and the <c>obj</c> keyword likewise, the object number shall be preceded by an EOL marker,
/// the <c>obj</c> keyword shall be followed by an EOL marker, the <c>endobj</c> keyword shall be
/// preceded by an EOL marker, and the <c>endobj</c> keyword shall be followed by an EOL marker
/// (§6.1.9-1).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.9 and ISO 32000-1:2008, 7.3.10. Clean-room: derived from the
/// specification text, not from any third-party validation profile. Each object is located by its
/// cross-reference byte offset (<see cref="PreflightContext.ObjectOffset"/>) and the few header bytes
/// there are inspected directly, so the check never scans for <c>obj</c> (which also appears inside
/// <c>endobj</c> and could occur in a binary body) and is safe across incremental updates.
/// <para>
/// The <c>endobj</c> EOL checks use <see cref="PreflightContext.ObjectEndOffset"/> and are scoped to
/// objects in the newest revision only (using <see cref="PreflightContext.Revisions"/>), to avoid
/// re-validating superseded objects whose end boundaries may have been overwritten by a later revision.
/// A null end-offset (object stream resident, or not found in the scan window) is treated as
/// indeterminate and does not fire.
/// </para>
/// </remarks>
internal sealed class ObjectLayoutRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.9-object-spacing";

    public string Clause => "ISO 19005-2:2011, 6.1.9";

    public void Evaluate(PreflightContext context)
    {
        var bytes = context.FileBytes.Span;

        // For the endobj boundary checks, scope to objects in the newest revision only:
        // objects written in a prior incremental revision may share byte-ranges with newer
        // revisions' bodies, so re-validating them risks false positives. Objects in the newest
        // revision have header offsets between the previous revision's xref offset (exclusive) and
        // the newest revision's xref offset (exclusive). For a single-revision file every object
        // qualifies (prevXrefEnd == 0).
        var newestXrefOffset = context.Revisions.Count > 0
            ? context.Revisions[^1].XrefOffset
            : int.MaxValue;
        var prevXrefEnd = context.Revisions.Count >= 2
            ? context.Revisions[^2].XrefOffset
            : 0;

        foreach (var objectNumber in context.ObjectNumbers)
        {
            if (context.ObjectOffset(objectNumber) is not { } offsetLong)
                continue;
            var offset = (int)offsetLong;
            if (offset < 0 || offset >= bytes.Length)
                continue;

            if (!HeaderComplies(bytes, offset))
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "An indirect object's 'N G obj' header is not laid out as required: the object and "
                    + "generation numbers and the obj keyword shall be separated by single white-space "
                    + "characters, the object number preceded by an EOL, and obj followed by an EOL (§6.1.9).");
                return; // One report suffices; the verdict is unaffected by the count.
            }

            // endobj EOL checks: only for objects in the newest revision's body.
            if (offset < prevXrefEnd || offset >= newestXrefOffset)
                continue;

            if (context.ObjectEndOffset(objectNumber) is not { } endOffset)
                continue; // Cannot verify (object stream or scan miss) — FP-safe, do not fire.

            if (!EndobjComplies(bytes, endOffset))
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "An indirect object's 'endobj' keyword is not surrounded by EOL markers as required (§6.1.9).");
                return;
            }
        }
    }

    // Validates "<EOL>N<ws>G<ws>obj<EOL>" at the object's byte offset.
    private static bool HeaderComplies(ReadOnlySpan<byte> bytes, int offset)
    {
        // The object number shall be preceded by an EOL marker.
        if (offset > 0 && !IsEol(bytes[offset - 1]))
            return false;

        var p = offset;
        if (!ReadDigits(bytes, ref p)) // object number
            return true; // not a digit-led object header — leave to other rules, don't false-positive.
        if (!SingleWhitespace(bytes, ref p)) // between object and generation number
            return false;
        if (!ReadDigits(bytes, ref p)) // generation number
            return false;
        if (!SingleWhitespace(bytes, ref p)) // between generation number and obj
            return false;
        if (!Matches(bytes, p, "obj"u8)) // obj keyword
            return false;
        p += 3;

        // The obj keyword shall be followed by an EOL marker.
        return p < bytes.Length && IsEol(bytes[p]);
    }

    private static bool ReadDigits(ReadOnlySpan<byte> bytes, ref int p)
    {
        var start = p;
        while (p < bytes.Length && bytes[p] is >= (byte)'0' and <= (byte)'9')
            p++;
        return p > start;
    }

    // Exactly one PDF white-space byte at p (the following byte must not also be white-space).
    private static bool SingleWhitespace(ReadOnlySpan<byte> bytes, ref int p)
    {
        if (p >= bytes.Length || !IsWhite(bytes[p]))
            return false;
        p++;
        return p >= bytes.Length || !IsWhite(bytes[p]);
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, int at, ReadOnlySpan<byte> word)
        => at >= 0 && at + word.Length <= bytes.Length && bytes.Slice(at, word.Length).SequenceEqual(word);

    // Validates the two endobj EOL requirements using the offset one past the 'endobj' keyword.
    // endOffset is the position of the first byte after 'endobj' (6 bytes).
    //   • The byte preceding 'endobj' (bytes[endOffset - 7]) shall be an EOL marker.
    //   • The byte following 'endobj' (bytes[endOffset]) shall be an EOL marker.
    // Returns true (compliant) when both conditions hold or when bounds prevent a check.
    private static bool EndobjComplies(ReadOnlySpan<byte> bytes, int endOffset)
    {
        const int kEndobj = 6; // length of "endobj"
        var startOfEndobj = endOffset - kEndobj;
        if (startOfEndobj <= 0)
            return true; // cannot verify preceding EOL — FP-safe
        if (!IsEol(bytes[startOfEndobj - 1]))
            return false;
        if (endOffset >= bytes.Length)
            return true; // at end of file — FP-safe (%%EOF may follow)
        return IsEol(bytes[endOffset]);
    }

    private static bool IsEol(byte b) => b is (byte)'\r' or (byte)'\n';

    // PDF white-space characters (ISO 32000-1 Table 1).
    private static bool IsWhite(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;
}
