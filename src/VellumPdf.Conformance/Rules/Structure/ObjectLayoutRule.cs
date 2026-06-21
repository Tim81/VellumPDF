// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.9 (Indirect objects). In an <c>N G obj … endobj</c> indirect object the object
/// number and generation number shall be separated by a single white-space character, the generation
/// number and the <c>obj</c> keyword likewise, the object number shall be preceded by an EOL marker,
/// and the <c>obj</c> keyword shall be followed by an EOL marker (§6.1.9-1).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.9 and ISO 32000-1:2008, 7.3.10. Clean-room: derived from the
/// specification text, not from any third-party validation profile. Each object is located by its
/// cross-reference byte offset (<see cref="PreflightContext.ObjectOffset"/>) and the few header bytes
/// there are inspected directly, so the check never scans for <c>obj</c> (which also appears inside
/// <c>endobj</c> and could occur in a binary body) and is safe across incremental updates.
/// <para>
/// The two further §6.1.9-1 sub-conditions on the <c>endobj</c> keyword (it shall be preceded and
/// followed by an EOL marker) require the object's end boundary, which is not reliably recoverable for
/// every object across revisions without risking a false positive, and are deferred.
/// </para>
/// </remarks>
internal sealed class ObjectLayoutRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.9-object-spacing";

    public string Clause => "ISO 19005-2:2011, 6.1.9";

    public void Evaluate(PreflightContext context)
    {
        var bytes = context.FileBytes.Span;
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

    private static bool IsEol(byte b) => b is (byte)'\r' or (byte)'\n';

    // PDF white-space characters (ISO 32000-1 Table 1).
    private static bool IsWhite(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;
}
