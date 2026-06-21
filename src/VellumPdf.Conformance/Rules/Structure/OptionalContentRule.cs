// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.9 (Optional content). Each optional content configuration dictionary — the value
/// of the catalog's <c>/OCProperties</c> <c>/D</c> key and every element of the <c>/Configs</c>
/// array — shall contain a non-empty <c>/Name</c> string (§6.9-1) and shall not contain the
/// <c>/AS</c> (automatic state) key (§6.9-4).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.9. Clean-room: derived from the specification text, not from any
/// third-party validation profile. These are object-graph presence checks on the configuration
/// dictionaries reached from the catalog's <c>/OCProperties</c>.
/// <para>
/// Deferred: the <c>/Name</c>-uniqueness constraint (§6.9-2) and the requirement that an
/// <c>/Order</c> array reference every OCG in the file (§6.9-3) each need cross-configuration and
/// full-OCG-set analysis; they are separate, later vectors.
/// </para>
/// </remarks>
internal sealed class OptionalContentRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.9-1-config-name";

    public string Clause => "ISO 19005-2:2011, 6.9";

    private static readonly PdfName _ocProperties = new("OCProperties");
    private static readonly PdfName _d = new("D");
    private static readonly PdfName _configs = new("Configs");
    private static readonly PdfName _name = new("Name");
    private static readonly PdfName _as = new("AS");

    public void Evaluate(PreflightContext context)
    {
        if (context.Resolve(context.Catalog.Get(_ocProperties)) is not PdfDictionary ocProperties)
            return;

        if (context.Resolve(ocProperties.Get(_d)) is PdfDictionary defaultConfig)
            CheckConfig(context, defaultConfig);

        if (context.Resolve(ocProperties.Get(_configs)) is PdfArray configs)
            for (var i = 0; i < configs.Count; i++)
                if (context.Resolve(configs[i]) is PdfDictionary config)
                    CheckConfig(context, config);
    }

    private void CheckConfig(PreflightContext context, PdfDictionary config)
    {
        // §6.9-1: each configuration dictionary shall contain a Name key with a non-empty string value.
        var name = context.Resolve(config.Get(_name));
        var hasName = name switch
        {
            PdfLiteralString s => s.Bytes.Length > 0,
            PdfHexString s => s.Bytes.Length > 0,
            _ => false,
        };
        if (!hasName)
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                "An optional content configuration dictionary lacks a non-empty /Name string, which "
                + "PDF/A-2 requires (§6.9).");

        // §6.9-4: the AS key shall not appear in any configuration dictionary.
        if (config.Get(_as) is not null)
            context.Report(
                "ISO19005-2:6.9-4-config-as", Clause, PreflightSeverity.Error,
                "An optional content configuration dictionary contains an /AS entry, which is not "
                + "permitted in PDF/A-2 (§6.9).");
    }
}
