// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Reader;

/// <summary>
/// A parsed PDF document opened via <see cref="PdfReader.Open(byte[])"/>.
/// Provides lazy object resolution, catalog access, and signature navigation.
/// </summary>
/// <remarks>
/// Instances are not thread-safe: object resolution and signature collection populate an
/// internal cache without synchronization. Use one reader per thread.
/// </remarks>
public sealed class PdfDocumentReader : IDisposable
{
    private readonly Dictionary<int, XrefEntry> _xref;
    private readonly Dictionary<int, PdfObject> _cache = new();
    private readonly Dictionary<int, ParsedStream> _streamCache = new();
    // ObjStm cache: container obj number → (decoded body, First offset, N count, header offset map)
    private readonly Dictionary<int, (byte[] Body, int First, int N, Dictionary<int, int> OffsetMap)> _objStmCache = new();
    // Containers currently being loaded — guards against a container whose /Filter (or /Length)
    // indirectly references an object inside itself, which would recurse into LoadObjectStream
    // forever (uncatchable StackOverflow) since the cache is only populated once loading completes.
    private readonly HashSet<int> _loadingObjStm = new();
    // Bounds the NESTING DEPTH of indirect-reference resolution. The cycle guards above reject a
    // reference chain that revisits an in-progress object, but not an *acyclic* chain of distinct
    // objects whose /Filter or /Length each points into the next — that would recurse one stack
    // frame per link (Resolve → … → Resolve) until StackOverflow (uncatchable). Legitimate nesting
    // is 1–2 deep, so a generous cap costs nothing and stays far under the thread stack limit.
    private int _resolveDepth;
    private const int MaxResolveDepth = 100;
    private IReadOnlyList<PdfSignature>? _signatures;

    // Caps AcroForm field-tree recursion.
    private const int MaxFieldTreeDepth = 512;

    internal ReadOnlyMemory<byte> Bytes { get; }
    internal PdfDictionary Trailer { get; }

    /// <summary>The byte offset recorded in the last startxref.</summary>
    internal int StartXrefOffset { get; }

    /// <summary>Total length of the PDF byte buffer.</summary>
    internal int TotalLength => Bytes.Length;

    /// <summary>The document catalog dictionary (/Root).</summary>
    public PdfDictionary Catalog { get; }

    /// <summary>All digital signatures found in the document's AcroForm, in field-tree order.</summary>
    public IReadOnlyList<PdfSignature> Signatures => _signatures ??= CollectSignatures();

    internal PdfDocumentReader(
        ReadOnlyMemory<byte> bytes,
        Dictionary<int, XrefEntry> xref,
        PdfDictionary trailer,
        int startXrefOffset)
    {
        Bytes = bytes;
        _xref = xref;
        Trailer = trailer;
        StartXrefOffset = startXrefOffset;

        if (!trailer.TryGet(PdfName.Root, out var rootObj) || rootObj is null)
            throw new InvalidDataException("Malformed PDF: trailer is missing /Root.");

        var rootResolved = ResolveValue(rootObj);
        if (rootResolved is not PdfDictionary catalog)
            throw new InvalidDataException("Malformed PDF: /Root does not resolve to a dictionary.");

        Catalog = catalog;
    }

    /// <summary>
    /// Validates a byte offset taken from the cross-reference table — an xref-stream field can hold
    /// a value larger than <see cref="int.MaxValue"/> — and narrows it to an int, throwing rather
    /// than wrapping silently to a negative parser position (which would crash with an
    /// <see cref="IndexOutOfRangeException"/>).
    /// </summary>
    private int CheckedOffset(long offset)
    {
        if (offset < 0 || offset >= Bytes.Length)
            throw new InvalidDataException(
                $"Malformed PDF: object offset {offset} is outside the file (length {Bytes.Length}).");
        return (int)offset;
    }

    // Length-object numbers currently being resolved, to break a stream whose /Length references
    // itself (directly or in a cycle) — such a reference simply falls back to the endstream scan.
    private readonly HashSet<int> _resolvingLength = new();

