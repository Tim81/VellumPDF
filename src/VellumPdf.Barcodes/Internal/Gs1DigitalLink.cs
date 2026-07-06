// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Builds a canonical GS1 Digital Link URI from an element string. The URI's path carries the
/// identification key first, then any remaining AIs as <c>/{ai}/{value}</c> segment pairs, under
/// the community resolver host <c>id.gs1.org</c> (GS1 Digital Link Standard: URI Syntax). Value
/// segments are percent-encoded per RFC 3986's path rules. This is the compressed-free
/// "uncompressed" form; it does not reorder attribute AIs beyond moving the primary key first.
/// </summary>
internal static class Gs1DigitalLink
{
    private const string CanonicalHost = "id.gs1.org";

    // The GS1 primary identification keys, by AI, in the order the URI syntax prefers when more
    // than one identifier is present. GTIN (01) is by far the most common; the others cover the
    // remaining keys the syntax defines a path position for.
    private static readonly string[] PrimaryKeyAisInPreferredOrder =
        ["01", "8006", "8013", "8010", "414", "417", "8017", "8018", "255", "00", "253", "401", "402", "8003", "8004"];

    /// <summary>
    /// Builds the canonical Digital Link URI for the given GS1 element string (accepted in either
    /// the raw-payload or parenthesized convention, via <see cref="Gs1ElementString.Parse"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The element string has no application identifiers.</exception>
    /// <exception cref="FormatException">The element string is not well-formed, or contains no primary identification key.</exception>
    internal static string Build(string elementString)
    {
        var parsed = Gs1ElementString.Parse(elementString);
        return Build(parsed.Elements);
    }

    /// <summary>Builds the canonical Digital Link URI from already-parsed (AI, value) pairs.</summary>
    /// <exception cref="FormatException">No element in <paramref name="elements"/> is a primary identification key.</exception>
    internal static string Build(IReadOnlyList<Gs1Element> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var primaryIndex = FindPrimaryKeyIndex(elements);
        if (primaryIndex < 0)
            throw new FormatException("A GS1 Digital Link URI requires a primary identification key (e.g. AI 01, GTIN).");

        var builder = new StringBuilder("https://").Append(CanonicalHost);

        var primary = elements[primaryIndex];
        AppendSegmentPair(builder, primary);

        for (var i = 0; i < elements.Count; i++)
        {
            if (i == primaryIndex) continue;
            AppendSegmentPair(builder, elements[i]);
        }

        return builder.ToString();
    }

    private static int FindPrimaryKeyIndex(IReadOnlyList<Gs1Element> elements)
    {
        foreach (var ai in PrimaryKeyAisInPreferredOrder)
            for (var i = 0; i < elements.Count; i++)
                if (elements[i].Ai == ai)
                    return i;
        return -1;
    }

    private static void AppendSegmentPair(StringBuilder builder, Gs1Element element)
    {
        builder.Append('/').Append(element.Ai).Append('/').Append(EncodePathSegment(element.Value));
    }

    /// <summary>
    /// Percent-encodes a value for use as a single URI path segment: the RFC 3986 unreserved set
    /// (letters, digits, <c>- . _ ~</c>) passes through, as do the sub-delimiters the syntax
    /// permits unescaped in a Digital Link path value; everything else becomes <c>%HH</c>.
    /// </summary>
    private static string EncodePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (IsUnreservedOrAllowed(c))
            {
                builder.Append(c);
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes([c]))
                    builder.Append('%').Append(((int)b).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreservedOrAllowed(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}
