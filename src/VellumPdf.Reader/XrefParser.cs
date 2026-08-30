// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Where a single PDF revision's cross-reference section begins, as recorded in the xref chain.
/// Oldest revision is index 0 in the <see cref="XrefParser.Parse"/> result list.
/// </summary>
internal readonly struct XrefRevision
{
    /// <summary>Byte offset of this revision's xref table or xref stream.</summary>
    public int XrefOffset { get; }

    internal XrefRevision(int xrefOffset)
    {
        XrefOffset = xrefOffset;
    }
}

/// <summary>
/// Parses cross-reference tables and streams from a PDF byte buffer
/// (ISO 32000-2 §7.5.4, §7.5.5, and §7.5.8). Supports classic xref tables,
/// cross-reference streams, and hybrid (XRefStm) files.
/// </summary>
internal sealed class XrefParser
{
    private static readonly byte[] StartxrefBytes = "startxref"u8.ToArray();
    private static readonly PdfName _encryptKey = new("Encrypt");

    /// <summary>
    /// Parses the xref table/stream chain from <paramref name="data"/>.
    /// Returns the merged xref table (newer revisions win), the newest trailer dictionary,
    /// the byte offset of the xref from the last startxref, and the revision list oldest-first.
    /// </summary>
    /// <remarks>
    /// Six elements is already one past comfortable for a tuple; #184 adds a seventh
    /// (<c>WasReconstructed</c>), and its approved plan already calls for this to become a named
    /// result type once that lands. Kept as a tuple here rather than converted early: the sole
    /// caller (<see cref="PdfReader.Open(byte[], string?)"/>) makes either choice the same
    /// one-call-site change, so there is no cost saved by doing it now.
    /// </remarks>
    public static (Dictionary<int, XrefEntry> Xref, PdfDictionary Trailer, int StartXrefOffset, IReadOnlyList<XrefRevision> Revisions, IReadOnlySet<long> CrossReferenceStreamOffsets, bool DroppedOrphanedObjectStreamMembers) Parse(
        ReadOnlyMemory<byte> data)
    {
        var startxrefOffset = FindLastStartxref(data);
        var xref = new Dictionary<int, XrefEntry>();
        // Object numbers a newer revision recorded as free. Revisions are walked newest-first below,
        // so an older revision's entry for the same object must not resurrect it via xref.TryAdd —
        // a freed object is a deletion, not a fallback to whatever the object used to be.
        //
        // This removes a recovery behaviour some producers relied on: a tool that writes a full
        // "0 N" subsection on every incremental update, marking every untouched object 'f' instead
        // of only the ones it actually freed, used to have those spurious frees silently overridden
        // by xref.TryAdd resurrecting the real (older-revision) entry. Under this stricter tracking
        // they are treated as genuine deletions and disappear instead. No real-world fixture
        // exhibiting this has been found; if one turns up, the xref-rebuild fallback #184 tracks (for
        // structurally broken xref tables) is the natural place to also recover a spuriously-freed
        // object, since both cases end with "the xref lied about this object, fall back to scanning".
        var freed = new HashSet<int>();
        // The byte OFFSETS at which cross-reference streams were actually read. ISO 32000-1
        // §7.5.8.2 exempts them from encryption, and this is the only trustworthy way to know which
        // objects those are: /Type /XRef is a key the file's author controls, so sniffing it would
        // let a document opt an ordinary stream out of decryption by mislabelling it.
        //
        // Offsets rather than object numbers, because a revision walk visits superseded revisions
        // too. An incremental update is free to reuse the object number an older revision gave its
        // cross-reference stream — for ordinary encrypted content — and a number harvested from that
        // older revision would exempt the new object for the life of the reader.
        var crossReferenceStreamOffsets = new HashSet<long>();
        var (trailer, revisions) = ParseRevisionChain(data, startxrefOffset, xref, freed, crossReferenceStreamOffsets);
        var droppedOrphanedObjectStreamMembers = DropMembersOfFreedContainers(xref);
        return (xref, trailer, startxrefOffset, revisions, crossReferenceStreamOffsets, droppedOrphanedObjectStreamMembers);
    }

