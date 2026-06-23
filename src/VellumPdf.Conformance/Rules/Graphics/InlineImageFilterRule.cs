// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.1.10 test 1. The filter value of the <c>F</c> (or <c>Filter</c>) key in an
/// inline-image dictionary shall not be <c>LZWDecode</c>, <c>Crypt</c>, a value not listed in
/// ISO 32000-1:2008 Table 6, or an array containing any such value.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.10 and ISO 32000-1:2008, Table 6. The permitted set for
/// inline images is the subset of stream filters listed in Table 6 that are allowed in inline
/// images: ASCIIHexDecode (abbrev. AHx), ASCII85Decode (A85), FlateDecode (Fl), RunLengthDecode
/// (RL), CCITTFaxDecode (CCF), and DCTDecode (DCT). JPXDecode, LZWDecode, Crypt, and any other
/// filter name fail the rule. Clean-room: derived from the specification text and empirical
/// veraPDF-oracle probing, not from any third-party validation profile.
/// <para>
/// Both the abbreviated key name (<c>/F</c>) and the full key name (<c>/Filter</c>) are legal in
/// inline-image dictionaries per ISO 32000-1:2008 §8.9.7. veraPDF honours both — confirmed
/// empirically — so this rule checks both. Likewise, both abbreviated and full filter names are
/// accepted as permitted values.
/// </para>
/// <para>
/// <strong>Scope:</strong> page content streams (via <see cref="ContentStreamUsage.GetPageContent"/>)
/// plus all non-page content streams reachable from each page (via
/// <see cref="ContentStreamUsage.GetReachableContentStreams"/>):
/// <list type="bullet">
///   <item><description>Form XObjects that are actually drawn by a <c>Do</c> operator (directly
///   from the page or from a drawn ancestor Form XObject — depth-first, cycle-guarded). Form XObjects
///   present in <c>/Resources /XObject</c> but never <c>Do</c>-invoked are excluded (reachability
///   policy empirically confirmed against veraPDF 1.30.2 on 2026-06-23).</description></item>
///   <item><description>All <c>/CharProcs</c> entries of every Type 3 font that is selected by a
///   <c>Tf</c> operator in the page content.</description></item>
///   <item><description>All annotation <c>/AP /N</c> appearance streams (and every state within a
///   sub-dictionary <c>/N</c>), regardless of annotation visibility flags.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Defensive operation:</strong> on any decode failure or lexer error the scan stops
/// and retains findings already collected; no spurious finding is emitted for malformed content.
/// Collector failure for non-page streams is silently swallowed — the page content scan always
/// proceeds regardless.
/// </para>
/// </remarks>
internal sealed class InlineImageFilterRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.10-1";

    public string Clause => "ISO 19005-2:2011, 6.1.10";

    // Permitted filter names for inline images (ISO 32000-1:2008, Table 6 subset).
    // Both abbreviated and full names are accepted.
    private static readonly HashSet<string> PermittedFilters = new(StringComparer.Ordinal)
    {
        // ASCIIHexDecode (abbreviated: AHx)
        "ASCIIHexDecode", "AHx",
        // ASCII85Decode (abbreviated: A85)
        "ASCII85Decode", "A85",
        // FlateDecode (abbreviated: Fl)
        "FlateDecode", "Fl",
        // RunLengthDecode (abbreviated: RL)
        "RunLengthDecode", "RL",
        // CCITTFaxDecode (abbreviated: CCF)
        "CCITTFaxDecode", "CCF",
        // DCTDecode (abbreviated: DCT)
        "DCTDecode", "DCT",
    };

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            ScanPage(context, page);
            ScanNonPageStreams(context, page);
        }
    }

    private void ScanPage(PreflightContext context, PdfDictionary page)
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

        ScanBytes(content, context);
    }

    /// <summary>
    /// Scans non-page content streams reachable from <paramref name="page"/>: drawn Form XObjects,
    /// Type 3 CharProcs (of selected fonts), and annotation appearance streams. Uses the shared
    /// <see cref="ContentStreamUsage.GetReachableContentStreams"/> collector whose reachability policy
    /// was determined by empirical veraPDF probing (2026-06-23).
    /// </summary>
    private void ScanNonPageStreams(PreflightContext context, PdfDictionary page)
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
            ScanBytes(cs.Bytes, context);
    }

    private void ScanBytes(byte[] content, PreflightContext context)
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

                if (op == "BI")
                {
                    CheckInlineImageFilters(context, lexer, content);
                    // After CheckInlineImageFilters the lexer is positioned past EI.
                }
                else if (op == "ID")
                {
                    // A stray ID without a preceding BI (or one that CheckInlineImageFilters
                    // did not consume) — skip the image data defensively.
                    ContentStreamUsage.SkipInlineImageData(lexer, content);
                }
            }
        }
        catch
        {
            // Lexer error in malformed content — keep findings collected so far.
        }
    }

    /// <summary>
    /// Reads the inline-image dictionary tokens that follow the <c>BI</c> keyword up to and
    /// including <c>ID</c>, extracts the <c>/F</c> or <c>/Filter</c> value, then skips the
    /// binary sample data (up to <c>EI</c>).
    /// </summary>
    private void CheckInlineImageFilters(PreflightContext context, PdfLexer lexer, ReadOnlySpan<byte> content)
    {
        // Parse key/value pairs until we hit the ID keyword.
        // Keys are Name tokens; values may be Name, Integer, Boolean (Keyword), or an Array.
        string? currentKey = null;
        var inArray = false;
        List<string>? arrayFilters = null;
        string? filterValue = null;   // single-name filter
        List<string>? filterArray = null; // array filter

        try
        {
            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    return;

                if (token.Kind == TokenKind.Keyword)
                {
                    var kw = Encoding.Latin1.GetString(token.Raw.Span);

                    // true/false/null are value keywords — operands, not operators.
                    if (kw is "true" or "false" or "null")
                    {
                        // These are values; consuming them means the current key is consumed.
                        currentKey = null;
                        continue;
                    }

                    if (kw == "ID")
                    {
                        // End of inline-image dict; validate filters then skip sample data.
                        break;
                    }

                    // Any other keyword inside the inline-image dict is unexpected; stop parsing.
                    return;
                }

                if (inArray)
                {
                    if (token.Kind == TokenKind.ArrayEnd)
                    {
                        // Array closed; store it as the filter value if the key was /F or /Filter.
                        if (currentKey is "F" or "Filter")
                            filterArray = arrayFilters;
                        currentKey = null;
                        inArray = false;
                        arrayFilters = null;
                        continue;
                    }

                    if (token.Kind == TokenKind.Name)
                    {
                        var name = DecodeName(token.Raw.Span);
                        arrayFilters?.Add(name);
                    }
                    // Other token kinds inside the array (numbers, strings) are not filter names.
                    continue;
                }

                if (token.Kind == TokenKind.Name)
                {
                    var name = DecodeName(token.Raw.Span);

                    if (currentKey is null)
                    {
                        // This Name is a key.
                        currentKey = name;
                    }
                    else
                    {
                        // This Name is the value for currentKey.
                        if (currentKey is "F" or "Filter")
                            filterValue = name;
                        currentKey = null;
                    }
                    continue;
                }

                if (token.Kind == TokenKind.ArrayBegin)
                {
                    if (currentKey is "F" or "Filter")
                    {
                        inArray = true;
                        arrayFilters = new List<string>();
                    }
                    else
                    {
                        // Skip the array tokens for keys we don't care about.
                        inArray = true;
                        arrayFilters = null; // null = discard
                    }
                    continue;
                }

                // Integer, String, or other scalar — it is the value for currentKey.
                // We don't care about non-name values for /F or /Filter (they'd be invalid anyway).
                currentKey = null;
            }
        }
        catch
        {
            // Malformed dict tokens — stop; do not emit a spurious finding.
            return;
        }

        // Skip the inline image binary sample data (ID...EI).
        try
        {
            ContentStreamUsage.SkipInlineImageData(lexer, content);
        }
        catch
        {
            // Skip failure — harmless; just don't crash.
        }

        // Now validate the collected filter name(s).
        if (filterArray is not null)
        {
            foreach (var f in filterArray)
                ReportIfNotPermitted(context, f);
        }
        else if (filterValue is not null)
        {
            ReportIfNotPermitted(context, filterValue);
        }
        // No filter (or no /F//Filter key) is permitted.
    }

    private void ReportIfNotPermitted(PreflightContext context, string filterName)
    {
        if (!PermittedFilters.Contains(filterName))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"Inline image uses not permitted or unknown filter {filterName}");
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        // raw includes the leading '/'. Decode #XX escapes (ISO 32000-1 §7.3.5).
        var sb = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length && Hex(raw[i + 1]) >= 0 && Hex(raw[i + 2]) >= 0)
            {
                sb.Append((char)((Hex(raw[i + 1]) << 4) | Hex(raw[i + 2])));
                i += 2;
            }
            else
            {
                sb.Append((char)raw[i]);
            }
        }
        return sb.ToString();
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