    /// <summary>
    /// Resolves an indirect stream <c>/Length</c> to its integer value, or null when it cannot be
    /// resolved (so the parser falls back to the endstream scan). Guards against self-reference.
    /// </summary>
    private long? ResolveLength(PdfIndirectReference reference)
    {
        if (!_resolvingLength.Add(reference.ObjectNumber))
            return null;
        try
        {
            return Resolve(reference.ObjectNumber) is PdfInteger length ? length.Value : null;
        }
        finally
        {
            _resolvingLength.Remove(reference.ObjectNumber);
        }
    }

    /// <summary>Resolves an indirect reference by object number, returning its dictionary or value.</summary>
    internal PdfObject? Resolve(int objectNumber)
    {
        if (_cache.TryGetValue(objectNumber, out var cached))
            return cached;

        if (!_xref.TryGetValue(objectNumber, out var entry))
            return null;

        if (_resolveDepth >= MaxResolveDepth)
            throw new InvalidDataException(
                $"Malformed PDF: indirect-object resolution nested deeper than {MaxResolveDepth} " +
                "(cyclic or pathologically chained /Filter or /Length references).");

        _resolveDepth++;
        try
        {
            PdfObject value;
            if (entry.Kind == XrefEntryKind.Uncompressed)
            {
                var parser = new PdfObjectParser(Bytes, CheckedOffset(entry.Offset), ResolveLength);
                var result = parser.ParseIndirectObject();

                if (result.ObjectNumber != objectNumber)
                    return null;

                value = result.IsStream
                    ? result.Stream!.Dictionary
                    : result.Value ?? PdfNull.Instance;

                if (result.IsStream)
                    _streamCache.TryAdd(objectNumber, result.Stream!);
            }
            else
            {
                var obj = ResolveFromObjectStream(objectNumber, entry);
                if (obj is null) return null;
                value = obj;
            }

            _cache[objectNumber] = value;
            return value;
        }
        finally
        {
            _resolveDepth--;
        }
    }

    /// <summary>
    /// Returns the <see cref="ParsedStream"/> for a stream object, or null if the
    /// object is not a stream or does not exist.
    /// </summary>
    internal ParsedStream? ResolveStream(int objectNumber)
    {
        // If already in stream cache, return it.
        if (_streamCache.TryGetValue(objectNumber, out var cached))
            return cached;

        if (!_xref.TryGetValue(objectNumber, out var entry))
            return null;

        // Objects in object streams cannot themselves be streams.
        if (entry.Kind == XrefEntryKind.InObjectStream)
            return null;

        var parser = new PdfObjectParser(Bytes, CheckedOffset(entry.Offset), ResolveLength);
        var result = parser.ParseIndirectObject();

        if (result.ObjectNumber != objectNumber)
            return null;

        if (!result.IsStream)
            return null;

        var stream = result.Stream!;
        _streamCache.TryAdd(objectNumber, stream);

        // Also populate dict cache
        _cache.TryAdd(objectNumber, stream.Dictionary);

        return stream;
    }

    /// <summary>
    /// Decodes the filter chain for <paramref name="stream"/> and returns the decoded bytes.
    /// Returns null when an image filter (DCTDecode, JPXDecode, etc.) prevents full decode.
    /// </summary>
    internal byte[]? GetDecodedStreamData(ParsedStream stream) => PdfFilters.Decode(stream, ResolveMaybe);

    /// <summary>Resolves an indirect reference.</summary>
    internal PdfObject? Resolve(PdfIndirectReference r) => Resolve(r.ObjectNumber);

    /// <summary>
    /// If <paramref name="obj"/> is a <see cref="PdfIndirectReference"/>, resolves and returns
    /// the target object. Otherwise returns <paramref name="obj"/> unchanged.
    /// </summary>
    internal PdfObject? ResolveValue(PdfObject obj) =>
        obj is PdfIndirectReference r ? Resolve(r) : obj;