    /// <summary>
    /// A container with no live entry anywhere in the merged table takes its compressed members
    /// with it. Case 2 below only guards on the MEMBER's own object number
    /// (<c>!freed.Contains(objNum)</c>), not the container's, so such a container leaves its
    /// type-2 rows sitting in the merged table, pointing at a container
    /// <see cref="XrefEntryKind.Uncompressed"/> no longer resolves. Left alone,
    /// <c>PdfDocumentReader.ResolveFromObjectStream</c> throws <c>InvalidDataException</c> for a
    /// member nobody asked to free — harsher than qpdf, which resolves such a member to null (with
    /// a warning) and keeps the document open.
    ///
    /// A single check made while a type-2 row is being added cannot fully close this: whether the
    /// row that frees a container has been seen yet depends on parse order, and that order isn't
    /// fixed. A pure xref-stream revision may place a container's own type-0 row in a later /Index
    /// subsection than its members' type-2 rows — a /Index array only has to sort its subsections
    /// in ascending order by object number (Table 17, ISO 32000-2 §7.5.8.2), which a
    /// higher-numbered container satisfies while still landing after its lower-numbered members —
    /// so the free is still sitting in this revision's own local free set, not yet folded into
    /// `freed`, when the member rows are read. So this runs once, after the whole revision chain
    /// (every revision, both classic tables and every /XRefStm) has been parsed, and sweeps the
    /// fully merged table instead of trying to catch every order a single stream could put its rows
    /// in.
    ///
    /// The test is `!xref.ContainsKey(container)` alone, not paired with
    /// `freed.Contains(container)` the way an earlier version of this method read. That pairing
    /// looked like it distinguished two things — a container this revision chain genuinely freed,
    /// versus one no revision ever mentions — but it does not: object 0 is the free-list head every
    /// document's ORIGINAL cross-reference table designates free (ISO 32000-2 §7.5.4, "the first
    /// entry in the table ... shall always be free"), and `freed` unions across the whole revision
    /// chain, so `freed.Contains(0)` is already true from the base revision on regardless of
    /// whether any later incremental update repeats that entry. The same is true of any object an
    /// ordinary incremental update deletes along the way. So `freed.Contains(container)` is true
    /// for most of the container numbers a real file ever names, and tells the two cases apart from
    /// neither. What actually
    /// reaches this sweep is narrower than either case: a CLEAN deletion frees a container and its
    /// members together (the shape ISO 32000-2 §7.5.8.4's own EXAMPLE writes), and case 2's
    /// member-level guard already drops those members one layer earlier, before this method ever
    /// sees them. So a type-2 row that does reach here with its container absent from `xref` is
    /// always a self-contradictory file — a member kept live while its container was freed, or
    /// never defined at all — and qpdf answers `null` for both, not just one. There was nothing for
    /// the two-part test to distinguish; keeping `freed.Contains(container)` only meant this file
    /// shape resolved to `null` when the container happened to be object 0 or something an
    /// incremental update also deleted, and threw `InvalidDataException` from
    /// `ResolveFromObjectStream`'s own "container N not found" check otherwise, for what is the
    /// same contradiction either way.
    ///
    /// This drops the member-nobody-freed case further than #206 otherwise reaches: a dangling
    /// type-2 reference to a container no revision ever mentions, in a file with no free entry
    /// anywhere near it, now also resolves to null rather than throwing
    /// `InvalidDataException`, because there is no longer a `freed`-based way to tell it apart from
    /// a genuinely freed container, and qpdf treats both alike.
    ///
    /// This sweep is a single pass over the merged table, not a fixed point: if the member it
    /// removes was itself, in some other row, a container — i.e. some other type-2 entry names
    /// this member's object number as ITS container — that other entry does not get re-examined
    /// and is left pointing at a container `xref` no longer holds. No fixture has been built where
    /// this matters: `ResolveFromObjectStream` already rejects a container whose own xref entry is
    /// type-2 (an object stream cannot itself be a member of another object stream — ISO 32000-2
    /// §7.5.7), so a member this sweep removes was never a legal container for anything else to
    /// begin with.
    ///
    /// Dropping the member from `xref` loses nothing a conformant reader could have recovered
    /// anyway: ISO 32000-2 §7.3.10 requires that "an indirect reference to an undefined object
    /// shall not be considered an error by a PDF processor; it shall be treated as a reference to
    /// the null object." The member's bytes lived only inside the now-absent container, so once
    /// this method runs, any surviving reference to it is exactly such a reference — and means the
    /// same null it would have meant reading the source file directly, not a new kind of failure
    /// introduced by this sweep.
    ///
    /// That is also why the removal stays sound through a rewrite rather than merely being a
    /// resolve-time convenience: <c>PdfDocumentReader.ObjectNumbers</c> is <c>_xref.Keys</c>, the
    /// same table this method edits, so a member dropped here is absent from that enumeration too
    /// — the one a future full re-serialisation (#186) would walk to decide what to emit. A file
    /// written from the post-sweep table omits the member and keeps every reference to it, which
    /// §7.3.10 makes legal and still null, so the written copy agrees with what reading the source
    /// returned instead of becoming a different kind of broken; nothing has to go scrub the kept
    /// objects for dangling references first. <c>AppendRevision</c> is unaffected by any of this
    /// today: it is purely incremental, copying the base document's bytes forward by construction,
    /// so it never re-emits an existing object, orphaned or not.
    /// </summary>
    /// <returns><see langword="true"/> if at least one type-2 entry was removed.</returns>
    private static bool DropMembersOfFreedContainers(Dictionary<int, XrefEntry> xref)
    {
        List<int>? toRemove = null;
        foreach (var (objNum, entry) in xref)
        {
            if (entry.Kind != XrefEntryKind.InObjectStream)
                continue;
            var container = entry.ObjStmObjectNumber;
            if (xref.ContainsKey(container))
                continue;
            (toRemove ??= []).Add(objNum);
        }

        if (toRemove is null)
            return false;
        foreach (var objNum in toRemove)
            xref.Remove(objNum);
        return true;
    }

