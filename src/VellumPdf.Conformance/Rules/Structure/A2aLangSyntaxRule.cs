// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.7.4 (PDF/A-2a — language identifier syntax). Every <c>/Lang</c> value in the
/// document — the document catalog's <c>/Lang</c> and the <c>/Lang</c> entry of any structure
/// element dictionary — shall either be the empty string (language unknown) or a syntactically
/// valid language tag per RFC 3066 (BCP 47): one or more subtags of 1–8 ASCII letters/digits
/// separated by hyphens, with the primary subtag restricted to letters only.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.7.4 and the veraPDF predicate on <c>CosLang</c>:
/// <c>unicodeValue == '' || /^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$/.test(unicodeValue)</c>.
///
/// <para>Scope (empirically verified against veraPDF 1.30.2, flavour 2a):</para>
/// <list type="bullet">
///   <item>The document catalog <c>/Lang</c> — bad syntax fires 6.7.4-1.</item>
///   <item>Structure element <c>/Lang</c> — bad syntax fires 6.7.4-1.</item>
///   <item>An absent <c>/Lang</c> is accepted; the empty string <c>()</c> is also accepted
///         (veraPDF explicitly allows it per the predicate <c>unicodeValue == ''</c>).</item>
/// </list>
///
/// <para>Cross-validated against veraPDF 1.30.2:
/// <list type="bullet">
///   <item><c>/Lang (invalid!!bad)</c> on the document catalog: 6.7.4-1 fires.</item>
///   <item><c>/Lang (invalid!!bad)</c> on a StructElem: 6.7.4-1 fires.</item>
///   <item><c>/Lang (en-US)</c> on either location: 6.7.4-1 does not fire.</item>
///   <item>No <c>/Lang</c> at all: 6.7.4-1 does not fire.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class A2aLangSyntaxRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.7.4-1";

    public string Clause => "ISO 19005-2:2011, 6.7.4";

    // BCP 47 / RFC 3066 syntax: primary subtag (letters only, 1–8 chars) optionally followed
    // by extension subtags (letters or digits, 1–8 chars) separated by hyphens.
    private static readonly Regex _bcp47 =
        new(@"^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly PdfName _lang = new("Lang");

    public void Evaluate(PreflightContext context)
    {
        // Check the document catalog /Lang value.
        CheckLang(context, context.Resolve(context.Catalog.Get(_lang)));

        // Check every structure element's /Lang value.
        var tree = StructureTree.Analyze(context);
        foreach (var node in tree.AllNodes)
        {
            var langObj = context.Resolve(node.Dict.Get(_lang));
            if (langObj is not null)
                CheckLang(context, langObj);
        }
    }

    private void CheckLang(PreflightContext context, PdfObject? langObj)
    {
        if (langObj is null)
            return; // Absent /Lang is allowed — no requirement to specify language in A2a.

        ReadOnlySpan<byte> raw = langObj switch
        {
            PdfLiteralString s => s.Bytes.Span,
            PdfHexString h => h.Bytes.Span,
            _ => default,
        };

        // Not a string type — structural issue caught elsewhere; skip silently.
        if (raw.IsEmpty && langObj is not (PdfLiteralString or PdfHexString))
            return;

        var langStr = DecodeString(raw);

        // Empty string is explicitly allowed by the predicate: "unicodeValue == ''".
        if (langStr.Length == 0)
            return;

        if (!_bcp47.IsMatch(langStr))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"A /Lang value \"{langStr}\" is not a syntactically valid language tag "
                + "(required: empty string or RFC 3066 / BCP 47 format "
                + "primary-subtag[-extension]*, 1–8 ASCII letters/digits per subtag, "
                + "primary subtag letters only). ISO 19005-2 §6.7.4 requires all /Lang "
                + "values to conform to this syntax.");
        }
    }

    private static string DecodeString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        return Encoding.Latin1.GetString(bytes);
    }
}