    /// <summary>Null-tolerant <see cref="ResolveValue"/> for use as a filter-chain resolver.</summary>
    private PdfObject? ResolveMaybe(PdfObject? obj) => obj is null ? null : ResolveValue(obj);

    /// <inheritdoc />
    public void Dispose() { }

    // ── Object stream resolution ─────────────────────────────────────────────

    private PdfObject? ResolveFromObjectStream(int objNum, XrefEntry entry)
    {
        var containerObjNum = entry.ObjStmObjectNumber;

        if (!_xref.TryGetValue(containerObjNum, out var containerEntry))
            throw new InvalidDataException(
                $"Object stream container {containerObjNum} not found in xref.");

        // A type-2 entry pointing to a type-2 container is illegal.
        if (containerEntry.Kind == XrefEntryKind.InObjectStream)
            throw new InvalidDataException(
                $"Object stream container {containerObjNum} is itself a type-2 (in-object-stream) entry; " +
                "nested object streams are not permitted (ISO 32000-2 §7.5.7).");

        if (!_objStmCache.TryGetValue(containerObjNum, out var cached))
            cached = LoadObjectStream(containerObjNum, containerEntry);

        var (body, first, n, offsetMap) = cached;

        if (!offsetMap.TryGetValue(objNum, out var relOffset))
            throw new InvalidDataException(
                $"Object {objNum} not found in object stream {containerObjNum}.");

        // relOffset comes from the (untrusted) object-stream header; guard against a negative or
        // overflowing offset producing an out-of-bounds slice (a non-InvalidDataException crash).
        if (relOffset < 0 || (long)first + relOffset >= body.Length)
            throw new InvalidDataException(
                $"Object {objNum} offset in object stream {containerObjNum} is out of range " +
                $"(first={first}, relative={relOffset}, body length={body.Length}).");

        var absoluteOffset = first + relOffset;

        var mem = new ReadOnlyMemory<byte>(body, absoluteOffset, body.Length - absoluteOffset);
        var parser = new PdfObjectParser(mem);
        return parser.ParseObject();
    }

    private (byte[] Body, int First, int N, Dictionary<int, int> OffsetMap) LoadObjectStream(
        int containerObjNum, XrefEntry containerEntry)
    {
        // Re-entry on the same container means a cyclic reference (e.g. the container's /Filter is an
        // indirect reference to an object stored inside the container). The cache is not populated
        // until this method returns, so without this guard the recursion is unbounded.
        if (!_loadingObjStm.Add(containerObjNum))
            throw new InvalidDataException(
                $"Malformed PDF: object stream {containerObjNum} is defined in terms of itself.");
        try
        {
            return LoadObjectStreamCore(containerObjNum, containerEntry);
        }
        finally
        {
            _loadingObjStm.Remove(containerObjNum);
        }
    }

