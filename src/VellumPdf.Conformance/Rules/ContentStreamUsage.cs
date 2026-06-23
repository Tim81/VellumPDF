// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// The kind of non-page content stream returned by
/// <see cref="ContentStreamUsage.GetReachableContentStreams"/>.
/// Used by future rules to scope resource lookups to the correct dictionary.
/// </summary>
internal enum ContentStreamKind
{
    /// <summary>
    /// A Form XObject stream reachable from the page via a drawn <c>Do</c> invocation (direct or
    /// through intermediate Form XObjects). Only streams actually invoked during rendering are
    /// included; Form XObjects present in <c>/Resources /XObject</c> but never drawn are excluded
    /// because veraPDF does not validate their content (empirically confirmed, 2026-06-23).
    /// </summary>
    FormXObject,

    /// <summary>
    /// A glyph procedure stream from a Type 3 font's <c>/CharProcs</c> dictionary. All CharProcs in
    /// every Type 3 font that is <em>selected</em> by a <c>Tf</c> operator on the page are included,
    /// regardless of whether each individual glyph was actually shown. Type 3 fonts present in
    /// <c>/Resources /Font</c> but never selected by a <c>Tf</c> operator are excluded — veraPDF
    /// validates all CharProcs of selected Type 3 fonts, but not unselected ones
    /// (empirically confirmed, 2026-06-23).
    /// </summary>
    Type3CharProc,

    /// <summary>
    /// An annotation appearance stream — the <c>/N</c> entry of an annotation's <c>/AP</c>
    /// dictionary (either directly as a stream, or every state stream inside a sub-dictionary).
    /// All appearance streams for every annotation on the page are included regardless of annotation
    /// visibility flags — veraPDF validates appearance content even for hidden annotations
    /// (empirically confirmed, 2026-06-23).
    /// </summary>
    AnnotationAppearance,
}

/// <summary>
/// One non-page content stream reachable from a page, as returned by
/// <see cref="ContentStreamUsage.GetReachableContentStreams"/>.
/// </summary>
/// <param name="Bytes">
/// The fully-decoded content-stream bytes. Never null or empty — streams that could not be decoded
/// are silently dropped by the collector.
/// </param>
/// <param name="Resources">
/// The effective <c>/Resources</c> dictionary for this content stream, resolved per the rules in
/// ISO 32000-1:
/// <list type="bullet">
///   <item><description>Form XObject: the XObject's own <c>/Resources</c> (or <see langword="null"/>
///   when the XObject omits it — in which case the caller's resources apply, but the collector does
///   not inherit them; individual rules must decide how to fall back).</description></item>
///   <item><description>Type 3 CharProc: the parent Type 3 font's <c>/Resources</c> entry (or
///   <see langword="null"/> when absent).</description></item>
///   <item><description>Annotation appearance: the appearance Form XObject's own <c>/Resources</c>
///   (or <see langword="null"/> — annotations do not inherit from the page per ISO 32000-1
///   §12.5.5).</description></item>
/// </list>
/// </param>
/// <param name="Kind">The origin of this content stream (for resource-scoping by future rules).</param>
internal sealed record ReachableContentStream(byte[] Bytes, PdfDictionary? Resources, ContentStreamKind Kind);

/// <summary>
/// Records one text-show event in a page content stream: the font resource name selected by the
/// most recent <c>Tf</c> operator, the text rendering mode (<c>Tr</c>, default 0), and the raw
/// string bytes that were shown. <c>FontResourceName</c> may be <see langword="null"/> when no
/// preceding <c>Tf</c> was seen; <c>RenderingMode</c> is –1 when the rendering mode could not be
/// determined (parse gap — consumers should treat this as "unknown" / not a confirmed visible draw).
/// </summary>
internal sealed record TextShow(string? FontResourceName, int RenderingMode, byte[] Bytes);

/// <summary>
/// One marked-content sequence (BMC/BDC … EMC) captured from a page content stream.
/// Properties come from the inline property dict of a BDC operator; they are null for
/// BMC sequences and for BDC sequences that reference a named Properties resource rather than
/// carrying an inline dict.
/// </summary>
internal sealed class MarkedContentSequence
{
    internal MarkedContentSequence(
        string tag,
        int? mcid,
        string? lang,
        string? actualText,
        string? alt,
        string? expansion,
        string? inheritedLang,
        bool hasArtifactAncestor,
        int? ancestorMcid)
    {
        Tag = tag;
        Mcid = mcid;
        Lang = lang;
        ActualText = actualText;
        Alt = alt;
        Expansion = expansion;
        InheritedLang = inheritedLang;
        IsArtifact = string.Equals(tag, "Artifact", StringComparison.Ordinal);
        HasArtifactAncestor = hasArtifactAncestor;
        AncestorMcid = ancestorMcid;
    }

    /// <summary>The tag name from the BMC/BDC operator (e.g. "Artifact", "P", "Span").</summary>
    public string Tag { get; }

    /// <summary>The /MCID integer from the BDC inline property dict, or null.</summary>
    public int? Mcid { get; }

    /// <summary>The /Lang string from the BDC inline property dict, or null.</summary>
    public string? Lang { get; }

    /// <summary>The /ActualText string from the BDC inline property dict, or null.</summary>
    public string? ActualText { get; }

    /// <summary>The /Alt string from the BDC inline property dict, or null.</summary>
    public string? Alt { get; }

    /// <summary>The /E (expansion) string from the BDC inline property dict, or null.</summary>
    public string? Expansion { get; }

    /// <summary>
    /// The /Lang inherited from the nearest enclosing BDC ancestor that carries /Lang,
    /// or null if no ancestor has /Lang. Does not include <see cref="Lang"/> itself.
    /// </summary>
    public string? InheritedLang { get; }

    /// <summary>True when <see cref="Tag"/> is "Artifact".</summary>
    public bool IsArtifact { get; }

    /// <summary>
    /// True when any enclosing ancestor BDC/BMC in the marked-content stack at the time this
    /// sequence was opened carries the tag "Artifact". Used by §7.1-2 to detect tagged content
    /// nested inside an artifact.
    /// </summary>
    public bool HasArtifactAncestor { get; }

    /// <summary>
    /// The /MCID from the nearest enclosing ancestor BDC that carries an MCID, or null when no
    /// ancestor carries an MCID. Used by §7.1-1 to determine whether an Artifact BDC is nested
    /// inside a struct-linked (tagged) ancestor sequence (i.e., <c>isTaggedContent</c> for the
    /// Artifact in veraPDF's model).
    /// </summary>
    public int? AncestorMcid { get; }
}

