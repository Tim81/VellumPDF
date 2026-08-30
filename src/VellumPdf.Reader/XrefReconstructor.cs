// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Which security handler family, if any, a candidate dictionary's structure resembles (ISO
/// 32000-2 §7.6.5.2, Table 27), computed by <see cref="XrefReconstructor.ClassifyEncryptionDictionary"/>.
/// Not PR2's own refusal signal — see that method's doc comment — kept here, currently unused by
/// any production code path, so PR3 can reuse this exact disambiguation once it needs to tell the
/// Standard handler apart from a public-key one.
/// </summary>
internal enum EncryptionDictionaryClass
{
    /// <summary>Neither the required pair (<c>/Filter</c> + <c>/V</c>) nor a disambiguator present.</summary>
    None,

    /// <summary>Carries <c>/R</c>, <c>/O</c> and <c>/U</c> together, the Standard handler's Table 21 shape.</summary>
    StandardHandler,

    /// <summary>Carries a <c>/SubFilter</c> naming a public-key handler (Table 23/Table 27).</summary>
    PublicKeyHandler,
}

/// <summary>
/// Phase A of cross-reference reconstruction (#184): rebuilds a cross-reference table by walking
/// the file for object structure when <c>startxref</c> is missing or broken — the recovery ISO
/// 32000-2 Annex C.4 (informative) describes, not §7.5.6 (Incremental updates). A single monotone
/// cursor walks the buffer once, dispatching on byte class and delegating every real object parse
/// to <see cref="PdfObjectParser"/>/<see cref="PdfLexer"/>: this type only decides where the parser
/// points and what a parse failure does, never how to read PDF syntax itself. Whitespace and
/// delimiter classification comes from <see cref="PdfLexer.IsWhitespaceByte"/> and
/// <see cref="PdfLexer.IsDelimiterByte"/> rather than a second copy of Table 1/Table 2.
/// <para>
/// Everything read here is a name, an indirect reference, or an integer — the object kinds ISO
/// 32000-2 §7.6.2's exemption list carries — so this can run before any password has been supplied
/// or checked. This PR refuses outright the instant any evidence suggests the document is
/// encrypted: a whole-file sweep independent of what the walk actually tokenized
/// (<see cref="ScanWholeFileForEncryptionEvidence"/>), the walk's own declared/structural checks in
/// <see cref="RecoverTrailer"/> (<see cref="HasPr2EncryptionEvidenceShape"/>), and the exhaustion
/// path below. A later PR lifts that once reconstruction can also authenticate.
/// </para>
/// </summary>
internal static class XrefReconstructor
{
    private static readonly byte[] EndstreamMarker = "endstream"u8.ToArray();
    private static readonly PdfName _encryptKey = new("Encrypt");
    private static readonly PdfName _objStmType = new("ObjStm");
    private static readonly PdfName _filterKey = new("Filter");
    private static readonly PdfName _vKey = new("V");
    private static readonly PdfName _rKey = new("R");
    private static readonly PdfName _oKey = new("O");
    private static readonly PdfName _uKey = new("U");
    private static readonly PdfName _subFilterKey = new("SubFilter");
    private static readonly PdfName _byteRangeKey = new("ByteRange");
    private static readonly PdfName _cfKey = new("CF");
    private static readonly PdfName _stmFKey = new("StmF");
    private static readonly PdfName _strFKey = new("StrF");
    private static readonly PdfName _recipientsKey = new("Recipients");
    private static readonly PdfName _encryptMetadataKey = new("EncryptMetadata");

    // The trailer keys reconstruction knows how to recover (A5). Building the recovered trailer by
    // setting exactly these keys — never by cloning a whole candidate dictionary — is what A5b
    // needs: a real "trailer<<...>>" section's own /Prev or /XRefStm (advertising a revision chain
    // this pass deliberately does not walk) is simply never copied in the first place.
    private static readonly PdfName[] _recoverableTrailerKeys =
        [PdfName.Root, _encryptKey, PdfName.ID, PdfName.Info, PdfName.Size];

    // A2's cost ceiling. Ample headroom for a genuinely damaged file's own structure while still
    // bounding a file deliberately padded with decoy headers or nested constructs: the aggregate
    // is what refuses, not any single per-construct cap (rows 1, 3, 5, 14 — none of them survive as
    // a fixed constant in this design; the budget is the only backstop).
    private const long MinReconstructionByteBudget = 1L * 1024 * 1024; // 1 MiB
    private const int ReconstructionByteBudgetMultiplier = 8;

    // Row 4: a /Length that verifies exactly is preferred, but a stale value off by a handful of
    // bytes (a rounding difference, or an incremental edit that forgot to update it) still counts
    // as verified within this window either side of the position it names, rather than falling all
    // the way to the T_scan tiers below.
    private const int LengthNearMissWindowBytes = 64;

    // A small, fixed accounting unit for a boundary check in FindLineInitialTierOneTerminator
    // below: each candidate examined touches only a short, bounded look-ahead (a handful of bytes
    // to confirm 'endobj' or the next "N G obj" shape), but the plan charges every boundary check
    // against the aggregate budget rather than leaving any check entirely free, so a decoy cannot
    // multiply a cheap-looking operation into unbounded uncharged work.
    private const int BoundaryCheckChargeBytes = 16;

    /// <summary>
    /// The byte extent of one object the walk actually confirmed: the dictionary region starting
    /// at <see cref="DictStart"/> — or, for a non-stream object, the whole value — and, for a
    /// stream, the body region <c>[BodyStart, BodyEnd)</c>. <see cref="Dictionary"/> is the parsed
    /// dictionary when the object's value is one (a stream's own dictionary, or a bare top-level
    /// dictionary object), and null otherwise — an array, a number, anything that cannot carry
    /// /Root, /Encrypt, or /Type.
    /// </summary>
    private readonly record struct ObjectExtent(
        int ObjNum, int Generation, int DictStart, int BodyStart, int BodyEnd, bool HasBody,
        PdfDictionary? Dictionary);

