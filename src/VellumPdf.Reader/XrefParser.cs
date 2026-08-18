// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// The byte boundaries of a single PDF revision as recorded in the xref chain.
/// Oldest revision is index 0 in the <see cref="XrefParser.Parse"/> result list.
/// </summary>
internal readonly struct XrefRevision
{
    /// <summary>Byte offset of this revision's xref table or xref stream.</summary>
    public int XrefOffset { get; }

    /// <summary>The startxref value that pointed to this revision's xref table or stream.</summary>
    public int StartXrefOffset { get; }

    internal XrefRevision(int xrefOffset, int startXrefOffset)
    {
        XrefOffset = xrefOffset;
        StartXrefOffset = startXrefOffset;
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

    /// <summary>
    /// Parses the xref table/stream chain from <paramref name="data"/>.
    /// Returns the merged xref table (newer revisions win), the newest trailer dictionary,
    /// the byte offset of the xref from the last startxref, the revision list oldest-first, and
    /// whether the table had to be reconstructed by scanning the file (#184).
    /// </summary>
    public static (Dictionary<int, XrefEntry> Xref, PdfDictionary Trailer, int StartXrefOffset, IReadOnlyList<XrefRevision> Revisions, bool WasReconstructed) Parse(
        ReadOnlyMemory<byte> data, bool allowReconstruction)
    {
        if (TryParseFromStartxref(data, out var result))
            return (result.Xref, result.Trailer, result.StartXrefOffset, result.Revisions, false);

        // startxref is missing, its offset can't be used, or the xref it points at isn't
        // recognisable as a classic table or a cross-reference stream. Reconstruction is
        // opt-in (PdfReaderOptions.AllowReconstruction) rather than automatic: it is a
        // best-effort recovery over structure the file's own xref has already failed to
        // describe correctly, and can synthesize the wrong catalog for a layout it doesn't
        // fully understand (an object packed into an /ObjStm, a document carrying another PDF
        // as an embedded file) or silently open a file whose /Encrypt it failed to preserve.
        // A caller that hasn't asked for that trade-off gets the same hard failure as before.
        if (!allowReconstruction)
            throw new InvalidDataException(
                "Malformed PDF: startxref is missing, unusable, or does not point at a "
                + "recognisable xref table or stream. Pass "
                + "PdfReaderOptions { AllowReconstruction = true } to PdfReader.Open to recover "
                + "a document like this by scanning the file for object headers.");

        // Rebuild the table by scanning for "N G obj" headers — the recovery ISO 32000-2 §7.5.6
        // describes for a damaged cross-reference section — and recover the trailer separately.
        var reconstructed = ReconstructXref(data);
        return (reconstructed.Xref, reconstructed.Trailer, reconstructed.StartXrefOffset,
            reconstructed.Revisions, true);
    }

    private static bool TryParseFromStartxref(
        ReadOnlyMemory<byte> data,
        out (Dictionary<int, XrefEntry> Xref, PdfDictionary Trailer, int StartXrefOffset, IReadOnlyList<XrefRevision> Revisions) result)
    {
        result = default;

        int startxrefOffset;
        try
        {
            startxrefOffset = FindLastStartxref(data);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        // Confirm the offset actually leads somewhere recognisable — a classic "xref" keyword, or
        // an "N G obj" header a cross-reference stream could start with — WITHOUT running any of
        // the deep validation that follows (field widths, /Index ranges, offset bounds, and so on).
        // Those are legitimate hostile-input guards with their own regression coverage; catching
        // their InvalidDataException here would silently mask a rejected crafted file behind
        // reconstruction instead of the guard's own error. Only the shape of the header — not
        // whether what follows it is well-formed — decides whether this offset is "usable".
        if (!LooksLikeXrefAt(data, startxrefOffset))
            return false;

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
        // exhibiting this has been found; if one turns up, PR #193's xref-rebuild fallback (for
        // structurally broken xref tables) is the natural place to also recover a spuriously-freed
        // object, since both cases end with "the xref lied about this object, fall back to scanning".
        var freed = new HashSet<int>();
        var (trailer, revisions) = ParseRevisionChain(data, startxrefOffset, xref, freed);
        result = (xref, trailer, startxrefOffset, revisions);
        return true;
    }

    private static bool LooksLikeXrefAt(ReadOnlyMemory<byte> data, int offset)
    {
        var span = data.Span;
        if (offset < 0 || offset >= span.Length)
            return false;

        if (offset + 4 <= span.Length && span[offset..].StartsWith("xref"u8))
            return true;

        if (!IsDigit(span[offset]))
            return false;

        // A cross-reference stream begins "N G obj"; confirm that shape only.
        var p = offset;
        while (p < span.Length && IsDigit(span[p])) p++;
        var wsMark = p;
        while (p < span.Length && IsWhitespace(span[p])) p++;
        if (p == wsMark) return false;
        var genStart = p;
        while (p < span.Length && IsDigit(span[p])) p++;
        if (p == genStart) return false;
        wsMark = p;
        while (p < span.Length && IsWhitespace(span[p])) p++;
        if (p == wsMark) return false;
        return p + 3 <= span.Length && span[p] == (byte)'o' && span[p + 1] == (byte)'b' && span[p + 2] == (byte)'j';
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
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref, HashSet<int> freed)
    {
        var seenOffsets = new HashSet<int>();
        PdfDictionary? newestTrailer = null;
        var revisionsNewestFirst = new List<XrefRevision>();

        var currentOffset = xrefOffset;
        var startxrefForCurrent = xrefOffset;
        var revisionCount = 0;

        while (true)
        {
            if (!seenOffsets.Add(currentOffset))
                throw new InvalidDataException(
                    $"Malformed PDF: cycle detected in /Prev xref chain at offset {currentOffset}.");
            if (++revisionCount > 100)
                throw new InvalidDataException(
                    "Malformed PDF: xref chain exceeds 100 revisions; aborting to prevent infinite loop.");

            revisionsNewestFirst.Add(new XrefRevision(currentOffset, startxrefForCurrent));

            var trailer = ParseOneRevision(data, currentOffset, xref, freed, seenOffsets);
            newestTrailer ??= trailer;

            // Check for unsupported features.
            if (trailer.Get(new PdfName("Encrypt")) is not null)
                throw new UnsupportedPdfFeatureException(
                    "Encryption is not supported yet (see VellumPdf issue #97).");

            if (trailer.TryGet(PdfName.Prev, out var prevObj) && prevObj is PdfInteger prevInt)
            {
                // Validate the full 64-bit value before narrowing: a value such as 0x1_0000_0005
                // would wrap to a small in-range int and bypass the range check if cast first.
                var prevValue = prevInt.Value;
                if (prevValue < 0 || prevValue >= data.Length)
                    throw new InvalidDataException(
                        $"Malformed PDF: /Prev offset {prevValue} is out of range.");
                startxrefForCurrent = (int)prevValue;
                currentOffset = (int)prevValue;
            }
            else
            {
                break;
            }
        }

        revisionsNewestFirst.Reverse();
        return (newestTrailer!, revisionsNewestFirst);
    }

    private static PdfDictionary ParseOneRevision(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref, HashSet<int> freed,
        HashSet<int> seenOffsets)
    {
        var span = data.Span;

        if (xrefOffset >= data.Length)
            throw new InvalidDataException(
                $"Malformed PDF: xref offset {xrefOffset} is out of range.");

        var b = span[xrefOffset];

        // Object numbers THIS revision frees. Kept apart from `freed` (deletions carried in from a
        // newer revision) until both halves of a hybrid file have been parsed: a classic-table 'f'
        // entry commonly exists precisely because the real definition lives in this same revision's
        // /XRefStm (ISO 32000-2 §7.5.8.4) — the object is free to a reader that only understands
        // classic tables, not free in this revision as a whole. A *later* /Prev revision must still
        // see the deletion, so `localFreed` is folded into `freed` only once both halves have had
        // their chance to add the object back to `xref`.
        var localFreed = new HashSet<int>();

        if (IsDigit(b))
        {
            // Cross-reference stream: "N G obj << ... >> stream ... endstream endobj"
            var streamTrailer = ParseXrefStream(data, xrefOffset, xref, freed, localFreed);
            freed.UnionWith(localFreed);
            return streamTrailer;
        }

        // Classic xref table
        if (xrefOffset + 4 > data.Length ||
            !span[xrefOffset..].StartsWith("xref"u8))
            throw new InvalidDataException(
                $"Malformed PDF: expected 'xref' keyword at offset {xrefOffset}.");

        var trailer = ParseClassicXrefTable(data, xrefOffset, xref, freed, localFreed);

        // Hybrid: if the classic trailer has /XRefStm, also parse that xref stream.
        // Classic entries win, so we've already added them — the stream entries are added
        // with TryAdd and will be skipped if already present. Suppressed only by `freed` (a newer
        // revision's deletion), never by `localFreed`: the classic table's own 'f' entries above
        // must not block this same revision's stream from resolving the object (see the comment
        // on `localFreed` above).
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
                // stream returns.
                var xrefStmDict = ParseXrefStream(data, stmOffset, xref, freed, localFreed);

                // #183: in a hybrid-reference file the classic trailer above is the one callers see,
                // but /Encrypt is only required to be *reachable* — ISO 32000-2 §7.5.8.4 permits a
                // producer to put it on the XRefStm dictionary instead. Missing this let an encrypted
                // hybrid file fall through as if it were plain, producing garbage rather than the
                // clean UnsupportedPdfFeatureException every other /Encrypt path throws.
                if (xrefStmDict.Get(new PdfName("Encrypt")) is not null)
                    throw new UnsupportedPdfFeatureException(
                        "Encryption is not supported yet (see VellumPdf issue #97).");
            }
        }

        freed.UnionWith(localFreed);
        return trailer;
    }

    // ── Reconstruction (startxref missing or broken) ─────────────────────────

    // Bounds worst-case reconstruction cost independent of file size: a hostile file can pack in
    // an unbounded number of coincidental "trailer"/"/Catalog" occurrences, so both the number of
    // candidates tried and the cost of trying one are capped (#184).
    private const int MaxReconstructionCandidates = 256;
    private const int MaxTrailerDictScanBytes = 1 << 16;

    private static (Dictionary<int, XrefEntry> Xref, PdfDictionary Trailer, int StartXrefOffset, IReadOnlyList<XrefRevision> Revisions)
        ReconstructXref(ReadOnlyMemory<byte> data)
    {
        var (xref, catalogObjNum) = ScanForObjectHeaders(data);
        if (xref.Count == 0)
            throw new InvalidDataException(
                "Malformed PDF: startxref is missing or unusable, and no 'N G obj' object headers "
                + "were found to reconstruct the cross-reference table from.");

        var trailer = RecoverTrailer(data, xref, catalogObjNum);

        // The startxref-driven path checks /Encrypt on the trailer ParseRevisionChain returns;
        // reconstruction bypasses that path entirely; so repeat the check here rather than silently
        // parsing an encrypted file as if it were not (the same defect #183 fixes for /XRefStm).
        if (trailer.Get(EncryptKey) is not null)
            throw new UnsupportedPdfFeatureException(
                "Encryption is not supported yet (see VellumPdf issue #97).");

        // A reconstructed file has no reliable revision history — the xref chain that would
        // normally describe it is exactly what's missing or broken. An earlier version of this
        // reported a single (0, 0) revision here to "look well-formed rather than an empty list",
        // but that sentinel is strictly worse than an empty one for the two rules that consume
        // Revisions: ObjectLayoutRule reads Revisions.Count == 0 as "no revision info; check every
        // object" (falling back to int.MaxValue), but the (0, 0) sentinel made every object's
        // offset satisfy `offset >= newestXrefOffset(0)` and skipped the §6.1.9 endobj-EOL check
        // entirely; SignatureRule's §6.4.3-1 gap-exemption loop never sees a real "a later revision
        // occupies the gap" case either way, so an empty list changes nothing there — which is the
        // conservative, correct default: reconstruction has no trustworthy revision chain to grant
        // that exemption from in the first place.
        return (xref, trailer, 0, []);
    }

    // Caps the number of candidates whose /Type is checked for "/ObjStm" during reconstruction's
    // object-stream pass, and (independently) the byte cost the underlying decode can incur is
    // already bounded by PdfFilters.MaxDecodedBytes. Reused from the trailer-scan budget: both
    // guard the same class of risk (a hostile file inflating how much reconstruction-only work
    // gets attempted).
    private static readonly PdfName ObjStmType = new("ObjStm");

    /// <summary>
    /// Scans the whole buffer for "N G obj" headers and records, for each object number, the
    /// header written last in the file — later definitions (including a later incremental-update
    /// revision) override earlier ones, the same rule a well-formed /Prev chain already applies.
    /// A single linear pass: every candidate that fails partway advances past only the bytes it
    /// actually consumed, so a hostile file cannot force quadratic rescanning.
    /// </summary>
    /// <remarks>
    /// A candidate that parses as a genuine indirect object is skipped over using the parser's own
    /// notion of where it ends, rather than continuing the raw byte scan through its content. This
    /// matters most for a stream: its binary body could itself contain "N G obj"-shaped bytes —
    /// most dangerously a whole PDF stored as an <c>/EmbeddedFile</c>, whose own "1 0 obj" would
    /// otherwise be picked up as if it were top-level content and, being later in the file, win
    /// under the last-definition-wins rule above (#184 C3). A candidate that fails to parse as a
    /// genuine object (a coincidental digit run) still gets a best-effort entry, matching the
    /// previous, simpler behaviour, and the scan continues past just the header.
    /// <para/>
    /// A candidate whose dictionary declares <c>/Type /ObjStm</c> is queued and, once the whole
    /// buffer has been scanned, decoded so the objects packed inside it also become resolvable —
    /// without this, a modern xref-stream-plus-object-stream file's own catalog is usually
    /// compressed and therefore invisible to this scan, and the <c>/Type /Catalog</c> fallback in
    /// <see cref="RecoverTrailer"/> would find the literal bytes inside the container's own
    /// (undecoded) body and misidentify the container itself as the catalog (#184 C2).
    /// </remarks>
    private static (Dictionary<int, XrefEntry> Xref, int? CatalogObjNum) ScanForObjectHeaders(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        var xref = new Dictionary<int, XrefEntry>();
        var objStmContainers = new List<int>();
        int? catalogObjNum = null;
        var i = 0;

        while (i < span.Length)
        {
            if (!IsDigit(span[i]) || (i > 0 && !IsWhitespace(span[i - 1])))
            {
                i++;
                continue;
            }

            var start = i;
            var p = i;
            while (p < span.Length && IsDigit(span[p])) p++;
            var digitEnd = p;
            if (digitEnd - start > 10) { i = p; continue; }

            var wsMark = p;
            while (p < span.Length && IsWhitespace(span[p])) p++;
            if (p == wsMark) { i = start + 1; continue; }

            var genStart = p;
            while (p < span.Length && IsDigit(span[p])) p++;
            if (p == genStart || p - genStart > 10) { i = start + 1; continue; }
            var genEnd = p;

            wsMark = p;
            while (p < span.Length && IsWhitespace(span[p])) p++;
            if (p == wsMark) { i = start + 1; continue; }

            if (p + 3 > span.Length || span[p] != (byte)'o' || span[p + 1] != (byte)'b' || span[p + 2] != (byte)'j')
            {
                i = start + 1;
                continue;
            }
            var afterObj = p + 3;
            if (afterObj < span.Length && !IsDelimiterOrWhitespace(span[afterObj]))
            {
                i = start + 1;
                continue;
            }

            if (!TryParseBoundedInt(span[start..digitEnd], out var objNum))
            {
                i = afterObj;
                continue;
            }

            // Recover the generation from the header too. Reconstruction runs precisely when the
            // cross-reference table is unusable, so the header is the only authority left; writing
            // 0 here would make a legitimately nonzero-generation object unresolvable at every
            // generation (#121). Out of the ISO 32000-2 §7.5.4 range is recorded as unknown, on
            // the same reasoning as the xref-side generation fields above.
            var generation = TryParseBoundedInt(span[genStart..genEnd], out var g) && g <= 65535
                ? g
                : XrefEntry.UnknownGeneration;

            var skipTo = afterObj;
            try
            {
                var parser = new PdfObjectParser(data, start);
                var result = parser.ParseIndirectObject();
                var dict = result.IsStream ? result.Stream!.Dictionary : result.Value as PdfDictionary;
                if (result.IsStream && dict?.Get(PdfName.Type) is PdfName typeName && typeName.Equals(ObjStmType))
                    objStmContainers.Add(objNum);
                else if (dict?.Get(PdfName.Type) is PdfName catalogTypeName && catalogTypeName.Equals(PdfName.Catalog))
                    catalogObjNum = objNum; // last one found wins, matching the xref's own rule

                // For a stream, parser.Position lands right after 'endstream' (ParseIndirectObject
                // does not consume 'endobj' for a stream — see ParseStreamBody), which is exactly
                // what's needed here: everything up to that point is this object's own content and
                // must not be rescanned as if it were top-level bytes.
                if (parser.Position > skipTo)
                    skipTo = parser.Position;
            }
            catch (InvalidDataException)
            {
                // Not a genuine object at this offset (a coincidental digit run) — record a
                // best-effort entry as before, and continue past just the header since the true
                // extent of whatever this actually is isn't known.
            }

            xref[objNum] = XrefEntry.Uncompressed(start, generation);
            i = skipTo;
        }

        foreach (var containerObjNum in objStmContainers.Count > MaxReconstructionCandidates
            ? objStmContainers.GetRange(0, MaxReconstructionCandidates)
            : objStmContainers)
        {
            var found = TryExpandObjectStream(data, xref, containerObjNum);
            if (found is not null)
                catalogObjNum = found; // last one found wins, matching the xref's own rule
        }

        return (xref, catalogObjNum);
    }

    /// <summary>
    /// Decodes the object stream at <paramref name="containerObjNum"/> (already known-uncompressed
    /// in <paramref name="xref"/>) and records an entry for every object number packed inside it
    /// that the byte-level header scan couldn't see. An entry the header scan already found wins:
    /// a real top-level "N G obj" header is stronger evidence than one recovered from inside an
    /// object stream, and this also stops a crafted /ObjStm header from overriding reconstruction's
    /// own bookkeeping (#184 C2). Failures are swallowed — reconstruction is already best-effort,
    /// and a container that doesn't actually decode just contributes nothing.
    /// <para/>
    /// Returns the object number of a packed object declaring <c>/Type /Catalog</c>, if any. A
    /// modern (xref-stream + object-stream) file's catalog is usually compressed, so without this
    /// the raw-byte <c>/Type /Catalog</c> fallback scan can only ever map back to the CONTAINER's
    /// own top-level header, not the object actually packed inside it (#184 C2).
    /// </summary>
    private static int? TryExpandObjectStream(ReadOnlyMemory<byte> data, Dictionary<int, XrefEntry> xref, int containerObjNum)
    {
        try
        {
            if (!xref.TryGetValue(containerObjNum, out var entry) || entry.Kind != XrefEntryKind.Uncompressed)
                return null;
            if (entry.Offset < 0 || entry.Offset >= data.Length)
                return null;

            var parser = new PdfObjectParser(data, (int)entry.Offset);
            var result = parser.ParseIndirectObject();
            if (!result.IsStream)
                return null;

            var streamObj = result.Stream!;
            var dict = streamObj.Dictionary;
            var decoded = PdfFilters.Decode(streamObj);
            if (decoded is null)
                return null;

            if (dict.Get(new PdfName("N")) is not PdfInteger nObj || nObj.Value is < 0 or > 1_000_000)
                return null;
            var n = (int)nObj.Value;

            if (dict.Get(new PdfName("First")) is not PdfInteger firstObj)
                return null;
            if (firstObj.Value < 0 || firstObj.Value > decoded.Length)
                return null;
            var first = (int)firstObj.Value;

            var decodedMem = new ReadOnlyMemory<byte>(decoded);
            var headerParser = new PdfObjectParser(decodedMem[..first]);
            int? catalogObjNum = null;
            for (var idx = 0; idx < n; idx++)
            {
                var numObj = headerParser.ParseObject();
                var offObj = headerParser.ParseObject();
                if (numObj is not PdfInteger numInt || offObj is not PdfInteger offInt
                    || numInt.Value is < 0 or > int.MaxValue || offInt.Value is < 0 or > int.MaxValue)
                    return catalogObjNum;

                var innerObjNum = (int)numInt.Value;
                if (!xref.ContainsKey(innerObjNum))
                    xref[innerObjNum] = XrefEntry.InObjStm(containerObjNum, idx);

                // Peek at the packed object's own /Type while it's already at hand — this is the
                // only way reconstruction can identify a compressed catalog at all: the raw-byte
                // /Type /Catalog scan RecoverTrailer falls back to can only see top-level bytes,
                // never anything inside a decoded object stream body (#184 C2).
                var relOffset = (int)offInt.Value;
                if (first + relOffset < decoded.Length)
                {
                    try
                    {
                        var valueParser = new PdfObjectParser(decodedMem[(first + relOffset)..]);
                        if (valueParser.ParseObject() is PdfDictionary innerDict
                            && innerDict.Get(PdfName.Type) is PdfName innerType && innerType.Equals(PdfName.Catalog))
                        {
                            catalogObjNum = innerObjNum;
                        }
                    }
                    catch (InvalidDataException)
                    {
                        // Not a genuine object at this relative offset — leave it recorded in xref
                        // (a real resolve attempt gets its own clean error) and move on.
                    }
                }
            }

            return catalogObjNum;
        }
        catch (InvalidDataException)
        {
            // Not a genuine, decodable object stream at this offset -- skip silently.
            return null;
        }
    }

    /// <summary>
    /// Recovers a trailer dictionary when startxref can't locate one: first the newest classic
    /// "trailer" section declaring /Root (this preserves its other keys, notably /Encrypt); failing
    /// that, a generic scan for a "/Root N G R" entry, which also covers a cross-reference-stream
    /// dictionary (it folds the trailer into the stream dict rather than a separate "trailer"
    /// section); failing that, any object declaring /Type /Catalog directly.
    /// </summary>
    private static readonly PdfName EncryptKey = new("Encrypt");

    private static PdfDictionary RecoverTrailer(
        ReadOnlyMemory<byte> data, Dictionary<int, XrefEntry> xref, int? discoveredCatalogObjNum)
    {
        // A real "trailer<<...>>" section, when one exists, is preferred over both fallbacks below
        // because it preserves every key it declares — /Encrypt and /ID included — rather than just
        // the ones this method knows to go looking for.
        var trailerDict = FindTrailerWithRoot(data);
        if (trailerDict is not null)
            return trailerDict;

        var rootObjNum = FindIndirectReferenceValue(data, "/Root"u8);
        if (rootObjNum is not null && xref.ContainsKey(rootObjNum.Value))
            return BuildSynthesizedTrailer(data, xref, rootObjNum.Value);

        // Prefer the catalog ScanForObjectHeaders already identified while it had a real parsed
        // dictionary in hand — for an object packed inside an object stream, that is the ONLY way
        // to find it at all (#184 C2): the raw-byte /Type /Catalog scan below can only map back to
        // a top-level "N G obj" header, never to something recovered from inside a decoded stream.
        if (discoveredCatalogObjNum is not null && xref.ContainsKey(discoveredCatalogObjNum.Value))
            return BuildSynthesizedTrailer(data, xref, discoveredCatalogObjNum.Value);

        var catalogObjNum = FindCatalogObjectNumber(data, xref);
        if (catalogObjNum is not null)
            return BuildSynthesizedTrailer(data, xref, catalogObjNum.Value);

        throw new InvalidDataException(
            "Malformed PDF: startxref is unusable and no /Root or /Type /Catalog object could be "
            + "found to reconstruct a trailer from.");
    }

    /// <summary>
    /// Builds a trailer around a recovered <c>/Root</c> object number for the two fallback recovery
    /// paths that don't have a real trailer dictionary to preserve — the generic "/Root N G R" scan
    /// (covers a cross-reference-stream dictionary, which has no separate "trailer" section at
    /// all) and the last-resort "/Type /Catalog" object scan.
    /// </summary>
    /// <remarks>
    /// Also scans for <c>/Encrypt</c> using the same technique and, when found, includes it: the
    /// two fallbacks used to synthesize a trailer containing <em>only</em> <c>/Root</c>, so a file
    /// whose <c>/Encrypt</c> lives in a cross-reference stream's own dictionary (the normal PDF
    /// 1.5+ layout — no separate "trailer" section at all) or is merely out of the trailer-scan's
    /// candidate budget opened as if it were unencrypted, handing a caller ciphertext with no
    /// exception (#184 C1). <c>/Info</c> is recovered the same way for completeness, since it costs
    /// nothing once the mechanism exists; <c>/ID</c> is not — it's an inline array of two strings,
    /// not an indirect reference, so it needs its own scan and is far less consequential to lose.
    /// </remarks>
    private static PdfDictionary BuildSynthesizedTrailer(ReadOnlyMemory<byte> data, Dictionary<int, XrefEntry> xref, int rootObjNum)
    {
        var dict = new PdfDictionary().Set(PdfName.Root, MakeReference(xref, rootObjNum));

        var encryptObjNum = FindIndirectReferenceValue(data, "/Encrypt"u8);
        if (encryptObjNum is not null && xref.ContainsKey(encryptObjNum.Value))
            dict.Set(EncryptKey, MakeReference(xref, encryptObjNum.Value));

        var infoObjNum = FindIndirectReferenceValue(data, "/Info"u8);
        if (infoObjNum is not null && xref.ContainsKey(infoObjNum.Value))
            dict.Set(PdfName.Info, MakeReference(xref, infoObjNum.Value));

        return dict;
    }

    /// <summary>
    /// Builds a reference to <paramref name="objNum"/> at the generation reconstruction actually
    /// recorded for it. A hardcoded generation 0 here was the specific defect a review round found
    /// in this method: reconstruction's object-header scan already recovers each object's real
    /// generation (#121), so a catalog or other object at a nonzero generation would resolve
    /// correctly on the xref side and then fail here anyway, at "/Root does not resolve to a
    /// dictionary", because the synthesized reference asked for generation 0 instead.
    /// </summary>
    private static PdfIndirectReference MakeReference(Dictionary<int, XrefEntry> xref, int objNum)
    {
        var generation = xref.TryGetValue(objNum, out var entry) && entry.Generation is >= 0 and <= 65535
            ? entry.Generation
            : 0; // XrefEntry.UnknownGeneration (or a genuinely absent entry) — 0 is safe here: the
                 // resolver treats an UnknownGeneration xref entry as matching any requested
                 // generation (see PdfDocumentReader.Resolve), so this value is never checked.
        return new PdfIndirectReference(objNum, generation);
    }

    private static PdfDictionary? FindTrailerWithRoot(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        ReadOnlySpan<byte> marker = "trailer"u8;
        var attempts = 0;

        // Scan backward from EOF: the first candidate that parses into a dictionary with /Root is
        // the newest trailer in the file, matching what a /Prev chain would have picked.
        for (var i = span.Length - marker.Length; i >= 0; i--)
        {
            if (!span.Slice(i, marker.Length).SequenceEqual(marker))
                continue;

            var precededOk = i == 0 || IsWhitespace(span[i - 1]);
            var afterIdx = i + marker.Length;
            var followedOk = afterIdx >= span.Length || IsDelimiterOrWhitespace(span[afterIdx]);
            if (!precededOk || !followedOk)
                continue;

            if (++attempts > MaxReconstructionCandidates)
                break;

            var dict = TryParseDictionaryAt(data, afterIdx);
            if (dict is not null && dict.Get(PdfName.Root) is not null)
                return dict;
        }

        return null;
    }

    private static PdfDictionary? TryParseDictionaryAt(ReadOnlyMemory<byte> data, int offset)
    {
        // A bounded slice caps the cost of one attempt even when the candidate is a coincidental
        // "trailer" occurrence inside otherwise-unrelated content followed by an unterminated-
        // looking "<<": the parser gives up at the slice boundary rather than scanning to the true
        // end of the file.
        var sliceLen = Math.Min(MaxTrailerDictScanBytes, data.Length - offset);
        if (sliceLen <= 0)
            return null;
        try
        {
            var parser = new PdfObjectParser(data.Slice(offset, sliceLen));
            return parser.ParseObject() as PdfDictionary;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scans the whole file for a "/&lt;key&gt; N G R" entry and returns the object number of the
    /// last valid one found. The same textual shape appears whether the key sits in a classic
    /// "trailer&lt;&lt;...&gt;&gt;" section or directly in a cross-reference stream's own dictionary
    /// (which folds the trailer's keys into itself rather than using a separate "trailer" section),
    /// so this one scan covers both layouts.
    /// </summary>
    private static int? FindIndirectReferenceValue(ReadOnlyMemory<byte> data, ReadOnlySpan<byte> keyMarker)
    {
        var span = data.Span;
        int? found = null;

        for (var i = 0; i <= span.Length - keyMarker.Length; i++)
        {
            if (!span.Slice(i, keyMarker.Length).SequenceEqual(keyMarker))
                continue;

            var precededOk = i == 0 || IsDelimiterOrWhitespace(span[i - 1]);
            var afterIdx = i + keyMarker.Length;
            var followedOk = afterIdx >= span.Length || IsDelimiterOrWhitespace(span[afterIdx]);
            if (!precededOk || !followedOk)
                continue;

            if (TryParseIndirectReference(span, afterIdx, out var objNum))
                found = objNum; // keep scanning; the last valid entry in the file wins
        }

        return found;
    }

    private static int? FindCatalogObjectNumber(ReadOnlyMemory<byte> data, Dictionary<int, XrefEntry> xref)
    {
        var span = data.Span;
        ReadOnlySpan<byte> marker = "/Catalog"u8;

        // Pre-sort the recovered object offsets once so each candidate maps to its enclosing
        // object via a bounded binary search rather than a full rescan of every recovered object.
        var sorted = new List<(long Offset, int ObjNum)>(xref.Count);
        foreach (var (objNum, entry) in xref)
        {
            if (entry.Kind == XrefEntryKind.Uncompressed)
                sorted.Add((entry.Offset, objNum));
        }
        sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var attempts = 0;
        for (var i = 0; i <= span.Length - marker.Length; i++)
        {
            if (!span.Slice(i, marker.Length).SequenceEqual(marker))
                continue;

            var precededOk = i == 0 || IsDelimiterOrWhitespace(span[i - 1]);
            var afterIdx = i + marker.Length;
            var followedOk = afterIdx >= span.Length || IsDelimiterOrWhitespace(span[afterIdx]);
            if (!precededOk || !followedOk)
                continue;

            if (++attempts > MaxReconstructionCandidates)
                break;

            var objNum = FindEnclosingObject(i, sorted);
            if (objNum is not null)
                return objNum;
        }

        return null;
    }

    /// <summary>
    /// Finds the object whose "N G obj" header is the last one at or before <paramref name="pos"/> —
    /// i.e. the object that textually contains it.
    /// </summary>
    private static int? FindEnclosingObject(int pos, List<(long Offset, int ObjNum)> sortedOffsets)
    {
        var lo = 0;
        var hi = sortedOffsets.Count - 1;
        int? result = null;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sortedOffsets[mid].Offset <= pos)
            {
                result = sortedOffsets[mid].ObjNum;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    private static bool TryParseIndirectReference(ReadOnlySpan<byte> span, int pos, out int objNum)
    {
        objNum = 0;
        var p = pos;
        while (p < span.Length && IsWhitespace(span[p])) p++;

        var numStart = p;
        while (p < span.Length && IsDigit(span[p])) p++;
        if (p == numStart || p - numStart > 10 || !TryParseBoundedInt(span[numStart..p], out objNum))
            return false;

        var wsMark = p;
        while (p < span.Length && IsWhitespace(span[p])) p++;
        if (p == wsMark) return false;

        var genStart = p;
        while (p < span.Length && IsDigit(span[p])) p++;
        if (p == genStart || p - genStart > 10) return false;

        wsMark = p;
        while (p < span.Length && IsWhitespace(span[p])) p++;
        if (p == wsMark) return false;

        if (p >= span.Length || span[p] != (byte)'R') return false;
        var after = p + 1;
        return after >= span.Length || IsDelimiterOrWhitespace(span[after]);
    }

    private static bool TryParseBoundedInt(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        if (digits.Length == 0 || digits.Length > 10)
            return false;
        long v = 0;
        foreach (var b in digits)
            v = v * 10 + (b - (byte)'0');
        if (v is < 0 or > int.MaxValue)
            return false;
        value = (int)v;
        return true;
    }

    private static bool IsDelimiterOrWhitespace(byte b) =>
        b is 0 or 9 or 10 or 12 or 13 or 32
          or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
          or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
          or (byte)'/' or (byte)'%';

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
                    // `localFreed`, not `freed`: a hybrid file's own /XRefStm (parsed next, same
                    // revision) may still define the object, and that must not be suppressed by this
                    // table's 'f' entry. An older revision's 'n' entry for the same number is
                    // suppressed once ParseOneRevision folds `localFreed` into `freed` after both
                    // halves of this revision have run.
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
        HashSet<int> localFreed)
    {
        var parser = new PdfObjectParser(data, xrefOffset);
        var result = parser.ParseIndirectObject();

        if (result.Stream is null)
            throw new InvalidDataException(
                $"Malformed PDF: expected xref stream object at offset {xrefOffset}.");

        var streamObj = result.Stream;
        var dict = streamObj.Dictionary;

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