/// <summary>
/// The marked-content context at the time of one text-show operator. Used by
/// §7.2-34 (SETextItem natural-language determination) to check whether a text show
/// has a determinable language.
/// </summary>
internal sealed record TextShowMcContext(
    int ShowIndex,   // zero-based index into ContentUsage.TextShows
    int? Mcid,       // MCID of the innermost BDC at the time of the show, or null
    string? DirectLang,    // /Lang from the innermost BDC's own property dict, or null
    string? InheritedLang); // /Lang inherited from any ancestor BDC, or null

/// <summary>
/// One "simple content item" as defined by ISO 14289-1 §7.1-3: an operator that creates a
/// content item (Tj/TJ/'/", S/s/f/F/f*/B/B*/b/b*, EI, image-Do, sh). Used by
/// <c>UaSimpleContentItemRule</c> to check that every real-content operator is either tagged
/// (its nearest enclosing BDC with an MCID resolves in the ParentTree) or inside an Artifact.
/// </summary>
/// <param name="EffectiveMcid">
/// The MCID of the nearest enclosing BDC that carries any MCID (direct <c>Mcid</c> or inherited
/// <c>AncestorMcid</c> from the MC-stack top), or <see langword="null"/> when the content item
/// is outside any MCID-carrying BDC. <see langword="null"/> + no Artifact = untagged real content.
/// </param>
/// <param name="IsInsideArtifact">
/// <see langword="true"/> when the content item's current MC-stack context is an Artifact sequence
/// or has any Artifact ancestor. Corresponds to veraPDF's <c>parentsTags.contains('Artifact')</c>.
/// </param>
internal sealed record SimpleContentItem(int? EffectiveMcid, bool IsInsideArtifact);

/// <summary>
/// The result of scanning a page's content streams for graphics-state usage. All properties
/// mirror the seven operands tracked before Batch A5a, extended with <see cref="TextShows"/>,
/// <see cref="MarkedContentSequences"/>, <see cref="TextShowContexts"/>, and
/// <see cref="SimpleContentItems"/>.
/// </summary>
internal sealed class ContentUsage
{
    internal ContentUsage(
        HashSet<string> appliedExtGStates,
        bool usesDeviceColour,
        bool usesDeviceRgb,
        bool usesDeviceCmyk,
        bool usesDeviceGray,
        HashSet<string> drawnXObjects,
        HashSet<string> renderingIntents,
        HashSet<string> selectedColorSpaces,
        HashSet<string> usedFonts,
        HashSet<string> paintedShadings,
        List<TextShow> textShows,
        List<MarkedContentSequence> markedContentSequences,
        List<TextShowMcContext> textShowContexts,
        List<SimpleContentItem> simpleContentItems)
    {
        AppliedExtGStates = appliedExtGStates;
        UsesDeviceColour = usesDeviceColour;
        UsesDeviceRgb = usesDeviceRgb;
        UsesDeviceCmyk = usesDeviceCmyk;
        UsesDeviceGray = usesDeviceGray;
        DrawnXObjects = drawnXObjects;
        RenderingIntents = renderingIntents;
        SelectedColorSpaces = selectedColorSpaces;
        UsedFonts = usedFonts;
        PaintedShadings = paintedShadings;
        TextShows = textShows;
        MarkedContentSequences = markedContentSequences;
        TextShowContexts = textShowContexts;
        SimpleContentItems = simpleContentItems;
    }

    /// <summary>The ExtGState resource names actually applied by a <c>gs</c> operator.</summary>
    public HashSet<string> AppliedExtGStates { get; }

    /// <summary>True when the page paints with device-dependent colour.</summary>
    public bool UsesDeviceColour { get; }

    /// <summary>True when the page uses DeviceRGB colour (<c>rg</c>/<c>RG</c> operators).</summary>
    public bool UsesDeviceRgb { get; }

    /// <summary>True when the page uses DeviceCMYK colour (<c>k</c>/<c>K</c> operators).</summary>
    public bool UsesDeviceCmyk { get; }

    /// <summary>True when the page uses DeviceGray colour (<c>g</c>/<c>G</c> operators).</summary>
    public bool UsesDeviceGray { get; }

    /// <summary>The XObject resource names actually painted by a <c>Do</c> operator.</summary>
    public HashSet<string> DrawnXObjects { get; }

    /// <summary>Rendering intents set by the <c>ri</c> operator.</summary>
    public HashSet<string> RenderingIntents { get; }

    /// <summary>Colour space names set by <c>cs</c>/<c>CS</c> operators.</summary>
    public HashSet<string> SelectedColorSpaces { get; }

    /// <summary>Font resource names selected by <c>Tf</c> operators (usage-scoped).</summary>
    public HashSet<string> UsedFonts { get; }

    /// <summary>Shading resource names painted by <c>sh</c> operators.</summary>
    public HashSet<string> PaintedShadings { get; }

    /// <summary>
    /// One entry per text-show operator (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c>) in document order.
    /// Each entry records the current font resource name, the current text rendering mode (–1 = unknown),
    /// and the raw bytes of the string shown. For <c>TJ</c>, one <see cref="TextShow"/> is emitted per
    /// string element in the array; number elements (spacing adjustments) are skipped.
    /// </summary>
    public IReadOnlyList<TextShow> TextShows { get; }

    /// <summary>
    /// Every marked-content sequence (BMC/BDC … EMC) encountered in the page content stream,
    /// in the order they were opened. Includes only sequences from the page content streams
    /// (not Form XObjects, Type 3 CharProcs, or annotation appearance streams).
    /// For BDC sequences with an inline property dict the <see cref="MarkedContentSequence"/>
    /// carries its MCID, Lang, ActualText, Alt, and E properties; for BMC sequences and BDC
    /// sequences referencing a named Properties resource these properties are null.
    /// </summary>
    public IReadOnlyList<MarkedContentSequence> MarkedContentSequences { get; }

    /// <summary>
    /// One entry per text-show operator, recording the marked-content context (MCID, direct
    /// /Lang, inherited /Lang) in effect at the moment of each show. The <see cref="TextShowMcContext.ShowIndex"/>
    /// matches the corresponding index in <see cref="TextShows"/>.
    /// </summary>
    public IReadOnlyList<TextShowMcContext> TextShowContexts { get; }

