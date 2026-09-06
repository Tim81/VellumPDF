// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
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
public sealed partial class PdfDocumentReader : IDisposable
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

    /// <summary>
    /// The object number of the trailer's <c>/Encrypt</c> dictionary, or <see langword="null"/> for
    /// an unencrypted document. <see cref="SaveDecrypted(Stream)"/> excludes this object explicitly
    /// (#186's blocking security requirement) — removing <c>/Encrypt</c> from the trailer is not the
    /// same as removing the object itself, and the object still carries <c>/O</c>, <c>/U</c>,
    /// <c>/OE</c>, <c>/UE</c> and <c>/Perms</c>, all of it offline-cracking material against the
    /// original document's passwords. Captured here, not recomputed from the trailer at save time,
    /// because <see cref="PurgeObjectsCachedDuringAuthentication"/> already needed the same value —
    /// this just keeps the one fact around instead of re-deriving it from a trailer that may no
    /// longer carry <c>/Encrypt</c> by the time a caller asks (see <see cref="ReconstructionPhaseB"/>,
    /// which never touches <c>/Encrypt</c>, so this stays correct there too).
    /// </summary>
    private readonly int? _encryptObjectNumber;
    private readonly Dictionary<string, CryptFilterMethod> _cryptFilterTable = new(StringComparer.Ordinal);
    private readonly bool _encryptMetadata = true;

    // Resolved from PdfReaderOptions by ReaderLimits.Resolve (see PdfReader.Open) — the same values
    // XrefParser.Parse and XrefReconstructor.Reconstruct were already handed for this read, so every
    // decode and reconstruction budget check made through this instance agrees with the caller's
    // chosen ceiling instead of a fixed constant.
    private readonly ReaderLimits _limits;

    /// <summary>
    /// The resource ceilings this document was opened with. A caller that opens a nested or
    /// embedded document from bytes found INSIDE this one — <c>VellumPdf.Conformance</c>'s
    /// recursive PDF/A validation of an embedded file is the one case in this codebase today —
    /// must pass this through to that nested <see cref="PdfReader.Open(byte[], ReaderLimits, string?)"/>
    /// call. Otherwise a caller who tightened <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> or
    /// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/> on THIS read gets the untightened
    /// 512 MiB / 8× defaults back the moment a rule opens attacker-supplied bytes it found inside the
    /// document — exactly the escape hatch the caller's tightened options were meant to close.
    /// </summary>
    internal ReaderLimits Limits => _limits;

    internal ReadOnlyMemory<byte> Bytes { get; }
    internal PdfDictionary Trailer { get; }

    /// <summary>The byte offset recorded in the last startxref.</summary>
    internal int StartXrefOffset { get; }

    /// <summary>
    /// Xref revisions in the file, oldest-first. Used by PDF/A §6.4.3-1 under-coverage analysis.
    /// A single-revision file yields a one-element list.
    /// </summary>
    internal IReadOnlyList<XrefRevision> Revisions { get; }

    /// <summary>
    /// <see langword="true"/> if <c>XrefParser.DropMembersOfFreedContainers</c> removed at least
    /// one compressed-object-stream member from the merged xref table because its container had no
    /// live entry — a self-contradictory file (see that method's doc comment). A repaired object
    /// graph like this is a best-effort guess in the same sense a reconstructed one is, so
    /// <see cref="AppendRevision"/> refuses on this flag exactly as it refuses on
    /// <see cref="WasReconstructed"/> — building a PAdES revision on top of either would hand back an
    /// artifact this library cannot reliably reopen.
    /// </summary>
    internal bool DroppedOrphanedObjectStreamMembers { get; }

    /// <summary>
    /// Whether this document's cross-reference table was rebuilt by scanning the file (ISO 32000-2
    /// Annex C.4) rather than read from the file's own <c>startxref</c> chain — see
    /// <see cref="PdfReaderOptions.AllowReconstruction"/>. Reconstruction is best-effort: it can
    /// infer the wrong catalog for a layout it doesn't fully understand, and it deliberately loses
    /// the revision history a well-formed xref would carry (<see cref="Revisions"/> is empty). A
    /// caller that needs to know whether it should trust byte-level layout claims about this
    /// document (linearization, PAdES revision boundaries) should check this first.
    /// </summary>
    public bool WasReconstructed { get; }

    private readonly DiagnosticSink _diagnostics;

    /// <summary>
    /// Conditions the reader noticed while opening or decoding this document — a rebuilt
    /// cross-reference table, a dropped orphaned object-stream member, a filter chain entry that
    /// did not resolve the way it declared itself — in the order they were observed. #385's
    /// notify-and-continue channel: something recorded here did not stop the read, unlike an
    /// exception, but is worth a caller's attention.
    /// </summary>
    /// <remarks>
    /// A live view over the reader's own list: not thread-safe, like every other collection this
    /// type exposes, and, unlike them, its contents can grow as the reader resolves more of the
    /// file (a stream is decoded lazily, on first access). Bounded by
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> —
    /// past that cap, further conditions are folded into one
    /// <see cref="PdfReaderDiagnosticCode.DiagnosticsSuppressed"/> entry rather than growing the
    /// list without limit.
    /// </remarks>
    public IReadOnlyList<PdfReaderDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Bytes actually charged during reconstruction's walk (<see cref="XrefReconstructor.Reconstruct"/>),
    /// or 0 when <see cref="WasReconstructed"/> is false. Diagnostic only, so a test can pin that a
    /// hostile file cannot blow reconstruction past a linear multiple of its own length.
    /// </summary>
    internal long ReconstructionBytesConsumed { get; }

    /// <summary>
    /// Raw object-stream body bytes charged against <see cref="ReaderLimits.MaxAggregateReconstructionDecodeBytes"/>
    /// during Phase B's container expansion (B1), or 0 when <see cref="WasReconstructed"/> is false.
    /// Charged BEFORE a container is decoded, and the charge stands even when decoding then throws
    /// (a container that turns out not to be a genuine, decodable object stream) — otherwise a file
    /// built from many such containers could retry the same aggregate budget once per container
    /// instead of spending it once, defeating the point of an aggregate cap.
    /// </summary>
    internal long ReconstructionObjStmBytesCharged { get; private set; }

    /// <summary>Total length of the PDF byte buffer.</summary>
    internal int TotalLength => Bytes.Length;

    /// <summary>The document catalog dictionary (/Root).</summary>
    public PdfDictionary Catalog { get; }

    /// <summary>
    /// The document's Standard security handler settings, or <see langword="null"/> when the
    /// document is not encrypted (no <c>/Encrypt</c> in the trailer). Never null for a document that
    /// opened successfully via a password-protected path — <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/>
    /// throws <see cref="PdfPasswordException"/> before a <see cref="PdfDocumentReader"/> exists at
    /// all when the supplied password does not authenticate.
    /// </summary>
    public PdfEncryptionInfo? Encryption { get; }

    /// <summary>All digital signatures found in the document's AcroForm, in field-tree order.</summary>
    public IReadOnlyList<PdfSignature> Signatures => _signatures ??= CollectSignatures();

    internal PdfDocumentReader(
        ReadOnlyMemory<byte> bytes,
        XrefParseResult parseResult,
        ReaderLimits limits,
        string? password = null)
    {
        Bytes = bytes;
        _limits = limits;
        _xref = parseResult.Xref;
        var trailer = parseResult.Trailer;
        Trailer = trailer;
        StartXrefOffset = parseResult.StartXrefOffset;
        Revisions = parseResult.Revisions;
        _crossReferenceStreamOffsets = parseResult.CrossReferenceStreamOffsets;
        DroppedOrphanedObjectStreamMembers = parseResult.DroppedOrphanedObjectStreamMembers;
        WasReconstructed = parseResult.WasReconstructed;
        ReconstructionBytesConsumed = parseResult.ReconstructionBytesConsumed;

        // Created before anything below gets a chance to throw, so a condition this constructor
        // itself observes — reconstruction having run, an orphaned object-stream member having
        // been dropped — is recorded even if a LATER step (authentication, Phase B, /Root
        // resolution) fails and the exception unwinds past every field assignment below this one.
        // DiagnosticSink.Diagnostics already hands back a ReadOnlyCollection wrapping its own live
        // list (see that type), so this property is that same wrapper, not a second one.
        _diagnostics = new DiagnosticSink(_limits.MaxDiagnostics);
        Diagnostics = _diagnostics.Diagnostics;

        if (WasReconstructed)
        {
            _diagnostics.Report(
                PdfReaderDiagnosticCode.XrefReconstructed,
                "The cross-reference table could not be read from its own startxref chain and was "
                + "rebuilt by scanning the file (ISO 32000-2 Annex C.4, informative).");
        }

        if (DroppedOrphanedObjectStreamMembers)
        {
            _diagnostics.Report(
                PdfReaderDiagnosticCode.OrphanedObjectStreamMembersDropped,
                "One or more compressed-object-stream members were dropped from the merged "
                + "cross-reference table because their container had no live entry.");
        }

        // Everything from here on can throw partway through an encrypted document — a bad /Encrypt
        // shape, a failed Phase B recovery, a /Root that never resolves, a catalog that isn't a
        // dictionary — and unlike Dispose (below), a throw from inside a constructor never hands a
        // caller a live instance to dispose: nothing downstream ever gets the chance to zero
        // _fileKey once it exists. Key-parity with Dispose's own ZeroMemory call is the target
        // here, not a stronger guarantee — _cache, _objStmCache and _streamCache are left alone for
        // the same reason Dispose leaves them (see its own comment): only the key itself is secret.
        try
        {
            // /Encrypt must be resolved and authenticated BEFORE anything else: Resolve() and
            // GetDecodedStreamData() key their decryption on _decryptor being set, and /Root
            // (resolved just below) is itself an encrypted object in an encrypted document.
            // Resolving /Encrypt here, with _decryptor still null, is also what keeps its own
            // strings (/O, /U, /OE, /UE) from ever being run through string decryption — see the
            // constructor's caching of this object, and DecryptObjectGraph's doc comment, for why
            // no separate guard is needed beyond this ordering.
            if (trailer.TryGet(new PdfName("Encrypt"), out var encryptRaw) && encryptRaw is not null)
            {
                var encryptObjectNumber = (encryptRaw as PdfIndirectReference)?.ObjectNumber;
                _encryptObjectNumber = encryptObjectNumber;
                var encryptDict = ResolveValue(encryptRaw) as PdfDictionary
                    ?? throw new InvalidDataException("Malformed PDF: /Encrypt does not resolve to a dictionary.");

                var setup = EncryptionSetup.Authenticate(encryptDict, trailer, password, ResolveMaybe);
                _decryptor = setup.Decryptor;
                _fileKey = setup.FileKey;
                _cryptFilterTable = setup.CryptFilterTable;
                _embeddedFileFilter = setup.EmbeddedFileFilter;
                _encryptMetadata = setup.EncryptMetadata;
                PurgeObjectsCachedDuringAuthentication(encryptObjectNumber);

                Encryption = new PdfEncryptionInfo(
                    setup.Decryptor.V, setup.Decryptor.R, setup.Cipher, setup.StringCipher,
                    setup.KeyLengthBits, setup.Permissions, setup.EncryptMetadata, setup.IsOwnerAccess);
            }

            // Phase B of reconstruction (#184): runs after authentication and before /Root is
            // resolved, since its job is to make sure Trailer actually HAS a /Root that resolves to
            // a catalog before the normal checks just below ever see it. Reachable on an encrypted
            // document since PR3 (#184): Phase A can now carry a recovered or synthesized /Encrypt
            // through to here instead of refusing outright.
            if (WasReconstructed)
                ReconstructionPhaseB(parseResult.ObjectStreamContainers, parseResult.CandidateRoots);

            if (!trailer.TryGet(PdfName.Root, out var rootObj) || rootObj is null)
                throw new InvalidDataException("Malformed PDF: trailer is missing /Root.");

            var rootResolved = ResolveValue(rootObj);
            if (rootResolved is not PdfDictionary catalog)
                throw new InvalidDataException("Malformed PDF: /Root does not resolve to a dictionary.");

            Catalog = catalog;
        }
        catch
        {
            if (_fileKey is not null)
                CryptographicOperations.ZeroMemory(_fileKey);
            throw;
        }
    }

    /// <summary>
    /// Drops everything <c>Resolve</c> cached while the decryptor was still null.
    /// </summary>
    /// <remarks>
    /// ISO 32000-1 §7.6.1 lets every non-string entry of the encryption dictionary be an indirect
    /// reference, and <c>EncryptionSetup</c> follows them — deliberately with no decryptor, so that
    /// <c>/O</c>, <c>/U</c>, <c>/OE</c> and <c>/UE</c> are never run through string decryption.
    /// <c>Resolve</c> caches what it hands back, so an entry pointing at an ordinary object
    /// (<c>/Length 2 0 R</c>, say) leaves THAT object in the cache as ciphertext, and every later
    /// reader of object 2 gets the undecrypted copy — silently, since nothing downstream can tell a
    /// cached value from a freshly decrypted one. Dropping the entries makes the next resolve
    /// re-read and decrypt normally.
    /// <para>
    /// The encryption dictionary itself is the one object that must STAY cached: its own strings are
    /// exempt (§7.6.1), and the cached copy is what stops a later resolve of that object number from
    /// decrypting them.
    /// </para>
    /// </remarks>
    private void PurgeObjectsCachedDuringAuthentication(int? encryptObjectNumber)
    {
        foreach (var objectNumber in _cache.Keys.ToArray())
        {
            if (objectNumber != encryptObjectNumber)
                _cache.Remove(objectNumber);
        }

        foreach (var objectNumber in _streamCache.Keys.ToArray())
        {
            if (objectNumber != encryptObjectNumber)
                _streamCache.Remove(objectNumber);
        }

        // Object streams too. §7.5.7 forbids the encryption dictionary itself from living in one, but
        // §7.6.1 lets its non-string VALUES be references, and one of those may point into an object
        // stream — which is then decoded with no decryptor and cached undecoded. No such document is
        // readable by anything (decoding the container needs the file key that entry is part of
        // deriving), so this closes the asymmetry rather than a reachable defect.
        _objStmCache.Clear();
    }

    // Aggregate budget for Phase B's B1, across every container reconstruction found. This bounds
    // RAW INPUT volume, not decoded output: the charge below is RawBodyLength (each container's
    // pre-decode extent, as Phase A measured it), not the size LoadObjectStream's decode later
    // produces — so ReaderLimits.MaxAggregateReconstructionDecodeBytes (512 MiB by default) caps
    // roughly that much compressed-on-disk body spread across every container, and the actual
    // decoded volume can exceed it by whatever the containers' own compression ratio is (a highly
    // compressible ObjStm body could still decode to several times this many bytes in aggregate). A
    // single container's OWN decode is separately bounded by ReaderLimits.MaxDecodedBytes; without
    // this aggregate raw-input cap, N reconstructed containers would let that per-container ceiling
    // apply N times over — unbounded in total, since reconstruction has no xref-declared /Size to
    // bound N by ahead of time. The raw-input charge, not a decoded-bytes one, is deliberate (row
    // 15): it has to be knowable BEFORE the decode attempt, so a container that turns out not to be
    // genuine still gets charged for the bytes it occupies rather than refunding itself by failing
    // to decode.

    /// <summary>
    /// Phase B of cross-reference reconstruction (#184), run once — after authentication, before
    /// <c>/Root</c> is resolved — for a document whose table was rebuilt by scanning the file rather
    /// than read from its own <c>startxref</c> chain. Phase A (<see cref="XrefReconstructor"/>)
    /// cannot check its own guess at <c>/Root</c>: doing so means resolving objects, and object
    /// resolution needs authentication and object-stream expansion to have already happened.
    /// </summary>
    private void ReconstructionPhaseB(
        IReadOnlyList<(int ObjNum, int RawBodyLength)> objectStreamContainers,
        IReadOnlyList<PdfIndirectReference> candidateRoots)
    {
        // B1: expand every object stream Phase A found so the objects packed inside become
        // resolvable. Best-effort — reconstruction is already scanning for structure the file's own
        // xref failed to describe, so a container that fails to load is skipped rather than
        // aborting the whole recovery. Each container's RAW body length is pre-charged against the
        // aggregate budget BEFORE the decode attempt, and the charge stands even when decoding then
        // throws (row 15): otherwise a file built from many bogus "ObjStm" candidates could dodge
        // the aggregate cap entirely, since each one would fail to decode and refund its own charge.
        var reconstructionObjStmBytesCharged = 0L;
        foreach (var (container, rawBodyLength) in objectStreamContainers)
        {
            if (reconstructionObjStmBytesCharged >= _limits.MaxAggregateReconstructionDecodeBytes)
                break; // Budget spent — stop expanding. Security was already decided in Phase A;
                       // this cap only bounds how much further decode work Phase B does.

            reconstructionObjStmBytesCharged += Math.Max(0, rawBodyLength);

            if (!_xref.TryGetValue(container, out var containerEntry) || containerEntry.Kind != XrefEntryKind.Uncompressed)
                continue;

            try
            {
                var (_, _, _, offsetMap) = LoadObjectStream(container, containerEntry);
                foreach (var objNum in offsetMap.Keys)
                {
                    // A real top-level header (added by the walk) is stronger evidence than a
                    // number merely packed inside a container — never overwrite one.
                    if (!_xref.ContainsKey(objNum))
                        _xref[objNum] = XrefEntry.InObjStm(container, 0); // index is bookkeeping only.
                }
            }
            catch (InvalidDataException)
            {
                // Not a genuine, decodable object stream — reconstruction is best-effort; move on.
                // The charge above stands regardless.
                _diagnostics.Report(
                    PdfReaderDiagnosticCode.ObjectStreamContainerUnreadable,
                    $"Object {container} looked like an object-stream container while reconstructing "
                    + "the cross-reference table, but could not be decoded as one.",
                    container);
            }
        }
        ReconstructionObjStmBytesCharged = reconstructionObjStmBytesCharged;

        // B2: validate, then fall back — two passes over candidateRoots (A6's evidence ranking, the
        // trailer's own recovered /Root first). Pass 1 additionally requires /Pages to resolve to a
        // /Type /Pages dictionary — stronger corroboration than the plain /Type /Catalog check pass
        // 2 falls back to when nothing clears that bar. Reconstruction is deliberately stricter here
        // than the normal path just below, which requires only that /Root resolve to a dictionary:
        // when the answer is inferred, /Type /Catalog is the cheapest corroboration available.
        foreach (var candidate in candidateRoots)
        {
            if (Resolve(candidate) is PdfDictionary d && IsCatalogType(d) && CatalogPagesResolve(d))
            {
                Trailer.Set(PdfName.Root, candidate);
                return;
            }
        }
        foreach (var candidate in candidateRoots)
        {
            if (Resolve(candidate) is PdfDictionary d && IsCatalogType(d))
            {
                Trailer.Set(PdfName.Root, candidate);
                return;
            }
        }

        // B3: last resort — a catalog packed into an object stream, whether or not something else
        // shadows its object number at the top level. Iterating every loaded container's own
        // OffsetMap (not merely the InObjectStream entries B1 added) is what lets this find a
        // catalog whose number ALSO carries a top-level definition that failed B2 above — a bare
        // number collision is otherwise indistinguishable from "the real catalog lives here", and
        // reconstruction has no revision history to say which one is newer. The first /Type
        // /Catalog member found wins and REBINDS that number's xref entry into the object stream,
        // deliberately overwriting whatever the top level had (#184; this rebind is what changes
        // resolution for a shadowed number document-wide, not just for this one lookup).
        //
        // "First" has to mean something deterministic, not "whatever order Dictionary enumeration
        // happens to produce" — insertion order for _objStmCache and for OffsetMap is an
        // implementation detail .NET makes no guarantee about, so relying on it would make which
        // catalog wins depend on incidental hashing behaviour rather than the file's own content.
        // Containers are walked by object number ascending, and each container's members by their
        // OffsetMap value ascending — the position each member actually occupies within the
        // container's decoded body, i.e. definition order inside that ObjStm, mirroring how a
        // top-level scan orders candidates by file offset rather than by object number.
        foreach (var containerObjNum in _objStmCache.Keys.Order())
        {
            var cached = _objStmCache[containerObjNum];
            var orderedMembers = cached.OffsetMap
                .OrderBy(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Select(kv => kv.Key);

            foreach (var objNum in orderedMembers)
            {
                if (TryParseObjectStreamMemberDirect(cached, objNum) is not PdfDictionary d || !IsCatalogType(d))
                    continue;

                _xref[objNum] = XrefEntry.InObjStm(containerObjNum, 0);
                _cache.Remove(objNum); // drop any wrong resolution B2 may have cached for this number.
                Trailer.Set(PdfName.Root, new PdfIndirectReference(objNum, 0));
                return;
            }
        }

        throw new InvalidDataException(
            "Malformed PDF: reconstruction could not find a usable document catalog. The file's "
            + "cross-reference table is missing or broken, and no /Root, /Type /Catalog object, or "
            + "object-stream member of that shape could be recovered by scanning.");
    }

    private static bool IsCatalogType(PdfDictionary d) => d.Get(PdfName.Type) is PdfName t && t.Equals(PdfName.Catalog);

    private bool CatalogPagesResolve(PdfDictionary catalog) =>
        catalog.Get(PdfName.Pages) is PdfIndirectReference pagesRef
        && Resolve(pagesRef) is PdfDictionary pages
        && pages.Get(PdfName.Type) is PdfName t && t.Equals(PdfName.Pages);

    /// <summary>
    /// Parses one member directly out of an already-loaded object stream's cached body, bypassing
    /// the top-level cross-reference table entirely — B3's own re-entry point, deliberately separate
    /// from <see cref="ResolveFromObjectStream"/>: that method resolves THROUGH the xref, so a
    /// member whose object number is currently shadowed by a (failed-B2) top-level entry is exactly
    /// what it cannot reach.
    /// </summary>
    private static PdfObject? TryParseObjectStreamMemberDirect(
        (byte[] Body, int First, int N, Dictionary<int, int> OffsetMap) cached, int objNum)
    {
        var (body, first, _, offsetMap) = cached;
        if (!offsetMap.TryGetValue(objNum, out var relOffset))
            return null;
        if (relOffset < 0 || (long)first + relOffset >= body.Length)
            return null;

        var absoluteOffset = first + relOffset;
        var mem = new ReadOnlyMemory<byte>(body, absoluteOffset, body.Length - absoluteOffset);
        var parser = new PdfObjectParser(mem);
        try
        {
            return parser.ParseObject();
        }
        catch (InvalidDataException)
        {
            return null;
        }
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
        ThrowIfDisposed();

        // One lookup regardless of whether the caller specifies a generation: the cache tuple
        // already carries this object's authoritative generation (see the field comment above), so
        // a warm hit never needs a second trip to _xref to check it.
        if (_cache.TryGetValue(objectNumber, out var cached))
        {
            if (generation is null || cached.Generation == generation)
                return cached.Value;

            // Reported here too, not just on the cold path below — otherwise whether this
            // condition is recorded would depend on request order: a cold mismatch reports and
            // caches nothing (this method returns before ever reaching _cache[objectNumber] = …),
            // so the SAME mismatched request made twice must still call Report both times (the
            // sink decides whether the repeat is recorded), and a caller who resolved the correct
            // generation first must still see it on a later mismatched request against the
            // now-warm cache.
            _diagnostics.Report(
                PdfReaderDiagnosticCode.ObjectGenerationMismatch,
                $"Reference asked for generation {generation}, but the cross-reference table records "
                + $"object {objectNumber} at generation {cached.Generation} (ISO 32000-2 §7.3.10).",
                objectNumber, generation);
            return null;
        }

        if (!_xref.TryGetValue(objectNumber, out var entry))
            return null;

        // XrefEntry.UnknownGeneration means the xref's generation field itself could not be parsed
        // (garbled text, or an xref-stream row whose value overflows int). The xref cannot be the
        // authority for an entry it doesn't actually have an opinion on, so this falls through to
        // the object's own header below instead of rejecting (or silently guessing 0 for) every
        // generation up front.
        var xrefIsAuthoritative = entry.Generation != XrefEntry.UnknownGeneration;
        if (generation is not null && xrefIsAuthoritative && generation != entry.Generation)
        {
            _diagnostics.Report(
                PdfReaderDiagnosticCode.ObjectGenerationMismatch,
                $"Reference asked for generation {generation}, but the cross-reference table records "
                + $"object {objectNumber} at generation {entry.Generation} (ISO 32000-2 §7.3.10).",
                objectNumber, generation);
            return null;
        }

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
                {
                    _diagnostics.Report(
                        PdfReaderDiagnosticCode.ObjectHeaderMismatch,
                        $"The cross-reference table points object {objectNumber} at an offset whose "
                        + $"own \"N G obj\" header names object {result.ObjectNumber} instead.",
                        objectNumber, generation);
                    return null;
                }

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
                if (_decryptor is not null && !IsCrossReferenceStream(objectNumber))
                    value = DecryptObjectGraph(value, objectNumber, actualGeneration);

                if (result.IsStream)
                    _streamCache.TryAdd(objectNumber, (Restamped(result.Stream!, actualGeneration), actualGeneration));
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
        ThrowIfDisposed();

        // See Resolve(int, int?) for the single-lookup reasoning and what "authoritative" means.
        if (_streamCache.TryGetValue(objectNumber, out var cached))
        {
            if (generation is null || cached.Generation == generation)
                return cached.Stream;

            // Reported here too, not just on the cold path below — see Resolve(int, int?)'s own
            // warm-cache comment for why request order must not change whether this is recorded.
            _diagnostics.Report(
                PdfReaderDiagnosticCode.ObjectGenerationMismatch,
                $"Reference asked for generation {generation}, but the cross-reference table records "
                + $"object {objectNumber} at generation {cached.Generation} (ISO 32000-2 §7.3.10).",
                objectNumber, generation);
            return null;
        }

        if (!_xref.TryGetValue(objectNumber, out var entry))
            return null;

        // Objects in object streams cannot themselves be streams.
        if (entry.Kind == XrefEntryKind.InObjectStream)
            return null;

        var xrefIsAuthoritative = entry.Generation != XrefEntry.UnknownGeneration;
        if (generation is not null && xrefIsAuthoritative && generation != entry.Generation)
        {
            // Mirrors Resolve(int, int?)'s own report for the identical condition — this method is
            // a second entry point into the same object graph, not a different one, and a caller
            // reaching a mismatched stream through here deserves the same diagnostic a caller
            // reaching it through Resolve gets.
            _diagnostics.Report(
                PdfReaderDiagnosticCode.ObjectGenerationMismatch,
                $"Reference asked for generation {generation}, but the cross-reference table records "
                + $"object {objectNumber} at generation {entry.Generation} (ISO 32000-2 §7.3.10).",
                objectNumber, generation);
            return null;
        }

        var parser = new PdfObjectParser(Bytes, CheckedOffset(entry.Offset), ResolveLength);
        var result = parser.ParseIndirectObject();

        if (result.ObjectNumber != objectNumber)
        {
            // Mirrors Resolve(int, int?)'s own report for the identical condition.
            _diagnostics.Report(
                PdfReaderDiagnosticCode.ObjectHeaderMismatch,
                $"The cross-reference table points object {objectNumber} at an offset whose "
                + $"own \"N G obj\" header names object {result.ObjectNumber} instead.",
                objectNumber, generation);
            return null;
        }

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

        var stream = Restamped(result.Stream!, actualGeneration);

        // The same decrypt walk Resolve(int, int?) runs, for the same reason and under the same
        // identity. Without it this method would be a second way into the same object that skips
        // decryption — and since it also populates _cache below, whichever of the two ran first
        // would decide for the rest of the reader's life whether that dictionary's strings are
        // plaintext or ciphertext. The Conformance package resolves streams before objects, so the
        // ciphertext ordering was the usual one. A stream reached through Resolve first is already
        // in _streamCache and returns above, so nothing is walked twice.
        if (_decryptor is not null && !IsCrossReferenceStream(objectNumber))
            DecryptObjectGraph(stream.Dictionary, objectNumber, actualGeneration);

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
    /// <see cref="PdfFilters.Decode(ParsedStream, ReaderLimits, Func{PdfObject?, PdfObject?}?, DiagnosticSink?)"/> unchanged; one
    /// that needs decrypting is wrapped in a throwaway <see cref="ParsedStream"/> carrying the
    /// decrypted bytes, never exposed outside this method.
    /// </remarks>
    internal byte[]? GetDecodedStreamData(ParsedStream stream) =>
        PdfFilters.Decode(DecryptedStreamView(stream), _limits, ResolveMaybe, _diagnostics);

    /// <summary>
    /// Returns a <see cref="ParsedStream"/> view of <paramref name="stream"/> whose body is
    /// decrypted (or, for an unencrypted document or an Identity crypt filter, unchanged) but has
    /// NOT been run through <see cref="PdfFilters"/> — i.e. the same thing <c>stream.RawBody</c>
    /// used to give every caller before #97, except correct on an encrypted document.
    ///
    /// <para>
    /// Exists because <see cref="PdfFilters.Decode(ParsedStream, ReaderLimits, Func{PdfObject?, PdfObject?}?, DiagnosticSink?)"/>
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
        ThrowIfDisposed();

        if (_decryptor is null)
            return stream;

        var method = CryptFilterResolver.ResolveStreamMethod(
            stream.Dictionary, _decryptor.StreamFilter, _cryptFilterTable, _encryptMetadata, ResolveMaybe,
            IsCrossReferenceStream(stream.ObjectNumber),
            IsDocumentMetadataStream(stream.ObjectNumber),
            _embeddedFileFilter,
            _decryptor.V >= 4);

        if (method == CryptFilterMethod.Identity)
            return stream;

        var decryptedBody = _decryptor.DecryptWithMethod(
            _fileKey!, stream.ObjectNumber, stream.Generation, stream.RawBody.Span, method);
        return new ParsedStream(stream.Dictionary, decryptedBody, stream.BodyOffset, stream.ObjectNumber, stream.Generation);
    }

    /// <summary>
    /// The parser stamps a <see cref="ParsedStream"/> with the generation from the object's own
    /// header. Where the cross-reference table disagrees and is authoritative, the table wins (#192,
    /// ISO 32000-2 §7.3.10) — and it must win for the stream too, not just the dictionary: the
    /// dictionary is decrypted in <see cref="Resolve(int, int?)"/> under the table's generation
    /// while the body is decrypted in <see cref="DecryptedStreamView"/> under whatever the stream
    /// carries, so leaving the header's value here would key the two halves of one object
    /// differently. ISO 32000-1 §7.6.2 Algorithm 1 has one identity per object, not one per half.
    /// </summary>
    private static ParsedStream Restamped(ParsedStream stream, int generation) =>
        stream.Generation == generation
            ? stream
            : new ParsedStream(stream.Dictionary, stream.RawBody, stream.BodyOffset, stream.ObjectNumber, generation);

    // The offsets XrefParser actually read cross-reference streams at. ISO 32000-1 §7.5.8.2 exempts
    // them from encryption, body and dictionary strings alike, and this is what decides it — not a
    // /Type /XRef entry, which the document's author controls and could put on an ordinary content
    // stream to have its ciphertext handed to a preflight rule unexamined.
    private readonly IReadOnlySet<long> _crossReferenceStreamOffsets;

    // The crypt filter /EFF names for embedded file streams. Null where the document declares no
    // /EFF, or below /V 4 where Table 20 makes the entry meaningless — CryptFilterResolver reads
    // that null as "an embedded file stream is an ordinary stream here" and falls through to
    // /StmF, so filling it in with /StmF's method would defeat the /V gate.
    private CryptFilterMethod? _embeddedFileFilter;

    /// <summary>
    /// Whether <paramref name="objectNumber"/> is the document's own metadata stream — the object
    /// the catalog's <c>/Metadata</c> names. ISO 32000-2 Table 21 scopes <c>/EncryptMetadata</c> to
    /// that one stream, so a page's or an XObject's metadata stays encrypted even when it is false;
    /// qpdf's <c>--cleartext-metadata</c> writes exactly that arrangement.
    /// </summary>
    private bool IsDocumentMetadataStream(int objectNumber)
    {
        // Catalog is assigned at the END of the constructor, and getting there can decode a stream:
        // /Root inside an object stream — the layout every modern producer emits — routes
        // Resolve -> LoadObjectStreamCore -> GetDecodedStreamData -> DecryptedStreamView back here
        // with Catalog still null. Answering "no" until it exists is not a compromise: the only
        // streams decoded before that point are object streams and the cross-reference stream, and
        // the metadata stream can be neither, since a stream cannot live inside an object stream
        // (ISO 32000-2 §7.5.7).
        // Still correct once #184/PR3 can reach this on a reconstructed, encrypted document: the
        // guard above returns false for everything Phase B itself decodes (object streams only —
        // ReconstructionPhaseB's own B1/B3), and Catalog is not assigned until after Phase B and
        // /Root resolution both finish, so nothing decoded before that point can be mistaken for
        // the metadata stream.
        if (Catalog is null)
            return false;

        if (!_documentMetadataResolved)
        {
            // Assigned only once the lookup has actually happened. Set first, a caller that swallowed
            // an exception from it would leave the exemption permanently and silently switched off.
            _documentMetadataObjectNumber = (Catalog.Get(_metadataKey) as PdfIndirectReference)?.ObjectNumber;
            _documentMetadataResolved = true;
        }

        return _documentMetadataObjectNumber == objectNumber;
    }

    private static readonly PdfName _metadataKey = new("Metadata");
    private bool _documentMetadataResolved;
    private int? _documentMetadataObjectNumber;

    /// <summary>
    /// Whether <paramref name="objectNumber"/> resolves, in the MERGED cross-reference table, to an
    /// offset a cross-reference stream was actually parsed at. Keyed on the offset and not the
    /// object number because the revision walk sees superseded revisions too: an incremental update
    /// may reuse the number an older revision gave its cross-reference stream, and that object is
    /// ordinary encrypted content that has to be decrypted like any other.
    /// </summary>
    private bool IsCrossReferenceStream(int objectNumber) =>
        _crossReferenceStreamOffsets.Count > 0
        && _xref.TryGetValue(objectNumber, out var entry)
        && entry.Kind == XrefEntryKind.Uncompressed
        && _crossReferenceStreamOffsets.Contains(entry.Offset);

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
    /// <c>PdfObjectRemapper.RemapStreamInPlace</c>'s reasoning): both of its callers —
    /// <see cref="Resolve(int, int?)"/> and <see cref="ResolveStream(int, int?)"/> — run it on an
    /// object graph that was JUST parsed for that one resolution and is not yet cached or shared
    /// anywhere else, so mutating it is safe and avoids allocating a full parallel copy of every
    /// dictionary and array in the document.
    /// </para>
    /// <para>
    /// <strong>Signature <c>/Contents</c> exemption.</strong> ISO 32000-1 says nothing about whether
    /// a signature dictionary's <c>/Contents</c> is exempt from string encryption — §7.6 does not
    /// mention it and §12.8 requires only that the values be direct objects.
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
    /// library's own <c>ArchiveTimestampBuilder</c> writes for a PAdES B-LTA archive timestamp (this
    /// library refuses to sign and encrypt the same document, but nothing stops another tool from
    /// encrypting one it signed — qpdf does it by default, and does NOT exempt /Contents on either
    /// of the two shapes below, so those come back as ciphertext here; the CHANGELOG entry for this
    /// work states that trade-off in full). A <c>/Type</c>-less
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

    /// <summary> Null-tolerant <see cref="ResolveValue"/> for use as a filter-chain resolver.
    /// Internal (not private) since #98: <c>ImageDecoder</c> passes this as the resolver
    /// <c>PdfFilters.DecodeForImage</c> and <c>ColorSpaceReader</c> take, the same role it already
    /// plays for <see cref="GetDecodedStreamData"/>.
    /// </summary>
    internal PdfObject? ResolveMaybe(PdfObject? obj) => obj is null ? null : ResolveValue(obj);

    /// <inheritdoc />
    /// <summary>
    /// Clears the file encryption key. The reader holds no unmanaged resources, and the object and
    /// stream caches are ordinary managed memory the collector reclaims, so the key is the only thing
    /// worth releasing explicitly — but it is worth releasing: <see cref="PdfEncryptionInfo"/> goes
    /// out of its way to expose nothing an attacker could use to skip authentication, and a key left
    /// in the managed heap for the life of the process undoes some of that.
    ///
    /// <para>
    /// The reader is unusable afterwards, and says so: resolving an object on a disposed reader
    /// throws <see cref="ObjectDisposedException"/> rather than decrypting against the zeroed key,
    /// which under RC4 would return plausible-looking garbage and report nothing. Disposing twice is
    /// not an error.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _disposed = true;

        if (_fileKey is not null)
            CryptographicOperations.ZeroMemory(_fileKey);
    }

    // Zeroing the key makes a disposed reader dangerous rather than merely useless: under RC4 a
    // resolve against an all-zero key returns garbage with no error at all, and under Flate it
    // surfaces as an inflate failure blamed on the file. Every entry point that needs the key
    // checks this first, so the caller gets the disposal error the situation actually is.
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocumentReader));
    }

    private bool _disposed;

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

        // Restamped for the same reason ResolveStream is: the container's body is decrypted under
        // the identity the ParsedStream carries, and where the object's header disagrees with the
        // cross-reference table, the table is the authority (#192). Without this the container
        // decodes correctly through ResolveStream and incorrectly here, on one document.
        var streamObj = Restamped(
            result.Stream!,
            containerEntry.Generation == XrefEntry.UnknownGeneration
                ? result.Stream!.Generation
                : containerEntry.Generation);
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
    /// <exception cref="InvalidOperationException">
    /// This document's cross-reference table was reconstructed (<see cref="WasReconstructed"/>) or
    /// repaired by dropping orphaned object-stream members
    /// (<see cref="DroppedOrphanedObjectStreamMembers"/>). Either way the object graph is a
    /// best-effort guess rather than what the file's own xref actually says, and an incremental
    /// update needs two things a guess cannot supply: <see cref="StartXrefOffset"/> feeds straight
    /// into the new revision's <c>/Prev</c>, and a reconstructed one is 0 — landing on
    /// <c>%PDF-</c>, not a real xref — while the recovered trailer's <c>/ID</c> has no reliable
    /// value to carry forward either. Building a PAdES/DSS revision on top of either would produce
    /// a file this library reports success writing and then cannot reliably reopen.
    /// </exception>
    internal byte[] AppendRevision(IReadOnlyList<(int ObjectNumber, int Generation, PdfObject Value)> objects)
    {
        if (WasReconstructed)
            throw new InvalidOperationException(
                "Cannot append a revision to a document whose cross-reference table was "
                + "reconstructed by scanning the file: StartXrefOffset is 0 (there is no real xref "
                + "for /Prev to point at) and the recovered trailer's /ID is not reliable enough to "
                + "carry into a new revision.");
        if (DroppedOrphanedObjectStreamMembers)
            throw new InvalidOperationException(
                "Cannot append a revision to a document whose cross-reference table was repaired by "
                + "dropping orphaned object-stream members: the object graph this library is about "
                + "to build on is a best-effort recovery of a self-contradictory file, not what the "
                + "original xref actually declared, so writing a new revision on top of it would "
                + "produce an artifact this library cannot reliably vouch for.");

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
        var visitedSignatureValues = new HashSet<int>();
        for (var i = 0; i < fieldsArray.Count; i++)
            CollectFieldSignatures(fieldsArray[i], sigs, visited, visitedSignatureValues, 0, inheritedFt: null);

        return sigs;
    }

    /// <summary>
    /// Walks one field-tree node. <paramref name="inheritedFt"/> is the nearest ancestor's own
    /// <c>/FT</c>, threaded down because <c>/FT</c> is itself inheritable (ISO 32000-2 §12.7.4.1):
    /// a non-terminal node can declare <c>/FT /Sig</c> once, with each kid supplying its own
    /// <c>/V</c> (the actual terminal field) and no <c>/FT</c> of its own. An earlier version of
    /// this method checked only the current node's OWN <c>/FT</c> and returned the moment it saw
    /// <c>/FT /Sig</c> — whether or not that node had a <c>/V</c> — so a signature living on such a
    /// kid was never reached: <see cref="Signatures"/> reported none, and
    /// <see cref="SaveDecrypted(Stream)"/>'s opt-in guard, which used to trust that count, missed a
    /// real signature entirely.
    /// <para>
    /// <paramref name="visitedSignatureValues"/> is a SEPARATE dedupe set from
    /// <paramref name="visited"/>, keyed on the <c>/V</c> TARGET's object number rather than the
    /// field node's own — dropping the early return above (to fix the inheritance gap this
    /// comment's first paragraph describes) means a node with its own <c>/V</c> that also has
    /// <c>/Kids</c> now falls through to descend them too, and a kid whose own <c>/V</c> names the
    /// SAME signature object (a widget-merged field repeating <c>/V</c> on both itself and its
    /// parent, say) would otherwise be recorded twice.
    /// </para>
    /// </summary>
    private void CollectFieldSignatures(
        PdfObject fieldObj, List<PdfSignature> sigs, HashSet<int> visited, HashSet<int> visitedSignatureValues,
        int depth, PdfName? inheritedFt)
    {
        if (depth > MaxFieldTreeDepth)
            return;
        if (fieldObj is PdfIndirectReference fieldRef && !visited.Add(fieldRef.ObjectNumber))
            return;

        var resolved = ResolveValue(fieldObj);
        if (resolved is not PdfDictionary field)
            return;

        // /FT may itself be an indirect reference; a node without one inherits the ancestor's.
        var ftRaw = field.Get(new PdfName("FT"));
        var ownFt = (ftRaw is not null ? ResolveValue(ftRaw) : null) as PdfName;
        var effectiveFt = ownFt ?? inheritedFt;

        if (effectiveFt is not null && effectiveFt.Value == "Sig")
        {
            var vObj = field.Get(new PdfName("V"));
            // An indirect /V is deduped by its target's object number; a direct (inline) /V has no
            // stable identity to dedupe by and is always recorded — a distinct dictionary object
            // literally embedded in this one field can't also be reached from anywhere else.
            var alreadyRecorded = vObj is PdfIndirectReference vRef && !visitedSignatureValues.Add(vRef.ObjectNumber);
            if (vObj is not null && !alreadyRecorded)
            {
                var sigDict = ResolveValue(vObj) as PdfDictionary;
                if (sigDict is not null)
                {
                    var sig = ExtractSignature(sigDict);
                    if (sig is not null)
                        sigs.Add(sig);
                }
            }
        }

        var kidsObj = field.Get(PdfName.Kids);
        if (kidsObj is not null)
        {
            var kids = ResolveValue(kidsObj);
            if (kids is PdfArray kidsArray)
            {
                for (var i = 0; i < kidsArray.Count; i++)
                    CollectFieldSignatures(kidsArray[i], sigs, visited, visitedSignatureValues, depth + 1, effectiveFt);
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
