// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace VellumPdf.Layout.Core;

/// <summary>
/// How a table cell and a running band read the white space in their text, so that a character
/// no font draws never reaches glyph lookup.
/// </summary>
/// <remarks>
/// A character is a control space when <see cref="char.IsWhiteSpace(char)"/> holds for it and it is
/// not in the Unicode category Space Separator: the C0 and C1 controls tab, line feed, line
/// tabulation, form feed, carriage return and next line, and the line and paragraph separators
/// U+2028 and U+2029. The no-break spaces and the other space separators, such as U+2003 EM SPACE,
/// are not control spaces: a font draws them, so they are left as written.
/// </remarks>
internal static class LayoutText
{
    /// <summary>
    /// True for a white-space character that no font draws a glyph for: every character for which
    /// <see cref="char.IsWhiteSpace(char)"/> is true except the space separators (U+0020, the
    /// no-break spaces and the Unicode space separators).
    /// </summary>
    internal static bool IsControlSpace(char c) =>
        char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.SpaceSeparator;

    /// <summary>True when <paramref name="text"/> holds a control space.</summary>
    internal static bool HasControlSpace(string? text)
    {
        if (text is null) return false;
        foreach (var c in text)
            if (IsControlSpace(c)) return true;
        return false;
    }

    /// <summary>True when <paramref name="text"/> holds a carriage return or a line feed.</summary>
    internal static bool HasLineBreak(string? text) =>
        text is not null && text.AsSpan().IndexOfAny('\r', '\n') >= 0;

    /// <summary>
    /// Splits <paramref name="text"/> into hard lines: a carriage return followed by a line feed, a
    /// lone carriage return and a lone line feed each end one. A trailing break leaves a last,
    /// empty line. Text with no break is returned as the one line it is.
    /// </summary>
    internal static string[] SplitHardLines(string text)
    {
        if (!HasLineBreak(text)) return [text];

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            lines.Add(text.Substring(start, i - start));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(text.Substring(start));
        return [.. lines];
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every control space removed, or the same instance when
    /// it holds none.
    /// </summary>
    internal static string RemoveControlSpaces(string text)
    {
        var at = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsControlSpace(text[i])) { at = i; break; }
        }
        if (at < 0) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        sb.Append(text, 0, at);
        for (var i = at + 1; i < text.Length; i++)
            if (!IsControlSpace(text[i])) sb.Append(text[i]);
        return sb.ToString();
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every control space replaced by U+0020, or the same
    /// instance when it holds none.
    /// </summary>
    internal static string ControlSpacesToSpaces(string text) => Replace(text, IsControlSpace);

    private static string Replace(string text, Func<char, bool> test)
    {
        var at = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (test(text[i])) { at = i; break; }
        }
        if (at < 0) return text;

        return string.Create(text.Length, (text, at, test), static (span, state) =>
        {
            state.text.AsSpan().CopyTo(span);
            for (var i = state.at; i < span.Length; i++)
                if (state.test(span[i])) span[i] = ' ';
        });
    }
}