    /// <summary>
    /// Rebuilds a cross-reference table by walking the whole file once for object structure.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when no object headers were found at all, or when the walk could not complete within
    /// its cost budget and no encryption evidence was found either (see the exhaustion path below).
    /// </exception>
    /// <exception cref="UnsupportedPdfFeatureException">
    /// Thrown when the walk — or, on exhaustion, an uncharged raw sweep of whatever remains unwalked
    /// — finds any evidence the document is encrypted. This PR is plaintext-only; a later PR lifts
    /// this refusal without needing to restructure anything here.
    /// </exception>
    internal static XrefParseResult Reconstruct(ReadOnlyMemory<byte> data)
    {
        var length = data.Length;
        var budget = Math.Max(MinReconstructionByteBudget, (long)length * ReconstructionByteBudgetMultiplier);
        long consumed = 0;

        var xref = new Dictionary<int, XrefEntry>();
        var primaryByObjNum = new Dictionary<int, ObjectExtent>();
        var secondaryExtents = new List<ObjectExtent>();
        // Row 8: an indirect /Length may resolve ONLY through an already-confirmed, non-stream,
        // lower-file-offset object — never a forward reference, and never through a stream (whose
        // own extent this same mechanism is busy resolving). Populated as the walk confirms
        // non-stream integer objects, in file order, so "lower offset" falls out of "already in
        // this map when a later stream candidate looks it up".
        var confirmedIntegers = new Dictionary<int, (int Offset, long Value)>();
        var trailerCandidates = new List<(int Offset, PdfDictionary Dict)>();
        var objectStreamContainers = new List<(int ObjNum, int RawBodyLength)>();

        // Pass 0: one O(N) sweep locating every 'endstream' occurrence, once, before any candidate
        // is resolved. Not charged (see Charge below) — a single forward pass over the whole buffer
        // cannot be made quadratic by any candidate density.
        var (endstreamAll, endstreamLineInitial) = ScanEndstreamOccurrences(data);

        // C1: encryption evidence has to be a WHOLE-FILE property, computed independently of what
        // the tokenizing walk below actually visits. The walk jumps straight over large regions it
        // never re-examines — an unresolved stream body runs to EOF (ResolveStreamExtent's own
        // fallback), and an unterminated literal or hex string also consumes to EOF
        // (SkipBalancedLiteralString/SkipHexString) — so evidence gathered only from what the walk
        // tokenized can miss a file's own /Encrypt trailer entry or encryption dictionary sitting
        // inside one of those swallowed regions, and the walk then completes normally with a
        // plaintext-looking, /Encrypt-free trailer: the document opens and hands back ciphertext as
        // if it were content. This sweep runs once, unconditionally, on EVERY completion path — not
        // only when the cost budget is exhausted — and is uncharged, since it is a single O(N) pass
        // that cannot itself be made expensive by anything the file contains.
        var wholeFileEncryptionEvidence = ScanWholeFileForEncryptionEvidence(data.Span);

        var cursorPos = 0;

        void Charge(long amount)
        {
            consumed += amount;
            if (consumed >= budget)
            {
                ThrowOnExhaustion(
                    data, cursorPos, budget, xref, primaryByObjNum, secondaryExtents, trailerCandidates,
                    wholeFileEncryptionEvidence);
            }
        }

        var span = data.Span;
        var pos = 0;
        while (pos < length)
        {
            cursorPos = pos;
            var b = span[pos];

            // Whitespace and comments: the single-visit cursor advancing through ordinary file
            // structure — never charged (see the budget-semantics note on Charge above).
            if (PdfLexer.IsWhitespaceByte(b))
            {
                pos++;
                continue;
            }

            if (b == (byte)'%')
            {
                pos++;
                while (pos < length && span[pos] is not 10 and not 13)
                    pos++;
                continue;
            }

            // Balanced literal string: consumed as a single token so nothing inside it — including
            // a byte sequence that would otherwise look like "N G obj" or 'trailer' — is ever
            // re-examined as a candidate (rows 6/7). Unterminated fails closed: nothing after an
            // unterminated string has a trustworthy start, so the walk simply ends.
            if (b == (byte)'(')
            {
                pos = SkipBalancedLiteralString(span, pos);
                continue;
            }

            if (b == (byte)'<')
            {
                if (pos + 1 < length && span[pos + 1] == (byte)'<')
                {
                    // A dictionary or array reached outside an "N G obj" header — e.g. nested
                    // inside another value already being walked past, or genuinely stray. Parsed
                    // and charged like any other real construct; it carries no object number of
                    // its own, so nothing further is recorded for it beyond consuming its bytes.
                    pos = ParseObjectCharged(data, pos, Charge).NewPos;
                    continue;
                }

                pos = SkipHexString(span, pos);
                continue;
            }

            if (b == (byte)'[')
            {
                pos = ParseObjectCharged(data, pos, Charge).NewPos;
                continue;
            }

            if (IsDigitByte(b))
            {
                if (TryMatchObjectHeaderShape(span, pos, out var objNum, out var generation, out var afterObj))
                {
                    pos = ConfirmCandidate(
                        data, pos, objNum, generation, afterObj, xref, primaryByObjNum, confirmedIntegers,
                        objectStreamContainers, secondaryExtents, endstreamAll, endstreamLineInitial, Charge);
                    continue;
                }

                // Not an "N G obj" shape at this position — ordinary text; resync minimally rather
                // than skipping the rest of the digit run, so an offset one byte further in still
                // gets its own chance (e.g. a decoy separator that starts the run one byte early).
                pos++;
                continue;
            }

            // 'trailer' is the one keyword the walk recognises: no attempt cap (row 3) — every
            // occurrence outside a confirmed stream body (which the cursor never revisits, having
            // jumped straight to its BodyEnd when the stream was confirmed) is a genuine candidate.
            if (b == (byte)'t' && TryMatchKeyword(span, pos, "trailer"u8, out var afterTrailer))
            {
                var (newPos, value) = ParseObjectCharged(data, afterTrailer, Charge);
                if (value is PdfDictionary trailerDict)
                    trailerCandidates.Add((pos, trailerDict));
                pos = newPos;
                continue;
            }

            // 'xref' tables, 'obj'/'endobj'/'stream'/'endstream' keywords reached outside a
            // confirmed header, and everything else: walked through harmlessly, one byte at a
            // time. A classic xref subsection's 20-byte rows end in 'n'/'f', not 'obj', so they
            // never match the header shape above and cost nothing beyond this resync.
            pos++;
        }

        // Secondary (quarantined) results merge in only after the primary walk completes, and only
        // via TryAdd: a primary definition always wins, matching A3's rule for the confirmed table.
        foreach (var e in secondaryExtents)
            xref.TryAdd(e.ObjNum, XrefEntry.Uncompressed(e.DictStart, e.Generation));

        if (xref.Count == 0)
            throw new InvalidDataException(
                "Malformed PDF: startxref is missing or unusable, and no 'N G obj' object headers "
                + "were found to reconstruct the cross-reference table from.");

        // A5: recover a trailer, refusing outright on any evidence the document is encrypted.
        var trailer = RecoverTrailer(
            xref, primaryByObjNum, secondaryExtents, trailerCandidates, wholeFileEncryptionEvidence);

        // A6: rank catalog candidates for Phase B (PdfDocumentReader's constructor) to validate —
        // this pass cannot check its own answer, since checking it means resolving objects, which
        // needs authentication to have already happened.
        var candidateRoots = BuildCandidateRoots(xref, primaryByObjNum, trailer);
        if (candidateRoots.Count > 0)
            trailer.Set(PdfName.Root, candidateRoots[0]);

        // A4 is PR3's: cross-reference-stream offset evidence is deliberately NOT populated here.
        // It must never be keyed on a reconstructed object's /Type /XRef — that key is
        // author-controlled, which is exactly why PdfDocumentReader.IsCrossReferenceStream and
        // CryptFilterResolver key the real exemption on where a stream was actually READ as an
        // xref stream, never on what it claims to be.
        //
        // A reconstructed document has no trustworthy revision history either — the /Prev chain
        // that would normally describe one is exactly what's missing or broken here. An empty
        // list, not a (0, 0) sentinel, is what ObjectLayoutRule already reads as "no revision
        // info, check every object" (Revisions.Count == 0).
        return new XrefParseResult(
            xref, trailer, StartXrefOffset: 0, Revisions: [], CrossReferenceStreamOffsets: new HashSet<long>(),
            DroppedOrphanedObjectStreamMembers: false,
            WasReconstructed: true,
            ObjectStreamContainers: objectStreamContainers,
            CandidateRoots: candidateRoots,
            ReconstructionBytesConsumed: consumed);
    }

    // ── A2: candidate confirmation ────────────────────────────────────────────

    private static int ConfirmCandidate(
        ReadOnlyMemory<byte> data, int start, int objNum, int generation, int afterObj,
        Dictionary<int, XrefEntry> xref, Dictionary<int, ObjectExtent> primaryByObjNum,
        Dictionary<int, (int Offset, long Value)> confirmedIntegers,
        List<(int ObjNum, int RawBodyLength)> objectStreamContainers, List<ObjectExtent> secondaryExtents,
        List<int> endstreamAll, List<int> endstreamLineInitial, Action<long> charge)
    {
        // Register a header-only entry immediately. A3's "last definition wins" falls out of
        // always overwriting here as the walk proceeds in file order; if the probe below fails,
        // this header-only entry stands — Annex C.4's own header shape is still evidence, even
        // when what follows it doesn't parse.
        xref[objNum] = XrefEntry.Uncompressed(start, generation);

        var probeParser = new PdfObjectParser(data, start);
        HeaderProbeResult probe;
        try
        {
            probe = probeParser.ProbeIndirectObjectHeader();
            charge(probeParser.Position - start);
        }
        catch (InvalidDataException)
        {
            charge(Math.Max(1, probeParser.Position - start));
            // Resume after 'obj' — not wherever the probe gave up — so the interior gets
            // re-tokenised by the ordinary walk instead of being skipped as one opaque unit
            // (rows 6/7 hold on every path, including a failed probe).
            return afterObj;
        }

        if (probe.ObjectNumber != objNum)
            return afterObj; // not reachable under a single coherent byte stream; defensive only.

        if (!probe.HasStreamBody)
        {
            var dict = probe.Value as PdfDictionary;
            primaryByObjNum[objNum] = new ObjectExtent(objNum, generation, start, 0, 0, false, dict);
            if (probe.Value is PdfInteger pi)
                confirmedIntegers[objNum] = (start, pi.Value);
            return probeParser.Position;
        }

        var dict2 = (PdfDictionary)probe.Value!;
        var absoluteBodyStart = probe.StreamBodyStart; // the probe parser reads the full buffer, so its positions are already absolute
        var bodyEnd = ResolveStreamExtent(
            data, dict2, absoluteBodyStart, start, confirmedIntegers, endstreamAll, endstreamLineInitial,
            charge, out var verifiedByLength);

        primaryByObjNum[objNum] = new ObjectExtent(objNum, generation, start, absoluteBodyStart, bodyEnd, true, dict2);

        if (dict2.Get(PdfName.Type) is PdfName typeName && typeName.Equals(_objStmType))
            objectStreamContainers.Add((objNum, (int)Math.Clamp((long)bodyEnd - absoluteBodyStart, 0, int.MaxValue)));

        // Secondary recovery (row 11): only when T_len itself verified AND an earlier terminator,
        // strictly inside the body it accepted, is corroborated by what follows it. Line-initial
        // placement (checked by the T_scan tiers above) is an Annex C.4 PRODUCER convention, not an
        // evidence gate — the same lesson row 5's whitespace cap teaches: an honest stream body with
        // no trailing EOL of its own abuts 'endstream' directly (no preceding CR/LF), so a real
        // terminator can sit at a non-line-initial offset. Searching every occurrence in
        // `endstreamAll`, not just the line-initial subset, and gating each candidate on
        // FollowedByPlausibleBoundary (the same 'endobj'/"N G obj" corroboration
        // FindLineInitialTierOneTerminator uses) is what keeps this from re-triggering on every
        // coincidental "endstream" byte run inside genuine binary stream data — the quarantine below
        // is the actual safety net, but a corroborated trigger means secondary walking is rarely
        // invoked for nothing.
        if (verifiedByLength)
        {
            var earlier = FindCorroboratedTerminatorBefore(data.Span, absoluteBodyStart, bodyEnd, endstreamAll, charge);
            if (earlier is int et)
            {
                var secondaryStart = et + EndstreamMarker.Length;
                if (secondaryStart < bodyEnd)
                    WalkSecondary(data, secondaryStart, bodyEnd, confirmedIntegers, secondaryExtents, charge);
            }
        }

        return bodyEnd;
    }

