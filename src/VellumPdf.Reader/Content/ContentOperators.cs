// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Content;

/// <summary>
/// The 73 content-stream operators ISO 32000-2 Annex A Table A.1 lists, alphabetically, with each
/// one's operand count for <see cref="ContentInterpreter"/>'s stack-discipline check. A count of
/// <see cref="Variable"/> marks the four colour operators (<c>SC</c>, <c>sc</c>, <c>SCN</c>,
/// <c>scn</c>) whose own tables (§8.6.8) give them 1 to 4 numeric operands plus, for the <c>N</c>
/// suffix, an optional trailing pattern name: no single fixed count describes them, so this
/// interpreter accepts any operand count for them rather than reporting
/// <see cref="PdfReaderDiagnosticCode.OperandStackMalformed"/> on a legitimately variable call.
/// </summary>
internal static class ContentOperators
{
    /// <summary>Sentinel operand count for an operator this interpreter does not arity-check.</summary>
    internal const int Variable = -1;

    private static readonly Dictionary<string, int> _arity = new(StringComparer.Ordinal)
    {
        // Table 59: path-painting operators.
        ["b"] = 0,
        ["B"] = 0,
        ["b*"] = 0,
        ["B*"] = 0,
        ["f"] = 0,
        ["F"] = 0, // deprecated alias of f (ISO 32000-2 §8.5.3.1)
        ["f*"] = 0,
        ["n"] = 0,
        ["s"] = 0,
        ["S"] = 0,

        // Table 33: compatibility operators.
        ["BX"] = 0,
        ["EX"] = 0,

        // Table 351/352: marked-content operators.
        ["BDC"] = 2,
        ["BMC"] = 1,
        ["DP"] = 2,
        ["EMC"] = 0,
        ["MP"] = 1,

        // Table 105/107: text object / text-showing operators.
        ["BT"] = 0,
        ["ET"] = 0,
        ["Tj"] = 1,
        ["TJ"] = 1,
        ["'"] = 1,
        ["\""] = 3,

        // Table 58: path construction operators.
        ["c"] = 6,
        ["h"] = 0,
        ["l"] = 2,
        ["m"] = 2,
        ["re"] = 4,
        ["v"] = 4,
        ["y"] = 4,

        // Table 56: graphics state operators.
        ["cm"] = 6,
        ["d"] = 2,
        ["gs"] = 1,
        ["i"] = 1,
        ["j"] = 1,
        ["J"] = 1,
        ["M"] = 1,
        ["ri"] = 1,
        ["w"] = 1,

        // Table 73: colour operators.
        ["CS"] = 1,
        ["cs"] = 1,
        ["G"] = 1,
        ["g"] = 1,
        ["K"] = 4,
        ["k"] = 4,
        ["RG"] = 3,
        ["rg"] = 3,
        ["SC"] = Variable,
        ["sc"] = Variable,
        ["SCN"] = Variable,
        ["scn"] = Variable,

        // Table 60: clipping path operators.
        ["W"] = 0,
        ["W*"] = 0,

        // Table 76: shading operator.
        ["sh"] = 1,

        // Table 86: XObject operator.
        ["Do"] = 1,

        // Table 90: inline image operators. Never reach the generic arity check (ContentInterpreter
        // intercepts BI before generic operand collection begins), listed here only so the known-set
        // membership check (IsKnown) recognises them as legitimate operators.
        ["BI"] = 0,
        ["ID"] = 0,
        ["EI"] = 0,

        // Table 103: text state operators.
        ["Tc"] = 1,
        ["Tf"] = 2,
        ["Tr"] = 1,
        ["Ts"] = 1,
        ["Tw"] = 1,
        ["Tz"] = 1,
        ["TL"] = 1,

        // Table 106: text-positioning operators.
        ["Td"] = 2,
        ["TD"] = 2,
        ["Tm"] = 6,
        ["T*"] = 0,

        // Table 111: Type 3 font operators.
        ["d0"] = 2,
        ["d1"] = 6,

        // Table 56: graphics state save/restore.
        ["q"] = 0,
        ["Q"] = 0,
    };

    /// <summary>True when <paramref name="operatorName"/> is one of the 73 operators Annex A Table
    /// A.1 lists.</summary>
    internal static bool IsKnown(string operatorName) => _arity.ContainsKey(operatorName);

    /// <summary>
    /// The operand count <paramref name="operatorName"/> expects, or <see cref="Variable"/> for the
    /// four colour operators this interpreter does not arity-check. Throws
    /// <see cref="KeyNotFoundException"/> for a name <see cref="IsKnown"/> would report false for;
    /// every call site checks that first.
    /// </summary>
    internal static int ExpectedOperandCount(string operatorName) => _arity[operatorName];
}
