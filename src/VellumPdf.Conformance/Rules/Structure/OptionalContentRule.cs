// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.9 (Optional content). Each optional content configuration dictionary — the value
/// of the catalog's <c>/OCProperties</c> <c>/D</c> key and every element of the <c>/Configs</c>
/// array — shall contain a non-empty <c>/Name</c> string (§6.9-1), and those names shall be unique
/// across all configuration dictionaries (§6.9-2); a configuration dictionary that carries an
/// <c>/Order</c> array shall reference, in that array, every optional content group in the file
/// (§6.9-3); and a configuration dictionary shall not contain the <c>/AS</c> (automatic state) key
/// (§6.9-4).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.9. Clean-room: derived from the specification text, not from any
/// third-party validation profile. These are object-graph checks on the configuration dictionaries
/// reached from the catalog's <c>/OCProperties</c>: the <c>/Order</c> array is walked recursively
/// (its nested grouping arrays and leading label strings are handled) and the optional content groups
/// it references are compared against the <c>/OCGs</c> array.
/// </remarks>
internal sealed class OptionalContentRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.9-1-config-name";

    public string Clause => "ISO 19005-2:2011, 6.9";

    private const int MaxDepth = 64;

    private static readonly PdfName _ocProperties = new("OCProperties");
    private static readonly PdfName _d = new("D");
    private static readonly PdfName _configs = new("Configs");
    private static readonly PdfName _ocgs = new("OCGs");
    private static readonly PdfName _order = new("Order");
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

        CheckNameUniqueness(context, configs);
        CheckOrderCompleteness(context, ocProperties, configs);
    }

    private void CheckConfig(PreflightContext context, PdfDictionary config)
    {
        // §6.9-1: each configuration dictionary shall contain a Name key with a non-empty string value.
        if (ConfigName(context, config) is null)
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

    // §6.9-2: the /Name values shall be unique across all configuration dictionaries.
    private void CheckNameUniqueness(PreflightContext context, List<PdfDictionary> configs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var config in configs)
            if (ConfigName(context, config) is { } name && !seen.Add(name))
            {
                context.Report(
                    "ISO19005-2:6.9-2-config-name-unique", Clause, PreflightSeverity.Error,
                    "Two optional content configuration dictionaries share the same /Name; PDF/A-2 "
                    + "requires the names to be unique (§6.9).");
                return; // One report suffices; the verdict is unaffected by the count.
            }
    }

    // §6.9-3: a configuration dictionary's /Order array (if present) shall reference every OCG.
    private void CheckOrderCompleteness(PreflightContext context, PdfDictionary ocProperties, List<PdfDictionary> configs)
    {
        if (context.Resolve(ocProperties.Get(_ocgs)) is not PdfArray ocgs)
            return;

        var allOcgs = new HashSet<int>();
        for (var i = 0; i < ocgs.Count; i++)
            if (ocgs[i] is PdfIndirectReference r)
                allOcgs.Add(r.ObjectNumber);
        if (allOcgs.Count == 0)
            return;

        foreach (var config in configs)
        {
            if (config.Get(_order) is not { } orderObj)
                continue;
            var referenced = new HashSet<int>();
            CollectOrderRefs(context, orderObj, referenced, 0);
            if (!allOcgs.IsSubsetOf(referenced))
            {
                context.Report(
                    "ISO19005-2:6.9-3-order-complete", Clause, PreflightSeverity.Error,
                    "An optional content configuration dictionary has an /Order array that does not "
                    + "reference every optional content group in the file (§6.9).");
                return;
            }
        }
    }

    // Gathers the OCG object numbers referenced by an /Order value: an /Order is an array whose
    // elements are OCG references or nested grouping arrays (optionally led by a label string).
    private static void CollectOrderRefs(PreflightContext context, PdfObject? node, HashSet<int> into, int depth)
    {
        if (depth > MaxDepth)
            return;
        if (node is PdfIndirectReference r)
        {
            if (context.Resolve(r) is PdfArray nested)
                CollectOrderRefs(context, nested, into, depth + 1);
            else
                into.Add(r.ObjectNumber);
            return;
        }
        if (context.Resolve(node) is PdfArray array)
            for (var i = 0; i < array.Count; i++)
                CollectOrderRefs(context, array[i], into, depth + 1);
    }

    private static string? ConfigName(PreflightContext context, PdfDictionary config)
        => context.Resolve(config.Get(_name)) switch
        {
            PdfLiteralString s when s.Bytes.Length > 0 => Convert.ToHexString(s.Bytes.Span),
            PdfHexString s when s.Bytes.Length > 0 => Convert.ToHexString(s.Bytes.Span),
            _ => null,
        };
}
