// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Content;

/// <summary>
/// A PDF transformation matrix (ISO 32000-2 §8.3.4), written in content streams as six operands
/// <c>a b c d e f</c> representing
/// <c>[ a b 0 ; c d 0 ; e f 1 ]</c>. Applied to a row vector on the left: a point
/// <c>(x, y)</c> maps to <c>(a·x + c·y + e, b·x + d·y + f)</c>.
/// </summary>
internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity matrix <c>[1 0 0 1 0 0]</c>: §8.3.4's own default for <c>/Matrix</c>
    /// and the CTM at the start of every content stream.</summary>
    internal static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>
    /// Composes this matrix with <paramref name="other"/> so that applying the result to a point is
    /// the same as applying this matrix first, then <paramref name="other"/>, i.e.
    /// <c>this.Concat(other) == this × other</c> in row-vector convention. This is the operation
    /// <c>cm</c> uses to fold its operand matrix into the CTM (§8.3.4: "when a new transformation
    /// is concatenated with an existing one, the matrix representing it shall be multiplied
    /// before (premultiplied with) the existing transformation matrix",
    /// <c>CTM_new = M × CTM_old</c>, i.e. <c>m.Concat(ctm)</c>), and the one <c>Td</c>/<c>TD</c> use
    /// to fold a translation into the text line matrix (§9.4.2).
    /// </summary>
    internal Matrix Concat(Matrix other) => new(
        A: A * other.A + B * other.C,
        B: A * other.B + B * other.D,
        C: C * other.A + D * other.C,
        D: C * other.B + D * other.D,
        E: E * other.A + F * other.C + other.E,
        F: E * other.B + F * other.D + other.F);

    /// <summary>Builds the translation matrix <c>[1 0 0 1 tx ty]</c> that <c>Td</c>/<c>TD</c>
    /// concatenate onto the text line matrix (§9.4.2).</summary>
    internal static Matrix Translation(double tx, double ty) => new(1, 0, 0, 1, tx, ty);
}
