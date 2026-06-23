// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.2.4.4-2 (Separation consistency). All Separation colour-space arrays within
/// a single PDF/A-2 file that share the same colourant name shall have the same
/// <c>tintTransform</c> (element 3) and <c>alternateSpace</c> (element 2). Compression and
/// whether an object is direct or indirect shall be ignored when evaluating equivalence.
/// </summary>
/// <remarks>
/// <para>
/// Authored from ISO 19005-2:2011, §6.2.4.4 and ISO 32000-1:2008, §8.6.6.4 (Separation).
/// Clean-room: derived from the specification text and empirical oracle probing (veraPDF 1.30.2),
/// not from any third-party validation profile.
/// </para>
/// <para>
/// <b>Scope (determined empirically via veraPDF 1.30.2):</b> veraPDF compares Separation colour
/// spaces that are <em>used</em> by page content AND by non-page content streams reachable from
/// the page (drawn Form XObjects, all CharProcs of Tf-selected Type 3 fonts, annotation /AP /N
/// appearance streams). A Separation present in <c>/Resources /ColorSpace</c> but never selected
/// by a <c>cs</c> or <c>CS</c> operator triggers no violation. A Separation is "used" when:
/// <list type="bullet">
/// <item>it is selected by a <c>cs</c> or <c>CS</c> operator via a named resource, or</item>
/// <item>it appears in the <c>/Colorants</c> dictionary of a DeviceN colour space that is itself
/// selected by a <c>cs</c> or <c>CS</c> operator.</item>
/// </list>
/// </para>
/// <para>
/// <b>Equivalence semantics:</b> two Separation arrays have the same alternateSpace/tintTransform
/// when their elements are <em>structurally equal</em> after normalisation:
/// <list type="bullet">
/// <item>indirect references are resolved before comparison (object-number identity is irrelevant);</item>
/// <item>dictionaries compare key-by-key (key sets must match exactly; values are compared
/// recursively);</item>
/// <item>arrays compare element-wise;</item>
/// <item>names, integers, reals, booleans and strings compare by value; integer and real are
/// interchangeable (1 == 1.0);</item>
/// <item>for stream objects the <em>decoded</em> body is compared (compression is ignored) plus
/// the stream dictionary minus encoding-only keys (<c>/Length</c>, <c>/Filter</c>,
/// <c>/DecodeParms</c>, <c>/DL</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>FP-safety:</b> only emit a finding when a difference is <em>positively established</em>.
/// When a comparison cannot be completed (unresolvable reference, stream-decode failure,
/// recursion depth exceeded) the pair is treated as "cannot prove inequality" → no finding.
/// Probe A (structurally identical tint functions at distinct object numbers) → no finding.
/// </para>
/// <para>
/// <b>FP-safety (pool growth):</b> adding non-page used-Separations only GROWS the comparison
/// pool toward what veraPDF already compares. Any inconsistency we detect, veraPDF also detects;
/// we never flag a pair that veraPDF accepts. Empirically confirmed via probes N4-A/B/C
/// (veraPDF 1.30.2, 2026-06-23): veraPDF fires 6.2.4.4-2 when page and a drawn Form XObject
/// share a Separation name with different alternateSpace; it does NOT fire when alt/tint match,
/// or when the inconsistent form is not drawn.
/// </para>
/// <para>
/// <b>Deferred edges:</b> image <c>/ColorSpace</c> and alternate spaces of other colour spaces
/// are not yet walked — under-detection rather than over-rejection. Non-page streams with a null
/// <c>/Resources</c> are skipped (names cannot be resolved; under-detection, FP-safe).
/// </para>
/// </remarks>
internal sealed class SeparationConsistencyRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.4.4-2";

    public string Clause => "ISO 19005-2:2011, 6.2.4.4";

    private static readonly PdfName _colorSpace = new("ColorSpace");
    private static readonly PdfName _colorants = new("Colorants");

    // Stream dictionary keys that carry only compression/length metadata; ignored when
    // comparing structurally equivalent streams (spec: "compression … shall be ignored").
    private static readonly HashSet<string> _streamFilterKeys = new(StringComparer.Ordinal)
    {
        "Length", "Filter", "DecodeParms", "DL",
    };

    // Guard against pathological recursion.
    private const int MaxCompareDepth = 64;

    public void Evaluate(PreflightContext context)
    {
        // Map colourant name → first observed (rawAlt, rawTint) — the raw (possibly-indirect)
        // objects from the Separation array. On a subsequent occurrence under the same name,
        // compare structurally; report once per name.
        var firstSeen = new Dictionary<string, (PdfObject alt, PdfObject tint)>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        // Colour-space objects shared via the same indirect reference across pages / forms are
        // deduped so they count as one occurrence, not N.
        var seenCsObjects = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            // ── Page content ───────────────────────────────────────────────────────────────────────
            if (context.ResolveInherited(page, PdfName.Resources) is PdfDictionary pageResources)
            {
                var selected = ContentStreamUsage.Analyze(context, page).SelectedColorSpaces;
                if (selected.Count > 0)
                    ProcessResourceScope(context, pageResources, selected, firstSeen, reported, seenCsObjects);
            }

            // ── Non-page content streams (N4 batch, 2026-06-23) ───────────────────────────────────
            // FP-safety: adding non-page used-Separations only GROWS the comparison pool toward
            // what veraPDF already compares (veraPDF's used-Separation set ⊇ ours both before and
            // after this extension); any inconsistency detected here, veraPDF also detects; we
            // never flag a pair veraPDF accepts.
            IReadOnlyList<ReachableContentStream> reachable;
            try
            {
                reachable = ContentStreamUsage.GetReachableContentStreams(context, page);
            }
            catch
            {
                continue; // collector failure — skip non-page streams for this page
            }

            foreach (var cs in reachable)
            {
                // Skip streams with no /Resources: colour-space names cannot be resolved.
                // Under-detection is FP-safe.
                if (cs.Resources is null)
                    continue;

                try
                {
                    var selected = ScanSelectedColorSpaces(cs.Bytes);
                    if (selected.Count > 0)
                        ProcessResourceScope(context, cs.Resources, selected, firstSeen, reported, seenCsObjects);
                }
                catch
                {
                    // Malformed stream or resources — skip; do not abort.
                }
            }
        }
    }

    /// <summary>
    /// Iterates the <c>/ColorSpace</c> subdictionary of <paramref name="resources"/>, filters to
    /// entries whose key is in <paramref name="selected"/>, and feeds each Separation (or DeviceN
    /// /Colorants entry) to <see cref="CheckSeparation"/> via the shared comparison state.
    /// </summary>
    private void ProcessResourceScope(
        PreflightContext context,
        PdfDictionary resources,
        IReadOnlySet<string> selected,
        Dictionary<string, (PdfObject alt, PdfObject tint)> firstSeen,
        HashSet<string> reported,
        HashSet<int> seenCsObjects)
    {
        if (context.Resolve(resources.Get(_colorSpace)) is not PdfDictionary colorSpaces)
            return;

        foreach (var entry in colorSpaces.Entries)
        {
            if (!selected.Contains(entry.Key.Value))
                continue;

            // Deduplicate shared colour-space objects (same indirect reference → same object).
            if (entry.Value is PdfIndirectReference csRef && !seenCsObjects.Add(csRef.ObjectNumber))
                continue;

            var csObj = context.Resolve(entry.Value);
            if (csObj is not PdfArray csArray || csArray.Count < 4)
                continue;

            var csType = context.Resolve(csArray[0]) as PdfName;
            if (csType is null)
                continue;

            if (csType.Value == "Separation")
            {
                // [/Separation name alt tint]
                if (context.Resolve(csArray[1]) is PdfName name)
                    CheckSeparation(context, name.Value, csArray[2], csArray[3], firstSeen, reported);
            }
            else if (csType.Value == "DeviceN" && csArray.Count >= 5)
            {
                // [/DeviceN names alt tint attrs] — inspect /Colorants
                CollectFromColorants(context, csArray[4], firstSeen, reported);
            }
        }
    }

    /// <summary>
    /// Scans <paramref name="bytes"/> for <c>cs</c> and <c>CS</c> operators, collecting every
    /// colour-space resource name that precedes one. This mirrors <c>ContentStreamUsage.Analyze</c>'s
    /// <c>SelectedColorSpaces</c> tracking but operates on arbitrary stream bytes without needing
    /// the full <c>Analyze</c> pass. Best-effort and defensive (inline-image data is skipped;
    /// any parse error produces an empty set).
    /// </summary>
    private static IReadOnlySet<string> ScanSelectedColorSpaces(byte[] bytes)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        try
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
                    lastName = DecodeName(token.Raw.Span);
                    continue;
                }

                if (token.Kind == TokenKind.Keyword)
                {
                    var op = Encoding.Latin1.GetString(token.Raw.Span);
                    if ((op == "cs" || op == "CS") && lastName is not null)
                        selected.Add(lastName);
                    else if (op == "ID")
                        ContentStreamUsage.SkipInlineImageData(lexer, bytes);

                    lastName = null;
                }
            }
        }
        catch
        {
            // Malformed content — return whatever was collected.
        }
        return selected;
    }

    /// <summary>
    /// Walks the <c>/Colorants</c> dictionary of a DeviceN <c>attrs</c> element, visiting every
    /// Separation entry and registering it in the comparison set.
    /// </summary>
    private void CollectFromColorants(
        PreflightContext context,
        PdfObject attrsObj,
        Dictionary<string, (PdfObject alt, PdfObject tint)> firstSeen,
        HashSet<string> reported)
    {
        if (context.Resolve(attrsObj) is not PdfDictionary attrsDict)
            return;
        if (context.Resolve(attrsDict.Get(_colorants)) is not PdfDictionary colorantsDict)
            return;

        foreach (var colorant in colorantsDict.Entries)
        {
            var colorantCs = context.Resolve(colorant.Value);
            if (colorantCs is not PdfArray arr || arr.Count < 4)
                continue;
            if (context.Resolve(arr[0]) is not PdfName { Value: "Separation" })
                continue;
            if (context.Resolve(arr[1]) is PdfName sepName)
                CheckSeparation(context, sepName.Value, arr[2], arr[3], firstSeen, reported);
        }
    }

    /// <summary>
    /// Registers one Separation occurrence (by <paramref name="name"/>). On a second occurrence,
    /// compares structurally against the first; reports a finding only when a positive difference
    /// is established.
    /// </summary>
    private void CheckSeparation(
        PreflightContext context,
        string name,
        PdfObject rawAlt,
        PdfObject rawTint,
        Dictionary<string, (PdfObject alt, PdfObject tint)> firstSeen,
        HashSet<string> reported)
    {
        if (reported.Contains(name))
            return;

        if (!firstSeen.TryGetValue(name, out var prior))
        {
            firstSeen[name] = (rawAlt, rawTint);
            return;
        }

        // Only flag when difference is POSITIVELY established (null = inconclusive → no flag).
        var altEqual = ObjectsEqual(context, prior.alt, rawAlt, MaxCompareDepth);
        var tintEqual = ObjectsEqual(context, prior.tint, rawTint, MaxCompareDepth);

        if (altEqual == false || tintEqual == false)
        {
            reported.Add(name);
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"Several occurrences of a Separation colour space with the same name are not consistent (colourant: /{name}).");
        }
    }

    // ── Structural comparator ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structurally compares two PDF objects after resolving indirect references. Returns
    /// <see langword="true"/> for equal, <see langword="false"/> for a positively established
    /// difference, or <see langword="null"/> when the comparison cannot be completed (in which
    /// case the caller must NOT flag a violation — FP-safety invariant).
    /// </summary>
    private bool? ObjectsEqual(PreflightContext context, PdfObject? a, PdfObject? b, int depth)
    {
        if (depth <= 0)
            return null; // Recursion guard — inconclusive, not a finding.

        // Resolve through indirect references; detect streams.
        if (!TryResolve(context, a, out var aResolved, out var aStream))
            return null;
        if (!TryResolve(context, b, out var bResolved, out var bStream))
            return null;

        // Both are streams → compare decoded body + dictionary (minus filter keys).
        if (aStream is not null && bStream is not null)
            return StreamsEqual(context, aStream, bStream, depth - 1);

        // One is a stream, the other is not — different kinds → positive difference.
        if (aStream is not null || bStream is not null)
            return false;

        // Both are regular objects.
        if (aResolved is null || bResolved is null)
            return null; // Could not resolve — inconclusive.

        return (aResolved, bResolved) switch
        {
            (PdfNull, PdfNull) => true,
            (PdfNull, _) or (_, PdfNull) => false,

            (PdfName na, PdfName nb) => na.Value == nb.Value,

            (PdfBoolean ba, PdfBoolean bb) => ba.Value == bb.Value,

            // Numbers: integer ↔ real are interchangeable in PDF.
            (PdfInteger ia, PdfInteger ib) => ia.Value == ib.Value,
            (PdfReal ra, PdfReal rb) => ra.Value == rb.Value,
            (PdfInteger ic, PdfReal rd) => (double)ic.Value == rd.Value,
            (PdfReal re, PdfInteger ig) => re.Value == (double)ig.Value,

            // Strings: literal and hex strings with the same bytes are equivalent.
            (PdfLiteralString ls1, PdfLiteralString ls2) => ls1.Bytes.Span.SequenceEqual(ls2.Bytes.Span),
            (PdfHexString hs1, PdfHexString hs2) => hs1.Bytes.Span.SequenceEqual(hs2.Bytes.Span),
            (PdfLiteralString ls3, PdfHexString hs3) => ls3.Bytes.Span.SequenceEqual(hs3.Bytes.Span),
            (PdfHexString hs4, PdfLiteralString ls4) => hs4.Bytes.Span.SequenceEqual(ls4.Bytes.Span),

            (PdfArray arr1, PdfArray arr2) => ArraysEqual(context, arr1, arr2, depth - 1),
            (PdfDictionary d1, PdfDictionary d2) => DictionariesEqual(context, d1, d2, depth - 1),

            // Differing runtime types → positive difference.
            _ when aResolved.GetType() != bResolved.GetType() => false,

            // Anything else — inconclusive (do not flag).
            _ => null,
        };
    }

    /// <summary>
    /// Resolves <paramref name="obj"/> through any indirect reference. Sets
    /// <paramref name="resolved"/> to the non-stream result and <paramref name="stream"/> to
    /// the parsed stream (if the object is a stream). Returns <see langword="false"/> when
    /// resolution fails (caller should treat as inconclusive).
    /// </summary>
    private static bool TryResolve(
        PreflightContext context,
        PdfObject? obj,
        out PdfObject? resolved,
        out ParsedStream? stream)
    {
        resolved = null;
        stream = null;

        if (obj is null)
            return false;

        if (obj is PdfIndirectReference r)
        {
            // Try to get the object as a stream first (stream objects are accessed differently
            // from plain objects in the reader).
            try
            {
                stream = context.Reader.ResolveStream(r.ObjectNumber);
                if (stream is not null)
                    return true; // resolved stays null; stream is set.
            }
            catch { /* fall through to plain resolve */ }

            try
            {
                resolved = context.Reader.ResolveValue(obj);
                return resolved is not null;
            }
            catch
            {
                return false;
            }
        }

        // Inline (direct) object — cannot be a stream in PDF (streams are always indirect).
        resolved = obj;
        return true;
    }

    private bool? StreamsEqual(PreflightContext context, ParsedStream a, ParsedStream b, int depth)
    {
        // Compare decoded bodies (ignoring compression).
        byte[]? aBody, bBody;
        try
        {
            aBody = context.DecodeStream(a);
            bBody = context.DecodeStream(b);
        }
        catch
        {
            return null; // Decode failure — inconclusive.
        }

        if (aBody is null || bBody is null)
            return null; // Cannot decode — inconclusive.

        if (!aBody.AsSpan().SequenceEqual(bBody.AsSpan()))
            return false;

        // Compare the stream dictionaries, excluding filter/length keys.
        return StreamDictsEqual(context, a.Dictionary, b.Dictionary, depth);
    }

    private bool? StreamDictsEqual(
        PreflightContext context,
        PdfDictionary a,
        PdfDictionary b,
        int depth)
    {
        // Collect the content-relevant entries (exclude filter/length metadata).
        var aEntries = a.Entries.Where(kv => !_streamFilterKeys.Contains(kv.Key.Value)).ToList();
        var bEntries = b.Entries.Where(kv => !_streamFilterKeys.Contains(kv.Key.Value)).ToList();

        if (aEntries.Count != bEntries.Count)
            return false;

        var bMap = new Dictionary<string, PdfObject>(bEntries.Count, StringComparer.Ordinal);
        foreach (var kv in bEntries)
            bMap[kv.Key.Value] = kv.Value;

        foreach (var kv in aEntries)
        {
            if (!bMap.TryGetValue(kv.Key.Value, out var bVal))
                return false;
            var eq = ObjectsEqual(context, kv.Value, bVal, depth);
            if (eq == false) return false;
            if (eq is null) return null;
        }
        return true;
    }

    private bool? ArraysEqual(PreflightContext context, PdfArray a, PdfArray b, int depth)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            var eq = ObjectsEqual(context, a[i], b[i], depth);
            if (eq == false) return false;
            if (eq is null) return null;
        }
        return true;
    }

    private bool? DictionariesEqual(PreflightContext context, PdfDictionary a, PdfDictionary b, int depth)
    {
        var aEntries = a.Entries;
        var bEntries = b.Entries;

        if (aEntries.Count != bEntries.Count)
            return false;

        var bMap = new Dictionary<string, PdfObject>(bEntries.Count, StringComparer.Ordinal);
        foreach (var kv in bEntries)
            bMap[kv.Key.Value] = kv.Value;

        foreach (var kv in aEntries)
        {
            if (!bMap.TryGetValue(kv.Key.Value, out var bVal))
                return false;
            var eq = ObjectsEqual(context, kv.Value, bVal, depth);
            if (eq == false) return false;
            if (eq is null) return null;
        }
        return true;
    }

    // Decodes a name token's raw bytes (strips leading '/'; handles #XX escapes).
    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
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

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