    private static int FindLastStartxref(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        // ISO 32000 does not bound the distance from EOF to the last 'startxref'; files with large
        // trailers, big /ID arrays, or padding after %%EOF (some producers pad the tail to reserve
        // a byte-range window for a signature added later) place it further back than a small tail
        // would reach. 2 KiB proved too tight in practice (#105); this is still a bounded backward
        // scan, not a full-file one, so widen it generously.
        const int TailWindow = 1 << 20; // 1 MiB
        var searchStart = Math.Max(0, span.Length - TailWindow);
        var searchSpan = span[searchStart..];

        // Find the last occurrence of "startxref" in the tail of the file. Scan backward from the
        // end of the window and stop at the first hit: both directions want the same answer (the
        // occurrence nearest EOF), but scanning forward and keeping the last match — the previous
        // version of this loop — pays the full window every time regardless of how close that
        // occurrence actually is. A real file's last 'startxref' is typically tens of bytes from
        // EOF, so scanning backward turns a fixed 1 MiB cost into O(distance to the match).
        var lastFound = -1;
        for (var i = searchSpan.Length - StartxrefBytes.Length; i >= 0; i--)
        {
            if (searchSpan[i..].StartsWith(StartxrefBytes))
            {
                lastFound = i;
                break;
            }
        }

        if (lastFound < 0)
            throw new InvalidDataException(
                $"Malformed PDF: 'startxref' not found in the last {TailWindow} bytes.");

        var absolutePos = searchStart + lastFound + StartxrefBytes.Length;

        // Skip whitespace after 'startxref', then read the integer offset.
        while (absolutePos < span.Length && IsWhitespace(span[absolutePos]))
            absolutePos++;

        if (absolutePos >= span.Length || !IsDigit(span[absolutePos]))
            throw new InvalidDataException(
                "Malformed PDF: expected integer offset after 'startxref'.");

        var numStart = absolutePos;
        while (absolutePos < span.Length && IsDigit(span[absolutePos]))
            absolutePos++;

        var offsetStr = Encoding.ASCII.GetString(span[numStart..absolutePos]);
        if (!int.TryParse(offsetStr, NumberStyles.None, CultureInfo.InvariantCulture, out var xrefOffset)
            || xrefOffset < 0)
            throw new InvalidDataException(
                $"Malformed PDF: invalid startxref offset '{offsetStr}'.");

        if (xrefOffset >= data.Length)
            throw new InvalidDataException(
                $"Malformed PDF: startxref offset {xrefOffset} is beyond end of file ({data.Length} bytes).");

        return xrefOffset;
    }