    private (byte[] Body, int First, int N, Dictionary<int, int> OffsetMap) LoadObjectStreamCore(
        int containerObjNum, XrefEntry containerEntry)
    {
        var parser = new PdfObjectParser(Bytes, CheckedOffset(containerEntry.Offset), ResolveLength);
        var result = parser.ParseIndirectObject();

        if (!result.IsStream)
            throw new InvalidDataException(
                $"Object stream {containerObjNum} at offset {containerEntry.Offset} is not a stream object.");

        var streamObj = result.Stream!;
        var dict = streamObj.Dictionary;

        if (dict.Get(new PdfName("N")) is not PdfInteger nObj)
            throw new InvalidDataException($"Object stream {containerObjNum} missing /N.");
        var n = (int)nObj.Value;

        if (n < 0 || n > 1_000_000)
            throw new InvalidDataException(
                $"Object stream {containerObjNum} /N={n} is out of range.");

        if (dict.Get(new PdfName("First")) is not PdfInteger firstObj)
            throw new InvalidDataException($"Object stream {containerObjNum} missing /First.");
        var first = (int)firstObj.Value;

        // Decode the stream body
        var body = PdfFilters.Decode(streamObj, ResolveMaybe)
            ?? throw new InvalidDataException(
                $"Object stream {containerObjNum} uses an image filter that cannot be decoded.");

        if (first < 0 || first > body.Length)
            throw new InvalidDataException(
                $"Object stream {containerObjNum} /First={first} is out of range for body length {body.Length}.");

        // Parse the header: N pairs of (objNum, offset). Do NOT pre-size the dictionary to /N:
        // a malicious stream can declare /N up to 1,000,000 with a tiny body, and a capacity hint
        // of that size would allocate megabytes before the header parse fails (allocation
        // amplification). Let it grow to the number of pairs actually parsed.
        var headerMem = new ReadOnlyMemory<byte>(body, 0, first);
        var headerParser = new PdfObjectParser(headerMem);
        var offsetMap = new Dictionary<int, int>();

        for (var i = 0; i < n; i++)
        {
            var numObj = headerParser.ParseObject();
            var offObj = headerParser.ParseObject();

            if (numObj is not PdfInteger numInt || offObj is not PdfInteger offInt)
                throw new InvalidDataException(
                    $"Object stream {containerObjNum} header entry {i} is not a pair of integers.");

            offsetMap[(int)numInt.Value] = (int)offInt.Value;
        }

        var entry = (body, first, n, offsetMap);
        _objStmCache[containerObjNum] = entry;
        return entry;
    }

    // ── Incremental update / append ──────────────────────────────────────────

    /// <summary>
    /// The current object count from the base trailer's /Size field.
    /// </summary>
    internal int Size
    {
        get
        {
            if (Trailer.TryGet(PdfName.Size, out var sizeObj) && sizeObj is PdfInteger sizeInt)
                return (int)sizeInt.Value;
            return 0;
        }
    }

    /// <summary>
    /// Every object number present in the resolved cross-reference table. More robust than
    /// <c>1..Size</c> for whole-document scans: independent of a direct/absent <c>/Size</c> and
    /// inclusive of object numbers introduced by incremental updates.
    /// </summary>
    internal IReadOnlyCollection<int> ObjectNumbers => _xref.Keys;

    /// <summary>
    /// The byte offset at which the indirect object <paramref name="objectNumber"/> is written
    /// (the start of its <c>N G obj</c> header), or <see langword="null"/> when the object is not in
    /// the cross-reference table or lives inside an object stream (and so has no file offset of its
    /// own). Used by the §6.1.9 byte-level layout checks.
    /// </summary>
    internal long? UncompressedObjectOffset(int objectNumber)
        => _xref.TryGetValue(objectNumber, out var entry) && entry.Kind == XrefEntryKind.Uncompressed
            ? entry.Offset
            : null;

    /// <summary>
    /// Appends a new revision to this document and returns the full updated byte array.
    /// </summary>
    internal byte[] AppendRevision(IReadOnlyList<(int ObjectNumber, PdfObject Value)> objects)
    {
        if (objects.Count == 0)
            throw new ArgumentException("At least one object is required.", nameof(objects));

        var ms = new MemoryStream(Bytes.Length + 4096);
        ms.Write(Bytes.Span);

        var writer = new PdfWriter(ms, Bytes.Length);

        var written = new List<(int ObjectNumber, long ByteOffset)>(objects.Count);
        foreach (var (objNum, value) in objects)
        {
            var offset = writer.Position;
            new PdfIndirectObject(objNum, value).WriteTo(writer);
            writer.WriteByte((byte)'\n');
            written.Add((objNum, offset));
        }

        PdfIndirectReference catalogRef;
        if (Trailer.TryGet(PdfName.Root, out var rootRaw) && rootRaw is PdfIndirectReference rootRef)
            catalogRef = rootRef;
        else
            throw new InvalidDataException("Base trailer does not contain a valid /Root indirect reference.");

        PdfArray? documentId = null;
        if (Trailer.TryGet(PdfName.ID, out var idRaw) && idRaw is PdfArray idArr)
            documentId = idArr;

        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            Size,
            catalogRef,
            StartXrefOffset,
            documentId);