    /// <summary>
    /// One entry per "simple content item" operator in document order: Tj/TJ/'/",
    /// S/s/f/F/f*/B/B*/b/b* (painting path ops), sh (shading), EI (inline-image terminator),
    /// and Do when the named XObject has <c>/Subtype /Image</c>. Used by
    /// §7.1-3 (SESimpleContentItem) to verify that every real-content operator is either tagged
    /// or inside an Artifact. Path-construction-only ops (m/l/c/re), clip-only ops (W n/W* n),
    /// color/state ops, and Do on Form XObjects are NOT content items and are never emitted.
    /// Scope: page content streams only (Form XObjects, Type 3 CharProcs, annotation appearances
    /// not walked — under-detection, FP-safe).
    /// </summary>
    public IReadOnlyList<SimpleContentItem> SimpleContentItems { get; }
}

/// <summary>
/// A minimal content-stream operator scan. It reports which graphics-state (<c>/ExtGState</c>)
/// resources a page actually applies (via the <c>gs</c> operator), which XObjects it actually paints
/// (via the <c>Do</c> operator), which font resource names the page selects (via the <c>Tf</c>
/// operator), whether the page paints with device-dependent colour, and the marked-content
/// sequences (BMC/BDC … EMC) with their inline property-dict attributes.
/// </summary>
/// <remarks>
/// Best-effort and defensive: the page content is decoded and tokenised with the reader's lexer;
/// inline-image sample data (<c>ID … EI</c>) is skipped; on any malformed or undecodable content the
/// scan stops and returns what it gathered. It is not a full content-stream interpreter — it tracks
/// only the operands needed for the questions above.
/// </remarks>
internal static class ContentStreamUsage
{
    private static readonly PdfName _contents = new("Contents");
    private static readonly PdfName _resources = new("Resources");
    private static readonly PdfName _properties = new("Properties");
    private static readonly PdfName _mcidKey = new("MCID");
    private static readonly PdfName _xObject = new("XObject");
    private static readonly PdfName _subtype = new("Subtype");
    private static readonly PdfName _imageSubtype = new("Image");

    /// <summary>Scans <paramref name="page"/>'s content streams for graphics-state, colour,
    /// XObject-drawing, rendering-intent, selected-colour-space, selected-font, shading usage,
    /// per-show text rendering mode, and marked-content sequences with their inline properties.</summary>
    public static ContentUsage Analyze(PreflightContext context, PdfDictionary page)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var drawnXObjects = new HashSet<string>(StringComparer.Ordinal);
        var renderingIntents = new HashSet<string>(StringComparer.Ordinal);
        var selectedColorSpaces = new HashSet<string>(StringComparer.Ordinal);
        var usedFonts = new HashSet<string>(StringComparer.Ordinal);
        var paintedShadings = new HashSet<string>(StringComparer.Ordinal);
        var usesDeviceColour = false;
        var usesDeviceRgb = false;
        var usesDeviceCmyk = false;
        var usesDeviceGray = false;
        var textShows = new List<TextShow>();
        var markedContentSequences = new List<MarkedContentSequence>();
        var textShowContexts = new List<TextShowMcContext>();
        var simpleContentItems = new List<SimpleContentItem>();

        // Graphics-state stack for q/Q save-restore. Each entry is (fontResourceName, renderingMode).
        // Default rendering mode = 0 (fill text), default font = null (not yet selected).
        var gsStack = new Stack<(string? Font, int Mode)>();
        var currentFont = (string?)null;
        var currentRenderingMode = 0;

        // Marked-content stack: BMC/BDC pushes, EMC pops. Independent of q/Q (ISO 32000-1 §8.7.2).
        // Each entry is the sequence descriptor for the currently-open MC region.
        var mcStack = new Stack<MarkedContentSequence>();

        // Pre-build the named-Properties MCID map for this page (ISO 32000-1 §8.7.3.3).
        // Resolves /Resources /Properties /Name → /MCID for the named-reference BDC form.
        var namedPropertiesMcids = BuildNamedPropertiesMcids(context, page);

        // Pre-build the XObject subtype map: XObject resource name → true when /Subtype == /Image.
        // Used by the Do-operator content-item check (§7.1-3): only image Do ops create content items.
        var xObjectIsImage = BuildXObjectImageSet(context, page);

