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
    // RegexOptions.NonBacktracking rather than Compiled, and no match timeout.
    //
    // This carried TimeSpan.FromMilliseconds(50). A match timeout is a wall-clock assertion, and
    // it is checked against elapsed time rather than work done, so a thread descheduled mid-match
    // exceeds it on an input that needs microseconds. That is what happened on CI, where seven
    // test assemblies run at once: the eight-character tag "xyz!!bad" timed out, and the rule
    // reported "Rule evaluation failed" in place of the verdict it had already all but reached.
    // The same starvation happens on any loaded machine, so this was a defect for consumers and
    // not only a flaky test.
    //
    // Raising the number would move the flake rather than remove it. This pattern does not need a
    // guard at all: the hyphen is in neither subtag character class, so once {1,8} gives back a
    // character the next one is an alnum rather than a hyphen and the alternative dies at once.
    // Backtracking is bounded by eight attempts per subtag. Measured over 1.37 million inputs,
    // including exhaustive enumeration over small alphabets and 19-million-character stressors
    // built from nine-character subtags: the engines agree on every one, and the old one peaked
    // at 35 ms for ten million characters, so roughly 15 MB of /Lang would be needed to burn the
    // 50 ms this used to allow.
    //
    // So the switch is not about this pattern running away. Two other things make it right.
    // NonBacktracking makes linear time the engine's guarantee rather than a property of this
    // pattern that a later edit could lose. And under Native AOT, which eng/aot publishes for
    // VellumPdf.Cli, RegexOptions.Compiled has no Reflection.Emit to use and silently degrades to
    // the interpreter: measured on an AOT-published probe over 100,000 tags, Compiled and
    // interpreted are indistinguishable in both time and allocation, while NonBacktracking runs
    // 100,000 tags in 8.5 ms against their 11.8 -- so on the shipped preflight binary this is a
    // speed-up rather than a cost.
    //
    // Compiled is dropped because it is meaningless here, not because it is rejected: the two
    // options do combine, the Options property keeps both flags, and the symbolic engine runs
    // regardless. Keeping it would only mislead. What NonBacktracking genuinely refuses is
    // RightToLeft and ECMAScript, neither of which is used.
    //
    // One real behavioural difference, inert here: a capturing group inside a loop reports only
    // its final capture, so Groups[1].Captures.Count is 1 rather than 2 for "zh-Hans-CN". Both
    // rules call only IsMatch. An edit that starts reading Captures needs to know this.
    //
    // Costs, for the record: construction is about 23 ms and 161 KB against Compiled's 4.8 ms and
    // 24 KB, once per rule on the JIT stack and near-free under AOT.
    private static readonly Regex _bcp47 =
        new(@"^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", RegexOptions.NonBacktracking);

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
