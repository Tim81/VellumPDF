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
/// or checked. Only ONE case is carried into the recovered trailer instead of refusing: a declared
/// <c>/Encrypt</c> in some candidate trailer, or a confirmed object whose structure disambiguates
/// SPECIFICALLY as the Standard handler (<see cref="ClassifyEncryptionDictionary"/>) — see
/// <see cref="RecoverTrailer"/> and its exhaustion-path twin, which make the same decision. Every
/// other encryption-shaped case refuses: a public-key handler (unsupported at authentication
/// regardless), and an encryption-shaped object this pass cannot classify at all — Table 20 makes
/// <c>/Filter</c> and <c>/V</c> the only two Required entries of ANY encryption dictionary, and
/// §7.6.2 leaves everything past those two to the handler, so a bare pair with no further
/// disambiguator is still a legitimate, spec-minimal encryption dictionary this library has never
/// heard of, not proof of an ordinary one — and evidence a whole-file sweep finds in a region the
/// walk never tokenized at all (<see cref="ScanWholeFileForEncryptionEvidence"/> — the
/// <c>/Encrypt</c> name, the Standard handler's <c>/O</c>+<c>/U</c>+<c>/R</c> triad, or a
/// public-key handler's <c>adbe.pkcs7.s3</c>/<c>s4</c>/<c>s5</c> <c>/SubFilter</c>, ISO 32000-2
/// §7.6.5.2), which has nothing to reference either way. Opening as plaintext over ciphertext is
/// never an option, whether or not this pass knows which object to blame or which handler it is.
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
    private static readonly PdfName _wKey = new("W");
    private static readonly PdfName _indexKey = new("Index");

    // The trailer keys reconstruction knows how to recover (A5). Building the recovered trailer by
    // setting exactly these keys — never by cloning a whole candidate dictionary — is what A5b
    // needs: a real "trailer<<...>>" section's own /Prev or /XRefStm (advertising a revision chain
    // this pass deliberately does not walk) is simply never copied in the first place.
    private static readonly PdfName[] _recoverableTrailerKeys =
        [PdfName.Root, _encryptKey, PdfName.ID, PdfName.Info, PdfName.Size];

    // A2's cost ceiling. Ample headroom for a genuinely damaged file's own structure while still
    // bounding a file deliberately padded with decoy headers or nested constructs: the aggregate
    // is what refuses, not any single per-construct cap (rows 1, 3, 5, 14 — none of them survive as
    // a fixed constant in this design; the budget is the only backstop). The 1 MiB floor is fixed —
    // only the multiplier is a caller's choice (see ReaderLimits.ReconstructionBudgetMultiplier);
    // without it a tiny file's budget could be tightened to nothing at all.
    private const long MinReconstructionByteBudget = 1L * 1024 * 1024; // 1 MiB

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
    /// its cost budget with no encryption evidence at all — declared, structural, or raw — anywhere
    /// in the file (see the exhaustion path below).
    /// </exception>
    /// <exception cref="UnsupportedPdfFeatureException">
    /// Thrown when encryption evidence exists but this pass either cannot point a trailer
    /// <c>/Encrypt</c> reference at anything it actually confirmed — a whole-file sweep found the
    /// bytes only in a region the walk never tokenized, or the sole evidence sits in a quarantined
    /// secondary extent — or CAN point at something, but that something does not disambiguate
    /// specifically as the Standard handler (a public-key handler, or a shape this pass cannot
    /// classify at all — see <see cref="RecoverTrailer"/>). Only a declared <c>/Encrypt</c>, or a
    /// confirmed object classifying as Standard, is carried into the recovered trailer instead of
    /// refusing; opening the result still requires authenticating against it
    /// (<see cref="EncryptionSetup.Authenticate"/>).
    /// </exception>
    internal static XrefParseResult Reconstruct(ReadOnlyMemory<byte> data, ReaderLimits limits)
    {
        var length = data.Length;
        var budget = Math.Max(MinReconstructionByteBudget, (long)length * limits.ReconstructionBudgetMultiplier);
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
        try
        {
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
        }
        catch (ReconstructionEvidenceFoundEarlySignal)
        {
            // The walk's budget ran out, but ThrowOnExhaustion found a declared or structurally
            // classifying /Encrypt candidate among what was already confirmed — evidence this pass
            // CAN safely carry (see RecoverTrailer). Stopping the walk here and falling through to
            // the same pipeline a completed walk uses is exactly the "carry" decision RecoverTrailer
            // itself would make; there is nothing left for this catch to decide.
        }

        // Secondary (quarantined) results merge in only after the primary walk completes, and only
        // via TryAdd: a primary definition always wins, matching A3's rule for the confirmed table.
        foreach (var e in secondaryExtents)
            xref.TryAdd(e.ObjNum, XrefEntry.Uncompressed(e.DictStart, e.Generation));

        if (xref.Count == 0)
            throw new InvalidDataException(
                "Malformed PDF: startxref is missing or unusable, and no 'N G obj' object headers "
                + "were found to reconstruct the cross-reference table from.");

        // A5: recover a trailer. Declared or structurally referenceable encryption evidence is
        // carried into it (ClassifyEncryptionDictionary); evidence this pass cannot point a
        // reference at still refuses — see the method's own doc comment for the three-way split.
        var trailer = RecoverTrailer(
            xref, primaryByObjNum, secondaryExtents, trailerCandidates, wholeFileEncryptionEvidence);

        // A6: rank catalog candidates for Phase B (PdfDocumentReader's constructor) to validate —
        // this pass cannot check its own answer, since checking it means resolving objects, which
        // needs authentication to have already happened.
        var candidateRoots = BuildCandidateRoots(xref, primaryByObjNum, trailer);
        if (candidateRoots.Count > 0)
            trailer.Set(PdfName.Root, candidateRoots[0]);

        // A4: only meaningful once /Encrypt actually made it into the recovered trailer above —
        // an unencrypted reconstruction has no cross-reference-stream exemption to compute, since
        // PdfDocumentReader.IsCrossReferenceStream is never consulted when there is no decryptor.
        // Never keyed on a candidate's own /Type /XRef — that key is author-controlled, which is
        // exactly why the real exemption keys on where a stream was actually READ as an xref
        // stream, never on what it claims to be.
        var crossReferenceStreamOffsets = new HashSet<long>();
        if (trailer.Get(_encryptKey) is not null)
        {
            consumed = CollectCrossReferenceStreamOffsets(
                data, primaryByObjNum, secondaryExtents, budget, consumed, crossReferenceStreamOffsets, limits);
        }

        // A reconstructed document has no trustworthy revision history either — the /Prev chain
        // that would normally describe one is exactly what's missing or broken here. An empty
        // list, not a (0, 0) sentinel, is what ObjectLayoutRule already reads as "no revision
        // info, check every object" (Revisions.Count == 0).
        return new XrefParseResult(
            xref, trailer, StartXrefOffset: 0, Revisions: [], CrossReferenceStreamOffsets: crossReferenceStreamOffsets,
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
    /// <para>
    /// PR3's three-way encryption split lives here. (1) A candidate trailer that DECLARES
    /// <c>/Encrypt</c> needs nothing special: it is one of <see cref="_recoverableTrailerKeys"/>
    /// already, so the per-key merge below carries it like any other key once this method simply
    /// does not throw. (2) With nothing declared, a confirmed (primary, non-quarantined) extent
    /// that is encryption-SHAPED (<see cref="HasPr2EncryptionEvidenceShape"/>) AND disambiguates as
    /// the Standard handler (<see cref="ClassifyEncryptionDictionary"/>) gets a synthesized
    /// <c>/Encrypt N G R</c> pointed at it — the trailer-destroyed last resort. (3) Everything else
    /// that is encryption-shaped refuses rather than opens: a public-key dictionary (unsupported at
    /// authentication anyway), and an encryption-shaped dictionary this pass cannot classify at
    /// all. Table 20 makes <c>/Filter</c> and <c>/V</c> the only two Required entries of ANY
    /// encryption dictionary — the spec-minimal shape — and §7.6.2 leaves the rest to the handler
    /// ("the remaining contents of the encryption dictionary shall be determined by the security
    /// handler and may vary"; a processor "can optionally provide additional security handlers of
    /// its own"), so an unclassifiable pair is still a legitimate encryption dictionary this
    /// library has never heard of, not proof the document is safe to open. And the whole-file sweep
    /// (<see cref="ScanWholeFileForEncryptionEvidence"/> — <c>/Encrypt</c>, the Standard triad, or
    /// a public-key <c>/SubFilter</c>) finding one of those fingerprints in a region the walk never
    /// tokenized, or the sole sign sitting in a quarantined secondary extent, refuse the same way:
    /// opening as plaintext over ciphertext is never on the table, whether or not this pass knows
    /// which object to blame, and whether or not it recognises the handler.
    /// </para>
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

        // Declared, from a candidate the per-key merge below actually reads (a real
        // "trailer<<...>>" section, or a cross-reference-stream dictionary playing the trailer
        // role). This is the "safe to carry" case: /Encrypt is already in _recoverableTrailerKeys,
        // so nothing further is needed beyond not throwing.
        var primaryDeclaredEncrypt = allTrailerCandidates.Exists(c => c.Dict.Get(_encryptKey) is not null);

        // Structural fallback (§7.6.5.2, Table 27), searched only when nothing above declared
        // /Encrypt, over confirmed (primary) extents only: a secondary extent's own identity came
        // from a region the primary walk had already judged untrustworthy enough to need a second
        // look (see WalkSecondary's doc comment), so this pass does not point a synthesized
        // reference at one.
        //
        // A two-step gate, not one: HasPr2EncryptionEvidenceShape decides whether a dictionary is
        // encryption-shaped AT ALL (the bare /Filter name + /V integer pair, minus the
        // signature-dictionary shape — PR2's own broad threshold), and only a shaped dictionary is
        // even a candidate for the narrower ClassifyEncryptionDictionary disambiguation. A
        // dictionary that fails the shape check in the first place (an ordinary stream's own
        // /Filter, say — which never carries /V, so it never satisfies this shape at all) is simply
        // not evidence, and reconstruction moves on without it (T9's genuine false-positive case).
        // But once a dictionary IS shaped, "cannot disambiguate which handler" is no longer grounds
        // to treat it as absent: only a Standard-handler match is safe to carry — a public-key one
        // is refused at authentication regardless, and an UNCLASSIFIABLE shaped dictionary is not
        // thereby proven ordinary. Table 20 makes /Filter and /V the only two Required entries of
        // ANY encryption dictionary, and §7.6.2 leaves everything past those two to the handler —
        // "the remaining contents ... shall be determined by the security handler and may vary",
        // and a processor "can optionally provide additional security handlers of its own" — so a
        // bare /Filter+/V pair with no further disambiguator is still a legitimate, spec-minimal
        // encryption dictionary this library has never heard of, and its streams may be ciphertext
        // regardless of whether this pass can name the handler. Opening the rest of the document as
        // plaintext on that gamble is exactly the outcome the whole design forbids, so an
        // encryption-shaped, non-Standard candidate REFUSES instead of being ignored — a real cost
        // (a plaintext file that happens to carry a standalone, non-signature
        // /Filter+/V dictionary is refused too), accepted because the alternative risks handing
        // back ciphertext as content. Latest-in-file Standard match wins when more than one
        // classifies, matching A3/A5's own "later definition wins" convention.
        PdfIndirectReference? structuralReference = null;
        var primaryEncryptionShapedNotStandard = false;
        // The offending dictionary's own /Filter, captured for the refusal message below — a
        // genuinely better diagnostic than a generic refusal, and available for free at the exact
        // point this pass already decided the dictionary is encryption-shaped but not Standard.
        // Left null for the secondary/whole-file-sweep cases, which have no single parsed /Filter
        // to name.
        string? refusedHandlerFilter = null;
        if (!primaryDeclaredEncrypt)
        {
            var bestDictStart = -1;
            foreach (var extent in primaryByObjNum.Values)
            {
                if (extent.Dictionary is not { } dict || !HasPr2EncryptionEvidenceShape(dict))
                    continue;

                if (ClassifyEncryptionDictionary(dict) != EncryptionDictionaryClass.StandardHandler)
                {
                    primaryEncryptionShapedNotStandard = true;
                    refusedHandlerFilter = (dict.Get(_filterKey) as PdfName)?.Value ?? refusedHandlerFilter;
                    continue;
                }

                if (extent.DictStart < bestDictStart)
                    continue;
                bestDictStart = extent.DictStart;
                structuralReference = MakeReference(xref, extent.ObjNum);
            }
        }

        // Evidence this pass either cannot safely point a trailer reference at, or can point at
        // but does not trust to be the Standard handler: a primary extent that is encryption-shaped
        // but not Standard-classified (above), /Encrypt or a classifying structural shape sitting
        // only inside a quarantined secondary extent, or the whole-file raw sweep (C1) finding
        // /Encrypt-shaped tokens in a region the walk never tokenized at all. A primary non-Standard
        // match refuses unconditionally — even alongside a genuine Standard match elsewhere in the
        // same file, since a second, unclassifiable encryption-shaped object is exactly the kind of
        // thing this pass cannot afford to wave through. HasPr2EncryptionEvidenceShape — not
        // ClassifyEncryptionDictionary — governs the secondary check deliberately: a false positive
        // there only costs a refusal (the same asymmetry PR2 always took), so the broader,
        // undisambiguated shape is still the right net for evidence nobody is about to reference by
        // number.
        var unreferenceableEvidence = !primaryDeclaredEncrypt && (
            primaryEncryptionShapedNotStandard
            || (structuralReference is null && (
                secondaryExtents.Exists(e => e.Dictionary is not null && e.Dictionary.Get(_encryptKey) is not null)
                || secondaryExtents.Exists(e => e.Dictionary is not null && HasPr2EncryptionEvidenceShape(e.Dictionary))
                || wholeFileEncryptionEvidence)));

        if (unreferenceableEvidence)
        {
            // Named when a single parsed /Filter actually triggered the refusal (the common,
            // diagnosable case); generic otherwise — the secondary-quarantined and whole-file-sweep
            // paths have no one dictionary to point at.
            var handlerClause = refusedHandlerFilter is { } filterName
                ? $"found an encryption dictionary naming the security handler /{filterName}, which "
                  + "is a public-key or unrecognised handler this pass cannot open"
                : "found evidence that this document is encrypted, but either not in a place this "
                  + "pass can point a trailer reference at, or not disambiguated as the Standard "
                  + "handler";
            throw new UnsupportedPdfFeatureException(
                $"Malformed PDF: reconstruction {handlerClause} (ISO 32000-2 §7.6.5.2, Table 20) — "
                + "opening the file as plaintext over ciphertext is not an option, and this pass "
                + "only decrypts the Standard handler. Rebuilding the cross-reference table of a "
                + "document damaged this badly is not supported.");
        }

        var trailer = new PdfDictionary();
        foreach (var key in _recoverableTrailerKeys)
        {
            PdfObject? winner = null;
            var winnerOffset = -1;
            foreach (var (offset, dict) in allTrailerCandidates)
            {
                var value = dict.Get(key);
                if (value is null || offset < winnerOffset)
                    continue;

                // Table 15: /ID is required to be direct, and unencrypted, whenever /Encrypt is
                // present — an indirect reference, or one with an indirect element, is a shape
                // this pass never resolves pre-auth (EncryptionSetup.GetId0 refuses the same way,
                // as its own backstop). Treating an invalid shape as "no candidate here" lets an
                // EARLIER, valid /ID win instead of a later, malformed one silently shadowing it.
                if (key.Equals(PdfName.ID) && !IsDirectIdArray(value))
                    continue;

                winner = value;
                winnerOffset = offset;
            }
            if (winner is not null)
                trailer.Set(key, winner);
        }

        // The structural fallback only ever fills a gap left by the merge above: a declared
        // /Encrypt already flowed through it (the key is in _recoverableTrailerKeys), so this only
        // fires when nothing declared one at all.
        if (structuralReference is not null && trailer.Get(_encryptKey) is null)
            trailer.Set(_encryptKey, structuralReference);

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
    /// Table 15: whenever /Encrypt is present, /ID must be a direct array whose elements are
    /// themselves direct strings — never an indirect reference at either level. This pass has no
    /// password yet, so it can only ever trust a value it can read without resolving anything.
    /// </summary>
    private static bool IsDirectIdArray(PdfObject value)
    {
        if (value is not PdfArray { Count: > 0 } arr)
            return false;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not (PdfHexString or PdfLiteralString))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Structural classification of a candidate encryption dictionary (ISO 32000-2 §7.6.5.2,
    /// Table 20/Table 27). <c>/Filter</c> (a name) + <c>/V</c> (an integer) are the only two keys
    /// guaranteed present in both the Standard and a public-key handler's dictionary; neither
    /// disambiguator present classifies as <see cref="EncryptionDictionaryClass.None"/>. Only a
    /// <see cref="EncryptionDictionaryClass.StandardHandler"/> result is safe to point a
    /// synthesized <c>/Encrypt</c> reference at (<see cref="RecoverTrailer"/>) — a
    /// <see cref="EncryptionDictionaryClass.None"/> result does NOT mean "not encrypted, open as
    /// plaintext": Table 20 makes <c>/Filter</c> and <c>/V</c> the only two Required entries of ANY
    /// encryption dictionary, and §7.6.2 leaves everything past those two to the handler — a
    /// processor "can optionally provide additional security handlers of [its] own" — so an already
    /// encryption-shaped dictionary (<see cref="HasPr2EncryptionEvidenceShape"/>) that fails to
    /// classify here is a legitimate handler this method just does not recognise, not proof of a
    /// coincidence, and the caller refuses rather than guesses.
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
    /// A deliberately broader encryption-evidence threshold than
    /// <see cref="ClassifyEncryptionDictionary"/>'s disambiguation, and now the FIRST gate
    /// <see cref="RecoverTrailer"/> (and its exhaustion-path twin) apply to any candidate — primary
    /// or quarantined secondary alike — before that narrower classification is even consulted. A
    /// dictionary that fails this shape check is not encryption evidence at all and reconstruction
    /// simply moves on (an ordinary stream's own <c>/Filter</c> never carries a sibling <c>/V</c>,
    /// so it never reaches here); one that passes is evidence regardless of whether it goes on to
    /// classify. A false positive here only costs a refusal, so the bare <c>/Filter</c> (a name) +
    /// <c>/V</c> (an integer) pair is evidence on its own, with no disambiguator required: Table 20
    /// makes <c>/Filter</c> and <c>/V</c> the only two Required entries of ANY encryption
    /// dictionary — that pair alone is already the spec-minimal shape — and §7.6.2 leaves
    /// everything else to the handler, so <c>/SubFilter</c> being optional and <c>/O</c>/<c>/U</c>/
    /// <c>/R</c> being Standard-handler-only (Table 21) both follow: a minimal public-key
    /// dictionary can legally carry neither and still be a real one (row 2 — a top-level
    /// <c>&lt;&lt; /Filter /Adobe.PubSec /V 1 &gt;&gt;</c> with no other key at all), and the same
    /// is true of any other handler's own private, undocumented shape — §7.6.2 lets a security
    /// handler "encrypt any objects that are private to itself", and a processor "can optionally
    /// provide additional security handlers of [its] own". The asymmetry this whole method exists
    /// to honour: a false positive refuses a plaintext file; a false negative would open ciphertext
    /// as if it were content, so
    /// reconstruction takes the former every time — for a PRIMARY candidate, only a
    /// <see cref="EncryptionDictionaryClass.StandardHandler"/> result from
    /// <see cref="ClassifyEncryptionDictionary"/> earns the exception of actually being carried.
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

    // ── A4: cross-reference-stream offsets (PR3) ────────────────────────────────

    /// <summary>
    /// A4: identifies which confirmed stream extents are genuine cross-reference streams, so
    /// <see cref="PdfDocumentReader"/>'s encryption exemption (ISO 32000-2 §7.5.8.2 — a
    /// cross-reference stream is never itself encrypted) still applies to a reconstructed,
    /// encrypted document. Called from <see cref="Reconstruct"/> only once the recovered trailer
    /// actually carries <c>/Encrypt</c> — an unencrypted reconstruction has no exemption to
    /// compute, since nothing downstream ever asks.
    /// <para>
    /// Mirrors <see cref="XrefParser.ParseXrefStream"/>'s own <c>/W</c>, <c>/Size</c>, <c>/Index</c>
    /// validation exactly — the same field-width bounds (0..8, validated as a 64-bit value before
    /// narrowing), the same <c>/Index</c> pair bounds, the same default <c>[0 Size]</c> — and
    /// decodes the body through the identical <see cref="PdfFilters.Decode"/> path. Deliberately
    /// NEVER keyed on a candidate's own <c>/Type /XRef</c>: that key is author-controlled, exactly
    /// why <see cref="PdfDocumentReader.IsCrossReferenceStream"/> and <see cref="CryptFilterResolver"/>
    /// key the real exemption on where a stream was actually read as an xref stream, never on what
    /// it claims to be. A candidate that fails the shape checks, fails to decode, or decodes too
    /// short for its own declared row layout (<c>decoded.Length &gt;= rowSize × totalRows</c>) is
    /// skipped rather than trusted — ciphertext does not usually inflate to a self-consistent
    /// table, but a document large enough to make that a real possibility is exactly why this
    /// checks the decoded length rather than accepting any successful decode outright.
    /// </para>
    /// <para>
    /// Both primary and quarantined secondary extents are examined: unlike synthesizing an
    /// <c>/Encrypt</c> reference (<see cref="RecoverTrailer"/>), where quarantine matters because
    /// the SOURCE object's identity is what is being trusted, this method only ever decides whether
    /// an extent's own body decodes into a self-consistent table — a check the same strict gate
    /// applies to regardless of which walk confirmed it, so including secondary extents finds more
    /// genuine exemptions without weakening it for any of them.
    /// </para>
    /// <para>
    /// Charges against the SAME aggregate budget the walk itself charged against — reusing
    /// <paramref name="consumed"/>/<paramref name="budget"/> — and throws
    /// <see cref="InvalidDataException"/> outright on exhaustion, rather than degrading to a
    /// partial answer, mirroring Phase B's own B1 discipline
    /// (<c>PdfDocumentReader.ReconstructionPhaseB</c>'s comment on
    /// <c>MaxAggregateReconstructionObjStmDecodeBytes</c>): charge BEFORE the expensive step, and
    /// let the charge stand even when that step then fails, so a file built from many bogus
    /// candidates cannot dodge the aggregate cap by having each one fail cheaply.
    /// </para>
    /// <para>
    /// Unlike B1, though, the charge here has to be sized on the OUTPUT a decode could produce,
    /// not the raw input it reads — Phase B's raw-body pre-charge is charging against
    /// <c>MaxAggregateReconstructionObjStmDecodeBytes</c>, a bound on compressed-on-disk bytes, but
    /// this method's cost is <see cref="PdfFilters.Decode"/>'s decompression work, and a Flate or
    /// LZW body's compression ratio is attacker-controlled: a ~500 KB raw body can inflate to the
    /// full <see cref="ReaderLimits.MaxDecodedBytes"/> (512 MiB by default) before <c>Decode</c>
    /// notices and throws. Charging only on a SUCCESSFUL decode's length — this method's own first
    /// cut — let a run of such bombs each burn up to that cap's worth of decompression work
    /// uncharged before failing, since every one of them ends in the SAME
    /// <see cref="InvalidDataException"/> a genuinely malformed candidate does; the aggregate
    /// budget's fail-closed throw was never starved, but it was also never actually reached in time
    /// to matter. So every attempt is pre-charged for the worst case <see cref="PdfFilters.Decode"/>
    /// could produce — <c>min(limits.MaxDecodedBytes, remaining budget)</c> — before it runs, and
    /// only refunded down to the real decoded length
    /// once decoding actually succeeds; a throw (bomb or otherwise malformed) leaves the worst-case
    /// charge standing. A legitimate, small, unencrypted §7.5.8.2 cross-reference stream still
    /// records its offset — decoding succeeds and the charge shrinks back to its real size before
    /// the loop moves on — while a file packed with decompression bombs exhausts the budget within
    /// the first one or two attempts instead of running every one of them to completion first.
    /// </para>
    /// </summary>
    private static long CollectCrossReferenceStreamOffsets(
        ReadOnlyMemory<byte> data, Dictionary<int, ObjectExtent> primaryByObjNum,
        List<ObjectExtent> secondaryExtents, long budget, long consumed, HashSet<long> offsets,
        ReaderLimits limits)
    {
        // Checked AFTER an amount has already landed in `consumed` — never combined with the add
        // itself, unlike the main walk's own Charge — because the pre-charge below has to be
        // allowed to land, and possibly be refunded, before this decides whether the budget
        // actually ran out. Throwing at the moment of the (pessimistic) pre-charge, before a
        // legitimate small stream gets the chance to refund it back down, would fail every genuine
        // cross-reference stream in a small reconstruction the instant this method ran at all.
        void ThrowIfExhausted()
        {
            if (consumed >= budget)
                throw new InvalidDataException(
                    $"Malformed PDF: reconstruction could not verify this document's cross-reference "
                    + $"streams within its cost budget ({budget} bytes) after already determining it "
                    + "is encrypted. A skipped verification here would leave a real cross-reference "
                    + "stream treated as ordinary encrypted content and decrypted into garbage, so "
                    + "this fails closed instead of guessing.");
        }

        foreach (var extent in primaryByObjNum.Values.Concat(secondaryExtents))
        {
            if (!extent.HasBody || extent.Dictionary is not { } dict)
                continue;

            if (dict.Get(_wKey) is not PdfArray wArr || wArr.Count != 3)
                continue;
            if (!TryGetXrefStreamInt(wArr[0], out var w1L) || !TryGetXrefStreamInt(wArr[1], out var w2L)
                || !TryGetXrefStreamInt(wArr[2], out var w3L))
                continue;
            if (w1L is < 0 or > 8 || w2L is < 0 or > 8 || w3L is < 0 or > 8)
                continue;
            var rowSize = (int)(w1L + w2L + w3L);
            if (rowSize <= 0)
                continue;

            if (dict.Get(PdfName.Size) is not PdfInteger sizeObj || sizeObj.Value is < 0 or > int.MaxValue)
                continue;
            var streamSize = sizeObj.Value;

            long totalRows;
            if (dict.Get(_indexKey) is PdfArray indexArr)
            {
                if (indexArr.Count % 2 != 0)
                    continue;
                totalRows = 0;
                var indexValid = true;
                for (var i = 0; i < indexArr.Count; i += 2)
                {
                    if (!TryGetXrefStreamInt(indexArr[i], out var first)
                        || !TryGetXrefStreamInt(indexArr[i + 1], out var count)
                        || first is < 0 or > int.MaxValue || count is < 0 or > int.MaxValue
                        || first + count > int.MaxValue)
                    {
                        indexValid = false;
                        break;
                    }
                    totalRows += count;
                }
                if (!indexValid)
                    continue;
            }
            else
            {
                totalRows = streamSize;
            }

            // The extent's own BodyEnd sits right AFTER 'endstream' (ResolveStreamExtent's own
            // convention); the raw body PdfFilters.Decode needs ends right BEFORE it. A candidate
            // whose stream ran to EOF unresolved (no terminator found at all — ResolveStreamExtent's
            // last-resort fallback) has no marker to subtract, so the whole remaining extent is
            // used as-is; decoding it will fail closed the same way a truncated real stream would.
            var rawBodyEnd = extent.BodyEnd;
            if (rawBodyEnd >= EndstreamMarker.Length + extent.BodyStart
                && data.Span.Slice(rawBodyEnd - EndstreamMarker.Length, EndstreamMarker.Length).SequenceEqual(EndstreamMarker))
            {
                rawBodyEnd -= EndstreamMarker.Length;
            }
            var rawBody = data[extent.BodyStart..Math.Max(extent.BodyStart, rawBodyEnd)];
            var streamObj = new ParsedStream(dict, rawBody, extent.BodyStart, extent.ObjNum, extent.Generation);

            // Pre-charge the worst case Decode could produce (see the doc comment above for why
            // charging only the raw body, or only a successful decode's length, both leave a
            // Flate/LZW bomb's decompression work uncharged): landed BEFORE the decode attempt so
            // a failed candidate — bomb or otherwise malformed — cannot dodge the aggregate cap by
            // failing cheaply, but not itself checked against the budget yet (see ThrowIfExhausted
            // above): a legitimate small stream needs the chance to refund this back down first.
            var worstCase = Math.Min(limits.MaxDecodedBytes, Math.Max(0, budget - consumed));
            consumed += worstCase;

            byte[]? decoded;
            try
            {
                decoded = PdfFilters.Decode(streamObj, limits: limits);
            }
            catch (InvalidDataException)
            {
                // Not a genuine, decodable cross-reference stream — ciphertext, most likely, or a
                // decompression bomb; either way the pre-charge stands, and NOW the budget check
                // runs: one bomb's worth of pre-charge is enough to exhaust an ordinary
                // reconstruction's budget outright, so the very next candidate — bomb or not —
                // fails closed here instead of ever reaching another decode attempt.
                ThrowIfExhausted();
                continue;
            }

            if (decoded is null)
            {
                // An image filter in the chain — never a real xref stream's own shape; the
                // pre-charge stands here too, same reasoning as the throw case above.
                ThrowIfExhausted();
                continue;
            }

            // Real cost now known: refund the worst-case charge and charge the actual size, so a
            // genuine, small cross-reference stream is not left over-counted for the rest of this
            // pass's budget, then check whether even that real cost ran the budget out.
            consumed -= worstCase;
            consumed += decoded.Length;
            ThrowIfExhausted();

            if ((long)decoded.Length >= rowSize * totalRows)
                offsets.Add(extent.DictStart);
        }

        return consumed;
    }

    private static bool TryGetXrefStreamInt(PdfObject? obj, out long value)
    {
        if (obj is PdfInteger pi)
        {
            value = pi.Value;
            return true;
        }
        value = 0;
        return false;
    }

    // ── Exhaustion (un-starvable encryption evidence) ───────────────────────────

    /// <summary>
    /// Internal-only control-flow signal: the walk's budget ran out, but <see cref="ThrowOnExhaustion"/>
    /// found a declared or structurally referenceable <c>/Encrypt</c> candidate among what the walk
    /// had ALREADY confirmed — evidence <see cref="RecoverTrailer"/> can carry rather than refuse.
    /// Caught immediately around the walk loop in <see cref="Reconstruct"/>, never visible outside
    /// this type, so the walk simply stops where it was and falls through to the same post-loop
    /// pipeline (secondary merge, A5, A6, A4, final result) an ordinary completed walk uses —
    /// <see cref="RecoverTrailer"/>'s own carry-vs-refuse logic makes the actual decision from
    /// there, over whatever was gathered before the budget ran out.
    /// </summary>
    private sealed class ReconstructionEvidenceFoundEarlySignal : Exception;

    /// <summary>
    /// The walk's cost budget ran out. Encryption-evidence detection must complete regardless of
    /// any cap, and must reach the same carry-vs-refuse decision <see cref="RecoverTrailer"/> makes
    /// on a completed walk: (1) stop; (2) a declared /Encrypt among confirmed (primary) candidates,
    /// or one of them being encryption-SHAPED at all (<see cref="HasPr2EncryptionEvidenceShape"/>),
    /// is evidence <see cref="RecoverTrailer"/> still needs to see — whether that turns out to be a
    /// Standard-handler match it can carry, or a public-key/unclassifiable one it must refuse, is
    /// exactly the decision that method makes, so this throws
    /// <see cref="ReconstructionEvidenceFoundEarlySignal"/> rather than deciding here itself: the
    /// walk stops, and the shared pipeline below re-derives the same answer over whatever was
    /// confirmed; (3) otherwise, evaluate the same un-starvable backstop PR2 always used: a
    /// quarantined secondary extent's own declared or broadly-shaped evidence, the whole-file sweep
    /// (<see cref="ScanWholeFileForEncryptionEvidence"/>) computed once up front in
    /// <see cref="Reconstruct"/> — L2: that sweep is what closes the gap an un-walked-SUFFIX-only
    /// check leaves open, since a region the walk jumped over in the MIDDLE of the file (an
    /// unresolved stream body, say) is neither "confirmed" nor part of the suffix past
    /// <paramref name="cursorPos"/> — plus a final, deliberately over-broad, uncharged sweep of the
    /// un-walked tail (<see cref="ScanRemainderForEncryptionEvidenceRaw"/>), impossible to starve by
    /// spending the budget before reaching it; (4) any of that throws
    /// <see cref="UnsupportedPdfFeatureException"/>, otherwise <see cref="InvalidDataException"/>
    /// naming the cost budget — an ordinary, unencrypted decoy-padded file still hard-fails here
    /// exactly as before; nothing about PR3 lets budget exhaustion silently degrade a plaintext
    /// reconstruction into a partial one.
    /// </summary>
    private static void ThrowOnExhaustion(
        ReadOnlyMemory<byte> data, int cursorPos, long budget, Dictionary<int, XrefEntry> xref,
        Dictionary<int, ObjectExtent> primaryByObjNum, List<ObjectExtent> secondaryExtents,
        List<(int Offset, PdfDictionary Dict)> trailerCandidates, bool wholeFileEncryptionEvidence)
    {
        var primaryDeclaredEncrypt = trailerCandidates.Exists(c => c.Dict.Get(_encryptKey) is not null)
            || primaryByObjNum.Values.Any(e => e.HasBody && e.Dictionary is not null && e.Dictionary.Get(_encryptKey) is not null);
        var primaryEncryptionShapePresent = primaryByObjNum.Values.Any(
            e => e.Dictionary is not null && HasPr2EncryptionEvidenceShape(e.Dictionary));

        if (primaryDeclaredEncrypt || primaryEncryptionShapePresent)
            throw new ReconstructionEvidenceFoundEarlySignal();

        var unreferenceableEvidence =
            secondaryExtents.Exists(e => e.Dictionary is not null && e.Dictionary.Get(_encryptKey) is not null)
            || secondaryExtents.Exists(e => e.Dictionary is not null && HasPr2EncryptionEvidenceShape(e.Dictionary))
            || wholeFileEncryptionEvidence
            || ScanRemainderForEncryptionEvidenceRaw(data.Span[Math.Clamp(cursorPos, 0, data.Length)..]);

        if (unreferenceableEvidence)
            throw new UnsupportedPdfFeatureException(
                "Malformed PDF: reconstruction's cost budget ran out, but the file also carries "
                + "evidence that it is encrypted, in a region this pass could not safely point a "
                + "trailer reference at. Rebuilding the cross-reference table of a document damaged "
                + "this badly is not supported.");

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
    /// Three signals, each an escape-decoded, word-bounded PDF name token
    /// (<see cref="ContainsWordBoundedEscapedNameToken"/> — ISO 32000-2 §7.3.5's <c>#XX</c> hex
    /// escape decoded before comparison, not a bare substring match): the name <c>Encrypt</c>
    /// anywhere in the file; the co-occurrence of <c>O</c>, <c>U</c> and <c>R</c> — the Standard
    /// handler's Table 21 fingerprint (§7.6.5.2); or a <c>/SubFilter</c> value of
    /// <c>adbe.pkcs7.s3</c>, <c>adbe.pkcs7.s4</c> or <c>adbe.pkcs7.s5</c> — the public-key
    /// handler's Table 23 values (§7.6.5.2), unique enough on their own that no co-occurrence
    /// check is needed the way the Standard triad needs one: a signature's own <c>/SubFilter</c>
    /// values (<c>adbe.pkcs7.detached</c>, <c>adbe.pkcs7.sha1</c>, <c>ETSI.CAdES.detached</c>, and
    /// so on) never collide with the encryption-only <c>.s3</c>/<c>.s4</c>/<c>.s5</c> suffixes.
    /// Together the three catch an encryption dictionary sitting in a swallowed region even when
    /// nothing anywhere declares it as <c>/Encrypt</c>, under either handler family. Escape
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
    /// This is a REFUSAL signal only, never an authentication trigger: <see cref="RecoverTrailer"/>
    /// only ever reads this flag inside its <c>unreferenceableEvidence</c> check, which fires
    /// exclusively when nothing safely referenceable was ALSO found (a declared or structurally
    /// classifying candidate among what the walk actually confirmed). A plaintext file that
    /// happens to contain the literal (or escaped) bytes <c>/Encrypt</c> — in a comment, a string,
    /// or dead content the walk never resolves to anything, but ALSO carries a real, confirmed
    /// candidate elsewhere — is never misread as encrypted just because this sweep found the bytes
    /// too; a hit here only ever adds a reason to refuse, never a reason to open one way or another.
    /// </para>
    /// </summary>
    private static bool ScanWholeFileForEncryptionEvidence(ReadOnlySpan<byte> span)
    {
        if (ContainsWordBoundedEscapedNameToken(span, "Encrypt"u8))
            return true;

        if (ContainsWordBoundedEscapedNameToken(span, "O"u8)
            && ContainsWordBoundedEscapedNameToken(span, "U"u8)
            && ContainsWordBoundedEscapedNameToken(span, "R"u8))
            return true;

        return ContainsWordBoundedEscapedNameToken(span, "adbe.pkcs7.s3"u8)
            || ContainsWordBoundedEscapedNameToken(span, "adbe.pkcs7.s4"u8)
            || ContainsWordBoundedEscapedNameToken(span, "adbe.pkcs7.s5"u8);
    }

    // The longest evidence-target byte length across every name token this sweep and its
    // exhaustion-tail sibling (ScanRemainderForEncryptionEvidenceRaw) ever look for — currently
    // "adbe.pkcs7.s3"/"s4"/"s5" at 13 bytes each, ahead of "Encrypt" (7) and "Filter" (6). Kept as
    // its own constant, rather than folded into the one below, so that constant stays a derived
    // value instead of a bare number a future evidence target could silently outgrow again.
    private const int LongestEvidenceTargetBytes = 13;

    // A #XX hex escape (ISO 32000-2 §7.3.5) is exactly 3 raw bytes per decoded byte, so a raw
    // token longer than 3 × the longest target could never decode down to it — MUST stay
    // >= 3 * LongestEvidenceTargetBytes, or a token this sweep should have matched gets skipped
    // outright instead of decoded. That is exactly what happened here: this used to be a bare 32,
    // sized for "Encrypt" alone (7 bytes needs only 21) and never raised when
    // adbe.pkcs7.s3/s4/s5 (13 bytes, needing 39) joined the target set — a real
    // /adbe.pkcs7.s5 written with ten or more of its thirteen characters #XX-escaped parses fine
    // for PdfObjectParser.ParseName (raw length has no cap there) but has a raw length over 32, so
    // this sweep silently skipped it, letting a public-key encrypted file in a region the walk
    // never tokenized open as plaintext. Deriving the cap from the target set closes that gap by
    // construction rather than by remembering to bump a number by hand every time a new, longer
    // target is added. Skipping a token whose raw length exceeds this cap outright, rather than
    // decoding it anyway, keeps a single pathologically long name from costing more than a bounds
    // check; the stackalloc buffer below is sized from this same constant, so raising it raises
    // the buffer too.
    private const int MaxRawNameTokenBytesForEvidenceMatch = 3 * LongestEvidenceTargetBytes;

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