    private static (PdfDictionary Trailer, IReadOnlyList<XrefRevision> Revisions) ParseRevisionChain(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref, HashSet<int> freed,
        HashSet<long> crossReferenceStreamOffsets)
    {
        var seenOffsets = new HashSet<int>();
        PdfDictionary? newestTrailer = null;
        var revisionsNewestFirst = new List<XrefRevision>();
        var anyRevisionDeclaredEncrypt = false;

        var currentOffset = xrefOffset;
        var revisionCount = 0;

        while (true)
        {
            if (!seenOffsets.Add(currentOffset))
                throw new InvalidDataException(
                    $"Malformed PDF: cycle detected in /Prev xref chain at offset {currentOffset}.");
            if (++revisionCount > 100)
                throw new InvalidDataException(
                    "Malformed PDF: xref chain exceeds 100 revisions; aborting to prevent infinite loop.");

            revisionsNewestFirst.Add(new XrefRevision(currentOffset));

            var trailer = ParseOneRevision(data, currentOffset, xref, freed, seenOffsets, crossReferenceStreamOffsets);
            newestTrailer ??= trailer;
            anyRevisionDeclaredEncrypt |= trailer.TryGet(_encryptKey, out var revisionEncrypt) && revisionEncrypt is not null;

            if (trailer.TryGet(PdfName.Prev, out var prevObj) && prevObj is PdfInteger prevInt)
            {
                // Validate the full 64-bit value before narrowing: a value such as 0x1_0000_0005
                // would wrap to a small in-range int and bypass the range check if cast first.
                var prevValue = prevInt.Value;
                if (prevValue < 0 || prevValue >= data.Length)
                    throw new InvalidDataException(
                        $"Malformed PDF: /Prev offset {prevValue} is out of range.");
                currentOffset = (int)prevValue;
            }
            else
            {
                break;
            }
        }

        // Only the newest trailer's /Encrypt is honoured — an older revision's is stale, and
        // resurrecting it would decrypt objects the newest revision wrote in the clear. But an
        // incremental update over an encrypted document has to repeat /Encrypt (ISO 32000-1 §7.5.6:
        // the trailer of each update "shall contain" the entries the document needs), so a chain
        // where an older revision declares it and the newest does not is malformed either way, and
        // there is no reading of it that recovers the content. Opening it as plaintext is the one
        // outcome that must not happen: every stream would decode to ciphertext, nothing would
        // report it, and a caller would take the noise for the document.
        if (anyRevisionDeclaredEncrypt
            && !(newestTrailer!.TryGet(_encryptKey, out var newestEncrypt) && newestEncrypt is not null))
        {
            throw new InvalidDataException(
                "Malformed PDF: an earlier revision declares /Encrypt but the newest trailer does not, "
                + "so whether the document's objects are encrypted cannot be determined.");
        }

        revisionsNewestFirst.Reverse();
        return (newestTrailer!, revisionsNewestFirst);
    }

