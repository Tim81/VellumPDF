// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Layout.Core;

/// <summary>
/// The rule an element's <c>Anchor</c> is held to, applied by every <c>init</c> accessor that
/// takes one. It is the rule <see cref="PdfDocument.AddNamedDestination"/> applies to a name, so
/// an anchor that is accepted here is one the document can register.
/// </summary>
internal static class AnchorNames
{
    /// <summary>
    /// Returns <paramref name="value"/> when it is null (no anchor) or a valid name. Throws
    /// <see cref="ArgumentException"/> naming <c>value</c> for an empty name, one over 127
    /// characters, or one holding a character outside <c>A-Z a-z 0-9 . _ - :</c>. The message gives
    /// a length or a character position and never the name itself.
    /// </summary>
    internal static string? Validate(string? value)
    {
        if (value is not null)
            DestinationName.Validate(value, nameof(value));
        return value;
    }
}
