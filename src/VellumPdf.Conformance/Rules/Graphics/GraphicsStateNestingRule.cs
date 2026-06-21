// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.1.13-8 (graphics-state stack nesting). A conforming file shall not nest
/// <c>q</c>/<c>Q</c> pairs by more than 28 levels in a content stream.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.13 and the veraPDF rule Op_q_gsave (test number 8).
/// The rule states <c>nestingLevel &lt;= 28</c>; a depth that reaches 29 is non-conformant.
/// Clean-room: derived from the specification text, not from any third-party validation profile.
/// <para>
/// Only keyword tokens with text exactly <c>"q"</c> or <c>"Q"</c> are counted — bytes that happen
/// to spell those letters inside literal strings, hex strings, names, or comments are classified as
/// different token kinds by the lexer and are ignored. The running depth is floored at 0 on each
/// <c>Q</c> so a bare <c>Q</c> with no matching <c>q</c> is treated as a no-op rather than an
/// underflow. Each offending page is reported at most once.
/// </para>
/// <para>
/// Deferred edges: <c>q</c>/<c>Q</c> operators inside form XObjects, Type 3 glyph procedures,
/// tiling patterns, and annotation appearance streams are not yet scanned (consistent with the
/// other content-stream-scoped rules). Only page-level content streams are checked.
/// </para>
/// </remarks>
internal sealed class GraphicsStateNestingRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.13-8-q-nesting";

    public string Clause => "ISO 19005-2:2011, 6.1.13";

    private const int MaxNestingDepth = 28;

    public void Evaluate(PreflightContext context)
    {
        var pageIndex = 0;
        foreach (var page in context.EnumeratePages())
        {
            pageIndex++;
            var content = ContentStreamUsage.GetPageContent(context, page);
            if (content is null)
                continue;

            CheckPage(context, content, pageIndex);
        }
    }

    private void CheckPage(PreflightContext context, byte[] content, int pageIndex)
    {
        var depth = 0;
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
                if (op == "q")
                {
                    depth++;
                    if (depth > MaxNestingDepth)
                    {
                        context.Report(
                            RuleId, Clause, PreflightSeverity.Error,
                            $"Page {pageIndex} nests q/Q operators {depth} levels deep; "
                            + $"PDF/A-2 permits at most {MaxNestingDepth} levels (ISO 19005-2:2011, 6.1.13).");
                        return; // report once per page
                    }
                }
                else if (op == "Q")
                {
                    if (depth > 0)
                        depth--;
                }
            }
        }
        catch
        {
            // Malformed content — stop scanning this page; rules degrade rather than abort.
        }
    }
}