    private static PdfDictionary ParseOneRevision(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref, HashSet<int> freed,
        HashSet<int> seenOffsets, HashSet<long> crossReferenceStreamOffsets)
    {
        var span = data.Span;

        if (xrefOffset >= data.Length)
            throw new InvalidDataException(
                $"Malformed PDF: xref offset {xrefOffset} is out of range.");

        var b = span[xrefOffset];

        // Object numbers THIS revision frees, isolated from `freed` for the duration of a single
        // classic-table or xref-stream parse so that set can't watch itself grow mid-parse. The
        // digit branch below folds its own `localFreed` back into `freed` only after
        // `ParseXrefStream` returns, not while it runs, for the same reason.
        //
        // Within one revision, though, `localFreed` IS folded into `freed` between the two halves:
        // right after the classic table, below, and again once this revision's own /XRefStm
        // returns. That fold is what makes a same-revision 'f' entry suppress a same-revision
        // /XRefStm definition of the same object. ISO 32000-2 §7.5.8.4 has the search check the
        // classic table before the stream when an entry is "not found" there, but the clause's
        // normative text never says whether a free entry counts as found. Its one EXAMPLE glosses a
        // free object as "considered missing", but on the natural reading that sentence is about
        // the dictionary entry pointing at the object (/StructTreeRoot), not the cross-reference
        // entry itself, so it does not settle the question either. That gap is the whole hinge of
        // this reading, and it is ours, not the clause's. Treating a free entry as satisfying the
        // search is the reading pdf-association/pdf-issues#237 (open) leans toward and qpdf 12.3.2
        // matches; see the fixtures README for the sourcing behind that, kept out of here so the
        // argument has one home instead of drifting out of sync across six copies.
        //
        // The clause's instruction to disregard a free entry and keep looking is scoped to one
        // found in the *previous* section: the cross-section arrangement the mechanism was actually
        // designed around, where THIS revision's stream defines the object and an *earlier* one,
        // usually the one /Prev points at, frees it — so a PDF 1.4 reader stops at the free entry
        // and a PDF 1.5 reader reaches the stream. The outcome there does not depend on
        // `localFreed`, even though that older revision's 'f' entry goes through `localFreed.Add`
        // like any other free entry: revisions are walked newest-first with `xref.TryAdd`, so by
        // the time that entry lands in `freed`, the newer revision's stream has already added its
        // own definition, and `freed` only gates a future `TryAdd` — it can't withdraw one already
        // made.
        var localFreed = new HashSet<int>();

        if (IsDigit(b))
        {
            // Cross-reference stream: "N G obj << ... >> stream ... endstream endobj"
            var streamTrailer = ParseXrefStream(data, xrefOffset, xref, freed, localFreed, crossReferenceStreamOffsets);
            freed.UnionWith(localFreed);
            return streamTrailer;
        }

        // Classic xref table
        if (xrefOffset + 4 > data.Length ||
            !span[xrefOffset..].StartsWith("xref"u8))
            throw new InvalidDataException(
                $"Malformed PDF: expected 'xref' keyword at offset {xrefOffset}.");

        var trailer = ParseClassicXrefTable(data, xrefOffset, xref, freed, localFreed);

        // Fold this revision's classic-table frees into `freed` now, before the /XRefStm block
        // below runs — not after it, which was this method's behaviour before
        // pdf-association/pdf-issues#237 (see the comment on `localFreed` above). A same-revision
        // 'f' entry must be visible to the stream's own `freed.Contains` guard so it suppresses a
        // same-revision /XRefStm definition of the same object.
        freed.UnionWith(localFreed);

        // Hybrid: if the classic trailer has /XRefStm, also parse that xref stream.
        // Classic entries win, so we've already added them — the stream entries are added with
        // TryAdd and skipped if already present, or if `freed` already names the object (which, as
        // of the fold above, includes this revision's own classic-table frees, not just a newer
        // revision's deletions).
        if (trailer.TryGet(new PdfName("XRefStm"), out var xrefStmObj) && xrefStmObj is PdfInteger xrefStmInt)
        {
            // Validate as a 64-bit value before narrowing (see /Prev above): casting first would let
            // an offset like 0x1_0000_0005 wrap to a small in-range int and slip past the guard.
            var stmValue = xrefStmInt.Value;
            if (stmValue < 0 || stmValue >= data.Length)
                throw new InvalidDataException(
                    $"Malformed PDF: /XRefStm offset {stmValue} is out of range.");
            var stmOffset = (int)stmValue;
            // Avoid cycling into an already-processed offset, and record this one so a later /Prev
            // revision pointing at the same stream does not re-parse it.
            if (seenOffsets.Add(stmOffset))
            {
                // #192 threads the per-revision free sets through; #183 needs the dictionary the
                // stream returns. ISO 32000-2 §7.5.8.4 permits a producer to put /Encrypt on the
                // XRefStm dictionary instead of the classic trailer — the only place a hybrid file
                // can legally put it, since a pre-1.5 reader falling back to the classic table would
                // otherwise never see it at all. PdfDocumentReader only ever reads /Encrypt off the
                // dictionary XrefParser.Parse returns as `Trailer` (the classic one, for a hybrid
                // file — see ParseOneRevision below), so it has to be merged onto that dictionary
                // here for a hybrid+encrypted file to decrypt at all. The classic trailer's own
                // /Encrypt wins if both happen to declare one — that would be a malformed producer
                // either way, and the classic trailer is what every pre-1.5 (and this) reader treats
                // as authoritative for every other trailer key.
                var xrefStmDict = ParseXrefStream(data, stmOffset, xref, freed, localFreed, crossReferenceStreamOffsets);
                if (!trailer.TryGet(_encryptKey, out _) && xrefStmDict.TryGet(_encryptKey, out var stmEncrypt) && stmEncrypt is not null)
                    trailer.Set(_encryptKey, stmEncrypt);
            }
        }

        // Not a no-op even after the fold above: this revision's own /XRefStm, if present, may
        // have added its own type-0 (free) entries to `localFreed` (see ParseXrefStream), and an
        // older /Prev revision still needs those in `freed` to keep it from resurrecting them.
        freed.UnionWith(localFreed);
        return trailer;
    }

