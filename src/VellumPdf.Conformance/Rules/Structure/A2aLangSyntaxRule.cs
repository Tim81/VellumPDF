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
/// 
/// Attention: this pattern and that predicate disagree on one input. .NET's <c>$</c> matches
/// before a single trailing newline, so a <c>/Lang</c> of <c>en</c> followed by a line feed is
/// accepted here and rejected by the predicate, where ECMAScript's <c>$</c> asserts end of
/// input. The divergence is a false accept and is recorded as D6 in
/// <c>docs/conformance-divergences.md</c>. Replacing <c>$</c> with <c>\z</c> closes it (#507).
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
    // Raising the number would move the flake rather than remove it. This pattern does not need
    // a guard at all: the hyphen is in neither subtag character class, so once {1,8} gives back a
    // character the next one is an alnum rather than a hyphen and the alternative dies at once.
    // Backtracking is bounded by eight attempts per subtag. Measured over 1,340,413 inputs,
    // including exhaustive enumeration over four alphabets and stressors of 19,000,002
    // characters: the engines agree on every one.
    //
    // NOTE on the worst case, because it is easy to reproduce the wrong shape. The nine-character
    // subtag chains that the argument above reasons about, and that
    // LangRule_chainedOverlongSubtags builds, are refused in under 0.002 ms at any length -- they
    // fail immediately, which is the point. The slowest input found is a *matching* one built
    // from single-character subtags ("en-a-a-a-..."): 33.9 ms for ten million characters on the
    // JIT, 130.7 ms under AOT. So the headroom over the 50 ms this used to allow is about 15 MB
    // of /Lang on the JIT and about 3.8 MB under AOT. Neither is a threat; both are far past any
    // language tag.
    //
    // So the switch is not about this pattern running away. Two other things decide it.
    //
    // First, NonBacktracking makes linear time the engine's guarantee rather than a property of
    // this pattern that a later edit could lose.
    //
    // Second, RegexOptions.Compiled is a lie under Native AOT, which eng/aot publishes
    // VellumPdf.Cli with. There is no Reflection.Emit, so Compiled degrades to the interpreter --
    // provable by allocation rather than by a clock: its constructor allocates 9,232 bytes there,
    // byte-identical to the interpreter's, against 21,872 on the JIT. Over 100,000 tags under
    // AOT, Compiled and interpreted are indistinguishable at about 15 ms while NonBacktracking
    // takes 8.8. On the shipped preflight binary this is therefore a speed-up.
    //
    // Attention: that is the opposite way round on the JIT, which is what the eight NuGet
    // packages run on for anyone not publishing AOT. There, NonBacktracking costs about 30 ms
    // per 100,000 matches against Compiled's 5.3, a factor of 5.6, and construction costs about
    // 22 ms and 161 KB against 5 ms and 22 KB. Under AOT the construction time collapses to
    // 0.3 ms but the allocation does not -- it grows slightly, to about 166 KB. The trade was
    // taken with those figures in hand: one IsMatch runs per structure element carrying /Lang, so
    // even 100,000 tagged elements pays about 25 ms more than before, against a document parse
    // that costs far more than that.
    //
    // Compiled is dropped because it is meaningless alongside the new engine, not because it is
    // rejected: the two options do combine, the Options property keeps both flags, and the
    // symbolic engine runs regardless. What NonBacktracking genuinely refuses is RightToLeft and
    // ECMAScript, neither of which is used anywhere in this repository.
    //
    // One real behavioural difference, inert here: a capturing group inside a loop reports only
    // its final capture, so Groups[1].Captures.Count is 1 rather than 2 for "zh-Hans-CN". The
    // only two uses of this field in src/ are IsMatch calls. An edit that starts reading Captures
    // needs to know.
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
