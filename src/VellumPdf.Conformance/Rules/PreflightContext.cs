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
    /// The resource ceilings <see cref="Reader"/> was opened with. A rule that opens a NESTED
    /// document from bytes found inside this one — an embedded-file attachment, for instance —
    /// must pass this through to that nested open, or a caller who tightened either resource knob
    /// on the outer read gets the untightened defaults back for attacker-supplied bytes nested
    /// inside it. See <see cref="Reader.PdfDocumentReader.Limits"/>.
    /// </summary>
    public ReaderLimits Limits => Reader.Limits;

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
    /// device colour (issue #128). For the extended detection that also covers image XObjects and
    /// named-CS alternates, use <see cref="DocumentDeviceColourTypes"/>.
    /// </summary>
    public bool DocumentUsesDeviceColour()
    {
        foreach (var page in EnumeratePages())
            if (ContentStreamUsage.Analyze(this, page).UsesDeviceColour)
                return true;
        return false;
    }

    /// <summary>
    /// Returns which device colour types are used across all pages. Each flag is true when ANY page
    /// uses that device colour type — via direct operators (<c>rg</c>/<c>RG</c>, etc.), an image
    /// XObject's <c>/ColorSpace</c>, or the <c>/Alternate</c> space of a selected named colour space.
    /// </summary>
    /// <remarks>
    /// Detection is extended beyond content-stream operators (ISO 19005-2 §6.2.4.3):
    /// <list type="bullet">
    ///   <item>Image XObjects drawn from page content or reachable non-page streams: the image's
    ///   <c>/ColorSpace</c> entry is classified (one level deep, recursing into array forms).</item>
    ///   <item>Named colour spaces selected by <c>cs</c>/<c>CS</c>: their <c>/Alternate</c> field
    ///   (for <c>Separation</c> and <c>DeviceN</c>) and the base type (for <c>ICCBased</c>) are
    ///   classified. Pattern with an uncoloured base colour space: the base space is classified.</item>
    /// </list>
    /// Visited image object numbers are deduplicated. All existing exemptions (DefaultRGB/CMYK/Gray
    /// and output-intent matching) are applied by the caller.
    /// </remarks>
    public (bool Rgb, bool Cmyk, bool Gray) DocumentDeviceColourTypes()
    {
        bool rgb = false, cmyk = false, gray = false;
        var visitedImages = new HashSet<int>();

        foreach (var page in EnumeratePages())
        {
            var usage = ContentStreamUsage.Analyze(this, page);
            if (usage.UsesDeviceRgb) rgb = true;
            if (usage.UsesDeviceCmyk) cmyk = true;
            if (usage.UsesDeviceGray) gray = true;

            if (ResolveInherited(page, PdfName.Resources) is PdfDictionary pageResources)
            {
                // Scan drawn image XObjects for device colour in their /ColorSpace.
                ScanImagesForDeviceColour(this, page, pageResources, usage, visitedImages, ref rgb, ref cmyk, ref gray);

                // Scan selected named colour spaces for device colour in their /Alternate.
                if (usage.SelectedColorSpaces.Count > 0)
                    ScanNamedCsAlternatesForDeviceColour(this, pageResources, usage.SelectedColorSpaces, ref rgb, ref cmyk, ref gray);
            }

            if (rgb && cmyk && gray)
                return (true, true, true); // Short-circuit when all found.
        }
        return (rgb, cmyk, gray);
    }

    // Scans drawn image XObjects reachable from the page for device colour in their /ColorSpace.
    private static void ScanImagesForDeviceColour(
        PreflightContext context,
        PdfDictionary page,
        PdfDictionary pageResources,
        ContentUsage usage,
        HashSet<int> visitedImages,
        ref bool rgb, ref bool cmyk, ref bool gray)
    {
        if (context.Resolve(pageResources.Get(PdfName.XObject)) is not PdfDictionary xObjects)
            return;

        foreach (var drawn in usage.DrawnXObjects)
        {
            var xRef = xObjects.Get(new PdfName(drawn));
            if (xRef is null) continue;
            CheckImageColourSpace(context, xRef, visitedImages, ref rgb, ref cmyk, ref gray);
        }

        // Also walk reachable non-page streams (Form XObjects, Type 3 CharProcs, AP streams).
        try
        {
            var reachable = ContentStreamUsage.GetReachableContentStreams(context, page);
            foreach (var cs in reachable)
            {
                if (cs.Resources is null) continue;
                if (context.Resolve(cs.Resources.Get(PdfName.XObject)) is not PdfDictionary csXObjects) continue;
                foreach (var entry in csXObjects.Entries)
                    CheckImageColourSpace(context, entry.Value, visitedImages, ref rgb, ref cmyk, ref gray);
            }
        }
        catch { }
    }

    // Checks whether a single XObject (by reference) is an image with a device /ColorSpace.
    private static void CheckImageColourSpace(
        PreflightContext context,
        PdfObject xRef,
        HashSet<int> visitedImages,
        ref bool rgb, ref bool cmyk, ref bool gray)
    {
        if (xRef is PdfIndirectReference r && !visitedImages.Add(r.ObjectNumber))
            return;
        try
        {
            var xDict = context.Resolve(xRef) as PdfDictionary;
            if (xDict is null) return;
            if (context.Resolve(xDict.Get(PdfName.Subtype)) is not PdfName { Value: "Image" })
                return;
            var cs = xDict.Get(new PdfName("ColorSpace"));
            if (cs is null) return;
            ClassifyColourSpaceObject(context, context.Resolve(cs), ref rgb, ref cmyk, ref gray);
        }
        catch { }
    }

    // Scans selected named colour spaces for device colour in their /Alternate (Separation/DeviceN)
    // or uncoloured-pattern base space. Only one level of alternate (no deep recursion) to stay FP-safe.
    private static void ScanNamedCsAlternatesForDeviceColour(
        PreflightContext context,
        PdfDictionary resources,
        IReadOnlyCollection<string> selectedNames,
        ref bool rgb, ref bool cmyk, ref bool gray)
    {
        if (context.Resolve(resources.Get(new PdfName("ColorSpace"))) is not PdfDictionary csDict)
            return;

        foreach (var name in selectedNames)
        {
            try
            {
                var csObj = context.Resolve(csDict.Get(new PdfName(name)));
                if (csObj is not PdfArray csArray || csArray.Count < 2) continue;
                var csType = context.Resolve(csArray[0]) as PdfName;
                if (csType is null) continue;

                switch (csType.Value)
                {
                    case "Separation" when csArray.Count >= 3:
                        // [/Separation name alternate tint] — check the alternate space.
                        ClassifyColourSpaceObject(context, context.Resolve(csArray[2]), ref rgb, ref cmyk, ref gray);
                        break;

                    case "DeviceN" when csArray.Count >= 3:
                        // [/DeviceN names alternate tint (attrs?)] — check the alternate space.
                        ClassifyColourSpaceObject(context, context.Resolve(csArray[2]), ref rgb, ref cmyk, ref gray);
                        break;

                    case "Pattern" when csArray.Count >= 2:
                        // [/Pattern baseCS] — uncoloured tiling pattern; check the base colour space.
                        ClassifyColourSpaceObject(context, context.Resolve(csArray[1]), ref rgb, ref cmyk, ref gray);
                        break;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Classifies a resolved PDF colour space object, setting <paramref name="rgb"/>,
    /// <paramref name="cmyk"/>, or <paramref name="gray"/> when the object is (or directly
    /// contains) a device-dependent colour space. One level of resolution; does not recurse
    /// into alternates of alternates.
    /// </summary>
    internal static void ClassifyColourSpaceObject(
        PreflightContext context,
        PdfObject? cs,
        ref bool rgb, ref bool cmyk, ref bool gray)
    {
        if (cs is PdfName n)
        {
            switch (n.Value)
            {
                case "DeviceRGB": rgb = true; break;
                case "DeviceCMYK": cmyk = true; break;
                case "DeviceGray": gray = true; break;
            }
            return;
        }

        if (cs is not PdfArray arr || arr.Count < 1) return;
        var typeObj = context.Resolve(arr[0]) as PdfName;
        if (typeObj is null) return;

        switch (typeObj.Value)
        {
            case "DeviceRGB": rgb = true; break;
            case "DeviceCMYK": cmyk = true; break;
            case "DeviceGray": gray = true; break;

            case "Separation" when arr.Count >= 3:
                // Alternate space at index 2.
                ClassifyColourSpaceNameOrArray(context, context.Resolve(arr[2]), ref rgb, ref cmyk, ref gray);
                break;

            case "DeviceN" when arr.Count >= 3:
                ClassifyColourSpaceNameOrArray(context, context.Resolve(arr[2]), ref rgb, ref cmyk, ref gray);
                break;

            case "Pattern" when arr.Count >= 2:
                ClassifyColourSpaceNameOrArray(context, context.Resolve(arr[1]), ref rgb, ref cmyk, ref gray);
                break;
        }
    }

    // Helper: classifies a colour space that may be a Name or an Array (one level only, no recursion).
    private static void ClassifyColourSpaceNameOrArray(
        PreflightContext context,
        PdfObject? cs,
        ref bool rgb, ref bool cmyk, ref bool gray)
    {
        if (cs is PdfName n)
        {
            switch (n.Value)
            {
                case "DeviceRGB": rgb = true; break;
                case "DeviceCMYK": cmyk = true; break;
                case "DeviceGray": gray = true; break;
            }
            return;
        }

        if (cs is PdfArray arr && arr.Count >= 1)
        {
            if (context.Resolve(arr[0]) is PdfName typeName)
            {
                switch (typeName.Value)
                {
                    case "DeviceRGB": rgb = true; break;
                    case "DeviceCMYK": cmyk = true; break;
                    case "DeviceGray": gray = true; break;
                }
            }
        }
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
    /// Enumerates the distinct font dictionaries that are actually <em>used</em> by page content:
    /// only fonts whose resource key appears in a <c>Tf</c> text-font operator in that page's
    /// content stream are yielded. Each font object is yielded once even when shared across pages,
    /// matching veraPDF's behaviour of validating only fonts selected by the current graphics state
    /// rather than every font merely present in <c>/Resources /Font</c> (issue #118).
    /// </summary>
    /// <remarks>
    /// Limitation: fonts referenced only from form XObjects, Type 3 glyph procedures, or annotation
    /// appearance streams are not yet detected here — they are a deferred edge. This means
    /// fonts used <em>only</em> in those contexts are currently under-detected (not validated)
    /// rather than over-rejected, which is the conservative direction.
    /// </remarks>
    public IEnumerable<PdfDictionary> EnumerateUsedFonts()
    {
        var seen = new HashSet<int>();
        foreach (var page in EnumeratePages())
        {
            if (ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fonts)
                continue;
            var usedFontNames = ContentStreamUsage.Analyze(this, page).UsedFonts;
            foreach (var entry in fonts.Entries)
            {
                if (!usedFontNames.Contains(entry.Key.Value))
                    continue;
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
    /// not an indirect reference to a stream. Honours the reference's generation the same way
    /// <see cref="Resolve"/> does — a stream reference at the wrong generation resolves to
    /// nothing, matching <c>Resolve</c> rather than the object number alone (ISO 32000-2 §7.3.10).
    /// </summary>
    public ParsedStream? ResolveStream(PdfObject? obj)
        => obj is PdfIndirectReference r ? Reader.ResolveStream(r) : null;

    /// <summary>The number of indirect objects in the cross-reference table.</summary>
    public int IndirectObjectCount => Reader.ObjectNumbers.Count;

    /// <summary>The object numbers present in the cross-reference table.</summary>
    public IReadOnlyCollection<int> ObjectNumbers => Reader.ObjectNumbers;

    /// <summary>
    /// The byte offset where indirect object <paramref name="objectNumber"/> is written (the start of
    /// its <c>N G obj</c> header), or <see langword="null"/> for an object that lives in an object
    /// stream or is absent from the cross-reference table. Used by byte-level layout checks (§6.1.9).
    /// </summary>
    public long? ObjectOffset(int objectNumber) => Reader.UncompressedObjectOffset(objectNumber);

    /// <summary>
    /// Byte offset just after the <c>endobj</c> keyword for the specified object, or
    /// <see langword="null"/> if the object is in an object stream or not found within the scan window.
    /// </summary>
    public int? ObjectEndOffset(int objectNumber) => Reader.UncompressedObjectEndOffset(objectNumber);

    /// <summary>Xref revisions in the file, oldest-first. Used by PDF/A §6.4.3-1 analysis.</summary>
    public IReadOnlyList<XrefRevision> Revisions => Reader.Revisions;

    /// <summary>
    /// Enumerates the resolved value of every indirect object in the file. Used by file-structure
    /// rules (§6.1.13) that constrain every object value regardless of reachability.
    /// </summary>
    public IEnumerable<PdfObject> EnumerateIndirectObjects()
    {
        foreach (var objectNumber in Reader.ObjectNumbers)
        {
            PdfObject? value;
            try
            {
                value = Reader.Resolve(objectNumber);
            }
            catch
            {
                continue; // A malformed object must not abort the whole scan.
            }
            if (value is not null)
                yield return value;
        }
    }

    /// <summary>
    /// Enumerates every stream object in the file, by walking the cross-reference keyspace. Used by
    /// file-structure rules (§6.1.7) that constrain <em>all</em> streams — filters, external
    /// references — independent of whether the stream is reachable through the rendered content.
    /// </summary>
    public IEnumerable<ParsedStream> EnumerateStreams()
    {
        // Iterate the actual xref entries rather than 1..Size: robust to a non-direct or absent
        // /Size and inclusive of objects added by incremental updates.
        foreach (var objectNumber in Reader.ObjectNumbers)
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

    /// <summary>
    /// Returns <paramref name="stream"/>'s body decrypted (unchanged on an unencrypted document,
    /// or under an Identity crypt filter), but NOT run through the ordinary filter chain — for a
    /// rule that parses an image codec's own raw bytes directly (e.g. <c>Jpeg2000Rule</c> on a
    /// JPXDecode stream) and must not read <c>stream.RawBody</c> itself, which is ciphertext on an
    /// encrypted document. See <see cref="PdfDocumentReader.DecryptedStreamView"/>.
    /// </summary>
    public ReadOnlyMemory<byte> DecryptedRawBody(ParsedStream stream) => Reader.DecryptedStreamView(stream).RawBody;

    /// <summary>
    /// The longest message a <see cref="PreflightAssertion"/> retains. A message identifies a
    /// finding; it carries at most an excerpt of the producer's value, never the whole of it. Many
    /// rules interpolate a name, a string or a keyword the document controls, and ISO 32000-2 Annex
    /// C.1 sets no bound on any of those ("In general, this PDF standard does not restrict the size
    /// or quantity of things described in the PDF file format"), so without this cut one
    /// 900,000-byte /Filter name shared by 400 pages retained 705.7 MiB (GC delta) of message text
    /// from a 990 KB file (measured in #403). 1024 characters is roughly twice the longest sentence
    /// any rule composes on its own (522 characters, A2aContentItemTaggingRule) and short enough
    /// that a result list of thousands of findings stays a few megabytes.
    /// <para>
    /// This cut is the only bound most messages have. Ten sites whose message names a producer
    /// value (a /Filter, an action type, a named action, an annotation /Subtype or /AP key, a
    /// composite font's /Encoding CMap name in two rules, a /RoleMap or /Perms key, a blend mode)
    /// additionally excerpt it through the Reader's <see cref="DiagnosticExcerpt"/> before
    /// interpolating, so the sentence keeps its shape; every other producer-controlled
    /// interpolation is cut mid-value here when the value is oversized (#405 lists them). The two
    /// differ in what they can assume: a <see cref="PdfName"/> parsed from a document is Latin-1
    /// (one character per byte, never a surrogate pair), so
    /// <see cref="DiagnosticExcerpt.Quote(string)"/> slices freely and counts bytes, while this cut
    /// sees text decoded from UTF-16BE too and has to step around a surrogate pair.
    /// </para>
    /// </summary>
    internal const int MaxMessageChars = 1024;

    /// <summary>Records a finding for the current validation pass.</summary>
    /// <param name="ruleId">Stable rule identifier (typically the rule's <see cref="IConformanceRule.RuleId"/>).</param>
    /// <param name="clause">Specification clause citation.</param>
    /// <param name="severity">The finding's severity.</param>
    /// <param name="message">Human-readable description. Text past <see cref="MaxMessageChars"/>
    /// characters is replaced by <c>... (N chars)</c>, N being the full length in characters
    /// (UTF-16 code units). One character less is kept when the cut would split a surrogate
    /// pair.</param>
    /// <param name="objectRef">Optional <c>"N 0 R"</c> object location.</param>
    public void Report(
        string ruleId,
        string clause,
        PreflightSeverity severity,
        string message,
        string? objectRef = null)
    {
        if (message is { Length: > MaxMessageChars })
        {
            // A message can end mid-surrogate-pair when the producer value came from UTF-16BE
            // text (A2aLangSyntaxRule, UaLangSyntaxRule, XmpPacket): cutting inside the pair would
            // leave a lone high surrogate in the retained string, which the CLI's SARIF writer
            // (Formatter.WriteSarif) would then have to serialize (#403).
            var cut = MaxMessageChars;
            if (char.IsHighSurrogate(message[cut - 1]))
                cut--;
            message = string.Concat(message.AsSpan(0, cut), "... (", message.Length.ToString(),
                " chars)");
        }

        _assertions.Add(new PreflightAssertion(ruleId, clause, severity, message, objectRef));
    }
}
