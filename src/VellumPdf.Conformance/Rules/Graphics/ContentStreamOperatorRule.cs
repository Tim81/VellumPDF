// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.2.2 test 1. A page content stream shall not contain any operator keyword that is
/// not defined in ISO 32000-1, even when that operator is bracketed by <c>BX</c>/<c>EX</c>
/// compatibility-section delimiters.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.2 and ISO 32000-1:2008, Annex A (Table A.1). The allowed
/// operator set is the complete list from Table A.1; <c>BX</c>/<c>EX</c> are themselves valid
/// operators and are in the allowed set, but they do not exempt operators that appear between them.
/// Clean-room: derived from the specification text and empirical veraPDF-oracle probing, not from
/// any third-party validation profile.
/// <para>
/// <strong>Scope:</strong> page content streams only, via
/// <see cref="ContentStreamUsage.GetPageContent"/>. Inline-image binary sample data (between
/// <c>ID</c> and <c>EI</c>) is skipped by the shared lexer helper
/// <see cref="ContentStreamUsage"/>. Form XObject streams and Type 3 glyph procedures are not
/// scanned here; those content streams are reachable only when the XObject / glyph is actually
/// drawn, and adding that traversal is deferred to avoid false-positives from unused streams.
/// </para>
/// <para>
/// <strong>Defensive operation:</strong> on any decode failure or lexer error the scan stops and
/// retains the findings already collected; no spurious finding is emitted for malformed content.
/// Each unknown operator is reported at most once per document to avoid flooding the finding list
/// with many occurrences of the same bad keyword.
/// </para>
/// </remarks>
internal sealed class ContentStreamOperatorRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.2-1";

    public string Clause => "ISO 19005-2:2011, 6.2.2";

    // The complete set of operator keywords defined in ISO 32000-1:2008, Annex A, Table A.1.
    // Groups follow the table:
    //   Graphics state: q Q cm w J j M d ri i gs
    //   Path construction: m l c v y h re
    //   Path painting: S s f F f* B B* b b* n
    //   Clipping: W W*
    //   Colour: CS cs SC SCN sc scn G g RG rg K k
    //   Text objects: BT ET
    //   Text position: Td TD Tm T*
    //   Text state: Tc Tw Tz TL Tf Tr Ts
    //   Text showing: Tj TJ ' "
    //   Type3 font: d0 d1
    //   Shading: sh
    //   XObject: Do
    //   Inline image: BI ID EI
    //   Marked content: MP DP BMC BDC EMC
    //   Compatibility: BX EX
    private static readonly HashSet<string> Iso32000Operators = new(StringComparer.Ordinal)
    {
        // Graphics state
        "q", "Q", "cm", "w", "J", "j", "M", "d", "ri", "i", "gs",
        // Path construction
        "m", "l", "c", "v", "y", "h", "re",
        // Path painting
        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n",
        // Clipping
        "W", "W*",
        // Colour
        "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k",
        // Text objects
        "BT", "ET",
        // Text position
        "Td", "TD", "Tm", "T*",
        // Text state
        "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts",
        // Text showing
        "Tj", "TJ", "'", "\"",
        // Type3 font
        "d0", "d1",
        // Shading
        "sh",
        // XObject
        "Do",
        // Inline image — BI introduces the dict, ID starts sample data, EI ends it.
        // All three are lexed as keywords and all three are valid ISO 32000-1 operators.
        "BI", "ID", "EI",
        // Marked content
        "MP", "DP", "BMC", "BDC", "EMC",
        // Compatibility
        "BX", "EX",
    };

    public void Evaluate(PreflightContext context)
    {
        // Track unknown operators already reported so each keyword is flagged at most once per
        // document, regardless of how many pages or occurrences contain it.
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var page in context.EnumeratePages())
            ScanPage(context, page, reported);
    }

    private void ScanPage(PreflightContext context, PdfDictionary page, HashSet<string> reported)
    {
        byte[]? content;
        try
        {
            content = ContentStreamUsage.GetPageContent(context, page);
        }
        catch
        {
            // Undecodable content — skip this page; do not emit a finding.
            return;
        }

        if (content is not { Length: > 0 })
            return;

        try
        {
            var lexer = new PdfLexer(content);
            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;

                if (token.Kind != TokenKind.Keyword)
                    continue;

                var op = Encoding.Latin1.GetString(token.Raw.Span);

                // The content-stream lexer emits the value keywords true/false/null as Keyword
                // tokens, but in a content stream these are operands — e.g. an inline-image
                // `/Interpolate true`/`/IM true`, or a boolean in a `BDC` inline property dict —
                // never operators. veraPDF does not treat them as operators; skipping them here
                // avoids a false positive on perfectly valid content.
                if (op is "true" or "false" or "null")
                    continue;

                if (op == "ID")
                {
                    // Skip inline-image binary sample data so it is not mis-scanned as operators.
                    ContentStreamUsage.SkipInlineImageData(lexer, content);
                    continue;
                }

                if (!Iso32000Operators.Contains(op) && reported.Add(op))
                {
                    context.Report(
                        RuleId,
                        Clause,
                        PreflightSeverity.Error,
                        $"A content stream contains operator {op} not defined in ISO 32000-1.");
                }
            }
        }
        catch
        {
            // Lexer error in malformed content — keep findings collected so far; do not crash.
        }
    }
}
