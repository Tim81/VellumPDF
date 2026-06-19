// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// Minimal, dependency-free reader for the few XMP properties the conformance rules need. Rather
/// than parse the whole RDF/XML packet, it locates a property token and returns the value that
/// follows it in either element (<c>&gt;value&lt;</c>) or attribute (<c>="value"</c>) form.
/// </summary>
internal static class XmpReader
{
    /// <summary>
    /// Returns the value associated with <paramref name="token"/> in <paramref name="xmp"/>, or
    /// <see langword="null"/> when the token is absent.
    /// </summary>
    public static string? GetProperty(string xmp, string token)
    {
        var idx = xmp.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var i = idx + token.Length;
        while (i < xmp.Length && char.IsWhiteSpace(xmp[i]))
            i++;
        if (i >= xmp.Length)
            return null;

        char terminator;
        if (xmp[i] == '>')
        {
            i++;
            terminator = '<';
        }
        else if (xmp[i] == '=')
        {
            i++;
            while (i < xmp.Length && char.IsWhiteSpace(xmp[i]))
                i++;
            if (i >= xmp.Length || (xmp[i] != '"' && xmp[i] != '\''))
                return null;
            terminator = xmp[i];
            i++;
        }
        else
        {
            return null;
        }

        var start = i;
        while (i < xmp.Length && xmp[i] != terminator)
            i++;
        return xmp[start..i].Trim();
    }

    /// <summary>Whether <paramref name="xmp"/> mentions <paramref name="token"/> at all.</summary>
    public static bool Contains(string xmp, string token)
        => xmp.Contains(token, StringComparison.Ordinal);
}