        writer.Flush();
        return ms.ToArray();
    }

    // ── Signature navigation ─────────────────────────────────────────────────

    private List<PdfSignature> CollectSignatures()
    {
        var sigs = new List<PdfSignature>();

        var acroFormRaw = Catalog.Get(new PdfName("AcroForm"));
        if (acroFormRaw is null) return sigs;

        var acroFormObj = ResolveValue(acroFormRaw);
        if (acroFormObj is not PdfDictionary acroForm)
            return sigs;

        var fieldsRaw = acroForm.Get(new PdfName("Fields"));
        if (fieldsRaw is null)
            return sigs;

        var fields = ResolveValue(fieldsRaw);
        if (fields is not PdfArray fieldsArray)
            return sigs;

        var visited = new HashSet<int>();
        for (var i = 0; i < fieldsArray.Count; i++)
            CollectFieldSignatures(fieldsArray[i], sigs, visited, 0);

        return sigs;
    }

    private void CollectFieldSignatures(PdfObject fieldObj, List<PdfSignature> sigs, HashSet<int> visited, int depth)
    {
        if (depth > MaxFieldTreeDepth)
            return;
        if (fieldObj is PdfIndirectReference fieldRef && !visited.Add(fieldRef.ObjectNumber))
            return;

        var resolved = ResolveValue(fieldObj);
        if (resolved is not PdfDictionary field)
            return;

        var ftObj = field.Get(new PdfName("FT"));
        if (ftObj is PdfName ft && ft.Value == "Sig")
        {
            var vObj = field.Get(new PdfName("V"));
            if (vObj is not null)
            {
                var sigDict = ResolveValue(vObj) as PdfDictionary;
                if (sigDict is not null)
                {
                    var sig = ExtractSignature(sigDict);
                    if (sig is not null)
                        sigs.Add(sig);
                }
            }
            return;
        }

        var kidsObj = field.Get(PdfName.Kids);
        if (kidsObj is not null)
        {
            var kids = ResolveValue(kidsObj);
            if (kids is PdfArray kidsArray)
            {
                for (var i = 0; i < kidsArray.Count; i++)
                    CollectFieldSignatures(kidsArray[i], sigs, visited, depth + 1);
            }
        }
    }

    private static PdfSignature? ExtractSignature(PdfDictionary sigDict)
    {
        PdfName? subFilter = null;
        var sfObj = sigDict.Get(new PdfName("SubFilter"));
        if (sfObj is PdfName sfName)
            subFilter = sfName;

        var brObj = sigDict.Get(new PdfName("ByteRange"));
        int[] byteRange = [];
        if (brObj is PdfArray brArr)
        {
            byteRange = new int[brArr.Count];
            for (var i = 0; i < brArr.Count; i++)
            {
                if (brArr[i] is PdfInteger pi)
                    byteRange[i] = (int)pi.Value;
            }
        }

        var contentsObj = sigDict.Get(PdfName.Contents);
        ReadOnlyMemory<byte> contents = ReadOnlyMemory<byte>.Empty;
        if (contentsObj is PdfHexString hexStr)
            contents = hexStr.Bytes;

        string? signingTime = null;
        var mObj = sigDict.Get(new PdfName("M"));
        if (mObj is PdfLiteralString ls)
            signingTime = Encoding.Latin1.GetString(ls.Bytes.Span);
        else if (mObj is PdfHexString mHex)
            signingTime = Encoding.Latin1.GetString(mHex.Bytes.Span);

        if (contents.IsEmpty && byteRange.Length == 0)
            return null;

        return new PdfSignature(subFilter, byteRange, contents, signingTime);
    }
}