    /// <summary>
    /// Resolves a confirmed stream candidate's body extent: T_len first (an O(1)-ish verified
    /// <c>/Length</c>, direct or a row-8-restricted indirect one), then the T_scan tiers over the
    /// pass-0 <c>endstream</c> index. A confirmed stream always contributes a body region — when
    /// nothing verifies or scans to a terminator, the body runs to EOF (the budget-semantics
    /// decision: an unsuppressed stream candidate is the vulnerability, so an unresolved stream
    /// swallows everything after it rather than leaving what follows unsuppressed).
    /// </summary>
    /// <remarks>
    /// L3 (accepted risk, documented rather than fixed): "always contributes a body region" is a
    /// guarantee about SUPPRESSION — every confirmed stream removes SOME span of bytes from further
    /// consideration as top-level structure — not a guarantee that the chosen BOUNDARY is
    /// trustworthy. Tier 1's own corroboration (<see cref="FindLineInitialTierOneTerminator"/>: a
    /// line-initial <c>endstream</c> followed by <c>endobj</c> or a plausible "N G obj" header) is
    /// evidence a well-formed producer's output satisfies, not proof against a crafted one: nothing
    /// stops an attacker from planting that exact byte sequence inside their own stream's binary
    /// body to steer where this method decides the stream ends, and a forward indirect
    /// <c>/Length</c> is refused outright by row 8's lower-offset restriction rather than
    /// corroborated. What actually bounds the damage is A3's last-definition-wins rule applied
    /// downstream — a misjudged boundary changes what one stream's own body decodes to and,
    /// through suppression, which bytes elsewhere are read as top-level candidates, but it cannot
    /// resurrect an OBJECT NUMBER definition that a later, later-in-file one has already overridden.
    /// The tier CHOICE is attacker-influenceable; the blast radius from a wrong choice is not.
    /// </remarks>
    private static int ResolveStreamExtent(
        ReadOnlyMemory<byte> data, PdfDictionary dict, int bodyStart, int streamHeaderOffset,
        Dictionary<int, (int Offset, long Value)> confirmedIntegers, List<int> endstreamAll,
        List<int> endstreamLineInitial, Action<long> charge, out bool verifiedByLength)
    {
        verifiedByLength = false;

        if (TryResolveDeclaredLength(dict, confirmedIntegers, streamHeaderOffset, out var declaredLen)
            && TryVerifyLengthWithNearMiss(data, bodyStart, declaredLen, charge, out var verifiedEnd))
        {
            verifiedByLength = true;
            return verifiedEnd;
        }

        var span = data.Span;

        var tier1 = FindLineInitialTierOneTerminator(span, bodyStart, endstreamLineInitial, charge);
        if (tier1 is int t1)
            return t1 + EndstreamMarker.Length;

        var tier2 = FirstGreaterThan(endstreamLineInitial, bodyStart);
        if (tier2 is int t2)
            return t2 + EndstreamMarker.Length;

        var tier3 = FirstGreaterThan(endstreamAll, bodyStart);
        if (tier3 is int t3)
            return t3 + EndstreamMarker.Length;

        return data.Length;
    }

    /// <summary>
    /// Row 8: an indirect <c>/Length</c> resolves only through an object this walk has ALREADY
    /// confirmed as a non-stream integer at a strictly lower file offset than the stream candidate
    /// asking for it. A forward reference — or one into a stream, whose own extent this same
    /// mechanism would then be resolving circularly — is never trusted: a decoy stream could
    /// otherwise point its <c>/Length</c> at a later, attacker-placed integer crafted to make this
    /// check pass.
    /// </summary>
    private static bool TryResolveDeclaredLength(
        PdfDictionary dict, Dictionary<int, (int Offset, long Value)> confirmedIntegers,
        int streamHeaderOffset, out long len)
    {
        len = 0;
        if (!dict.TryGet(PdfName.Length, out var lenObj) || lenObj is null)
            return false;

        if (lenObj is PdfInteger direct)
        {
            len = direct.Value;
            return len >= 0;
        }

        if (lenObj is PdfIndirectReference r
            && confirmedIntegers.TryGetValue(r.ObjectNumber, out var confirmed)
            && confirmed.Offset < streamHeaderOffset)
        {
            len = confirmed.Value;
            return len >= 0;
        }

        return false;
    }

