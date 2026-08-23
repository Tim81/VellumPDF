// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Encryption;
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
    // Cached alongside this object's AUTHORITATIVE generation, which is a single fact about the
    // object number, not about how this cache entry was populated: the xref table's recorded
    // generation when it parsed cleanly (ISO 32000-2 treats the xref as authoritative, and most
    // readers do not additionally require the "N G obj" header to agree — see the Resolve(int,
    // int?) comment on XrefEntry.UnknownGeneration for what happens when it doesn't parse), or the
    // parsed header's generation for the rare entry whose xref generation field itself could not
    // be read (XrefEntry.UnknownGeneration; 0 for an /ObjStm member, fixed by §7.5.7). Storing this
    // once means a warm hit — whether the caller cares about generation or not — costs exactly one
    // dictionary lookup: no separate _xref lookup is needed to answer a generation-bearing call,
    // which matters because ResolveValue is the entry point for nearly every dictionary-value
    // dereference the Conformance package makes.
    private readonly Dictionary<int, (PdfObject Value, int Generation)> _cache = new();
    private readonly Dictionary<int, (ParsedStream Stream, int Generation)> _streamCache = new();
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

    // Non-null only for an encrypted document, and only once the supplied password has
    // authenticated (the constructor throws PdfPasswordException otherwise, so a live
    // PdfDocumentReader instance never observes _decryptor set without _fileKey also set).
    // Resolve() and GetDecodedStreamData() gate all decryption on this being non-null, so an
    // unencrypted document never pays for (or risks a bug in) any of this machinery.
    private readonly StandardSecurityDecryptor? _decryptor;
    private readonly byte[]? _fileKey;
    private readonly Dictionary<string, CryptFilterMethod> _cryptFilterTable = new(StringComparer.Ordinal);
    private readonly bool _encryptMetadata = true;

    internal ReadOnlyMemory<byte> Bytes { get; }
    internal PdfDictionary Trailer { get; }

    /// <summary>The byte offset recorded in the last startxref.</summary>
    internal int StartXrefOffset { get; }

    /// <summary>
    /// Xref revisions in the file, oldest-first. Used by PDF/A §6.4.3-1 under-coverage analysis.
    /// A single-revision file yields a one-element list.
    /// </summary>
    internal IReadOnlyList<XrefRevision> Revisions { get; }

    /// <summary>Total length of the PDF byte buffer.</summary>
    internal int TotalLength => Bytes.Length;

    /// <summary>The document catalog dictionary (/Root).</summary>
    public PdfDictionary Catalog { get; }

    /// <summary>
    /// The document's Standard security handler settings, or <see langword="null"/> when the
    /// document is not encrypted (no <c>/Encrypt</c> in the trailer). Never null for a document that
    /// opened successfully via a password-protected path — <see cref="PdfReader.Open(byte[], string?)"/>
    /// throws <see cref="PdfPasswordException"/> before a <see cref="PdfDocumentReader"/> exists at
    /// all when the supplied password does not authenticate.
    /// </summary>
    public PdfEncryptionInfo? Encryption { get; }

    /// <summary>All digital signatures found in the document's AcroForm, in field-tree order.</summary>
    public IReadOnlyList<PdfSignature> Signatures => _signatures ??= CollectSignatures();

    internal PdfDocumentReader(
        ReadOnlyMemory<byte> bytes,
        Dictionary<int, XrefEntry> xref,
        PdfDictionary trailer,
        int startXrefOffset,
        IReadOnlyList<XrefRevision> revisions,
        string? password = null)
    {
        Bytes = bytes;
        _xref = xref;
        Trailer = trailer;
        StartXrefOffset = startXrefOffset;
        Revisions = revisions;

        // /Encrypt must be resolved and authenticated BEFORE anything else: Resolve() and
        // GetDecodedStreamData() key their decryption on _decryptor being set, and /Root (resolved
        // just below) is itself an encrypted object in an encrypted document. Resolving /Encrypt
        // here, with _decryptor still null, is also what keeps its own strings (/O, /U, /OE, /UE)
        // from ever being run through string decryption — see the constructor's caching of this
        // object, and DecryptObjectGraph's doc comment, for why no separate guard is needed beyond
        // this ordering.
        if (trailer.TryGet(new PdfName("Encrypt"), out var encryptRaw) && encryptRaw is not null)
        {
            var encryptDict = ResolveValue(encryptRaw) as PdfDictionary
                ?? throw new InvalidDataException("Malformed PDF: /Encrypt does not resolve to a dictionary.");

            var setup = EncryptionSetup.Authenticate(encryptDict, trailer, password);
            _decryptor = setup.Decryptor;
            _fileKey = setup.FileKey;
            _cryptFilterTable = setup.CryptFilterTable;
            _encryptMetadata = setup.EncryptMetadata;

            Encryption = new PdfEncryptionInfo(
                setup.Decryptor.V, setup.Decryptor.R, setup.Cipher, setup.KeyLengthBits,
                setup.Permissions, setup.EncryptMetadata, setup.IsOwnerAccess);
        }

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
            return Resolve(reference) is PdfInteger length ? length.Value : null;
        }
        finally
        {
            _resolvingLength.Remove(reference.ObjectNumber);
        }
    }

    /// <summary>
    /// Resolves an indirect reference by object number, returning its dictionary or value, without
    /// regard to generation. Used where no specific generation is being asked for (e.g. scanning
    /// every object number the cross-reference table defines). Prefer <see cref="Resolve(PdfIndirectReference)"/>
    /// when a real reference — and therefore its generation — is available.
    /// </summary>
    internal PdfObject? Resolve(int objectNumber) => Resolve(objectNumber, generation: null);

    /// <summary>
    /// Resolves an indirect reference by object number and generation. A non-null
    /// <paramref name="generation"/> that does not match the cross-reference table's record for
    /// this object resolves to <see langword="null"/> rather than the wrong revision (ISO 32000-2
    /// §7.3.10) — e.g. <c>10 2 R</c> when the table holds object 10 at generation 0.
    /// </summary>
    private PdfObject? Resolve(int objectNumber, int? generation)
    {
        // One lookup regardless of whether the caller specifies a generation: the cache tuple
        // already carries this object's authoritative generation (see the field comment above), so
        // a warm hit never needs a second trip to _xref to check it.
        if (_cache.TryGetValue(objectNumber, out var cached))
            return generation is null || cached.Generation == generation ? cached.Value : null;

        if (!_xref.TryGetValue(objectNumber, out var entry))
            return null;

        // XrefEntry.UnknownGeneration means the xref's generation field itself could not be parsed
        // (garbled text, or an xref-stream row whose value overflows int). The xref cannot be the
        // authority for an entry it doesn't actually have an opinion on, so this falls through to
        // the object's own header below instead of rejecting (or silently guessing 0 for) every
        // generation up front.
        var xrefIsAuthoritative = entry.Generation != XrefEntry.UnknownGeneration;
        if (generation is not null && xrefIsAuthoritative && generation != entry.Generation)
            return null;

        if (_resolveDepth >= MaxResolveDepth)
            throw new InvalidDataException(
                $"Malformed PDF: indirect-object resolution nested deeper than {MaxResolveDepth} " +
                "(cyclic or pathologically chained /Filter or /Length references).");

        _resolveDepth++;
        try
        {
            PdfObject value;
            int actualGeneration;
            if (entry.Kind == XrefEntryKind.Uncompressed)
            {
                var parser = new PdfObjectParser(Bytes, CheckedOffset(entry.Offset), ResolveLength);
                var result = parser.ParseIndirectObject();

                if (result.ObjectNumber != objectNumber)
                    return null;

                if (xrefIsAuthoritative)
                {
                    actualGeneration = entry.Generation; // already matched against `generation` above
                }
                else
                {
                    // The xref didn't have a usable generation; the header is the only authority left.
                    if (generation is not null && result.Generation != generation)
                        return null;
                    actualGeneration = result.Generation;
                }

                value = result.IsStream
                    ? result.Stream!.Dictionary
                    : result.Value ?? PdfNull.Instance;

                // ISO 32000-1 §7.6.2, Algorithm 1 step (a): every string reachable from this
                // object's own structure is decrypted using ITS identity — the containing indirect
                // object's number and generation, not the string's own position. A stream's
                // dictionary is decrypted here too (it is `value` in that branch); the stream BODY
                // is a separate concern, decrypted lazily in GetDecodedStreamData off
                // ParsedStream.ObjectNumber/Generation, not here. _decryptor is still null while
                // /Encrypt's own dictionary is being resolved (see the constructor), so this never
                // touches /O, /U, /OE, or /UE.
                if (_decryptor is not null)
                    value = DecryptObjectGraph(value, objectNumber, actualGeneration);

                if (result.IsStream)
                    _streamCache.TryAdd(objectNumber, (result.Stream!, actualGeneration));
            }
            else
            {
                var obj = ResolveFromObjectStream(objectNumber, entry);
                if (obj is null) return null;
                value = obj;
                actualGeneration = 0; // ISO 32000-2 §7.5.7: an /ObjStm member is always generation 0.
            }

            _cache[objectNumber] = (value, actualGeneration);
            return value;
        }
        finally
        {
            _resolveDepth--;
        }
    }

    /// <summary>
    /// Returns the <see cref="ParsedStream"/> for a stream object, without regard to generation, or
    /// null if the object is not a stream or does not exist. Prefer
    /// <see cref="ResolveStream(PdfIndirectReference)"/> when a real reference is available.
    /// </summary>
    internal ParsedStream? ResolveStream(int objectNumber) => ResolveStream(objectNumber, generation: null);

    /// <summary>Resolves an indirect reference to a stream object, honouring its generation.</summary>
    internal ParsedStream? ResolveStream(PdfIndirectReference r) => ResolveStream(r.ObjectNumber, r.Generation);

    private ParsedStream? ResolveStream(int objectNumber, int? generation)
    {
        // See Resolve(int, int?) for the single-lookup reasoning and what "authoritative" means.
        if (_streamCache.TryGetValue(objectNumber, out var cached))
            return generation is null || cached.Generation == generation ? cached.Stream : null;

        if (!_xref.TryGetValue(objectNumber, out var entry))
            return null;

        // Objects in object streams cannot themselves be streams.
        if (entry.Kind == XrefEntryKind.InObjectStream)
            return null;

        var xrefIsAuthoritative = entry.Generation != XrefEntry.UnknownGeneration;
        if (generation is not null && xrefIsAuthoritative && generation != entry.Generation)
            return null;

        var parser = new PdfObjectParser(Bytes, CheckedOffset(entry.Offset), ResolveLength);
        var result = parser.ParseIndirectObject();

        if (result.ObjectNumber != objectNumber)
            return null;

        int actualGeneration;
        if (xrefIsAuthoritative)
        {
            actualGeneration = entry.Generation;
        }
        else
        {
            if (generation is not null && result.Generation != generation)
                return null;
            actualGeneration = result.Generation;
        }

        if (!result.IsStream)
            return null;

        var stream = result.Stream!;
        _streamCache.TryAdd(objectNumber, (stream, actualGeneration));

        // Also populate dict cache
        _cache.TryAdd(objectNumber, (stream.Dictionary, actualGeneration));

        return stream;
    }

    /// <summary>
    /// Decodes the filter chain for <paramref name="stream"/> and returns the decoded bytes.
    /// Returns null when an image filter (DCTDecode, JPXDecode, etc.) prevents full decode.
    /// </summary>
    /// <remarks>
    /// Decryption (when the document is encrypted) happens HERE, before the ordinary filter chain
    /// runs — not by mutating <see cref="ParsedStream.RawBody"/>, which stays the verbatim file
    /// bytes for §6.1.7.1 byte-level conformance checks (see the type's own doc comment). A stream
    /// whose effective crypt filter method is Identity is handed to
    /// <see cref="PdfFilters.Decode(ParsedStream, Func{PdfObject?, PdfObject?}?)"/> unchanged; one
    /// that needs decrypting is wrapped in a throwaway <see cref="ParsedStream"/> carrying the
    /// decrypted bytes, never exposed outside this method.
    /// </remarks>
    internal byte[]? GetDecodedStreamData(ParsedStream stream) => PdfFilters.Decode(DecryptedStreamView(stream), ResolveMaybe);

    /// <summary>
    /// Returns a <see cref="ParsedStream"/> view of <paramref name="stream"/> whose body is
    /// decrypted (or, for an unencrypted document or an Identity crypt filter, unchanged) but has
    /// NOT been run through <see cref="PdfFilters"/> — i.e. the same thing <c>stream.RawBody</c>
    /// used to give every caller before #97, except correct on an encrypted document.
    ///
    /// <para>
    /// Exists because <see cref="PdfFilters.Decode(ParsedStream, Func{PdfObject?, PdfObject?}?)"/>
    /// returns <see langword="null"/> whenever an image filter (DCTDecode, JPXDecode, …) is present
    /// — by design, it never attempts to decode image data — which makes it unusable for a caller
    /// like <c>Jpeg2000Rule</c> that wants the raw-but-decrypted JP2/codestream bytes precisely
    /// BECAUSE it parses that image data itself. Reading <c>stream.RawBody</c> directly there would
    /// hand back ciphertext on an encrypted document (see the design note on
    /// <see cref="ParsedStream.RawBody"/>), which is what this method is for.
    /// </para>
    /// </summary>
    internal ParsedStream DecryptedStreamView(ParsedStream stream)
    {
        if (_decryptor is null)
            return stream;

        var method = CryptFilterResolver.ResolveStreamMethod(
            stream.Dictionary, _decryptor.StreamFilter, _cryptFilterTable, _encryptMetadata, ResolveMaybe);

        if (method == CryptFilterMethod.Identity)
            return stream;

        var decryptedBody = _decryptor.DecryptWithMethod(
            _fileKey!, stream.ObjectNumber, stream.Generation, stream.RawBody.Span, method);
        return new ParsedStream(stream.Dictionary, decryptedBody, stream.BodyOffset, stream.ObjectNumber, stream.Generation);
    }

    // Well-known keys used only by the decrypt walk below.
    private static readonly PdfName _sigType = new("Sig");
    private static readonly PdfName _docTimeStampType = new("DocTimeStamp");
    private static readonly PdfName _typeKey = new("Type");
    private static readonly PdfName _byteRangeKey = new("ByteRange");

    /// <summary>
    /// Walks <paramref name="obj"/>'s own structure — recursing into nested dictionaries and
    /// arrays, but NOT following indirect references, which name separate objects that get their
    /// own decrypt pass under their own identity when resolved — decrypting every string found,
    /// using (<paramref name="objectNumber"/>, <paramref name="generation"/>) as the containing
    /// indirect object's identity per ISO 32000-1 §7.6.2, Algorithm 1 step (a): "If the string is a
    /// direct object, use the identifier of the indirect object containing it."
    ///
    /// <para>
    /// Dictionaries and arrays are mutated in place rather than rebuilt (matching
    /// <c>PdfObjectRemapper.RemapStreamInPlace</c>'s reasoning): this method only ever runs, from
    /// <see cref="Resolve(int, int?)"/>, on an object graph that was JUST parsed for this one
    /// resolution and is not yet cached or shared anywhere else, so mutating it is safe and avoids
    /// allocating a full parallel copy of every dictionary and array in the document.
    /// </para>
    /// <para>
    /// <strong>Signature <c>/Contents</c> exemption.</strong> ISO 32000-1 and ISO 32000-2 are both
    /// silent on whether a signature dictionary's <c>/Contents</c> is exempt from string encryption.
    /// It matters: a conformant signer patches <c>/Contents</c>' hex digits directly into the
    /// already-serialized file bytes after computing the signature over the file's own bytes
    /// (<c>PdfSignatureHelper</c> in this codebase does exactly this — see its placeholder/patch
    /// mechanism), which means those bytes were never run through the object-level string-encryption
    /// pipeline at write time in the first place, encrypted document or not. Decrypting them on read
    /// would corrupt the actual signature bytes and break <c>/ByteRange</c> verification. Since the
    /// spec does not say, this method takes the safe reading for signature verification and never
    /// decrypts <c>/Contents</c> when the containing dictionary is a signature dictionary by
    /// <see cref="IsSignatureDictionary"/>'s reading — every OTHER string in that same dictionary
    /// (<c>/Name</c>, <c>/Reason</c>, <c>/Location</c>, <c>/ContactInfo</c>, <c>/M</c>) is still
    /// decrypted normally.
    /// </para>
    /// </summary>
    private PdfObject DecryptObjectGraph(PdfObject obj, int objectNumber, int generation)
    {
        switch (obj)
        {
            case PdfLiteralString s:
                return new PdfLiteralString(_decryptor!.DecryptString(_fileKey!, objectNumber, generation, s.Bytes.Span));

            case PdfHexString h:
                return new PdfHexString(_decryptor!.DecryptString(_fileKey!, objectNumber, generation, h.Bytes.Span));

            case PdfDictionary d:
                var isSignatureDict = IsSignatureDictionary(d);
                foreach (var kv in d.Entries.ToList())
                {
                    if (isSignatureDict && kv.Key.Equals(PdfName.Contents))
                        continue;
                    var newVal = DecryptObjectGraph(kv.Value, objectNumber, generation);
                    if (!ReferenceEquals(newVal, kv.Value))
                        d.Set(kv.Key, newVal);
                }
                return d;

            case PdfArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var newVal = DecryptObjectGraph(a[i], objectNumber, generation);
                    if (!ReferenceEquals(newVal, a[i]))
                        a.SetAt(i, newVal);
                }
                return a;

            default:
                return obj;
        }
    }

    /// <summary>
    /// Whether <paramref name="d"/> is a signature dictionary, for the <c>/Contents</c> exemption
    /// <see cref="DecryptObjectGraph"/> documents. <c>/Type</c> alone is not enough to decide:
    /// ISO 32000-1 Table 252 makes it OPTIONAL in a signature dictionary ("if present, shall be
    /// Sig"), and a document timestamp carries <c>/DocTimeStamp</c> instead — the type this
    /// library's own <c>ArchiveTimestampBuilder</c> writes for a PAdES B-LTA archive timestamp, so a
    /// file this library produced and then encrypted is squarely in scope. A <c>/Type</c>-less
    /// dictionary is therefore recognised structurally, by the pair <c>/ByteRange</c> + a string
    /// <c>/Contents</c>: <c>/ByteRange</c> is what the byte-range digest is computed over, and ISO
    /// 32000-1 §12.8.1 requires it of approval and certification signatures alike, so a signer
    /// that drops the optional <c>/Type</c> still cannot drop it.
    /// </summary>
    private static bool IsSignatureDictionary(PdfDictionary d)
    {
        if (d.Get(_typeKey) is PdfName type)
            return type.Equals(_sigType) || type.Equals(_docTimeStampType);

        // No /Type at all (or a /Type that is not even a name): fall back to the structural tell.
        // Both halves are required — /ByteRange alone would exempt a dictionary with no signature
        // value to protect, and a string /Contents alone is far too common a key to exempt blindly.
        return d.Get(_byteRangeKey) is PdfArray && d.Get(PdfName.Contents) is PdfLiteralString or PdfHexString;
    }

    /// <summary>Resolves an indirect reference, honouring its generation.</summary>
    internal PdfObject? Resolve(PdfIndirectReference r) => Resolve(r.ObjectNumber, r.Generation);

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

        // Decode the stream body. Routed through GetDecodedStreamData, not PdfFilters.Decode
        // directly, so an encrypted document's object stream gets decrypted here, ONCE, using the
        // container's own identity — ISO 32000-2 §7.5.7: "In an encrypted file ... strings occurring
        // anywhere in an object stream shall not be separately encrypted." ResolveFromObjectStream
        // parses each member straight out of this already-plaintext body and never calls
        // DecryptObjectGraph on it, which is what keeps that half of the rule true.
        var body = GetDecodedStreamData(streamObj)
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

            // Range-checked before narrowing, and rejected on a duplicate. This offset map is the
            // ONLY authority for where a compressed object begins — unlike the uncompressed path
            // above, which re-reads the "N G obj" header and returns null when it does not match
            // the object it was asked for. So an out-of-range number that wraps onto a real one, or
            // a repeated number, silently substitutes an object's entire body: a header of
            // "1 0 4294967297 40" makes object 1 parse from relative offset 40, and every verdict
            // this library then reports describes content the document does not contain.
            //
            // Both checks are needed. Rejecting duplicates alone still lets "4294967297 40 1 0"
            // through, because the wrapped key is written first and the honest one is then the
            // duplicate.
            if (numInt.Value is < 0 or > int.MaxValue || offInt.Value is < 0 or > int.MaxValue)
                throw new InvalidDataException(
                    $"Malformed PDF: object stream {containerObjNum} header entry {i} "
                    + $"({numInt.Value} {offInt.Value}) is outside the representable range.");

            if (!offsetMap.TryAdd((int)numInt.Value, (int)offInt.Value))
                throw new InvalidDataException(
                    $"Malformed PDF: object stream {containerObjNum} header declares object "
                    + $"{numInt.Value} more than once.");
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
            if (!Trailer.TryGet(PdfName.Size, out var sizeObj) || sizeObj is not PdfInteger sizeInt)
                return 0;

            // Range-checked rather than narrowed. DssBuilder and ArchiveTimestampBuilder both use
            // this as the first object number for the objects they append, so a wrapped value hands
            // them numbers that already exist: an LTV or archive-timestamp revision would overwrite
            // base-revision objects and could alter or invalidate the very signature it augments.
            // The xref-stream path already range-checks its own /Size (XrefParser); the classic
            // trailer never did.
            if (sizeInt.Value is < 0 or > int.MaxValue)
                throw new InvalidDataException(
                    $"Malformed PDF: trailer /Size {sizeInt.Value} is outside the representable range.");

            return (int)sizeInt.Value;
        }
    }

    /// <summary>
    /// Every object number present in the resolved cross-reference table. More robust than
    /// <c>1..Size</c> for whole-document scans: independent of a direct/absent <c>/Size</c> and
    /// inclusive of object numbers introduced by incremental updates.
    /// </summary>
    internal IReadOnlyCollection<int> ObjectNumbers => _xref.Keys;

    /// <summary>
    /// The first object number an incremental update may use without colliding with an object the
    /// document already defines.
    /// </summary>
    /// <remarks>
    /// The trailer's <c>/Size</c> alone is not safe to number from. It is author-controlled and
    /// only advisory: it can be absent, indirect, a real, or simply understated, and every one of
    /// those yields a starting number that lands on top of existing objects — an appended /DSS or
    /// document-timestamp revision would then replace base-revision objects and could invalidate
    /// the very signature it was added to augment. Range-checking <c>/Size</c> catches only the
    /// case where it is too large to represent, which is the rarest of them. Taking the highest
    /// object the cross-reference table actually defines closes the rest, and <c>/Size</c> is still
    /// honoured when it is larger, since a conformant one exceeds every object number in the file.
    /// </remarks>
    internal int NextFreeObjectNumber
    {
        get
        {
            var highest = 0;
            foreach (var objectNumber in _xref.Keys)
            {
                if (objectNumber > highest)
                    highest = objectNumber;
            }

            return Math.Max(Size, highest + 1);
        }
    }

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
    /// Returns the exclusive byte offset just after the <c>endobj</c> keyword for
    /// <paramref name="objectNumber"/>, scanning at most <paramref name="maxScanBytes"/> bytes
    /// forward from the object's xref offset.
    /// Returns <see langword="null"/> when the object is absent from the xref table, lives in an
    /// object stream (which has no in-file <c>endobj</c>), or <c>endobj</c> is not found within
    /// the scan window (truncated or malformed file).
    /// </summary>
    internal int? UncompressedObjectEndOffset(int objectNumber, int maxScanBytes = 1 << 20)
    {
        if (!_xref.TryGetValue(objectNumber, out var entry) || entry.Kind != XrefEntryKind.Uncompressed)
            return null;

        if (entry.Offset < 0 || entry.Offset >= Bytes.Length) return null;
        var start = (int)entry.Offset;
        var windowEnd = (int)Math.Min(Bytes.Length, (long)start + maxScanBytes);
        var span = Bytes.Span[start..windowEnd];
        var needle = "endobj"u8;

        for (var i = 0; i <= span.Length - needle.Length; i++)
        {
            if (!span[i..].StartsWith(needle))
                continue;

            // Check word boundary: preceding byte must be non-regular or we're at window start.
            var precedingOk = i == 0 || !IsRegular(span[i - 1]);
            if (!precedingOk)
                continue;

            // Check word boundary: following byte must be non-regular or we're at window end.
            var afterIndex = i + needle.Length;
            var followingOk = afterIndex >= span.Length || !IsRegular(span[afterIndex]);
            if (!followingOk)
                continue;

            return start + afterIndex;
        }

        return null;
    }

    // A byte is "regular" if it is NOT PDF whitespace and NOT a PDF delimiter.
    // PDF whitespace: NUL, HT, LF, FF, CR, SP  (ISO 32000-2 Table 1)
    // PDF delimiters: ( ) < > [ ] { } / %       (ISO 32000-2 Table 2)
    private static bool IsRegular(byte b) => b is not (0 or 9 or 10 or 12 or 13 or 32
        or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'['
        or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');

    /// <summary>
    /// Appends a new revision to this document and returns the full updated byte array.
    /// </summary>
    /// <param name="objects">
    /// Every object to write in this revision, as (objectNumber, generation, value). Rewriting an
    /// object that already exists in the base document — the /Root catalog, a page, anything a
    /// caller resolved out of it — must pass that object's EXISTING generation, not 0: generation
    /// only advances when a freed number is reused for an unrelated object (ISO 32000-2 §7.5.4).
    /// Getting this wrong is silent at write time and fails differently depending on what was
    /// rewritten — a wrong /Root generation makes the whole appended revision fail to reopen
    /// (<see cref="PdfDocumentReader"/>'s constructor requires /Root to resolve); a wrong generation
    /// on anything else just makes that one object silently unresolvable, so a page or font
    /// vanishes with no exception anywhere (#121 C1).
    /// </param>
    internal byte[] AppendRevision(IReadOnlyList<(int ObjectNumber, int Generation, PdfObject Value)> objects)
    {
        if (objects.Count == 0)
            throw new ArgumentException("At least one object is required.", nameof(objects));

        var ms = new MemoryStream(Bytes.Length + 4096);
        ms.Write(Bytes.Span);

        var writer = new PdfWriter(ms, Bytes.Length);

        var written = new List<(int ObjectNumber, int Generation, long ByteOffset)>(objects.Count);
        foreach (var (objNum, generation, value) in objects)
        {
            var offset = writer.Position;
            new PdfIndirectObject(objNum, generation, value).WriteTo(writer);
            writer.WriteByte((byte)'\n');
            written.Add((objNum, generation, offset));
        }

        PdfIndirectReference catalogRef;
        if (Trailer.TryGet(PdfName.Root, out var rootRaw) && rootRaw is PdfIndirectReference rootRef)
            catalogRef = rootRef;
        else
            throw new InvalidDataException("Base trailer does not contain a valid /Root indirect reference.");

        // If this revision rewrites /Root's own object number, the header and xref entry just
        // written for it must agree with the generation the trailer is about to claim — otherwise
        // the appended trailer's /Root disagrees with what sits right next to it in the same
        // revision, and the whole result fails to reopen (#121 C1: this is exactly how that broke).
        foreach (var w in written)
        {
            if (w.ObjectNumber == catalogRef.ObjectNumber && w.Generation != catalogRef.Generation)
                throw new ArgumentException(
                    $"Object {w.ObjectNumber} (the /Root catalog) is being rewritten at generation " +
                    $"{w.Generation}, but the base trailer's /Root reference is generation " +
                    $"{catalogRef.Generation}. An incremental update that rewrites an existing " +
                    "object must keep that object's existing generation.",
                    nameof(objects));
        }

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
        long[] byteRange = [];
        if (brObj is PdfArray brArr)
        {
            byteRange = new long[brArr.Count];
            for (var i = 0; i < brArr.Count; i++)
            {
                // PdfInteger.Value is already a long, and these are file offsets: narrowing to int
                // silently wrapped every offset past 2 GB, so a large signed file was checked
                // against the wrong byte ranges without any error being reported.
                if (brArr[i] is PdfInteger pi)
                    byteRange[i] = pi.Value;
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