    // ── Classic xref table ───────────────────────────────────────────────────

    private static PdfDictionary ParseClassicXrefTable(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref, HashSet<int> freed,
        HashSet<int> localFreed)
    {
        var span = data.Span;
        var pos = xrefOffset + 4; // skip 'xref'

        while (true)
        {
            while (pos < span.Length && IsWhitespace(span[pos]))
                pos++;

            if (pos >= span.Length)
                throw new InvalidDataException("Malformed PDF: unexpected end of xref table.");

            if (pos + 7 <= span.Length && span[pos..].StartsWith("trailer"u8))
            {
                pos += 7;
                break;
            }

            var (firstObjNum, afterFirst) = ReadInt(span, pos);
            pos = afterFirst;

            while (pos < span.Length && IsWhitespace(span[pos]))
                pos++;

            var (count, afterCount) = ReadInt(span, pos);
            pos = afterCount;

            // A subsection cannot declare more 20-byte entries than the file could possibly hold;
            // reject a pathological count up front (also prevents firstObjNum + count overflow).
            if (count < 0 || firstObjNum < 0 || (long)count * 20 > span.Length || (long)firstObjNum + count > int.MaxValue)
                throw new InvalidDataException(
                    $"Malformed PDF: xref subsection ({firstObjNum} {count}) is out of range.");

            while (pos < span.Length && span[pos] is not 10 and not 13)
                pos++;
            if (pos < span.Length && span[pos] == 13) pos++;
            if (pos < span.Length && span[pos] == 10) pos++;

            for (var i = 0; i < count; i++)
            {
                if (pos + 20 > span.Length)
                    throw new InvalidDataException(
                        $"Malformed PDF: xref entry {i} in subsection starting at obj {firstObjNum} is truncated.");

                var entry = span.Slice(pos, 20);
                var objType = (char)entry[17];
                var objNum = firstObjNum + i;

                if (objType == 'n')
                {
                    var offsetStr = Encoding.ASCII.GetString(entry[..10]);
                    if (!int.TryParse(offsetStr, NumberStyles.None, CultureInfo.InvariantCulture,
                            out var objOffset))
                        throw new InvalidDataException(
                            $"Malformed PDF: bad xref entry offset '{offsetStr}' for obj {objNum}.");

                    // Lenient, unlike the offset field above: the generation field was never read
                    // before this PR, so a file that is sloppy here (space-padded rather than
                    // zero-padded, e.g.) used to open cleanly and must keep opening cleanly. Allow
                    // surrounding whitespace and a sign. But a value that is still unparseable,
                    // negative, or exceeds the ISO 32000-2 §7.5.4 ceiling of 65535 is not the same
                    // thing as a legitimate generation 0 — guessing 0 would make an object at, say,
                    // generation 3 silently unresolvable at every generation instead of merely
                    // failing the (correct) mismatch it would otherwise hit, and letting a value
                    // above 65535 through would mean this field can hold a generation no reference
                    // token can ever legitimately carry (PdfObjectParser.ParseGenerationLenient
                    // saturates at the same ceiling), reopening the aliasing risk the object-number
                    // half of this parser is careful to avoid. Record it as unknown instead and let
                    // PdfDocumentReader fall back to the object's own header, which this field
                    // cannot corrupt.
                    var genStr = Encoding.ASCII.GetString(entry[11..16]);
                    if (!int.TryParse(genStr, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var objGeneration) || objGeneration is < 0 or > 65535)
                        objGeneration = XrefEntry.UnknownGeneration;

                    // Newest revision wins: skip an object a newer revision already freed.
                    if (!freed.Contains(objNum))
                        xref.TryAdd(objNum, XrefEntry.Uncompressed(objOffset, objGeneration));
                }
                else if (objType == 'f')
                {
                    // A free entry means the object does not exist per this table. Record it in
                    // `localFreed`, not `freed`, so an earlier 'f' entry in this same table cannot
                    // prospectively suppress a later 'n' entry for the same object number: `freed`
                    // only gates a `TryAdd` that has not run yet, and both entries are read before
                    // ParseOneRevision folds `localFreed` into `freed` (see its comment). That fold
                    // happens as soon as this table finishes, before this revision's own /XRefStm is
                    // parsed, so this free entry does suppress a same-revision stream definition of
                    // the object, and an older /Prev revision's 'n' entry for the same number is
                    // suppressed too.
                    localFreed.Add(objNum);
                }

                pos += 20;
            }
        }

        var parser = new PdfObjectParser(data, pos);
        var trailerObj = parser.ParseObject();
        if (trailerObj is not PdfDictionary trailerDict)
            throw new InvalidDataException(
                $"Malformed PDF: expected dictionary after 'trailer', got {trailerObj.GetType().Name}.");

        return trailerDict;
    }