        var content = GetContentBytes(context, page);
        if (content is { Length: > 0 })
        {
            try
            {
                var lexer = new PdfLexer(content);
                // lastName tracks the most recent /Name token operand (for gs, Do, ri, cs/CS, Tf, sh).
                // lastInt tracks the most recent integer operand (for Tr).
                // lastStringBytes tracks the most recent string operand (for Tj, ', ").
                // None of these clear each other — they are independent tracking channels.
                // All three are cleared only when a keyword (operator) is encountered, because
                // an operator consumes all its pending operands.
                string? lastName = null;
                string? prevName = null; // second-to-last name (for /Tag /Resource BDC case)
                int? lastInt = null;
                byte[]? lastStringBytes = null;
                // Pending string operands for TJ: collected between [ and ]
                var tjStrings = new List<byte[]>();
                var inArray = false;
                // Inline dict collection for BDC property dicts.
                // When we see DictBegin the preceding lastName is the BDC tag; we collect
                // key-value pairs until DictEnd.
                string? pendingBdcTag = null;
                InlineDictProps? pendingProps = null;
                int dictDepth = 0;           // nesting depth inside a << ... >> we're collecting
                string? currentDictKey = null;
                InlineDictProps? buildingProps = null; // props being built for current dict

                while (!lexer.AtEnd)
                {
                    var token = lexer.NextToken();
                    if (token.Kind == TokenKind.EndOfInput)
                        break;

                    // ── Inline dict collection for BDC property dicts ──────────────────────────
                    // When we see a DictBegin after a Name token (which becomes the BDC tag),
                    // we enter dict-collection mode. DictEnd at depth 1 finishes collection.
                    if (token.Kind == TokenKind.DictBegin)
                    {
                        dictDepth++;
                        if (dictDepth == 1)
                        {
                            // Outer << starts a new inline dict — the preceding lastName is the BDC tag.
                            pendingBdcTag = lastName;
                            buildingProps = new InlineDictProps();
                            currentDictKey = null;
                        }
                        // Nested dicts inside the property dict: skip them (value is a dict,
                        // we don't need nested dict values for MCID/Lang/ActualText/Alt/E).
                        continue;
                    }

                    if (token.Kind == TokenKind.DictEnd)
                    {
                        if (dictDepth > 0)
                        {
                            dictDepth--;
                            if (dictDepth == 0)
                            {
                                // Finished collecting the outer dict.
                                pendingProps = buildingProps;
                                buildingProps = null;
                                currentDictKey = null;
                            }
                        }
                        continue;
                    }

                    // While inside a dict, collect key-value pairs for depth==1 entries only.
                    if (dictDepth > 0)
                    {
                        if (dictDepth == 1)
                        {
                            if (token.Kind == TokenKind.Name)
                            {
                                var n = DecodeName(token.Raw.Span);
                                if (currentDictKey is null)
                                    currentDictKey = n;
                                else
                                {
                                    // Previous key had a Name value — store it (e.g. /Type /Pagination)
                                    StoreInlineProp(buildingProps!, currentDictKey, null, null, n);
                                    currentDictKey = n;
                                }
                            }
                            else if (token.Kind == TokenKind.Integer && currentDictKey is not null)
                            {
                                var intVal = ParseInt(token.Raw.Span);
                                StoreInlineProp(buildingProps!, currentDictKey, intVal, null, null);
                                currentDictKey = null;
                            }
                            else if (token.Kind is TokenKind.LiteralString or TokenKind.HexString
                                     && currentDictKey is not null)
                            {
                                var strVal = DecodeStringToUtf16(token);
                                StoreInlineProp(buildingProps!, currentDictKey, null, strVal, null);
                                currentDictKey = null;
                            }
                            else if (token.Kind == TokenKind.Keyword && currentDictKey is not null)
                            {
                                // Booleans (true/false/null) as dict values — skip (no tracked attrs use them).
                                currentDictKey = null;
                            }
                            else if (token.Kind is TokenKind.Real && currentDictKey is not null)
                            {
                                currentDictKey = null;
                            }
                        }
                        // Inside dict at any depth — don't process as main-stream tokens.
                        continue;
                    }

                    if (token.Kind == TokenKind.Name)
                    {
                        // Name token — shift lastName to prevName, update lastName.
                        // Does NOT clear lastInt or lastStringBytes.
                        prevName = lastName;
                        lastName = DecodeName(token.Raw.Span);
                        continue;
                    }

                    if (token.Kind == TokenKind.Integer)
                    {
                        // Integer token — update lastInt; does NOT clear lastName or lastStringBytes.
                        lastInt = ParseInt(token.Raw.Span);
                        // (number elements inside a TJ array are spacing adjustments — not text)
                        continue;
                    }

                    if (token.Kind == TokenKind.Real)
                    {
                        // Real number — Tr only takes an integer, so a real before Tr is not valid;
                        // clear lastInt so we don't misinterpret it as the Tr mode. Does NOT clear
                        // lastName or lastStringBytes.
                        lastInt = null;
                        continue;
                    }

                    if (token.Kind is TokenKind.LiteralString or TokenKind.HexString)
                    {
                        var bytes = DecodeStringBytes(token);
                        lastStringBytes = bytes;
                        if (inArray)
                            tjStrings.Add(bytes);
                        continue;
                    }

                    if (token.Kind == TokenKind.ArrayBegin)
                    {
                        inArray = true;
                        tjStrings.Clear();
                        continue;
                    }

                    if (token.Kind == TokenKind.ArrayEnd)
                    {
                        inArray = false;
                        continue;
                    }

                    if (token.Kind == TokenKind.Keyword)
                    {
                        var op = Encoding.Latin1.GetString(token.Raw.Span);
                        switch (op)
                        {
                            case "gs":
                                if (lastName is not null)
                                    applied.Add(lastName);
                                break;

                            case "rg" or "RG":
                                usesDeviceColour = true;
                                usesDeviceRgb = true;
                                break;

                            case "k" or "K":
                                usesDeviceColour = true;
                                usesDeviceCmyk = true;
                                break;

                            case "g" or "G":
                                usesDeviceColour = true;
                                usesDeviceGray = true;
                                break;

                            case "Do":
                                if (lastName is not null)
                                {
                                    drawnXObjects.Add(lastName);
                                    // Emit a content item only for image XObjects (not Form).
                                    if (xObjectIsImage.Contains(lastName))
                                        simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                }
                                break;

                            case "ri":
                                if (lastName is not null)
                                    renderingIntents.Add(lastName);
                                break;

                            case "cs" or "CS":
                                if (lastName is not null)
                                    selectedColorSpaces.Add(lastName);
                                break;

                            case "Tf":
                                // /FontName size Tf — lastName holds the font resource name.
                                if (lastName is not null)
                                {
                                    usedFonts.Add(lastName);
                                    currentFont = lastName;
                                }
                                break;

                            case "sh":
                                if (lastName is not null)
                                {
                                    paintedShadings.Add(lastName);
                                    simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                }
                                break;

                            case "Tr":
                                // integer Tr — sets text rendering mode. lastInt is the operand.
                                currentRenderingMode = lastInt ?? -1;
                                break;

                            case "q":
                                // Save graphics state.
                                gsStack.Push((currentFont, currentRenderingMode));
                                break;

                            case "Q":
                                // Restore graphics state.
                                if (gsStack.Count > 0)
                                    (currentFont, currentRenderingMode) = gsStack.Pop();
                                break;

                            case "Tj":
                                // string Tj
                                if (lastStringBytes is not null)
                                {
                                    var show = new TextShow(currentFont, currentRenderingMode, lastStringBytes);
                                    textShowContexts.Add(BuildMcContext(textShows.Count, mcStack));
                                    textShows.Add(show);
                                    simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                }
                                break;

                            case "TJ":
                                // [ ... ] TJ — emit one TextShow per string element collected;
                                // emit one content item per string element (each is a separate text item).
                                foreach (var bytes in tjStrings)
                                {
                                    var show = new TextShow(currentFont, currentRenderingMode, bytes);
                                    textShowContexts.Add(BuildMcContext(textShows.Count, mcStack));
                                    textShows.Add(show);
                                    simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                }
                                tjStrings.Clear();
                                break;

                            case "'":
                                // string ' — move to next line then show string
                                if (lastStringBytes is not null)
                                {
                                    var show = new TextShow(currentFont, currentRenderingMode, lastStringBytes);
                                    textShowContexts.Add(BuildMcContext(textShows.Count, mcStack));
                                    textShows.Add(show);
                                    simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                }
                                break;

                            case "\"":
                                // aw ac string " — word spacing, char spacing, then show string.
                                if (lastStringBytes is not null)
                                {
                                    var show = new TextShow(currentFont, currentRenderingMode, lastStringBytes);
                                    textShowContexts.Add(BuildMcContext(textShows.Count, mcStack));
                                    textShows.Add(show);
                                    simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                }
                                break;

                            case "BMC":
                                // /tag BMC — tag is in lastName.
                                if (lastName is not null)
                                {
                                    var seq = new MarkedContentSequence(
                                        lastName, null, null, null, null, null,
                                        FindInheritedLang(mcStack),
                                        FindHasArtifactAncestor(mcStack),
                                        FindAncestorMcid(mcStack));
                                    mcStack.Push(seq);
                                    markedContentSequences.Add(seq);
                                }
                                break;

                            case "BDC":
                                // /tag name BDC (named resource) or /tag << ... >> BDC (inline dict).
                                // pendingBdcTag + pendingProps are set if we saw a << ... >> before this.
                                // Otherwise lastName holds the tag (second-to-last name before operator).
                                {
                                    string? bdcTag;
                                    InlineDictProps? props;
                                    if (pendingProps is not null && pendingBdcTag is not null)
                                    {
                                        // Inline dict case: tag is pendingBdcTag, dict is pendingProps.
                                        bdcTag = pendingBdcTag;
                                        props = pendingProps;
                                    }
                                    else
                                    {
                                        // Named resource case: /Tag /ResourceName BDC.
                                        // prevName holds the tag; lastName holds the resource name.
                                        // (For /Tag BDC with a single name, prevName is null and
                                        // lastName is the tag — use lastName as fallback.)
                                        bdcTag = prevName ?? lastName;
                                        // Resolve the named Properties resource to get its MCID
                                        // (ISO 32000-1 §8.7.3.3: /Resources /Properties /Name → dict).
                                        if (lastName is not null && prevName is not null
                                            && namedPropertiesMcids.TryGetValue(lastName, out var namedMcid))
                                        {
                                            props = new InlineDictProps { Mcid = namedMcid };
                                        }
                                        else
                                        {
                                            props = null;
                                        }
                                    }

                                    if (bdcTag is not null)
                                    {
                                        var seq = new MarkedContentSequence(
                                            bdcTag,
                                            props?.Mcid,
                                            props?.Lang,
                                            props?.ActualText,
                                            props?.Alt,
                                            props?.E,
                                            FindInheritedLang(mcStack),
                                            FindHasArtifactAncestor(mcStack),
                                            FindAncestorMcid(mcStack));
                                        mcStack.Push(seq);
                                        markedContentSequences.Add(seq);
                                    }
                                    pendingBdcTag = null;
                                    pendingProps = null;
                                }
                                break;

                            case "EMC":
                                if (mcStack.Count > 0)
                                    mcStack.Pop();
                                break;

                            case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                                // Painting path operators — each creates a content item.
                                simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                break;

                            case "ID":
                                // Inline image: the ID … EI pair is one content item.
                                // EI is consumed by SkipInlineImageData and not seen as a separate token,
                                // so we emit here (semantically equivalent: one item per inline image).
                                simpleContentItems.Add(BuildSimpleContentItem(mcStack));
                                SkipInlineImageData(lexer, content);
                                break;
                        }

                        // Keywords (operators) consume all pending operands; clear all tracking state.
                        lastName = null;
                        prevName = null;
                        lastInt = null;
                        lastStringBytes = null;
                        // Note: inArray is NOT reset here.
                    }
                }
            }
            catch
            {
                // Malformed content — keep whatever was collected before the failure.
            }
        }

