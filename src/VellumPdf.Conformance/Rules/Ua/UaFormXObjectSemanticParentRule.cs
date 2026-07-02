// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.20-2 (<c>PDXForm</c>: <c>isUniqueSemanticParent == true</c>): a Form XObject
/// whose content is incorporated into the logical structure (it carries a <c>/StructParents</c>
/// key in its stream dictionary) must be drawn from exactly one invocation context. Drawing it
/// from two or more distinct pages gives its structure-linked marked content two distinct
/// structural contexts while the /ParentTree can only record one set of parents — a violation of
/// "unique semantic parent."
/// </summary>
/// <remarks>
/// <para><strong>veraPDF predicate (ISO14289-1.xml):</strong> object <c>PDXForm</c>, clause
/// <c>7.20</c>, testNumber <c>2</c>: <c>isUniqueSemanticParent == true</c>. Fires when the
/// same Form XObject stream is drawn (<c>Do</c>) from two or more invocation sites that have
/// structurally distinct contexts.</para>
///
/// <para><strong>Detection condition (FP-safe subset):</strong></para>
/// <list type="bullet">
///   <item>The Form XObject stream dictionary carries a <c>/StructParents</c> key — the
///   canonical signal, per ISO 32000-1 §14.7.4.4, that the form's content is incorporated into
///   the document's logical structure via the number tree.</item>
///   <item>AND the same Form XObject (by indirect-object number) is drawn via <c>Do</c> from
///   two or more distinct page content streams (identified by page index in document order).
///   Different pages can never share the same MCID-array slot in the form's
///   <c>/ParentTree[/StructParents]</c> entry, so any multi-page invocation is always a
///   structural violation.</item>
/// </list>
///
/// <para><strong>What is intentionally NOT fired on (FP-safe skips):</strong></para>
/// <list type="bullet">
///   <item>Form XObjects with no <c>/StructParents</c> key — they contain no structure-linked
///   content and may legitimately be reused as decoration/templates.</item>
///   <item>Form XObjects drawn only from a single page — one invocation context, by definition
///   unique.</item>
///   <item>Direct (inline, non-indirect) Form XObject references — they cannot be shared by
///   object identity and are always unique.</item>
///   <item>Same-page double-invocations are under-detected (not fired upon) to avoid FP risk
///   on documents where the same template is stamped twice on one page within the same
///   structural BDC context.</item>
///   <item>Form XObjects reachable only from other Form XObjects (nested <c>Do</c> chains)
///   are under-detected — only top-level page-level <c>Do</c> invocations are tracked here.</item>
/// </list>
///
/// <para><strong>Scope:</strong> page-level <c>Do</c> operators only. All indirect Form XObject
/// references are de-duplicated by object number; the fired-once guard ensures at most one
/// finding per violation.</para>
///
/// <para>Authored clean-room from ISO 14289-1:2014 §7.20 and ISO 32000-1:2008 §14.7.4.4
/// (Form XObject /StructParents convention). veraPDF 1.30.2 used only as a behavioural oracle,
/// not as an implementation reference.</para>
/// </remarks>
internal sealed class UaFormXObjectSemanticParentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.20-2";

    public string Clause => "ISO 14289-1:2014, 7.20";

    private static readonly PdfName _xObject = new("XObject");
    private static readonly PdfName _structParents = new("StructParents");

    public void Evaluate(PreflightContext context)
    {
        // formObjNum → set of page-index values from which the form is drawn via Do.
        // We use page index (0-based document order) rather than page object number so
        // that direct-object pages (no object number) also get a stable identity.
        var invocationSites = new Dictionary<int, HashSet<int>>();

        var pageIndex = 0;
        foreach (var page in context.EnumeratePages())
        {
            ScanPageForStructuredFormInvocations(context, page, pageIndex, invocationSites);
            pageIndex++;
        }

        // Report any Form XObject drawn from 2+ distinct pages.
        var reported = new HashSet<int>();
        foreach (var (formObjNum, sites) in invocationSites)
        {
            if (sites.Count < 2)
                continue;
            if (!reported.Add(formObjNum))
                continue;

            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                "A Form XObject (object " + formObjNum + ") whose content is incorporated into "
                + "the logical structure (/StructParents present) is drawn via Do from "
                + sites.Count + " distinct pages. Each Do invocation establishes a different "
                + "structural context, but the form can record only one set of structure parents "
                + "in /ParentTree — violating the unique-semantic-parent requirement "
                + "(ISO 14289-1:2014, 7.20; ISO 32000-1:2008, 14.7.4.4).");
        }
    }

    // Scans the page content stream for Do operators, resolves each named XObject to a Form
    // XObject stream, and records the invocation in invocationSites when the form has /StructParents.
    private static void ScanPageForStructuredFormInvocations(
        PreflightContext context,
        PdfDictionary page,
        int pageIndex,
        Dictionary<int, HashSet<int>> invocationSites)
    {
        try
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                return;
            if (context.Resolve(resources.Get(_xObject)) is not PdfDictionary xObjects)
                return;

            var pageBytes = ContentStreamUsage.GetPageContent(context, page);
            if (pageBytes is not { Length: > 0 })
                return;

            string? lastName = null;
            var lexer = new PdfLexer(pageBytes);

            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;

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
                        ContentStreamUsage.SkipInlineImageData(lexer, pageBytes);
                    }
                    else if (op == "Do" && lastName is not null)
                    {
                        TryRecordFormInvocation(
                            context, xObjects, lastName, pageIndex, invocationSites);
                    }

                    lastName = null;
                }
            }
        }
        catch
        {
            // Malformed content — keep whatever was collected; do not abort other pages.
        }
    }

    // If the XObject named by resourceName is an indirect Form XObject with /StructParents,
    // records (formObjNum, pageIndex) in invocationSites.
    private static void TryRecordFormInvocation(
        PreflightContext context,
        PdfDictionary xObjects,
        string resourceName,
        int pageIndex,
        Dictionary<int, HashSet<int>> invocationSites)
    {
        try
        {
            var xObjRef = xObjects.Get(new PdfName(resourceName));
            if (xObjRef is not PdfIndirectReference iref)
                return; // direct objects cannot be shared — always unique, skip

            // Resolve to the stream dictionary. ResolveStream returns null for non-streams.
            var stream = context.Reader.ResolveStream(iref.ObjectNumber);
            if (stream is null)
                return;

            // Must be a Form XObject.
            var subtype = (context.Resolve(stream.Dictionary.Get(PdfName.Subtype)) as PdfName)?.Value;
            if (subtype != "Form")
                return;

            // /StructParents presence is the FP-safe gate: only structure-linked forms matter.
            if (stream.Dictionary.Get(_structParents) is null)
                return;

            if (!invocationSites.TryGetValue(iref.ObjectNumber, out var sites))
            {
                sites = new HashSet<int>();
                invocationSites[iref.ObjectNumber] = sites;
            }

            sites.Add(pageIndex);
        }
        catch
        {
            // Unresolvable or malformed XObject — skip defensively (FP-safe).
        }
    }

    // Decodes a PDF name token (strips leading '/', resolves #XX escapes).
    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length
                && Hex(raw[i + 1]) >= 0 && Hex(raw[i + 2]) >= 0)
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

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