    /// <summary>
    /// The O(1)-shaped half of extent resolution: confirms <c>endstream</c> sits at the declared
    /// position, an unbounded but CHARGED whitespace skip first (row 5 — no replacement constant;
    /// a small fixed cap is what let a merely padded body dodge exact verification), then a
    /// <see cref="LengthNearMissWindowBytes"/> window either side when the exact position misses
    /// (row 4).
    /// </summary>
    private static bool TryVerifyLengthWithNearMiss(
        ReadOnlyMemory<byte> data, int bodyStart, long len, Action<long> charge, out int bodyEnd)
    {
        bodyEnd = 0;
        if (len is < 0 or > int.MaxValue)
            return false;

        var declaredEnd = (long)bodyStart + len;
        if (declaredEnd < 0 || declaredEnd > data.Length)
            return false;

        var span = data.Span;
        var pos = (int)declaredEnd;
        var skipStart = pos;
        while (pos < data.Length && PdfLexer.IsWhitespaceByte(span[pos]))
            pos++;
        charge(pos - skipStart);

        if (TryMatchWordBounded(span, pos, EndstreamMarker))
        {
            bodyEnd = pos + EndstreamMarker.Length;
            return true;
        }

        var windowStart = (int)Math.Max(bodyStart, declaredEnd - LengthNearMissWindowBytes);
        var windowEnd = (int)Math.Min(data.Length, declaredEnd + LengthNearMissWindowBytes);
        charge(Math.Max(0, windowEnd - windowStart));
        for (var p = windowStart; p <= windowEnd - EndstreamMarker.Length; p++)
        {
            if (TryMatchWordBounded(span, p, EndstreamMarker))
            {
                bodyEnd = p + EndstreamMarker.Length;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ISO 32000-2 Annex C.4's strongest signal: a line-initial <c>endstream</c> immediately
    /// followed by <c>endobj</c> or the next object's own "N G obj" header. Searched from the
    /// pass-0 line-initial index, ascending from <paramref name="threshold"/>; each candidate's
    /// follow-check is charged as a boundary check, at the ACTUAL bytes it examined (C4 — see
    /// <see cref="FollowedByPlausibleBoundary"/>'s own comment for why a flat charge here was
    /// unsound), floored at <see cref="BoundaryCheckChargeBytes"/> so a trivially cheap check still
    /// costs something.
    /// </summary>
    private static int? FindLineInitialTierOneTerminator(
        ReadOnlySpan<byte> span, int threshold, List<int> lineInitial, Action<long> charge)
    {
        var idx = LowerBoundGreaterThan(lineInitial, threshold);
        for (; idx < lineInitial.Count; idx++)
        {
            var offset = lineInitial[idx];
            var corroborated = FollowedByPlausibleBoundary(span, offset + EndstreamMarker.Length, out var bytesExamined);
            charge(Math.Max(BoundaryCheckChargeBytes, bytesExamined));
            if (corroborated)
                return offset;
        }
        return null;
    }

    /// <summary>
    /// Row 11's secondary-recovery trigger: the first <c>endstream</c> occurrence in
    /// <paramref name="endstreamAll"/> strictly greater than <paramref name="threshold"/> and
    /// strictly less than <paramref name="upperBoundExclusive"/> whose follow-check corroborates it
    /// (<see cref="FollowedByPlausibleBoundary"/>) — the same corroboration
    /// <see cref="FindLineInitialTierOneTerminator"/> requires, but over EVERY occurrence rather
    /// than only the line-initial subset, since line-initial placement is a producer convention
    /// (Annex C.4), not something the walk can require of a terminator before trusting it as
    /// evidence. Stops as soon as a candidate reaches or passes <paramref name="upperBoundExclusive"/>
    /// (the list is sorted ascending), rather than scanning the whole list every time. Charges the
    /// actual bytes each follow-check examined, same reasoning as
    /// <see cref="FindLineInitialTierOneTerminator"/> (C4).
    /// </summary>
    private static int? FindCorroboratedTerminatorBefore(
        ReadOnlySpan<byte> span, int threshold, int upperBoundExclusive, List<int> endstreamAll, Action<long> charge)
    {
        var idx = LowerBoundGreaterThan(endstreamAll, threshold);
        for (; idx < endstreamAll.Count; idx++)
        {
            var offset = endstreamAll[idx];
            if (offset >= upperBoundExclusive)
                return null;
            var corroborated = FollowedByPlausibleBoundary(span, offset + EndstreamMarker.Length, out var bytesExamined);
            charge(Math.Max(BoundaryCheckChargeBytes, bytesExamined));
            if (corroborated)
                return offset;
        }
        return null;
    }

    /// <summary>
    /// C4: <paramref name="bytesExamined"/> reports how far the leading whitespace/comment skip
    /// actually advanced before this method looked for 'endobj' or an "N G obj" shape. Both call
    /// sites used to charge a flat <see cref="BoundaryCheckChargeBytes"/> (16) regardless of that
    /// distance, but a COMMENT is skipped here with no length limit — a file built from M blocks,
    /// each ending in a large comment before a boundary check that ultimately fails, does real work
    /// proportional to comment length per block while the flat charge under-reports it, so total
    /// work is Θ(N^1.5) while the counter grows only linearly and the budget never trips (measured:
    /// a 5.5 MB crafted input took 27s inside one <c>Open</c> call). Charging the true advance
    /// restores the linear bound the aggregate budget is supposed to guarantee.
    /// </summary>
    private static bool FollowedByPlausibleBoundary(ReadOnlySpan<byte> span, int pos, out int bytesExamined)
    {
        var p = pos;
        while (p < span.Length)
        {
            var b = span[p];
            if (b == (byte)'%')
            {
                p++;
                while (p < span.Length && span[p] is not 10 and not 13) p++;
            }
            else if (PdfLexer.IsWhitespaceByte(b))
            {
                p++;
            }
            else
            {
                break;
            }
        }

        bytesExamined = p - pos;

        if (p >= span.Length)
            return false;

        if (span[p..].StartsWith("endobj"u8))
        {
            var after = p + 6;
            return after >= span.Length || IsDelimiterOrWhitespaceByte(span[after]);
        }

        return TryMatchObjectHeaderShape(span, p, out _, out _, out _);
    }

    // ── Secondary recovery (row 11; quarantined) ────────────────────────────────

    /// <summary>
    /// Walks <c>[rangeStart, rangeEnd)</c> once, in secondary mode, recovering "N G obj" candidates
    /// a verified <c>/Length</c> swallowed past an earlier genuine terminator. Results are
    /// quarantined by the caller: merged into the live table only via <c>TryAdd</c> after the
    /// primary walk completes, never used for trailer recovery, catalog election, or another
    /// object's <c>/Length</c> resolution — but still examined for encryption evidence in
    /// <see cref="RecoverTrailer"/>, since the asymmetry a false negative there would create (silent
    /// ciphertext) is worse than a false positive refusing a plaintext file. Does not itself trigger
    /// further secondary recovery.
    /// </summary>
    private static void WalkSecondary(
        ReadOnlyMemory<byte> data, int rangeStart, int rangeEnd,
        Dictionary<int, (int Offset, long Value)> confirmedIntegers, List<ObjectExtent> secondaryExtents,
        Action<long> charge)
    {
        var span = data.Span;
        var pos = rangeStart;

        while (pos < rangeEnd)
        {
            var b = span[pos];

            if (PdfLexer.IsWhitespaceByte(b)) { pos++; continue; }

            if (b == (byte)'%')
            {
                pos++;
                while (pos < rangeEnd && span[pos] is not 10 and not 13) pos++;
                continue;
            }

            if (b == (byte)'(')
            {
                pos = Math.Min(rangeEnd, SkipBalancedLiteralString(span, pos));
                continue;
            }

            if (IsDigitByte(b) && TryMatchObjectHeaderShape(span, pos, out var objNum, out var generation, out _))
            {
                var probeParser = new PdfObjectParser(data, pos);
                try
                {
                    var probe = probeParser.ProbeIndirectObjectHeader();
                    charge(probeParser.Position - pos);

                    if (probe.ObjectNumber == objNum)
                    {
                        var dict = probe.Value as PdfDictionary;
                        if (probe.HasStreamBody)
                        {
                            var absBodyStart = probe.StreamBodyStart; // probe positions are absolute (full-buffer parser)
                            var bodyEnd = TryResolveDeclaredLength(dict!, confirmedIntegers, pos, out var len)
                                && TryVerifyLengthWithNearMiss(data, absBodyStart, len, charge, out var verifiedEnd)
                                    ? verifiedEnd
                                    : rangeEnd; // bounded by the secondary region itself — no scan.
                            secondaryExtents.Add(new ObjectExtent(objNum, generation, pos, absBodyStart, bodyEnd, true, dict));
                            pos = Math.Min(rangeEnd, Math.Max(bodyEnd, probeParser.Position));
                        }
                        else
                        {
                            secondaryExtents.Add(new ObjectExtent(objNum, generation, pos, 0, 0, false, dict));
                            pos = Math.Min(rangeEnd, probeParser.Position);
                        }
                        continue;
                    }
                }
                catch (InvalidDataException)
                {
                    charge(Math.Max(1, probeParser.Position - pos));
                }
            }

            pos++;
        }
    }

    // ── A5: trailer recovery ─────────────────────────────────────────────────

    /// <summary>
    /// A5: recovers a trailer dictionary. Candidates are every confirmed stream/dictionary extent's
    /// own dictionary region (the layout a cross-reference stream uses: its dictionary IS the
    /// trailer, no separate "trailer" section at all) plus every classic "trailer&lt;&lt;...&gt;&gt;"
    /// section the walk found. Each of a fixed set of keys is resolved independently,
    /// highest-offset candidate wins, rather than taking one whole "newest" dictionary: ISO
    /// 32000-2 §F.3.5 puts a linearized file's /Encrypt and /Root in the front first-page trailer
    /// while a tail cross-reference stream may carry only /ID, so the newest whole dictionary is
    /// the wrong unit to prefer.
    /// </summary>
    private static PdfDictionary RecoverTrailer(
        Dictionary<int, XrefEntry> xref, Dictionary<int, ObjectExtent> primaryByObjNum,
        List<ObjectExtent> secondaryExtents, List<(int Offset, PdfDictionary Dict)> trailerCandidates,
        bool wholeFileEncryptionEvidence)
    {
        var allTrailerCandidates = new List<(int Offset, PdfDictionary Dict)>(trailerCandidates);
        foreach (var e in primaryByObjNum.Values)
        {
            if (e.HasBody && e.Dictionary is not null)
                allTrailerCandidates.Add((e.DictStart, e.Dictionary));
        }

        // Sticky /Encrypt, mirroring XrefParser.ParseRevisionChain's anyRevisionDeclaredEncrypt:
        // ANY candidate declaring it is enough, even one that loses every per-key vote below.
        // Opening the document as plaintext is the one outcome that must not happen. Secondary
        // (quarantined) extents are checked here too, on the same asymmetry reasoning as the
        // structural check just below.
        var anyDeclaredEncrypt = allTrailerCandidates.Exists(c => c.Dict.Get(_encryptKey) is not null)
            || secondaryExtents.Exists(e => e.Dictionary is not null && e.Dictionary.Get(_encryptKey) is not null);

        // Structural last resort — see HasPr2EncryptionEvidenceShape for the rule and the /ByteRange
        // exclusion it carries. Secondary-quarantined extents are included on purpose: excluded
        // everywhere else, but not here — the asymmetry a false negative would create (silent
        // ciphertext) is worse than the false positive a coincidental match produces (refuses a
        // plaintext file).
        var structuralEncrypt =
            primaryByObjNum.Values.Any(e => e.Dictionary is not null && HasPr2EncryptionEvidenceShape(e.Dictionary))
            || secondaryExtents.Exists(e => e.Dictionary is not null && HasPr2EncryptionEvidenceShape(e.Dictionary));

        // This PR is plaintext-only. A false positive here refuses a file; a false negative would
        // hand back ciphertext as if it were content, which is the one outcome that must not happen
        // — so evidence either way throws rather than guessing. A later PR removes this throw
        // without needing to restructure anything above it: the per-key /Encrypt value is already
        // resolved below, exactly as every other recoverable key is.
        //
        // wholeFileEncryptionEvidence (C1) is the backstop for the other two checks: both only see
        // dictionaries the walk actually tokenized, and the walk can jump straight over a region —
        // an unresolved stream's body runs to EOF, an unterminated string consumes to EOF — without
        // ever tokenizing what's inside it. Without this third check, a file whose /Encrypt
        // declaration or Standard-handler dictionary sits in exactly such a swallowed region reaches
        // this line with both of the other two flags false and opens as plaintext over ciphertext.
        if (anyDeclaredEncrypt || structuralEncrypt || wholeFileEncryptionEvidence)
            throw new UnsupportedPdfFeatureException(
                "Malformed PDF: reconstruction found evidence that this document is encrypted "
                + "(a candidate trailer declares /Encrypt, or an object's structure matches a "
                + "security handler's encryption dictionary). Rebuilding the cross-reference table "
                + "of an encrypted document is not supported yet.");

        var trailer = new PdfDictionary();
        foreach (var key in _recoverableTrailerKeys)
        {
            PdfObject? winner = null;
            var winnerOffset = -1;
            foreach (var (offset, dict) in allTrailerCandidates)
            {
                var value = dict.Get(key);
                if (value is not null && offset >= winnerOffset)
                {
                    winner = value;
                    winnerOffset = offset;
                }
            }
            if (winner is not null)
                trailer.Set(key, winner);
        }

        // A5b is implicit above: the trailer is built key by key from a fixed allow-list, never by
        // cloning a candidate dictionary wholesale, so a real "trailer<<...>>" section's own /Prev
        // or /XRefStm — advertising a revision chain this pass deliberately does not walk — is
        // simply never copied.

        // A5c: /Size is author-controlled at the best of times, and a reconstructed document has no
        // author-declared value worth trusting when no candidate above supplied one.
        if (trailer.Get(PdfName.Size) is null)
        {
            var maxObjNum = 0;
            foreach (var num in xref.Keys)
                if (num > maxObjNum) maxObjNum = num;
            trailer.Set(PdfName.Size, (long)maxObjNum + 1);
        }

        return trailer;
    }

    /// <summary>
    /// Structural classification of a candidate encryption dictionary (ISO 32000-2 §7.6.5.2,
    /// Table 20/Table 27). <c>/Filter</c> (a name) + <c>/V</c> (an integer) are the only two keys
    /// guaranteed present in both the Standard and a public-key handler's dictionary; neither
    /// disambiguator present classifies as <see cref="EncryptionDictionaryClass.None"/> — a
    /// false-positive guard PR3 relies on for its own T9, where a false positive means refusing to
    /// OPEN a document that would otherwise decrypt cleanly. PR2 has no such document to protect
    /// (it never decrypts anything), so PR2's own evidence gate does not call this method; see
    /// <see cref="HasPr2EncryptionEvidenceShape"/> instead. Kept here, unused by PR2's production
    /// path, so PR3 reuses this exact disambiguation rather than re-deriving it.
    /// </summary>
    internal static EncryptionDictionaryClass ClassifyEncryptionDictionary(PdfDictionary dict)
    {
        if (dict.Get(_filterKey) is not PdfName || dict.Get(_vKey) is not PdfInteger)
            return EncryptionDictionaryClass.None;

        if (dict.Get(_rKey) is not null && dict.Get(_oKey) is not null && dict.Get(_uKey) is not null)
            return EncryptionDictionaryClass.StandardHandler;

        if (dict.Get(_subFilterKey) is PdfName subFilter
            && subFilter.Value is "adbe.pkcs7.s3" or "adbe.pkcs7.s4" or "adbe.pkcs7.s5")
            return EncryptionDictionaryClass.PublicKeyHandler;

        return EncryptionDictionaryClass.None;
    }

    /// <summary>
    /// PR2's own encryption-evidence threshold — deliberately broader than
    /// <see cref="ClassifyEncryptionDictionary"/>'s disambiguation. A false positive here only
    /// costs a refusal (this PR never opens an encrypted document either way), so the bare
    /// <c>/Filter</c> (a name) + <c>/V</c> (an integer) pair from ISO 32000-2 §7.6.5.2 is evidence
    /// on its own, with no disambiguator required: Table 20 makes <c>/SubFilter</c> optional and
    /// Table 21 makes <c>/O</c>/<c>/U</c>/<c>/R</c> Standard-handler-only, so a minimal public-key
    /// dictionary can legally carry neither and still be a real one (row 2 — a top-level
    /// <c>&lt;&lt; /Filter /Adobe.PubSec /V 1 &gt;&gt;</c> with no other key at all). The asymmetry
    /// this whole method exists to honour: a false positive refuses a plaintext file; a false
    /// negative would open ciphertext as if it were content, so reconstruction takes the former
    /// every time.
    /// <para>
    /// NARROW exclusion: a dictionary carrying <c>/ByteRange</c> is excluded only when it carries
    /// NONE of the keys an encryption dictionary might have (<c>/R</c>, <c>/O</c>, <c>/U</c>,
    /// <c>/SubFilter</c>, <c>/CF</c>, <c>/StmF</c>, <c>/StrF</c>, <c>/Recipients</c>,
    /// <c>/EncryptMetadata</c>). <c>/ByteRange</c> alone looks like a signature dictionary's own
    /// shape — a working signature requires it (ISO 32000-1 §12.8.1), and no Table 20/21/23
    /// encryption dictionary has one — so without SOME exclusion a damaged but otherwise ordinary
    /// SIGNED plaintext file (a signature dictionary's own <c>/Filter</c> is a name like
    /// <c>/Adobe.PPKLite</c>, and a <c>/V</c> integer sometimes sits beside it too) becomes
    /// unrecoverable: exactly the fails-on-ordinary-files defect class this rework exists to end.
    /// But excluding on <c>/ByteRange</c> ALONE, regardless of what else is present, defeats the
    /// check for a REAL encryption dictionary: a conforming reader ignores an unrecognised
    /// <c>/ByteRange</c> entry sitting beside <c>/R</c>/<c>/O</c>/<c>/U</c> and decrypts anyway, so
    /// a trailer-destroyed file whose only surviving encryption dictionary happens to carry a
    /// planted or coincidental <c>/ByteRange</c> would otherwise open as plaintext with this the
    /// only line of defense left (no trailer survives to declare <c>/Encrypt</c>, so the sticky
    /// declared-<c>/Encrypt</c> path above never fires either).
    /// </para>
    /// <para>
    /// The asymmetry this whole method exists to honour applies to the narrowing too: a false
    /// positive refuses a plaintext file; a false negative would open ciphertext as if it were
    /// content, so this errs toward treating <c>/ByteRange</c>-plus-an-encryption-key as evidence
    /// rather than toward excluding it.
    /// </para>
    /// <para>
    /// Flag for adversarial review: an attacker could plant <c>/ByteRange</c> inside a REAL
    /// encryption dictionary specifically to dodge this check — but doing so now requires ALSO
    /// stripping every one of the nine encryption keys above, which stops being an encryption
    /// dictionary the Standard or a public-key handler would recognise at all. And this exclusion
    /// only matters when no candidate trailer declares <c>/Encrypt</c> at all — the sticky
    /// declared-<c>/Encrypt</c> path (<c>anyDeclaredEncrypt</c> above, and its exhaustion-path twin)
    /// fires independently of this method and still refuses regardless of what this returns.
    /// </para>
    /// </summary>
    private static bool HasPr2EncryptionEvidenceShape(PdfDictionary dict)
    {
        if (dict.Get(_filterKey) is not PdfName || dict.Get(_vKey) is not PdfInteger)
            return false;

        if (dict.Get(_byteRangeKey) is null)
            return true;

        return dict.Get(_rKey) is not null
            || dict.Get(_oKey) is not null
            || dict.Get(_uKey) is not null
            || dict.Get(_subFilterKey) is not null
            || dict.Get(_cfKey) is not null
            || dict.Get(_stmFKey) is not null
            || dict.Get(_strFKey) is not null
            || dict.Get(_recipientsKey) is not null
            || dict.Get(_encryptMetadataKey) is not null;
    }

    // ── A6: candidate roots ──────────────────────────────────────────────────

    /// <summary>
    /// A6: builds the ordered candidate-root list Phase B walks — inference, not recovery, since
    /// this pass cannot corroborate a PACKED /Pages and has no way to check its own answer at all
    /// until objects can actually be resolved. Built entirely from confirmed extents (row 10: no
    /// text scan exists here at all — every earlier draft's <c>/Catalog</c> substring search is
    /// gone, since every dictionary this could name was already parsed during the walk).
    /// <para>
    /// H1: returns EVERY confirmed /Type /Catalog candidate, not one slot per tier. An earlier
    /// version kept a single <c>corroborated</c> and a single <c>bare</c> reference, each
    /// overwritten by every later match in that tier — so a genuine catalog corroborated only by a
    /// packed /Pages could lose its slot to a later bare-catalog decoy, and Phase B's B2 two-pass
    /// re-validation (which walks this list looking for the first candidate that actually resolves)
    /// had nothing left to fall back to. The list here carries every candidate instead: the
    /// trailer's own recovered /Root (from A5, when some candidate declared one) leads, then every
    /// corroborated candidate (a /Type /Catalog whose /Pages names a top-level object that is
    /// itself /Type /Pages), then every bare /Type /Catalog with no such corroboration — WITHIN each
    /// tier, ordered by <c>DictStart</c> DESCENDING (latest definition in the file first, matching
    /// A3's last-definition-wins rule for which object number means what). This also fixes M1: the
    /// old single-slot version tie-broke on <c>primaryByObjNum.Values</c> enumeration order, which
    /// is first-insertion for a <see cref="Dictionary{TKey, TValue}"/> and not even a guaranteed
    /// .NET behaviour — sorting by file position explicitly makes election deterministic regardless
    /// of dictionary implementation.
    /// </para>
    /// </summary>
    private static List<PdfIndirectReference> BuildCandidateRoots(
        Dictionary<int, XrefEntry> xref, Dictionary<int, ObjectExtent> primaryByObjNum, PdfDictionary trailer)
    {
        var roots = new List<PdfIndirectReference>();
        // Dedup via a HashSet, not List.Contains: a candidate-catalog decoy flood (measured: 80k
        // bare-catalog decoys in a ~3 MB file, well under budget) turned the old List.Contains
        // per-candidate scan into O(k^2) in candidate count — 19s for that input — and this loop
        // runs entirely AFTER the walk, so nothing charges it against the byte budget either. A set
        // membership check is O(1), restoring the linear cost the rest of this pass keeps to; the
        // List stays for output ORDER (corroborated tier before bare, each sorted by file
        // position), which a HashSet alone cannot preserve.
        var seen = new HashSet<PdfIndirectReference>();
        if (trailer.Get(PdfName.Root) is PdfIndirectReference declared)
        {
            roots.Add(declared);
            seen.Add(declared);
        }

        var corroborated = new List<(int DictStart, PdfIndirectReference Reference)>();
        var bare = new List<(int DictStart, PdfIndirectReference Reference)>();

        foreach (var extent in primaryByObjNum.Values)
        {
            if (extent.Dictionary is not { } dict)
                continue;
            if (dict.Get(PdfName.Type) is not PdfName t || !t.Equals(PdfName.Catalog))
                continue;

            var reference = MakeReference(xref, extent.ObjNum);

            if (dict.Get(PdfName.Pages) is PdfIndirectReference pagesRef
                && primaryByObjNum.TryGetValue(pagesRef.ObjectNumber, out var pagesExtent)
                && pagesExtent.Dictionary is { } pagesDict
                && pagesDict.Get(PdfName.Type) is PdfName pagesType && pagesType.Equals(PdfName.Pages))
            {
                corroborated.Add((extent.DictStart, reference));
            }
            else
            {
                bare.Add((extent.DictStart, reference));
            }
        }

        corroborated.Sort((a, b) => b.DictStart.CompareTo(a.DictStart));
        bare.Sort((a, b) => b.DictStart.CompareTo(a.DictStart));

        foreach (var (_, reference) in corroborated)
        {
            if (seen.Add(reference))
                roots.Add(reference);
        }
        foreach (var (_, reference) in bare)
        {
            if (seen.Add(reference))
                roots.Add(reference);
        }

        return roots;
    }

    private static PdfIndirectReference MakeReference(Dictionary<int, XrefEntry> xref, int objNum)
    {
        var generation = xref.TryGetValue(objNum, out var entry) && entry.Generation is >= 0 and <= 65535
            ? entry.Generation
            : 0; // XrefEntry.UnknownGeneration (or a genuinely absent entry) — 0 is safe here: the
                 // resolver treats an UnknownGeneration xref entry as matching any requested
                 // generation (PdfDocumentReader.Resolve), so this value is never checked.
        return new PdfIndirectReference(objNum, generation);
    }

    // ── Exhaustion (un-starvable encryption evidence) ───────────────────────────

    /// <summary>
    /// The walk's cost budget ran out. Encryption-evidence detection must complete regardless of
    /// any cap: (1) stop; (2) evaluate evidence over everything parsed so far (the same sticky
    /// /Encrypt and structural checks <see cref="RecoverTrailer"/> runs, over whatever candidates
    /// exist at this point), PLUS the whole-file sweep (<see cref="ScanWholeFileForEncryptionEvidence"/>)
    /// computed once up front in <see cref="Reconstruct"/> — L2: that sweep is what closes the gap
    /// the un-walked-SUFFIX-only checks below leave open, since a region the walk jumped over in
    /// the MIDDLE of the file (an unresolved stream body, say) is neither "parsed so far" nor part
    /// of the suffix past <paramref name="cursorPos"/>; (3) sweep the un-walked tail with
    /// <see cref="ScanRemainderForEncryptionEvidenceRaw"/> — deliberately over-broad, uncharged, and
    /// therefore impossible to starve by spending the budget before reaching it; (4) evidence
    /// throws <see cref="UnsupportedPdfFeatureException"/>, otherwise <see cref="InvalidDataException"/>
    /// naming the cost budget. A false positive here refuses a plaintext file; a false negative
    /// would hand back ciphertext unexamined — the asymmetry this whole path exists to avoid.
    /// </summary>
    private static void ThrowOnExhaustion(
        ReadOnlyMemory<byte> data, int cursorPos, long budget, Dictionary<int, XrefEntry> xref,
        Dictionary<int, ObjectExtent> primaryByObjNum, List<ObjectExtent> secondaryExtents,
        List<(int Offset, PdfDictionary Dict)> trailerCandidates, bool wholeFileEncryptionEvidence)
    {
        var declaredEvidence = trailerCandidates.Exists(c => c.Dict.Get(_encryptKey) is not null)
            || primaryByObjNum.Values.Any(e => e.HasBody && e.Dictionary is not null && e.Dictionary.Get(_encryptKey) is not null)
            || secondaryExtents.Exists(e => e.Dictionary is not null && e.Dictionary.Get(_encryptKey) is not null);

        var structuralEvidence =
            primaryByObjNum.Values.Any(e => e.Dictionary is not null && HasPr2EncryptionEvidenceShape(e.Dictionary))
            || secondaryExtents.Exists(e => e.Dictionary is not null && HasPr2EncryptionEvidenceShape(e.Dictionary));

        var rawTailEvidence = ScanRemainderForEncryptionEvidenceRaw(data.Span[Math.Clamp(cursorPos, 0, data.Length)..]);

        if (declaredEvidence || structuralEvidence || rawTailEvidence || wholeFileEncryptionEvidence)
            throw new UnsupportedPdfFeatureException(
                "Malformed PDF: reconstruction's cost budget ran out, but the file also carries "
                + "evidence that it is encrypted. Rebuilding the cross-reference table of an "
                + "encrypted document is not supported yet.");

        throw new InvalidDataException(
            $"Malformed PDF: reconstruction could not scan the file within its cost budget "
            + $"({budget} bytes). This looks like a file deliberately padded with decoy object "
            + "headers or nested constructs rather than a document with a merely damaged "
            + "cross-reference table.");
    }

    /// <summary>
    /// C1: a whole-buffer, UNCHARGED encryption-evidence sweep, independent of anything the walk
    /// itself confirmed or tokenized. The walk can jump straight over large regions and never
    /// re-examine them — an unresolved stream whose <c>/Length</c> can't be verified and has no
    /// scannable terminator runs to EOF (<see cref="ResolveStreamExtent"/>'s own fallback), and an
    /// unterminated literal or hex string consumes to EOF too
    /// (<see cref="SkipBalancedLiteralString"/>/<see cref="SkipHexString"/>) — so a file's own
    /// <c>/Encrypt</c> trailer entry or Standard-handler encryption dictionary can sit entirely
    /// inside a region the walk never tokenizes. Relying only on evidence gathered from what the
    /// walk actually visited lets such a file complete the walk normally, build a plaintext-looking
    /// trailer with no <c>/Encrypt</c> anywhere the walk saw, and open with every stream handed back
    /// as ciphertext — silently, since nothing downstream can tell content from noise. This method
    /// runs unconditionally, called once from <see cref="Reconstruct"/> before the walk even starts
    /// (it depends only on <paramref name="span"/>, not on walk state), on EVERY completion path —
    /// not only when the cost budget is exhausted.
    /// <para>
    /// Two signals, each an escape-decoded, word-bounded PDF name token
    /// (<see cref="ContainsWordBoundedEscapedNameToken"/> — ISO 32000-2 §7.3.5's <c>#XX</c> hex
    /// escape decoded before comparison, not a bare substring match): the name <c>Encrypt</c>
    /// anywhere in the file, or the co-occurrence of <c>O</c>, <c>U</c> and <c>R</c> — the Standard
    /// handler's Table 21 fingerprint (§7.6.5.2) — which catches an encryption dictionary sitting
    /// in a swallowed region even when nothing anywhere declares it as <c>/Encrypt</c>. Escape
    /// decoding matters here specifically: the real parser (<c>PdfObjectParser.ParseName</c>)
    /// decodes <c>/Encryp#74</c> to the name <c>Encrypt</c>, so a byte-literal search for the
    /// literal ASCII bytes <c>/Encrypt</c> misses a file whose trailer spells it with an escape —
    /// exactly the region this sweep exists to catch, since the walk-based checks
    /// (<c>anyDeclaredEncrypt</c>, <see cref="HasPr2EncryptionEvidenceShape"/>) already see through
    /// escapes by construction (they compare the PARSED dictionary, not raw bytes) and only need
    /// backup here for what the walk never reached at all. Deliberately NOT <c>/Filter</c>+<c>/V</c>
    /// here: <c>/Filter</c> is on every Flate-compressed stream in an ordinary, undamaged plaintext
    /// PDF, so that pair alone would refuse routine documents that were never encrypted at all. The
    /// narrower <c>/Filter</c>+<c>/V</c> co-occurrence check stays exactly where it already lived —
    /// the small exhaustion-tail remainder below (<see cref="ScanRemainderForEncryptionEvidenceRaw"/>)
    /// — where a false positive costs nothing further (that path is about to throw regardless) and
    /// a broader net is worth it precisely because the swept region is small.
    /// </para>
    /// <para>
    /// Accepted false positive: a contrived plaintext file that happens to contain bare <c>/O</c>,
    /// <c>/U</c> and <c>/R</c> name tokens with no encryption dictionary behind any of them — inside
    /// a literal string, say — refuses under this check even though it was never encrypted. That is
    /// the documented refusal asymmetry this whole PR takes throughout (a false positive refuses a
    /// plaintext file; a false negative would open ciphertext as if it were content), and it is
    /// rarer in practice than the <c>/Filter</c>+<c>/V</c> alternative rejected above, since
    /// <c>/Filter</c> alone appears on essentially every compressed stream in an ordinary document
    /// while three single-letter names co-occurring outside an encryption dictionary do not.
    /// </para>
    /// <para>
    /// This is a PR2 REFUSAL signal only. PR3, which lifts the refusal once reconstruction can also
    /// authenticate, must NOT turn this into an authentication trigger: a plaintext file that
    /// happens to contain the literal (or escaped) bytes <c>/Encrypt</c> — in a comment, a string,
    /// or dead content the walk never resolves to anything — has to stay openable once the refusal
    /// is gone, not be misread as encrypted because this sweep once found the bytes.
    /// </para>
    /// </summary>
    private static bool ScanWholeFileForEncryptionEvidence(ReadOnlySpan<byte> span)
    {
        if (ContainsWordBoundedEscapedNameToken(span, "Encrypt"u8))
            return true;

        return ContainsWordBoundedEscapedNameToken(span, "O"u8)
            && ContainsWordBoundedEscapedNameToken(span, "U"u8)
            && ContainsWordBoundedEscapedNameToken(span, "R"u8);
    }

    // A raw sweep only ever needs to decode-match short targets ("Encrypt" is the longest, at 7
    // bytes); decoding a #XX escape can only shrink a name's byte count (3 raw bytes -> 1 decoded
    // byte), never grow it, so no raw token longer than this could ever decode down to one of
    // those targets. Skipping a longer token outright, rather than decoding it anyway, keeps a
    // single pathologically long name from costing more than a bounds check.
    private const int MaxRawNameTokenBytesForEvidenceMatch = 32;

    /// <summary>
    /// True when the decoded form of <paramref name="decodedTarget"/> (a name's bytes WITHOUT the
    /// leading <c>/</c>, e.g. <c>"O"u8</c> for the name <c>/O</c>) occurs anywhere in
    /// <paramref name="span"/> as a genuine PDF name token. Delegates to
    /// <see cref="IndexOfWordBoundedEscapedNameToken"/>; see that method for how a token is found
    /// and decoded.
    /// </summary>
    private static bool ContainsWordBoundedEscapedNameToken(ReadOnlySpan<byte> span, ReadOnlySpan<byte> decodedTarget) =>
        IndexOfWordBoundedEscapedNameToken(span, decodedTarget) >= 0;

    /// <summary>
    /// The offset of the leading <c>/</c> of the first PDF name token in <paramref name="span"/>
    /// whose DECODED bytes equal <paramref name="decodedTarget"/>, or -1. A name in PDF syntax is
    /// self-delimiting — <c>/</c> is itself one of ISO 32000-2 Table 2's delimiter bytes, so a name
    /// token can never start in the middle of another regular-character run — so every <c>/</c>
    /// byte in the buffer is unambiguously a token start, with no separate "preceded by
    /// whitespace/delimiter" check needed (unlike a plain substring search). Each token's bytes run
    /// to the next delimiter or whitespace, then <see cref="DecodePdfNameToken"/> decodes any
    /// <c>#XX</c> hex escape (ISO 32000-2 §7.3.5) exactly as <c>PdfObjectParser.ParseName</c> does,
    /// so <c>/Encryp#74</c> compares equal to the decoded target <c>Encrypt</c> the same way the
    /// real parser would resolve it — closing the gap a byte-literal search left: an encrypted file
    /// whose trailer spells its <c>/Encrypt</c> declaration with an escape, sitting in a region the
    /// walk itself never tokenizes, used to evade this backstop entirely and open as plaintext over
    /// ciphertext.
    /// <para>
    /// Single forward O(N) pass: token regions are disjoint (each byte belongs to at most one
    /// token, since a token ends at the very next delimiter — including another <c>/</c>, which
    /// simultaneously ends one token and starts the next), so the total work across every token
    /// examined is bounded by the buffer length regardless of how many <c>/</c> bytes it contains.
    /// </para>
    /// </summary>
    private static int IndexOfWordBoundedEscapedNameToken(ReadOnlySpan<byte> span, ReadOnlySpan<byte> decodedTarget)
    {
        Span<byte> decodeBuf = stackalloc byte[MaxRawNameTokenBytesForEvidenceMatch];
        var pos = 0;
        while (pos < span.Length)
        {
            if (span[pos] != (byte)'/')
            {
                pos++;
                continue;
            }

            var tokenStart = pos + 1;
            var tokenEnd = tokenStart;
            while (tokenEnd < span.Length && !IsDelimiterOrWhitespaceByte(span[tokenEnd]))
                tokenEnd++;

            var rawLen = tokenEnd - tokenStart;
            if (rawLen > 0 && rawLen <= decodeBuf.Length)
            {
                var decodedLen = DecodePdfNameToken(span.Slice(tokenStart, rawLen), decodeBuf);
                if (decodeBuf[..decodedLen].SequenceEqual(decodedTarget))
                    return pos;
            }

            pos = tokenEnd;
        }
        return -1;
    }

    /// <summary>
    /// Decodes <c>#XX</c> hex escapes in a raw PDF name token's bytes (ISO 32000-2 §7.3.5),
    /// mirroring <c>PdfObjectParser.ParseName</c>'s own decode loop — including its bounds check —
    /// so this raw, byte-blind sweep resolves an escaped name exactly as the real parser would.
    /// <paramref name="raw"/> excludes the leading <c>/</c>. Returns the number of bytes written to
    /// <paramref name="decoded"/>, which must be at least as long as <paramref name="raw"/>
    /// (decoding only ever shrinks a name's byte count, never grows it).
    /// </summary>
    private static int DecodePdfNameToken(ReadOnlySpan<byte> raw, Span<byte> decoded)
    {
        var len = 0;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == (byte)'#' && i + 2 < raw.Length)
            {
                var hi = HexDigitValue(raw[i + 1]);
                var lo = HexDigitValue(raw[i + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    decoded[len++] = (byte)((hi << 4) | lo);
                    i += 3;
                    continue;
                }
            }
            decoded[len++] = c;
            i++;
        }
        return len;
    }

    private static int HexDigitValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// Deliberately over-broad, uncharged raw sweep of whatever the walk had not yet reached when
    /// its budget ran out — never invoked outside <see cref="ThrowOnExhaustion"/>. Looks for the
    /// escape-decoded name <c>Encrypt</c> (see <see cref="ContainsWordBoundedEscapedNameToken"/> —
    /// the same #XX-escape defence <see cref="ScanWholeFileForEncryptionEvidence"/> needs, for the
    /// same reason: a byte-literal search misses <c>/Encryp#74</c>), or <c>Filter</c> anywhere
    /// followed later by <c>V</c> — ISO 32000-2 §7.6.5.2's own minimum common shape. A crafted file
    /// could still make this over-trigger on coincidental bytes; that is an accepted false
    /// positive, not a bug, because the alternative — a scan precise enough to be gamed into
    /// finding nothing — is exactly the starvation this exists to close off. Uncharged so a decoy
    /// cannot exhaust the budget before this sweep gets to run. Narrower in scope than
    /// <see cref="ScanWholeFileForEncryptionEvidence"/>: this one only ever sees the un-walked tail
    /// past where the budget ran out, not the whole file, which is exactly why it can afford the
    /// broader /Filter+/V signal that method deliberately avoids.
    /// </summary>
    private static bool ScanRemainderForEncryptionEvidenceRaw(ReadOnlySpan<byte> tail)
    {
        if (ContainsWordBoundedEscapedNameToken(tail, "Encrypt"u8))
            return true;

        var filterIdx = IndexOfWordBoundedEscapedNameToken(tail, "Filter"u8);
        if (filterIdx < 0)
            return false;

        return ContainsWordBoundedEscapedNameToken(tail[filterIdx..], "V"u8);
    }

    // ── Pass 0 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single forward O(N) pass recording every offset at which the literal token
    /// <c>endstream</c> occurs in <paramref name="data"/> — real terminator, decoy, or coincidence
    /// alike; extent resolution above is what tells them apart, not this scan. Each occurrence also
    /// records whether it is line-initial (preceded by CR, LF, or nothing at all — offset 0), per
    /// ISO 32000-2 Annex C.4 (informative): a reconstructing reader "can" rely on <c>endstream</c>
    /// being placed at the start of a line, and a well-behaved producer avoids stream data that
    /// itself begins a line with that literal word. Both returned lists are already sorted
    /// ascending, since a forward scan can only find offsets in increasing order.
    /// </summary>
    private static (List<int> All, List<int> LineInitial) ScanEndstreamOccurrences(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        var all = new List<int>();
        var lineInitial = new List<int>();
        var pos = 0;

        while (pos < span.Length)
        {
            var idx = span[pos..].IndexOf((ReadOnlySpan<byte>)EndstreamMarker);
            if (idx < 0)
                break;

            var offset = pos + idx;
            var after = offset + EndstreamMarker.Length;
            if (after >= span.Length || IsDelimiterOrWhitespaceByte(span[after]))
            {
                all.Add(offset);
                if (offset == 0 || span[offset - 1] is (byte)'\n' or (byte)'\r')
                    lineInitial.Add(offset);
            }

            pos = offset + EndstreamMarker.Length; // 'endstream' cannot meaningfully overlap itself.
        }

        return (all, lineInitial);
    }

    // ── Small shared parsing/search primitives ──────────────────────────────────

    private static (int NewPos, PdfObject? Value) ParseObjectCharged(
        ReadOnlyMemory<byte> data, int startPos, Action<long> charge)
    {
        // C3: a 'trailer' keyword landing exactly at EOF is word-bounded by definition (there is no
        // byte after it to fail a delimiter/whitespace check against — see TryMatchKeyword), so the
        // main walk can call in here with startPos == data.Length. Reading data.Span[startPos] in
        // the catch block below would then index one past the end (IndexOutOfRangeException,
        // reachable from public PdfReader.Open on a bare 7-byte "trailer" input). Nothing to parse
        // at EOF anyway, so return immediately rather than ever constructing a parser over it.
        if (startPos >= data.Length)
            return (data.Length, null);

        var parser = new PdfObjectParser(data, startPos);
        try
        {
            var value = parser.ParseObject();
            charge(Math.Max(0, parser.Position - startPos));
            return (parser.Position, value);
        }
        catch (InvalidDataException)
        {
            charge(Math.Max(1, parser.Position - startPos));
            // Resync minimally past the failure — +2 past a failed '<<' start, +1 otherwise —
            // rather than trusting the parser's own position as a safe resumption point, so
            // whatever sits inside the failed construct is re-tokenised by the ordinary walk
            // instead of being silently skipped as one opaque unit (rows 6/7).
            var resyncBy = data.Span[startPos] == (byte)'<' ? 2 : 1;
            return (startPos + resyncBy, null);
        }
    }

    private static int SkipBalancedLiteralString(ReadOnlySpan<byte> span, int start)
    {
        var pos = start + 1; // consume '('
        var depth = 1;
        while (pos < span.Length)
        {
            var c = span[pos++];
            if (c == (byte)'\\')
            {
                if (pos < span.Length)
                    pos++; // skip the escaped byte (or first octal digit); balancing parens is all
                           // this walker needs, unlike PdfObjectParser.DecodeLiteralString's full decode.
            }
            else if (c == (byte)'(')
            {
                depth++;
            }
            else if (c == (byte)')')
            {
                depth--;
                if (depth == 0)
                    return pos;
            }
        }

        // Unterminated: consume to EOF rather than guessing where the string "should" have ended —
        // fail closed. The walk cannot resynchronise inside a string of unknown extent, so nothing
        // after it is trusted as a candidate header, trailer, or comment either.
        return span.Length;
    }

    private static int SkipHexString(ReadOnlySpan<byte> span, int start)
    {
        var pos = start + 1; // consume '<'
        while (pos < span.Length && span[pos] != (byte)'>')
            pos++;
        return pos < span.Length ? pos + 1 : span.Length; // unterminated: same fail-closed treatment.
    }

    private static bool TryMatchObjectHeaderShape(
        ReadOnlySpan<byte> span, int start, out int objNum, out int generation, out int afterObj)
    {
        objNum = 0;
        generation = 0;
        afterObj = start;

        var p = start;
        var digitStart = p;
        while (p < span.Length && IsDigitByte(span[p])) p++;
        if (p == digitStart) return false;
        var digitEnd = p;
        if (digitEnd - digitStart > 10) return false;

        var wsMark = p;
        while (p < span.Length && PdfLexer.IsWhitespaceByte(span[p])) p++;
        if (p == wsMark) return false;

        var genStart = p;
        while (p < span.Length && IsDigitByte(span[p])) p++;
        if (p == genStart || p - genStart > 10) return false;
        var genEnd = p;

        wsMark = p;
        while (p < span.Length && PdfLexer.IsWhitespaceByte(span[p])) p++;
        if (p == wsMark) return false;

        if (p + 3 > span.Length || span[p] != (byte)'o' || span[p + 1] != (byte)'b' || span[p + 2] != (byte)'j')
            return false;
        var after = p + 3;
        if (after < span.Length && !IsDelimiterOrWhitespaceByte(span[after]))
            return false;

        if (!TryParseBoundedInt(span[digitStart..digitEnd], out objNum))
            return false;
        generation = TryParseBoundedInt(span[genStart..genEnd], out var g) && g <= 65535
            ? g
            : XrefEntry.UnknownGeneration;
        afterObj = after;
        return true;
    }

    private static bool TryMatchKeyword(ReadOnlySpan<byte> span, int pos, ReadOnlySpan<byte> keyword, out int after)
    {
        after = pos;
        if (pos + keyword.Length > span.Length) return false;
        if (!span.Slice(pos, keyword.Length).SequenceEqual(keyword)) return false;

        var precededOk = pos == 0 || IsDelimiterOrWhitespaceByte(span[pos - 1]);
        var afterIdx = pos + keyword.Length;
        var followedOk = afterIdx >= span.Length || IsDelimiterOrWhitespaceByte(span[afterIdx]);
        if (!precededOk || !followedOk) return false;

        after = afterIdx;
        return true;
    }

    private static bool TryMatchWordBounded(ReadOnlySpan<byte> span, int pos, ReadOnlySpan<byte> marker)
    {
        if (pos < 0 || pos + marker.Length > span.Length) return false;
        if (!span.Slice(pos, marker.Length).SequenceEqual(marker)) return false;
        var after = pos + marker.Length;
        return after >= span.Length || IsDelimiterOrWhitespaceByte(span[after]);
    }

    /// <summary>The first value in <paramref name="sortedAscending"/> strictly greater than <paramref name="threshold"/>, or null.</summary>
    private static int? FirstGreaterThan(List<int> sortedAscending, int threshold)
    {
        var idx = LowerBoundGreaterThan(sortedAscending, threshold);
        return idx < sortedAscending.Count ? sortedAscending[idx] : null;
    }

    /// <summary>The index of the first value in <paramref name="sortedAscending"/> strictly greater than <paramref name="threshold"/>.</summary>
    private static int LowerBoundGreaterThan(List<int> sortedAscending, int threshold)
    {
        var lo = 0;
        var hi = sortedAscending.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sortedAscending[mid] > threshold) hi = mid;
            else lo = mid + 1;
        }
        return lo;
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

    private static bool IsDigitByte(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool IsDelimiterOrWhitespaceByte(byte b) =>
        PdfLexer.IsWhitespaceByte(b) || PdfLexer.IsDelimiterByte(b);
}