        return new ContentUsage(applied, usesDeviceColour, usesDeviceRgb, usesDeviceCmyk, usesDeviceGray,
            drawnXObjects, renderingIntents,
            selectedColorSpaces, usedFonts, paintedShadings, textShows,
            markedContentSequences, textShowContexts, simpleContentItems);
    }

    // Build a TextShowMcContext from the current MC stack state.
    private static TextShowMcContext BuildMcContext(int showIndex, Stack<MarkedContentSequence> mcStack)
    {
        if (mcStack.Count == 0)
            return new TextShowMcContext(showIndex, null, null, null);
        var top = mcStack.Peek();
        // DirectLang = /Lang on the innermost BDC itself.
        // InheritedLang = /Lang from any ancestor (stored on each MarkedContentSequence at push time).
        return new TextShowMcContext(showIndex, top.Mcid, top.Lang, top.InheritedLang);
    }

    // Builds a SimpleContentItem from the current MC stack for §7.1-3.
    // EffectiveMcid = top.Mcid (if set) else top.AncestorMcid (propagated from ancestors).
    // IsInsideArtifact = top is Artifact OR top has any Artifact ancestor.
    private static SimpleContentItem BuildSimpleContentItem(Stack<MarkedContentSequence> mcStack)
    {
        if (mcStack.Count == 0)
            return new SimpleContentItem(null, false);
        var top = mcStack.Peek();
        var effectiveMcid = top.Mcid ?? top.AncestorMcid;
        var isInsideArtifact = top.IsArtifact || top.HasArtifactAncestor;
        return new SimpleContentItem(effectiveMcid, isInsideArtifact);
    }

    // Returns the first /Lang found in the ancestor chain (not including the element itself).
    private static string? FindInheritedLang(Stack<MarkedContentSequence> mcStack)
    {
        foreach (var ancestor in mcStack)
        {
            if (ancestor.Lang is not null)
                return ancestor.Lang;
            if (ancestor.InheritedLang is not null)
                return ancestor.InheritedLang;
        }
        return null;
    }

    // Returns true when any ancestor BDC/BMC in the stack has tag "Artifact".
    // Used to populate MarkedContentSequence.HasArtifactAncestor for §7.1-2.
    private static bool FindHasArtifactAncestor(Stack<MarkedContentSequence> mcStack)
    {
        foreach (var ancestor in mcStack)
        {
            if (ancestor.IsArtifact)
                return true;
            // Short-circuit: if the ancestor itself already knows it has an Artifact ancestor
            // there's no need to look further up the chain.
            if (ancestor.HasArtifactAncestor)
                return true;
        }
        return false;
    }

    // Returns the MCID from the nearest ancestor BDC that carries an MCID, or null.
    // Used to populate MarkedContentSequence.AncestorMcid for §7.1-1.
    private static int? FindAncestorMcid(Stack<MarkedContentSequence> mcStack)
    {
        foreach (var ancestor in mcStack)
        {
            if (ancestor.Mcid is not null)
                return ancestor.Mcid;
            // If the ancestor itself has an ancestor MCID, propagate it upward.
            if (ancestor.AncestorMcid is not null)
                return ancestor.AncestorMcid;
        }
        return null;
    }

    // Stores one key-value entry from an inline property dict into the collector.
    private static void StoreInlineProp(InlineDictProps props, string key, int? intVal, string? strVal, string? nameVal)
    {
        switch (key)
        {
            case "MCID" when intVal is not null:
                props.Mcid = intVal;
                break;
            case "Lang" when strVal is not null:
                props.Lang = strVal;
                break;
            case "ActualText" when strVal is not null:
                props.ActualText = strVal;
                break;
            case "Alt" when strVal is not null:
                props.Alt = strVal;
                break;
            case "E" when strVal is not null:
                props.E = strVal;
                break;
        }
    }

    // Mutable bag for inline dict properties being collected.
    private sealed class InlineDictProps
    {
        public int? Mcid;
        public string? Lang;
        public string? ActualText;
        public string? Alt;
        public string? E;
    }

    // Builds a map of resource name → MCID for every entry in the page's
    // /Resources /Properties that resolves to a dict carrying /MCID (integer).
    // Empty when the page has no /Resources/Properties or no MCID entries.
    private static Dictionary<string, int> BuildNamedPropertiesMcids(
        PreflightContext context, PdfDictionary page)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (context.ResolveInherited(page, _resources) is not PdfDictionary resources)
            return map;
        if (context.Resolve(resources.Get(_properties)) is not PdfDictionary properties)
            return map;
        foreach (var entry in properties.Entries)
        {
            if (context.Resolve(entry.Value) is PdfDictionary propDict
                && context.Resolve(propDict.Get(_mcidKey)) is PdfInteger mcidInt)
            {
                map[entry.Key.Value] = (int)mcidInt.Value;
            }
        }
        return map;
    }

    // Builds the set of XObject resource names whose /Subtype is /Image.
    // Used by the Do-operator content-item check: only image-Do creates a content item (not form-Do).
    private static HashSet<string> BuildXObjectImageSet(PreflightContext context, PdfDictionary page)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (context.ResolveInherited(page, _resources) is not PdfDictionary resources)
            return set;
        if (context.Resolve(resources.Get(_xObject)) is not PdfDictionary xObjects)
            return set;
        foreach (var entry in xObjects.Entries)
        {
            if (context.Resolve(entry.Value) is PdfDictionary xobj
                && context.Resolve(xobj.Get(_subtype)) is PdfName subtypeName
                && subtypeName.Value == _imageSubtype.Value)
            {
                set.Add(entry.Key.Value);
            }
        }
        return set;
    }

    // ── Non-page content stream collector ─────────────────────────────────────────────────────────

    private const int MaxFormXObjectDepth = 32; // defensive recursion cap for nested Do chains

    private static readonly PdfName _font = new("Font");
    private static readonly PdfName _charProcs = new("CharProcs");
    private static readonly PdfName _annots = new("Annots");
    private static readonly PdfName _ap = new("AP");
    private static readonly PdfName _n = new("N");
    private static readonly PdfName _form = new("Form");

    /// <summary>
    /// Returns every non-page content stream reachable from <paramref name="page"/>, decoded and
    /// tagged with its kind and effective resource dictionary. The collector is:
    /// <list type="bullet">
    ///   <item><description><strong>Form XObjects:</strong> only those actually drawn by a <c>Do</c>
    ///   operator in the page content stream or in a drawn ancestor Form XObject (depth-first
    ///   traversal). A Form XObject that is present in <c>/Resources /XObject</c> but never
    ///   invoked is excluded — veraPDF does not validate its content (empirically confirmed,
    ///   2026-06-23).</description></item>
    ///   <item><description><strong>Type 3 CharProcs:</strong> all <c>/CharProcs</c> entries of
    ///   every Type 3 font <em>selected</em> by a <c>Tf</c> operator on the page, regardless of
    ///   whether each individual glyph was shown. Type 3 fonts in <c>/Resources /Font</c> but never
    ///   selected via <c>Tf</c> are excluded — veraPDF validates all CharProcs of selected fonts, not
    ///   unselected ones (empirically confirmed, 2026-06-23).</description></item>
    ///   <item><description><strong>Annotation appearances:</strong> all <c>/AP /N</c> streams
    ///   (and all state streams within an <c>/AP /N</c> sub-dictionary) for every annotation on
    ///   the page, regardless of annotation visibility flags — veraPDF validates appearance content
    ///   even for hidden annotations (empirically confirmed, 2026-06-23).</description></item>
    /// </list>
    /// <para>
    /// <strong>Cycle guard:</strong> Form XObject traversal tracks visited stream object numbers in a
    /// <see cref="HashSet{T}"/>; cycles (A draws B draws A) are detected and skipped.
    /// Recursion is also bounded to <see cref="MaxFormXObjectDepth"/> levels.
    /// </para>
    /// <para>
    /// <strong>Defensive:</strong> any decode or resolve failure on a single stream is silently
    /// swallowed; the collector never throws. The list may be empty when no non-page content streams
    /// are present or all fail to decode.
    /// </para>
    /// <para>
    /// Clean-room: derived from ISO 32000-1:2008 §8.8 (Form XObjects), §9.6.4 (Type 3 fonts),
    /// §12.5.5 (annotation appearances), and empirical veraPDF-oracle probing — not from any
    /// third-party validation profile.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReachableContentStream> GetReachableContentStreams(
        PreflightContext context, PdfDictionary page)
    {
        var result = new List<ReachableContentStream>();
        // Cycle guard: tracks object numbers of Form XObject streams already visited,
        // so a recursive Do chain (A → B → A) terminates.
        var visitedFormObjNums = new HashSet<int>();

        // ── 1. Form XObjects (drawn-only) ─────────────────────────────────────
        // Parse the page content to find all Do-drawn XObjects, then recurse.
        try
        {
            var pageBytes = GetContentBytes(context, page);
            if (pageBytes is { Length: > 0 })
            {
                if (context.ResolveInherited(page, _resources) is PdfDictionary pageResources)
                    CollectDrawnFormXObjects(context, pageBytes, pageResources, result, visitedFormObjNums, 0);
            }
        }
        catch
        {
            // Page content failure — skip Form XObject collection; do not abort other kinds.
        }

        // ── 2. Type 3 CharProcs (all glyphs for USED fonts) ──────────────────
        // veraPDF validates all CharProcs of every Type 3 font that is selected by a Tf operator
        // in the page content — not just the glyphs actually shown, but also not fonts that are
        // merely present in /Resources but never selected (empirically confirmed, 2026-06-23).
        try
        {
            if (context.ResolveInherited(page, _resources) is PdfDictionary pageResources
                && context.Resolve(pageResources.Get(_font)) is PdfDictionary fonts)
            {
                // Determine which font resource names are actually selected by Tf on this page.
                // Re-use the existing Analyze pass; the cost is one extra page-content scan.
                var usedFontNames = Analyze(context, page).UsedFonts;

                foreach (var fontEntry in fonts.Entries)
                {
                    // Only process Type3 fonts that are actually selected by content (Tf).
                    if (!usedFontNames.Contains(fontEntry.Key.Value))
                        continue;

                    try
                    {
                        if (context.Resolve(fontEntry.Value) is not PdfDictionary fontDict)
                            continue;
                        if (context.Resolve(fontDict.Get(_subtype)) is not PdfName subtypeName
                            || subtypeName.Value != "Type3")
                            continue;
                        if (context.Resolve(fontDict.Get(_charProcs)) is not PdfDictionary charProcs)
                            continue;

                        // The Type3 font's own /Resources (ISO 32000-1 §9.6.4 / Table 111).
                        var type3Resources = context.Resolve(fontDict.Get(_resources)) as PdfDictionary;

                        foreach (var procEntry in charProcs.Entries)
                        {
                            try
                            {
                                var stream = context.ResolveStream(procEntry.Value);
                                if (stream is null) continue;
                                var bytes = context.DecodeStream(stream);
                                if (bytes is not { Length: > 0 }) continue;
                                result.Add(new ReachableContentStream(bytes, type3Resources, ContentStreamKind.Type3CharProc));
                            }
                            catch { /* skip one bad CharProc */ }
                        }
                    }
                    catch { /* skip one bad font */ }
                }
            }
        }
        catch
        {
            // Font enumeration failure — skip; do not abort.
        }

        // ── 3. Annotation appearance streams (all, regardless of visibility) ───
        try
        {
            if (context.Resolve(page.Get(_annots)) is PdfArray annots)
            {
                for (var i = 0; i < annots.Count; i++)
                {
                    try
                    {
                        if (context.Resolve(annots[i]) is not PdfDictionary annot) continue;
                        if (context.Resolve(annot.Get(_ap)) is not PdfDictionary ap) continue;

                        // /AP /N: can be a stream (direct appearance) or a sub-dictionary
                        // (keyed appearance states, e.g. for button fields).
                        // ISO 32000-1 §12.5.5 Table 168: /N = normal appearance.
                        CollectApNStream(context, ap.Get(_n), result);
                    }
                    catch { /* skip one bad annotation */ }
                }
            }
        }
        catch
        {
            // Annots enumeration failure — skip; do not abort.
        }

        return result;
    }

    // Scans the content bytes for Do operators, resolves each drawn XObject that is a Form,
    // and recurses into its own content stream (depth-first, cycle-guarded).
    private static void CollectDrawnFormXObjects(
        PreflightContext context,
        byte[] contentBytes,
        PdfDictionary resources,
        List<ReachableContentStream> result,
        HashSet<int> visitedObjNums,
        int depth)
    {
        if (depth > MaxFormXObjectDepth)
            return;

        if (context.Resolve(resources.Get(_xObject)) is not PdfDictionary xObjects)
            return;

        string? lastName = null;
        try
        {
            var lexer = new PdfLexer(contentBytes);
            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput) break;

                if (token.Kind == TokenKind.Name)
                {
                    lastName = DecodeName(token.Raw.Span);
                    continue;
                }

                if (token.Kind == TokenKind.Keyword)
                {
                    var op = Encoding.Latin1.GetString(token.Raw.Span);
                    if (op == "ID")
                    {
                        // Skip inline-image binary data.
                        SkipInlineImageData(lexer, contentBytes);
                    }
                    else if (op == "Do" && lastName is not null)
                    {
                        // Resolve the XObject named by lastName.
                        var xObjRef = xObjects.Get(new PdfName(lastName));
                        if (xObjRef is PdfIndirectReference r)
                        {
                            // Cycle guard: skip if already visited.
                            if (!visitedObjNums.Add(r.ObjectNumber))
                            {
                                lastName = null;
                                continue;
                            }

                            try
                            {
                                if (context.Resolve(xObjRef) is PdfDictionary xObjDict
                                    && context.Resolve(xObjDict.Get(_subtype)) is PdfName xSubtype
                                    && xSubtype.Value == _form.Value)
                                {
                                    var stream = context.ResolveStream(xObjRef);
                                    if (stream is not null)
                                    {
                                        var bytes = context.DecodeStream(stream);
                                        if (bytes is { Length: > 0 })
                                        {
                                            // The Form XObject's own /Resources (may be null).
                                            var formResources = context.Resolve(xObjDict.Get(_resources)) as PdfDictionary;
                                            result.Add(new ReachableContentStream(bytes, formResources, ContentStreamKind.FormXObject));

                                            // Recurse: the Form XObject may itself draw other Forms.
                                            // Use formResources if present, otherwise fall back to parent resources.
                                            var resourcesForChildren = formResources ?? resources;
                                            CollectDrawnFormXObjects(context, bytes, resourcesForChildren, result, visitedObjNums, depth + 1);
                                        }
                                    }
                                }
                            }
                            catch { /* skip one bad XObject; cycle-guard entry already added */ }
                        }
                    }

                    lastName = null;
                }
            }
        }
        catch { /* lexer error — keep what was collected */ }
    }

    // Collects appearance streams from /AP /N (either a direct stream or a sub-dictionary
    // keyed by appearance state names per ISO 32000-1 §12.5.5).
    //
    // The branch decision uses ResolveStream to probe whether /N (or the object it references)
    // IS a stream.  The reader's Resolve() method returns the stream's inline dictionary (a
    // PdfDictionary, not a PdfStream) for stream objects, so "resolved is PdfDictionary" is true
    // for BOTH a stream object and a plain dictionary — making type-testing on the resolved value
    // unreliable.  ResolveStream, by contrast, returns non-null only when the object is actually
    // a stream.
    //
    // FIX 1 (indirect sub-dict):
    //   When /N = 8 0 R where object 8 is << /Off 6 0 R /On 7 0 R >> (a plain dictionary, not a
    //   stream), the old code branched on `nObj is PdfIndirectReference` and called
    //   ResolveStream(nRef), which returned null (object 8 is not a stream) and silently dropped all
    //   state streams.  The fix below calls ResolveStream(nObj) first; if it returns null the object
    //   is not a stream so we fall through to the sub-dictionary path, which now works for both
    //   direct and indirect sub-dicts.
    private static void CollectApNStream(
        PreflightContext context,
        PdfObject? nObj,
        List<ReachableContentStream> result)
    {
        if (nObj is null) return;

        // Try to resolve as a stream first.  ResolveStream returns non-null only for actual PDF
        // stream objects (regardless of whether /N is a direct or indirect reference).
        ParsedStream? directStream = null;
        try { directStream = context.ResolveStream(nObj); } catch { }

        if (directStream is not null)
        {
            // Direct appearance: /N is (or points to) a stream.
            try
            {
                var bytes = context.DecodeStream(directStream);
                if (bytes is not { Length: > 0 }) return;
                var apResources = context.Resolve(directStream.Dictionary.Get(_resources)) as PdfDictionary;
                result.Add(new ReachableContentStream(bytes, apResources, ContentStreamKind.AnnotationAppearance));
            }
            catch { }
        }
        else
        {
            // Not a stream: /N is (or points to) a sub-dictionary keyed by appearance state names,
            // e.g. << /Off 6 0 R /On 7 0 R >>.  Works whether the sub-dict is direct or indirect.
            var resolved = context.Resolve(nObj);
            if (resolved is not PdfDictionary subDict) return;

            foreach (var entry in subDict.Entries)
            {
                try
                {
                    var stream = context.ResolveStream(entry.Value);
                    if (stream is null) continue;
                    var bytes = context.DecodeStream(stream);
                    if (bytes is not { Length: > 0 }) continue;
                    var apResources = context.Resolve(stream.Dictionary.Get(_resources)) as PdfDictionary;
                    result.Add(new ReachableContentStream(bytes, apResources, ContentStreamKind.AnnotationAppearance));
                }
                catch { /* skip one bad appearance state */ }
            }
        }
    }

    // ── End of non-page content stream collector ───────────────────────────────────────────────────

    /// <summary>The page's concatenated, decoded content-stream bytes (or null when empty/undecodable).
    /// Exposed for rules that need a deeper content scan than the operand tracking above.</summary>
    public static byte[]? GetPageContent(PreflightContext context, PdfDictionary page)
        => GetContentBytes(context, page);

    private static byte[]? GetContentBytes(PreflightContext context, PdfDictionary page)
    {
        var contentsObj = page.Get(_contents);
        using var ms = new MemoryStream();
        if (context.Resolve(contentsObj) is PdfArray array)
        {
            for (var i = 0; i < array.Count; i++)
                AppendStream(context, array[i], ms);
        }
        else
        {
            AppendStream(context, contentsObj, ms);
        }
        return ms.Length == 0 ? null : ms.ToArray();
    }

    private static void AppendStream(PreflightContext context, PdfObject? streamRef, MemoryStream ms)
    {
        var stream = context.ResolveStream(streamRef);
        if (stream is null)
            return;
        var bytes = context.DecodeStream(stream);
        if (bytes is null)
            return;
        ms.Write(bytes);
        ms.WriteByte((byte)'\n'); // separate concatenated content streams (ISO 32000-1 §7.8.2)
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

    // Decodes raw integer bytes (ASCII digits, optional leading sign) to int. Returns null on failure.
    private static int? ParseInt(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) return null;
        var sign = 1;
        var i = 0;
        if (raw[0] == (byte)'-') { sign = -1; i = 1; }
        else if (raw[0] == (byte)'+') { i = 1; }
        if (i >= raw.Length) return null;
        var value = 0;
        for (; i < raw.Length; i++)
        {
            if (raw[i] < (byte)'0' || raw[i] > (byte)'9') return null;
            value = value * 10 + (raw[i] - '0');
        }
        return sign * value;
    }

    // Decodes the raw bytes of a LiteralString or HexString token to the content bytes.
    private static byte[] DecodeStringBytes(Token token)
    {
        if (token.Kind == TokenKind.LiteralString)
            return PdfObjectParser.DecodeLiteralString(token.Raw).Bytes.ToArray();
        if (token.Kind == TokenKind.HexString)
            return PdfObjectParser.DecodeHexString(token.Raw).Bytes.ToArray();
        return [];
    }

    // Decodes a string token to a UTF-16 / Latin-1 string for property dict values.
    private static string DecodeStringToUtf16(Token token)
    {
        var bytes = DecodeStringBytes(token);
        if (bytes.Length == 0) return string.Empty;
        // PDF strings: if starts with BOM (FE FF → UTF-16 BE, FF FE → UTF-16 LE), decode accordingly;
        // otherwise treat as PDFDocEncoding / Latin-1.
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.Latin1.GetString(bytes);
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    internal static void SkipInlineImageData(PdfLexer lexer, ReadOnlySpan<byte> content)
    {
        // After 'ID' a single whitespace precedes the raw samples, which run to a whitespace-delimited
        // 'EI'. Resume tokenising after that marker so binary samples are not mis-read as operators.
        var pos = lexer.Position;
        if (pos < content.Length && IsWhitespace(content[pos]))
            pos++;
        for (; pos + 1 < content.Length; pos++)
        {
            if (content[pos] == (byte)'E' && content[pos + 1] == (byte)'I'
                && (pos == 0 || IsWhitespace(content[pos - 1]))
                && (pos + 2 >= content.Length || IsWhitespace(content[pos + 2])))
            {
                lexer.Seek(pos + 2);
                return;
            }
        }
        lexer.Seek(content.Length);
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
}
