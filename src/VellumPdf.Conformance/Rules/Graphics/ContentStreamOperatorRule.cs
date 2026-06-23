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
/// <strong>Scope:</strong> page content streams (via <see cref="ContentStreamUsage.GetPageContent"/>)
/// plus all non-page content streams reachable from each page (via
/// <see cref="ContentStreamUsage.GetReachableContentStreams"/>):
/// <list type="bullet">
///   <item><description>Form XObjects that are actually drawn by a <c>Do</c> operator (directly
///   from the page or from a drawn ancestor Form XObject — depth-first, cycle-guarded). Form XObjects
///   present in <c>/Resources /XObject</c> but never <c>Do</c>-invoked are excluded; veraPDF does not
///   validate their operators (empirically confirmed, 2026-06-23).</description></item>
///   <item><description>All <c>/CharProcs</c> entries of every Type 3 font that is selected by a
///   <c>Tf</c> operator in the page content, regardless of which glyphs are actually shown. Type 3
///   fonts that are never selected by <c>Tf</c> are excluded (empirically confirmed,
///   2026-06-23).</description></item>
///   <item><description>All annotation <c>/AP /N</c> appearance streams (and every state within a
///   sub-dictionary <c>/N</c>), regardless of annotation visibility flags. veraPDF validates
///   appearance content even for hidden annotations (empirically confirmed,
///   2026-06-23).</description></item>
/// </list>
/// </para>
/// <para>
/// Inline-image binary sample data (between <c>ID</c> and <c>EI</c>) is skipped by the shared
/// <see cref="ContentStreamUsage.SkipInlineImageData"/> helper to prevent binary samples from
/// being mis-read as operator keywords.
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
        {
            ScanPage(context, page, reported);
            ScanNonPageStreams(context, page, reported);
        }
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

        ScanBytes(content, reported, context);
    }

    /// <summary>
    /// Scans non-page content streams reachable from <paramref name="page"/>: drawn Form XObjects,
    /// Type 3 CharProcs (of selected fonts), and annotation appearance streams. Uses the shared
    /// <see cref="ContentStreamUsage.GetReachableContentStreams"/> collector whose reachability policy
    /// was determined by empirical veraPDF probing (2026-06-23).
    /// </summary>
    private void ScanNonPageStreams(PreflightContext context, PdfDictionary page, HashSet<string> reported)
    {
        IReadOnlyList<ReachableContentStream> streams;
        try
        {
            streams = ContentStreamUsage.GetReachableContentStreams(context, page);
        }
        catch
        {
            // Collector failure — skip non-page streams for this page; do not emit a finding.
            return;
        }

        foreach (var cs in streams)
            ScanBytes(cs.Bytes, reported, context);
    }

    private void ScanBytes(byte[] content, HashSet<string> reported, PreflightContext context)
    {
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
