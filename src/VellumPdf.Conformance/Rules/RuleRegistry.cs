// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Actions;
using VellumPdf.Conformance.Rules.Annotations;
using VellumPdf.Conformance.Rules.Colour;
using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Conformance.Rules.Graphics;
using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Conformance.Rules.Transparency;
using VellumPdf.Conformance.Rules.Ua;

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// Maps each <see cref="PdfConformance"/> level to the ordered list of rules that define it.
/// Profiles are built from explicit, hand-written rule lists — no reflection, no assembly
/// scanning — so the validator is fully AOT- and trim-compatible.
/// </summary>
internal static class RuleRegistry
{
    // Baseline structural rules shared by every conformance level. As coverage grows
    // (issues #109–#113) each level's profile is composed from these plus its level-specific rules.
    private static readonly IConformanceRule[] CommonStructure =
    [
        new DocumentCatalogRule(),
    ];

    // ISO 19005-2 §6.1 file-structure rules (header, trailer). Shared by every PDF/A-2 level.
    private static readonly IConformanceRule[] PdfA2FileStructure =
    [
        new FileHeaderRule(),
        new FileTrailerRule(),
        new CrossReferenceRule(),
        new ObjectLayoutRule(),
        new HexStringRule(),
        new StreamRule(),
        new NumericLimitsRule(),
        new CatalogRestrictionsRule(),
        new OptionalContentRule(),
        new PermissionsRule(),
        new EmbeddedFileRule(),
        new EmbeddedFilePdfaRule(),
    ];

    // ISO 19005-2 §6.2 colour / §6.4 transparency rules. Shared by every PDF/A-2 level.
    private static readonly IConformanceRule[] PdfA2ColourAndTransparency =
    [
        new OutputIntentRule(),
        new BlendModeRule(),
        new DeviceNColorantRule(),
        new DeviceNColorantsRule(),
    ];

    // ISO 19005-2 §6.1.10 / §6.1.13 / §6.2.2 / §6.2.5 graphics-state / §6.2.6 rendering-intent /
    // §6.2.8 image / §6.2.9 XObject rules. Shared by every PDF/A-2 level.
    private static readonly IConformanceRule[] PdfA2Graphics =
    [
        new ContentStreamOperatorRule(),
        new InlineImageFilterRule(),
        new InheritedResourceRule(),
        new GraphicsStateRule(),
        new GraphicsStateNestingRule(),
        new ForbiddenXObjectRule(),
    ];

    // ISO 19005-2 §6.3 font rules. Shared by every PDF/A-2 level.
    private static readonly IConformanceRule[] PdfA2Fonts =
    [
        new FontEmbeddingRule(),
        new FontStructureRule(),
        new GlyphPresenceRule(),
        new CidRangeRule(),
    ];

    // ISO 19005-2 §6.5–§6.7 metadata, annotation, and action rules. Shared by every PDF/A-2 level.
    // The XMP rule keys off the level being validated, so it asserts B / U / A as appropriate.
    private static readonly IConformanceRule[] PdfA2Document =
    [
        new XmpConformanceRule(),
        new MetadataRule(),
        new ExtensionSchemaRule(),
        new PropertyUsageRule(),
        new AnnotationRule(),
        new ActionRule(),
        new Forms.XfaRule(),
        new Forms.InteractiveFormRule(),
    ];

    private static readonly IConformanceRule[] PdfA2BRules =
    [
        .. CommonStructure,
        .. PdfA2FileStructure,
        .. PdfA2ColourAndTransparency,
        .. PdfA2Graphics,
        .. PdfA2Fonts,
        .. PdfA2Document,
    ];

    // PDF/A-2u = PDF/A-2b plus the character-to-Unicode requirement (ISO 19005-2 §6.2.11.7.2).
    private static readonly IConformanceRule[] PdfA2URules =
    [
        .. PdfA2BRules,
        new ToUnicodeRule(),
    ];

    // PDF/A-2a = PDF/A-2u plus tagged logical structure (ISO 19005-2 §6.8).
    private static readonly IConformanceRule[] PdfA2ARules =
    [
        .. PdfA2URules,
        new LogicalStructureRule(),
    ];

    // PDF/UA-1 (ISO 14289-1) is a distinct standard from PDF/A: it shares the baseline catalog
    // structure but has its own metadata, tagging, language, title, and tab-order requirements.
    private static readonly IConformanceRule[] PdfUA1Rules =
    [
        .. CommonStructure,
        new UaMetadataRule(),
        new UaTaggingRule(),
        new UaLangRule(),
        new UaTitleRule(),
        new UaTabsRule(),
    ];

    /// <summary>
    /// Returns the rule profile for <paramref name="conformance"/>, or <see langword="false"/>
    /// when no profile is registered yet for that level.
    /// </summary>
    public static bool TryGetProfile(PdfConformance conformance, out IReadOnlyList<IConformanceRule> rules)
    {
        switch (conformance)
        {
            case PdfConformance.PdfA2B:
                rules = PdfA2BRules;
                return true;
            case PdfConformance.PdfA2U:
                rules = PdfA2URules;
                return true;
            case PdfConformance.PdfA2A:
                rules = PdfA2ARules;
                return true;
            case PdfConformance.PdfUA1:
                rules = PdfUA1Rules;
                return true;
            default:
                rules = [];
                return false;
        }
    }
}
