// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.2.2 test 2. Every content stream that references named resources shall have an
/// explicitly associated <c>/Resources</c> dictionary of its own — neither the page nor a non-page
/// stream (Form XObject, Type 3 CharProc, annotation appearance) may rely solely on a
/// <c>/Resources</c> dictionary inherited from an ancestor <c>Pages</c> node or from the calling
/// page's resource scope.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.2 and ISO 32000-1:2008, 7.8.3. Empirical veraPDF-oracle
/// probing (2026-06-23) establishes the precise trigger for each stream kind:
/// <list type="bullet">
///   <item><description>
///   <strong>Page content stream:</strong> fires when (a) the page dictionary itself has no
///   <c>/Resources</c> key (<c>page.Get(Resources)</c> returns null — inheritance from the Pages
///   tree does not satisfy the requirement) AND (b) the page content uses a named resource whose
///   name IS defined in the ancestor (Pages-tree) <c>/Resources</c> subdictionary for that
///   category. A name that is referenced in the page content but is <em>not</em> defined anywhere
///   in the ancestor scope does NOT trigger the rule — only names that would resolve via
///   inheritance are "inheritedResourceNames" in veraPDF's model (probe A1: veraPDF accepts a
///   resource-less page that uses an undefined name; probe A2: veraPDF fires on a name defined in
///   the ancestor; both confirmed empirically against veraPDF 1.30.2 on 2026-06-23). A page that
///   has its own <c>/Resources</c> entry — even an empty one — satisfies the "explicitly
///   associated" requirement for the page stream.
///   </description></item>
///   <item><description>
///   <strong>Non-page content streams</strong> (drawn Form XObjects, Type 3 CharProcs, annotation
///   <c>/AP /N</c> appearance streams): fires when (a) the stream's own <c>/Resources</c> is
///   absent entirely (null) AND (b) the stream content uses a named resource operator
///   (<c>Tf</c>, <c>Do</c>, <c>gs</c>, <c>cs</c>/<c>CS</c>, <c>sh</c>) referencing a name that
///   IS defined in the page's resolved <c>/Resources</c> subdictionary for that operator's
///   category. The name must actually appear in the ancestor resource scope — merely using an
///   undefined name is not flagged, because veraPDF's model checks <c>inheritedResourceNames</c>
///   (names that resolve via the ancestor chain), not all resource operator occurrences.
///   Confirmed empirically: veraPDF 1.30.2 fires clause 6.2.2 testNumber 2 with context
///   <c>…/xObject[0]/contentStream[0](N 0 obj PDContentStream)</c> only when the used name IS
///   defined in the page's resource scope (probe N5-A, 2026-06-23); when the name is not defined
///   anywhere, veraPDF accepts the document (probe N5-P9, 2026-06-23).
///   </description></item>
/// </list>
/// <para>
/// <strong>Categories detected (both page and non-page):</strong> Font (<c>Tf</c>), XObject
/// (<c>Do</c>), ExtGState (<c>gs</c>), ColorSpace (<c>cs</c>/<c>CS</c>), and Shading (<c>sh</c>).
/// Pattern names (<c>scn</c>/<c>SCN</c> in Pattern color space) and Properties names (<c>BDC</c>/
/// <c>DP</c> with a name operand) are not detected because reliable identification requires
/// stateful color-space and marked-content tracking respectively; omitting them keeps false
/// positives impossible at the cost of under-detecting those two categories.
/// </para>
/// <para>
/// <strong>Defensive operation:</strong> on any decode failure or lexer error the scan stops
/// and retains findings already collected; no spurious finding is emitted for malformed content.
/// </para>
/// </remarks>
internal sealed class InheritedResourceRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.2-2";

    public string Clause => "ISO 19005-2:2011, 6.2.2";

    private static readonly PdfName _font = new("Font");
    private static readonly PdfName _xObject = new("XObject");
    private static readonly PdfName _extGState = new("ExtGState");
    private static readonly PdfName _colorSpace = new("ColorSpace");
    private static readonly PdfName _shading = new("Shading");

    // Colour-space names that `cs`/`CS` resolve directly, WITHOUT a lookup in the page's
    // /Resources /ColorSpace subdictionary (ISO 32000-1 §8.6.3, Table 73; §8.6.8 for Pattern). A
    // page using only these needs no /Resources entry, so they are not "inherited resource names"
    // and must never be reported by this rule — veraPDF accepts them on a resource-less page.
    private static readonly HashSet<string> _directColorSpaces = new(StringComparer.Ordinal)
    {
        "DeviceGray", "DeviceRGB", "DeviceCMYK", "Pattern",
    };

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            EvaluatePage(context, page);
            EvaluateNonPageStreams(context, page);
        }
    }

    private void EvaluatePage(PreflightContext context, PdfDictionary page)
    {
        // Check whether the page has its OWN /Resources key — not inherited from a parent Pages
        // node. page.Get() looks only at the entries of this dictionary object itself; it does
        // not follow /Parent references. A non-null return means the page is explicitly associated
        // with a Resources dictionary and the rule is satisfied regardless of what is in it.
        var ownResources = page.Get(PdfName.Resources);
        if (ownResources is not null)
            return; // page has an explicit /Resources entry — rule satisfied

        // The page has no own /Resources. Resolve the inherited resource scope (from the parent
        // Pages tree). Per veraPDF's "inheritedResourceNames" model (empirically confirmed,
        // probe A1 2026-06-23): a page-content reference to a name fires 6.2.2-2 ONLY when that
        // name IS defined in the ancestor resource subdictionary. A name that is undefined in all
        // ancestor scopes does NOT trigger the rule — veraPDF accepts documents where a resource-
        // less page content uses a name that is absent from the entire inheritance chain.
        // If there are no ancestor resources at all, no name can be "inherited" → nothing to check.
        var inheritedResources = context.ResolveInherited(page, PdfName.Resources) as PdfDictionary;
        if (inheritedResources is null)
        {
            // No ancestor scope: ResolveInherited already skips the page's own key (page.Get
            // returned null above), so if it also returns null there are truly no inherited
            // resources. No inherited name can exist → rule satisfied.
            return;
        }

        // Pre-resolve the inherited resource subdictionaries once.
        var inhFont = context.Resolve(inheritedResources.Get(_font)) as PdfDictionary;
        var inhXObject = context.Resolve(inheritedResources.Get(_xObject)) as PdfDictionary;
        var inhExtGState = context.Resolve(inheritedResources.Get(_extGState)) as PdfDictionary;
        var inhColorSpace = context.Resolve(inheritedResources.Get(_colorSpace)) as PdfDictionary;
        var inhShading = context.Resolve(inheritedResources.Get(_shading)) as PdfDictionary;

        // If none of the relevant subdictionaries exist in the ancestor scope, nothing to report.
        if (inhFont is null && inhXObject is null && inhExtGState is null
            && inhColorSpace is null && inhShading is null)
            return;

        // Scan the page content for named-resource usage.
        HashSet<string> usedFonts, drawnXObjects, appliedExtGStates, selectedColorSpaces, paintedShadings;
        try
        {
            var u = ContentStreamUsage.Analyze(context, page);
            usedFonts = u.UsedFonts;
            drawnXObjects = u.DrawnXObjects;
            appliedExtGStates = u.AppliedExtGStates;
            selectedColorSpaces = u.SelectedColorSpaces;
            paintedShadings = u.PaintedShadings;
        }
        catch
        {
            // Undecodable content — skip this page; do not emit a finding.
            return;
        }

        // Report only names that ARE defined in the inherited ancestor scope for their category.
        // This mirrors veraPDF's inheritedResourceNames semantic: only names present in the
        // ancestor scope are "inherited resource names" — undefined names are not flagged.
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in usedFonts)
            if (inhFont is not null && NameDefinedIn(inhFont, name))
                ReportIfNew(context, name, reported);
        foreach (var name in drawnXObjects)
            if (inhXObject is not null && NameDefinedIn(inhXObject, name))
                ReportIfNew(context, name, reported);
        foreach (var name in appliedExtGStates)
            if (inhExtGState is not null && NameDefinedIn(inhExtGState, name))
                ReportIfNew(context, name, reported);
        foreach (var name in selectedColorSpaces)
            if (!_directColorSpaces.Contains(name)
                && inhColorSpace is not null && NameDefinedIn(inhColorSpace, name))
                ReportIfNew(context, name, reported);
        foreach (var name in paintedShadings)
            if (inhShading is not null && NameDefinedIn(inhShading, name))
                ReportIfNew(context, name, reported);
    }

    /// <summary>
    /// Scans non-page content streams reachable from <paramref name="page"/> via
    /// <see cref="ContentStreamUsage.GetReachableContentStreams"/>. For each stream whose
    /// <see cref="ReachableContentStream.Resources"/> is <see langword="null"/> (i.e. the stream
    /// omits its own <c>/Resources</c> entry entirely), the stream bytes are lexed for
    /// named-resource usage. A finding is emitted only when the used name IS defined in the
    /// page's resolved resource scope (making it a truly "inherited" resource name in veraPDF's
    /// model). Names that are undefined in all ancestor scopes are not flagged — veraPDF also
    /// does not fire on undefined references.
    /// <para>
    /// <strong>FP-safety argument:</strong>
    /// <list type="bullet">
    ///   <item><description>Streams with non-null own <c>/Resources</c> are never checked
    ///   (skipped unconditionally).</description></item>
    ///   <item><description>A name used in a stream content is only flagged when it appears as a
    ///   key in the PAGE's corresponding resource subdictionary. A false positive would require
    ///   veraPDF to accept a document where both conditions hold — impossible by empirical
    ///   confirmation of probe N5-A (2026-06-23).</description></item>
    ///   <item><description>If the page's /Resources is absent or cannot be resolved, the scan is
    ///   skipped entirely for that page (no ancestor scope → no inherited names → no finding).
    ///   </description></item>
    /// </list>
    /// </para>
    /// </summary>
    private void EvaluateNonPageStreams(PreflightContext context, PdfDictionary page)
    {
        // Resolve the page's effective (inherited) /Resources. This is the ancestor scope that
        // a resource-less form stream would inherit from. If no resources are available, no name
        // can be "inherited", so there is nothing to check.
        if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary pageResources)
            return;

        // Pre-build the page resource subdirectories once per page for O(1) name lookup.
        var pageFont = context.Resolve(pageResources.Get(_font)) as PdfDictionary;
        var pageXObject = context.Resolve(pageResources.Get(_xObject)) as PdfDictionary;
        var pageExtGState = context.Resolve(pageResources.Get(_extGState)) as PdfDictionary;
        var pageColorSpace = context.Resolve(pageResources.Get(_colorSpace)) as PdfDictionary;
        var pageShading = context.Resolve(pageResources.Get(_shading)) as PdfDictionary;

        // If none of the relevant subdictionaries exist on the page, no form can have inherited
        // resource names — skip.
        if (pageFont is null && pageXObject is null && pageExtGState is null
            && pageColorSpace is null && pageShading is null)
            return;

        IReadOnlyList<ReachableContentStream> streams;
        try
        {
            streams = ContentStreamUsage.GetReachableContentStreams(context, page);
        }
        catch
        {
            // Collector failure — skip non-page streams for this page.
            return;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cs in streams)
        {
            // Only scan streams that have NO own /Resources dict at all.
            // A stream with any /Resources (even empty) is skipped — FP-safe under-detection.
            if (cs.Resources is not null)
                continue;

            try
            {
                ScanStreamForInheritedResources(
                    cs.Bytes, reported, context,
                    pageFont, pageXObject, pageExtGState, pageColorSpace, pageShading);
            }
            catch
            {
                // Malformed stream — skip; do not crash or emit spurious finding.
            }
        }
    }

    /// <summary>
    /// Lexes <paramref name="bytes"/> for named-resource usage operators (<c>Tf</c>, <c>Do</c>,
    /// <c>gs</c>, <c>cs</c>, <c>CS</c>, <c>sh</c>) preceded by a Name token. For each used name,
    /// checks whether it appears as a key in the corresponding page resource subdictionary
    /// (<paramref name="pageFont"/>, <paramref name="pageXObject"/>, etc.). Only names that ARE
    /// defined in the page scope are reported — undefined names are not inherited and are not
    /// flagged (matching veraPDF's <c>inheritedResourceNames</c> model). Device colour-space names
    /// (<c>DeviceGray</c>, <c>DeviceRGB</c>, <c>DeviceCMYK</c>, <c>Pattern</c>) are exempt from
    /// the <c>cs</c>/<c>CS</c> check.
    /// </summary>
    private void ScanStreamForInheritedResources(
        byte[] bytes,
        HashSet<string> reported,
        PreflightContext context,
        PdfDictionary? pageFont,
        PdfDictionary? pageXObject,
        PdfDictionary? pageExtGState,
        PdfDictionary? pageColorSpace,
        PdfDictionary? pageShading)
    {
        var lexer = new PdfLexer(bytes);
        string? lastName = null;

        while (!lexer.AtEnd)
        {
            var token = lexer.NextToken();
            if (token.Kind == TokenKind.EndOfInput)
                break;

            if (token.Kind == TokenKind.Name)
            {
                // Decode the name: strip the leading '/' and resolve '#XX' hex escapes,
                // mirroring ContentStreamUsage's DecodeName logic (ISO 32000-1 §7.3.5).
                lastName = DecodeName(token.Raw.Span);
                continue;
            }

            if (token.Kind == TokenKind.Keyword)
            {
                var op = Encoding.Latin1.GetString(token.Raw.Span);

                if (op == "ID")
                {
                    // Skip inline-image binary sample data — do not mis-scan it as operators.
                    ContentStreamUsage.SkipInlineImageData(lexer, bytes);
                    lastName = null;
                    continue;
                }

                // The value keywords true/false/null are emitted as Keyword tokens by the
                // content-stream lexer (they are operands, not operators). Skip them so a
                // preceding Name token is not consumed by a bogus "operator".
                if (op is "true" or "false" or "null")
                    continue;

                if (lastName is not null)
                {
                    switch (op)
                    {
                        case "Tf":
                            // Font lookup: /Font subdictionary.
                            if (pageFont is not null && NameDefinedIn(pageFont, lastName))
                                ReportIfNew(context, lastName, reported);
                            break;

                        case "Do":
                            // XObject lookup: /XObject subdictionary.
                            if (pageXObject is not null && NameDefinedIn(pageXObject, lastName))
                                ReportIfNew(context, lastName, reported);
                            break;

                        case "gs":
                            // ExtGState lookup: /ExtGState subdictionary.
                            if (pageExtGState is not null && NameDefinedIn(pageExtGState, lastName))
                                ReportIfNew(context, lastName, reported);
                            break;

                        case "cs":   // non-stroke colour space
                        case "CS":   // stroke colour space
                            // Direct colour-space names never require a /Resources lookup.
                            if (!_directColorSpaces.Contains(lastName)
                                && pageColorSpace is not null
                                && NameDefinedIn(pageColorSpace, lastName))
                            {
                                ReportIfNew(context, lastName, reported);
                            }
                            break;

                        case "sh":
                            // Shading lookup: /Shading subdictionary.
                            if (pageShading is not null && NameDefinedIn(pageShading, lastName))
                                ReportIfNew(context, lastName, reported);
                            break;
                    }
                }

                lastName = null;
            }
            else
            {
                // Any non-Name, non-Keyword token resets the last-name slot.
                lastName = null;
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the PdfDictionary <paramref name="dict"/> contains
    /// an entry whose key matches <paramref name="name"/> (case-sensitive). This checks whether
    /// the resource name is actually defined in the given scope, which mirrors veraPDF's
    /// <c>inheritedResourceNames</c> model (only names present in the ancestor scope matter).
    /// </summary>
    private static bool NameDefinedIn(PdfDictionary dict, string name)
        => dict.Get(new PdfName(name)) is not null;

    /// <summary>
    /// Decodes a PDF Name token raw span to a string, stripping the leading <c>/</c> and
    /// expanding <c>#XX</c> hex escapes (ISO 32000-1 §7.3.5). Mirrors the logic in
    /// <see cref="ContentStreamUsage"/> so resource names compare correctly.
    /// </summary>
    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        // raw includes the leading '/'. Start from index 1 to skip it.
        var sb = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length)
            {
                var hi = HexVal(raw[i + 1]);
                var lo = HexVal(raw[i + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    sb.Append((char)((hi << 4) | lo));
                    i += 2;
                    continue;
                }
            }
            sb.Append((char)raw[i]);
        }
        return sb.ToString();
    }

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => -1,
    };

    private void ReportIfNew(PreflightContext context, string resourceName, HashSet<string> reported)
    {
        if (reported.Add(resourceName))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"A content stream refers to resource(s) {resourceName} not defined in an explicitly associated Resources dictionary");
        }
    }
}