    // ── Cross-reference stream ───────────────────────────────────────────────

    private static PdfDictionary ParseXrefStream(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref, HashSet<int> freed,
        HashSet<int> localFreed, HashSet<long> crossReferenceStreamOffsets)
    {
        var parser = new PdfObjectParser(data, xrefOffset);
        var result = parser.ParseIndirectObject();

        if (result.Stream is null)
            throw new InvalidDataException(
                $"Malformed PDF: expected xref stream object at offset {xrefOffset}.");

        var streamObj = result.Stream;
        var dict = streamObj.Dictionary;
        crossReferenceStreamOffsets.Add(xrefOffset);

        // Decode the stream body (typically FlateDecode, but use full chain for robustness)
        var decodeResult = PdfFilters.Decode(streamObj);
        if (decodeResult is null)
            throw new InvalidDataException(
                "Malformed PDF: xref stream uses an image filter that cannot be decoded.");
        var decoded = decodeResult;

        // /W [w1 w2 w3] — field widths
        if (dict.Get(new PdfName("W")) is not PdfArray wArr || wArr.Count != 3)
            throw new InvalidDataException("Malformed PDF: xref stream missing valid /W array.");

        // Validate each width as a 64-bit value BEFORE narrowing to int: a value like 0x1_0000_0008
        // would wrap to a valid-looking 8 if cast first. Each field is read big-endian into a long,
        // so a width must be 0..8; negative widths would produce silently-wrong offsets.
        var w1L = GetInt(wArr[0]);
        var w2L = GetInt(wArr[1]);
        var w3L = GetInt(wArr[2]);
        if (w1L is < 0 or > 8 || w2L is < 0 or > 8 || w3L is < 0 or > 8)
            throw new InvalidDataException("Malformed PDF: xref stream /W field width out of range.");
        int w1 = (int)w1L, w2 = (int)w2L, w3 = (int)w3L;
        var rowSize = w1 + w2 + w3;
        if (rowSize <= 0)
            throw new InvalidDataException("Malformed PDF: xref stream /W row size is zero.");

        // /Size
        if (dict.Get(PdfName.Size) is not PdfInteger sizeObj)
            throw new InvalidDataException("Malformed PDF: xref stream missing /Size.");
        if (sizeObj.Value is < 0 or > int.MaxValue)
            throw new InvalidDataException($"Malformed PDF: xref stream /Size {sizeObj.Value} is out of range.");
        var streamSize = (int)sizeObj.Value;

        // /Index — pairs of (firstObjNum, count); default is [0 Size]
        var indexPairs = new List<(int First, int Count)>();
        if (dict.Get(new PdfName("Index")) is PdfArray indexArr)
        {
            if (indexArr.Count % 2 != 0)
                throw new InvalidDataException("Malformed PDF: xref stream /Index array has odd element count.");
            for (var i = 0; i < indexArr.Count; i += 2)
            {
                // Validate as 64-bit before narrowing: a value such as 0x1_0000_0000 would wrap to a
                // small in-range int and slip past the guard (producing bogus object numbers).
                var first = GetInt(indexArr[i]);
                var count = GetInt(indexArr[i + 1]);
                if (first is < 0 or > int.MaxValue || count is < 0 or > int.MaxValue || first + count > int.MaxValue)
                    throw new InvalidDataException("Malformed PDF: xref stream /Index subsection is out of range.");
                indexPairs.Add(((int)first, (int)count));
            }
        }
        else
        {
            indexPairs.Add((0, streamSize));
        }

        var pos = 0;
        foreach (var (firstObj, count) in indexPairs)
        {
            for (var i = 0; i < count; i++)
            {
                if (pos + rowSize > decoded.Length)
                    throw new InvalidDataException(
                        "Malformed PDF: xref stream body is truncated.");

                var type = w1 > 0 ? ReadBigEndian(decoded, pos, w1) : 1; // default type is 1
                var field2 = w2 > 0 ? ReadBigEndian(decoded, pos + w1, w2) : 0;
                var field3 = w3 > 0 ? ReadBigEndian(decoded, pos + w1 + w2, w3) : 0;
                pos += rowSize;

                var objNum = firstObj + i;
                switch (type)
                {
                    case 1:
                        // field2 is a byte offset into the file, field3 the generation. A /W width
                        // up to 8 bytes can hold a generation far past both int range and the ISO
                        // 32000-2 §7.5.4 ceiling of 65535 — lenient here, unlike the offset check just
                        // below, since this field was never read before this PR and a row that is
                        // merely odd here must not newly turn a previously openable document into a
                        // hard failure. Capped at 65535, not merely at int range: letting a larger
                        // but in-range value through would mean this field could hold a generation no
                        // reference token can ever legitimately carry (PdfObjectParser.
                        // ParseGenerationLenient saturates at the same ceiling), reopening the exact
                        // aliasing risk the object-number half of that parser is careful to avoid —
                        // and, in the other direction, a generation this large can never be written
                        // back out through the 5-digit xref field IncrementalCrossReferenceBuilder
                        // uses for an incremental update. Recorded as unknown, not clamped to 0 (see
                        // the classic-table generation field above for why guessing is worse than
                        // admitting the xref doesn't know).
                        var objGeneration = field3 is >= 0 and <= 65535
                            ? (int)field3
                            : XrefEntry.UnknownGeneration;
                        if (field2 >= 0 && field2 < data.Length && !freed.Contains(objNum))
                            xref.TryAdd(objNum, XrefEntry.Uncompressed(field2, objGeneration));
                        break;
                    case 2:
                        // field2 = container object number, field3 = index within it; a /W width up
                        // to 8 bytes can exceed int range, so validate before narrowing.
                        if (field2 is < 0 or > int.MaxValue || field3 is < 0 or > int.MaxValue)
                            throw new InvalidDataException(
                                "Malformed PDF: xref stream type-2 entry field is out of range.");
                        if (!freed.Contains(objNum))
                            xref.TryAdd(objNum, XrefEntry.InObjStm((int)field2, (int)field3));
                        break;
                    case 0:
                        // free entry: the object does not exist per this stream. Recorded in
                        // `localFreed`, matching the classic-table 'f' handling above (see its
                        // comment) — a hybrid file's classic table, parsed first for this revision,
                        // already got the chance to add this object if it defines it live.
                        localFreed.Add(objNum);
                        break;
                    default:
                        // unknown type — ignore per spec (future compatibility)
                        break;
                }
            }
        }

        return dict;
    }

    private static long ReadBigEndian(byte[] data, int pos, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++)
            value = (value << 8) | data[pos + i];
        return value;
    }

    private static long GetInt(PdfObject obj)
    {
        if (obj is PdfInteger pi) return pi.Value;
        throw new InvalidDataException($"Expected integer in xref stream, got {obj.GetType().Name}.");
    }

    private static (int Value, int NextPos) ReadInt(ReadOnlySpan<byte> span, int pos)
    {
        if (pos >= span.Length || !IsDigit(span[pos]))
            throw new InvalidDataException(
                $"Malformed PDF: expected integer at offset {pos} in xref table.");

        var start = pos;
        while (pos < span.Length && IsDigit(span[pos]))
            pos++;

        var s = Encoding.ASCII.GetString(span[start..pos]);
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"Malformed PDF: could not parse integer '{s}'.");

        return (value, pos);
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}
