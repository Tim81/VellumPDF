// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.10 (Optional content). Each optional content configuration dictionary — the
/// <c>/D</c> default config and each entry of the <c>/Configs</c> array under
/// <c>/OCProperties</c> — shall satisfy two constraints:
/// <list type="bullet">
///   <item>§7.10-1: the configuration dictionary shall contain a non-empty <c>/Name</c> string.</item>
///   <item>§7.10-2: the configuration dictionary shall not contain an <c>/AS</c> (automatic state)
///     entry.</item>
/// </list>
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.10 (PDOCConfig predicates:
/// testNumber 1 <c>Name != null &amp;&amp; Name.length() &gt; 0</c>;
/// testNumber 2 <c>AS == null</c>) and empirically validated against veraPDF 1.30.2. Clean-room:
/// derived from the specification text and the veraPDF profile, not from any third-party
/// implementation.
/// <para>
/// The traversal of <c>/OCProperties /D</c> and <c>/OCProperties /Configs</c> matches the
/// existing PDF/A-2 <c>OptionalContentRule</c> (ISO 19005-2 §6.9), which enforces the
/// same structural constraints under a different clause reference.
/// </para>
/// </remarks>
internal sealed class UaOptionalContentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.10-1";

    public string Clause => "ISO 14289-1:2014, 7.10";

    private static readonly PdfName _ocProperties = new("OCProperties");
    private static readonly PdfName _d = new("D");
    private static readonly PdfName _configs = new("Configs");
    private static readonly PdfName _name = new("Name");
    private static readonly PdfName _as = new("AS");

    public void Evaluate(PreflightContext context)
    {
        if (context.Resolve(context.Catalog.Get(_ocProperties)) is not PdfDictionary ocProperties)
            return;

        var configs = new List<PdfDictionary>();
        if (context.Resolve(ocProperties.Get(_d)) is PdfDictionary defaultConfig)
            configs.Add(defaultConfig);
        if (context.Resolve(ocProperties.Get(_configs)) is PdfArray configArray)
            for (var i = 0; i < configArray.Count; i++)
                if (context.Resolve(configArray[i]) is PdfDictionary config)
                    configs.Add(config);

        foreach (var config in configs)
            CheckConfig(context, config);
    }

    private void CheckConfig(PreflightContext context, PdfDictionary config)
    {
        // §7.10-1: each configuration dictionary shall have a non-empty /Name string.
        if (!HasNonEmptyName(context, config))
            context.Report(
                "ISO14289-1:7.10-1", Clause, PreflightSeverity.Error,
                "An optional content configuration dictionary lacks a non-empty /Name string. "
                + "PDF/UA-1 requires every OC configuration to have a non-empty /Name "
                + "(ISO 14289-1:2014, 7.10).");

        // §7.10-2: the /AS (automatic state) key shall not appear in any configuration dictionary.
        if (config.Get(_as) is not null)
            context.Report(
                "ISO14289-1:7.10-2", "ISO 14289-1:2014, 7.10", PreflightSeverity.Error,
                "An optional content configuration dictionary contains an /AS (automatic state) entry. "
                + "PDF/UA-1 forbids the /AS key in OC configuration dictionaries "
                + "(ISO 14289-1:2014, 7.10).");
    }

    private static bool HasNonEmptyName(PreflightContext context, PdfDictionary config)
    {
        var raw = context.Resolve(config.Get(_name));
        return raw switch
        {
            PdfLiteralString s => s.Bytes.Length > 0,
            PdfHexString s => s.Bytes.Length > 0,
            _ => false,
        };
    }
}
