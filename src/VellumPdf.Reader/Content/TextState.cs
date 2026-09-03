// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Content;

/// <summary>
/// The text-positioning matrices ISO 32000-2 §9.4.2 tracks per text object: the text matrix and
/// text line matrix. Deliberately separate from <see cref="GraphicsState"/>: §9.4.1 says these two
/// matrices (along with the derived text rendering matrix this reader does not track) "may be
/// specified only within a text object and shall not persist from one text object to the
/// next", so they are never saved or restored by <c>q</c>/<c>Q</c> the way §8.4.4's own
/// graphics-state parameters are. Only <c>BT</c> resets them (to identity), and only <c>Td</c>,
/// <c>TD</c>, <c>Tm</c>, and <c>T*</c> update them. Not stacked; the interpreter owns exactly
/// one live instance.
/// </summary>
internal sealed class TextState
{
    /// <summary>The text matrix, <c>Tm</c>: maps text space to the CTM's user space.</summary>
    internal Matrix TextMatrix { get; set; } = Matrix.Identity;

    /// <summary>The text line matrix: the text matrix at the start of the current line, what
    /// <c>Td</c>/<c>TD</c>/<c>T*</c> advance from.</summary>
    internal Matrix TextLineMatrix { get; set; } = Matrix.Identity;

    /// <summary><c>BT</c> (§9.4.1): resets both matrices to identity at the start of a text object.</summary>
    internal void BeginText()
    {
        TextMatrix = Matrix.Identity;
        TextLineMatrix = Matrix.Identity;
    }

    /// <summary>
    /// <c>Td</c>/<c>TD</c> (§9.4.2): advances the text line matrix by <c>[1 0 0 1 tx ty]</c>
    /// premultiplied against the current text line matrix, then makes the text matrix track it.
    /// </summary>
    internal void MoveTextPosition(double tx, double ty)
    {
        TextLineMatrix = Matrix.Translation(tx, ty).Concat(TextLineMatrix);
        TextMatrix = TextLineMatrix;
    }

    /// <summary><c>Tm</c> (§9.4.2): replaces both matrices outright rather than concatenating.</summary>
    internal void SetTextMatrix(Matrix m)
    {
        TextMatrix = m;
        TextLineMatrix = m;
    }
}
