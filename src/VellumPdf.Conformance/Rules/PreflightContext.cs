// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// The state shared across all rules for a single validation pass: the document under test
/// and the sink that collects findings. Provides convenience accessors so individual rules
/// do not each reach into <see cref="PdfDocumentReader"/> internals.
/// </summary>
internal sealed class PreflightContext
{
    private readonly List<PreflightAssertion> _assertions;

    internal PreflightContext(
        PdfDocumentReader reader,
        PdfConformance conformance,
        List<PreflightAssertion> assertions)
    {
        Reader = reader;
        Conformance = conformance;
        _assertions = assertions;
    }

    /// <summary>The document being validated.</summary>
    public PdfDocumentReader Reader { get; }

    /// <summary>The conformance level being validated against.</summary>
    public PdfConformance Conformance { get; }

    /// <summary>The document catalog (/Root) dictionary.</summary>
    public PdfDictionary Catalog => Reader.Catalog;

    /// <summary>The file trailer dictionary (or the cross-reference-stream dictionary acting as the trailer).</summary>
    public PdfDictionary Trailer => Reader.Trailer;

    /// <summary>
    /// The raw PDF file bytes. Used by file-structure rules that inspect the physical layout
    /// (header line, binary marker) rather than the parsed object graph.
    /// </summary>
    public ReadOnlyMemory<byte> FileBytes => Reader.Bytes;

    // Caps page-tree and /Parent-chain traversal against cycles / pathological nesting.
    private const int MaxPageTreeDepth = 256;

    /// <summary>
    /// Resolves <paramref name="obj"/> through any indirect reference, returning the target
    /// value. Returns <see langword="null"/> when the input is null or cannot be resolved.
    /// </summary>
    public PdfObject? Resolve(PdfObject? obj) => obj is null ? null : Reader.ResolveValue(obj);

    /// <summary>
    /// Enumerates the leaf page dictionaries (<c>/Type /Page</c>) reachable from the catalog's
    /// <c>/Pages</c> node, in document order. Cycles and pathological nesting depth are guarded.
    /// </summary>
    public IEnumerable<PdfDictionary> EnumeratePages()
        => WalkPages(Catalog.Get(PdfName.Pages), new HashSet<int>(), 0);

    /// <summary>
    /// True if any page paints with device-dependent colour (a DeviceRGB/Gray/CMYK colour operator in
    /// its content stream). Output-intent requirements apply only to documents that actually use
    /// device colour (issue #128). Limitation: device colour reached only through images, form
    /// XObjects, or patterns is not detected by this page-content scan.
    /// </summary>
    public bool DocumentUsesDeviceColour()
    {
        foreach (var page in EnumeratePages())
            if (ContentStreamUsage.Analyze(this, page).UsesDeviceColour)
                return true;
        return false;
    }

    private IEnumerable<PdfDictionary> WalkPages(PdfObject? node, HashSet<int> visited, int depth)
    {
        if (depth > MaxPageTreeDepth)
            yield break;
        if (node is PdfIndirectReference r && !visited.Add(r.ObjectNumber))
            yield break;
        if (Resolve(node) is not PdfDictionary dict)
            yield break;

        if (dict.Get(PdfName.Type) is PdfName { Value: "Page" })
        {
            yield return dict;
            yield break;
        }

        if (Resolve(dict.Get(PdfName.Kids)) is PdfArray kids)
        {
            for (var i = 0; i < kids.Count; i++)
                foreach (var page in WalkPages(kids[i], visited, depth + 1))
                    yield return page;
        }
        else
        {
            // Untyped node with no /Kids: treat as a leaf so its resources are still inspected.
            yield return dict;
        }
    }

    /// <summary>
    /// Returns the value of an inheritable page attribute (e.g. <c>/Resources</c>), following the
    /// <c>/Parent</c> chain when <paramref name="page"/> does not define it itself
    /// (ISO 32000-2 §7.7.3.4). Returns <see langword="null"/> when no ancestor supplies it.
    /// </summary>
    public PdfObject? ResolveInherited(PdfDictionary page, PdfName key)
    {
        var current = page;
        for (var depth = 0; depth < MaxPageTreeDepth && current is not null; depth++)
        {
            if (current.Get(key) is { } value)
                return Resolve(value);
            current = Resolve(current.Get(PdfName.Parent)) as PdfDictionary;
        }
        return null;
    }

    /// <summary>
    /// Enumerates the distinct font dictionaries referenced by every page's <c>/Font</c> resources
    /// (own or inherited). Each font object is yielded once even when shared across pages.
    /// </summary>
    public IEnumerable<PdfDictionary> EnumerateFonts()
    {
        var seen = new HashSet<int>();
        foreach (var page in EnumeratePages())
        {
            if (ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fonts)
                continue;
            foreach (var entry in fonts.Entries)
            {
                if (entry.Value is PdfIndirectReference r && !seen.Add(r.ObjectNumber))
                    continue;
                if (Resolve(entry.Value) is PdfDictionary font)
                    yield return font;
            }
        }
    }

    /// <summary>
    /// Enumerates every annotation dictionary referenced by a page's <c>/Annots</c> array, across
    /// all pages in document order.
    /// </summary>
    public IEnumerable<PdfDictionary> EnumerateAnnotations()
    {
        foreach (var page in EnumeratePages())
        {
            if (Resolve(page.Get(PdfName.Annots)) is not PdfArray annots)
                continue;
            for (var i = 0; i < annots.Count; i++)
                if (Resolve(annots[i]) is PdfDictionary annot)
                    yield return annot;
        }
    }

    /// <summary>
    /// Resolves <paramref name="obj"/> to a stream object, or <see langword="null"/> when it is
    /// not an indirect reference to a stream.
    /// </summary>
    public ParsedStream? ResolveStream(PdfObject? obj)
        => obj is PdfIndirectReference r ? Reader.ResolveStream(r.ObjectNumber) : null;

    /// <summary>
    /// Enumerates every stream object in the file, by walking the indirect-object number space.
    /// Used by file-structure rules (§6.1.7) that constrain <em>all</em> streams — filters, external
    /// references — independent of whether the stream is reachable through the rendered content.
    /// </summary>
    public IEnumerable<ParsedStream> EnumerateStreams()
    {
        for (var objectNumber = 1; objectNumber < Reader.Size; objectNumber++)
        {
            ParsedStream? stream;
            try
            {
                stream = Reader.ResolveStream(objectNumber);
            }
            catch
            {
                continue; // A malformed object must not abort the whole scan.
            }
            if (stream is not null)
                yield return stream;
        }
    }

    /// <summary>
    /// Returns the fully-decoded bytes of <paramref name="stream"/>, or <see langword="null"/>
    /// when an image filter prevents full decoding.
    /// </summary>
    public byte[]? DecodeStream(ParsedStream stream) => Reader.GetDecodedStreamData(stream);

    /// <summary>Records a finding for the current validation pass.</summary>
    /// <param name="ruleId">Stable rule identifier (typically the rule's <see cref="IConformanceRule.RuleId"/>).</param>
    /// <param name="clause">Specification clause citation.</param>
    /// <param name="severity">The finding's severity.</param>
    /// <param name="message">Human-readable description.</param>
    /// <param name="objectRef">Optional <c>"N 0 R"</c> object location.</param>
    public void Report(
        string ruleId,
        string clause,
        PreflightSeverity severity,
        string message,
        string? objectRef = null)
        => _assertions.Add(new PreflightAssertion(ruleId, clause, severity, message, objectRef));
}
