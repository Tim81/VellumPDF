// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>What fills the space a tab leaves before a <see cref="TabStop"/>.</summary>
/// <remarks>
/// <b>A value this enumeration does not name is refused.</b> The <see cref="TabStop"/> constructor
/// throws <see cref="ArgumentOutOfRangeException"/>.
/// <para>A leader is drawn as a stroked line, not as characters, so it is not part of the text a
/// reader extracts from the page. In a tagged document it is drawn as an artifact, outside the
/// paragraph's structure element.</para>
/// </remarks>
public enum TabLeader
{
    /// <summary>The space is left empty.</summary>
    None,

    /// <summary>A row of round dots.</summary>
    Dots,

    /// <summary>A row of short dashes.</summary>
    Dashes,

    /// <summary>A continuous line.</summary>
    Rule,
}
