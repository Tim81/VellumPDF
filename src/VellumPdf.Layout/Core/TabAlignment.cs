// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// How the text after a tab sits against the <see cref="TabStop"/> it is placed at.
/// </summary>
/// <remarks>
/// <b>A value this enumeration does not name is refused.</b> The <see cref="TabStop"/> constructor
/// throws <see cref="ArgumentOutOfRangeException"/>, because the stop could not say where the text
/// goes.
/// </remarks>
public enum TabAlignment
{
    /// <summary>The text starts at the stop.</summary>
    Left,

    /// <summary>The text ends at the stop.</summary>
    Right,

    /// <summary>The text is centred on the stop.</summary>
    Center,

    /// <summary>
    /// The first <see cref="TabStop.DecimalCharacter"/> in the text starts at the stop. Text with
    /// none ends at the stop, as for <see cref="Right"/>.
    /// </summary>
    Decimal,
}
