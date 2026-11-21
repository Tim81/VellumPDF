// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// The rule for the name of a named destination, and its encoding as a name-tree key (#262).
/// Internal: <see cref="Annotations.PdfLinkAnnotation.DestName"/> and
/// <see cref="PdfDocument.AddNamedDestination"/> apply it, and their documentation states it.
/// </summary>
/// <remarks>
/// A name is 1 to 127 characters, each one of <c>A-Z a-z 0-9 . _ - :</c>. ISO 32000-2, 12.3.2.4 has
/// the <c>/Dests</c> name tree map "name strings" to destinations, and 7.9.6 and J.3.3 have a
/// reader compare keys byte by byte, so any self-consistent encoding is valid. This rule is
/// narrower on purpose: ASCII bytes are the same in every encoding a reader might assume, they sort
/// the same under a byte-wise and a converted comparison, and a name that fits is also within the
/// limit ISO 19005-2, 6.1.13 puts on a name object (127 bytes) and a string (32,767 bytes) in a
/// PDF/A-2 file. That part of the standard is not held in this repository, and the validator's own
/// limits rule enforces the same numbers.
/// <para>A name that breaks the rule is refused when it is added to a table, not when the table is
/// built. The message gives the length for a length defect and, for a bad character, its UTF-16
/// index and code point. It never quotes the name, so a hostile name cannot flood a log.</para>
/// </remarks>
internal static class DestinationName
{
    /// <summary>The longest name accepted, in characters.</summary>
    internal const int MaxLength = 127;

    /// <summary>
    /// Refuses a name that breaks the rule. A <see langword="null"/> name throws
    /// <see cref="ArgumentNullException"/>; an empty name, one longer than <see cref="MaxLength"/>
    /// characters, or one holding any other character throws <see cref="ArgumentException"/>. The
    /// length is checked first, so a long name that also holds a bad character is reported as too
    /// long.
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <param name="paramName">The parameter name carried by the exception.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, is longer than
    /// <see cref="MaxLength"/> characters, or holds a character outside
    /// <c>A-Z a-z 0-9 . _ - :</c>.</exception>
    internal static void Validate(string? name, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        if (name.Length == 0)
            throw new ArgumentException("A destination name is empty; it needs 1 to 127 characters.", paramName);
        if (name.Length > MaxLength)
            throw new ArgumentException($"A destination name has {name.Length} characters; the limit is {MaxLength}.", paramName);

        for (var i = 0; i < name.Length; i++)
        {
            if (IsAllowed(name[i]))
                continue;
            var codePoint = char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1])
                ? char.ConvertToUtf32(name[i], name[i + 1])
                : name[i];
            throw new ArgumentException(
                $"A destination name may hold only A-Z, a-z, 0-9, '.', '_', '-' and ':'; the character at index {i} is U+{codePoint:X4}.",
                paramName);
        }
    }

    /// <summary>
    /// The name as the bytes of its ASCII characters, which is the key the name tree stores.
    /// Applies <see cref="Validate"/> first, so it throws what that method throws, with the
    /// parameter name <c>name</c>.
    /// </summary>
    /// <param name="name">The name to encode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, is longer than
    /// <see cref="MaxLength"/> characters, or holds a character outside
    /// <c>A-Z a-z 0-9 . _ - :</c>.</exception>
    internal static byte[] Encode(string name)
    {
        Validate(name, nameof(name));
        var bytes = new byte[name.Length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)name[i];
        return bytes;
    }

    private static bool IsAllowed(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or ':';
}
