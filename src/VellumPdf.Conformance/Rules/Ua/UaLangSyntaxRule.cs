// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 (Text — natural language, language tag syntax). Every <c>/Lang</c> value
/// reachable from the document catalog — the catalog's own <c>/Lang</c> and the page-dictionary
/// <c>/Lang</c> on each page — shall conform to the RFC 1766 / BCP 47 syntax for language tags:
/// one or more subtags of 1–8 ASCII letters or digits separated by hyphens.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.2 (CosLang predicate:
/// <c>/^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$/.test(unicodeValue)</c>) and empirically validated
/// against veraPDF 1.30.2 (test id 7.2-29). Clean-room: derived from the specification text,
/// not from any third-party validation profile.
///
/// Scope (to avoid false positives empirically confirmed against veraPDF):
/// <list type="bullet">
///   <item>Only the document catalog <c>/Lang</c> is checked — veraPDF 7.2-29 does not fire for
///         an invalid <c>/Lang</c> on a page dictionary or structure element.  Struct-element
///         <c>/Lang</c> values require the structure-tree walker (a later slice) and are left
///         Deferred (test ids 7.2-30 through 7.2-43).</item>
///   <item>An empty <c>/Lang</c> string is treated as a violation — veraPDF 1.30.2 rejects it for
///         7.2-29 (empirically confirmed). Although PDF 32000-1 §14.9.2 permits an empty string to
///         inherit the default language, the UA-1 predicate requires a syntactically valid BCP-47
///         tag, and the empty string does not satisfy the regex.</item>
/// </list>
/// </remarks>
internal sealed class UaLangSyntaxRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-29";

    public string Clause => "ISO 14289-1:2014, 7.2";

    // BCP 47 syntax: one primary subtag (letters only, 1–8 chars) optionally followed by one
    // or more extension subtags (letters or digits, 1–8 chars each) separated by hyphens.
    private static readonly Regex _bcp47 =
        new(@"^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly PdfName _lang = new("Lang");

    public void Evaluate(PreflightContext context)
    {
        // Check the document catalog /Lang value.
        var langObj = context.Resolve(context.Catalog.Get(_lang));
        CheckLang(context, langObj, "document catalog");
    }

    private void CheckLang(PreflightContext context, PdfObject? langObj, string location)
    {
        if (langObj is null)
            return; // Absent is handled by the existing UaLangRule (7.2-lang).

        ReadOnlySpan<byte> raw = langObj switch
        {
            PdfLiteralString s => s.Bytes.Span,
            PdfHexString h => h.Bytes.Span,
            _ => default,
        };

        if (raw.IsEmpty && langObj is not (PdfLiteralString or PdfHexString))
            return; // Not a string type — structural issue caught elsewhere.

        var langStr = DecodeString(raw);

        if (!_bcp47.IsMatch(langStr))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"The /Lang value \"{langStr}\" in the {location} is not a syntactically valid "
                + "BCP-47 language tag (required format: primary-subtag[-extension-subtag]*, "
                + "1–8 ASCII letters/digits per subtag).");
        }
    }

    /// <summary>
    /// Decodes PDF string bytes to a .NET string. PDF strings may carry a UTF-16BE BOM (0xFE 0xFF)
    /// or a UTF-16LE BOM (0xFF 0xFE), in which case the appropriate Unicode encoding is used.
    /// Otherwise the bytes are treated as PDFDocEncoding (Latin-1 superset), which for ASCII-range
    /// language tags (BCP 47 uses only ASCII) is equivalent to ISO 8859-1.
    /// </summary>
    private static string DecodeString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        // No BOM → PDFDocEncoding (ASCII-compatible; Latin-1 for values ≤ 0x7F).
        return Encoding.Latin1.GetString(bytes);
    }
}
