// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Reader;

public sealed partial class PdfDocumentReader
{
    private static readonly PdfName _objStmTypeName = new("ObjStm");
    private static readonly PdfName _linearizedParamKey = new("Linearized");
    private static readonly PdfName _decodeParmsKeyForRewrite = new("DecodeParms");
    private static readonly PdfName _dpKeyForRewrite = new("DP");

    /// <summary>
    /// Writes a decrypted copy of this document to <paramref name="destination"/>: a complete,
    /// single-revision PDF with <c>/Encrypt</c> removed and every string and stream in plaintext —
    /// the library's equivalent of <c>qpdf --decrypt</c> (#186).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Unencrypted input.</strong> Accepted — ISO 32000-2 §7.6.2 defines an unencrypted
    /// document as one whose trailer has no <c>/Encrypt</c>, and this method's postcondition already
    /// holds for one. The output degenerates to a normalised single-revision rewrite: every
    /// incremental update collapsed, every object stream and cross-reference stream dissolved into
    /// the classic table this method always writes.
    /// </para>
    /// <para>
    /// <strong>A reconstructed document</strong> (<see cref="WasReconstructed"/>) is allowed, unlike
    /// <c>AppendRevision</c>, which refuses one outright. Annex C.4 describes the scan-and-rebuild
    /// this reader performs as the recovery mechanism for exactly this situation, and a full rewrite
    /// does not depend on the base file's own byte layout the way an incremental update does — there
    /// is no <c>/Prev</c> chain to extend and no <c>startxref</c> to trust. The result is still
    /// best-effort in the same sense the reconstruction itself is: a wrong guess at the object graph
    /// during recovery produces a wrong (but internally consistent) decrypted copy.
    /// </para>
    /// <para>
    /// <strong>Object identity.</strong> Object numbers and generations are preserved from the input.
    /// A compressed <c>/ObjStm</c> member is re-emitted top-level at generation 0 (ISO 32000-2
    /// §7.5.7 fixes every such member's generation regardless of what the container's own number
    /// carries). The output's cross-reference table is a single classic table with no <c>/Prev</c>;
    /// <c>/ObjStm</c> containers, cross-reference streams, and the document's own linearization
    /// parameter dictionary are all dissolved — see <see cref="ComputeEmitSet"/> for exactly which
    /// object numbers survive into the output. What is NOT dissolved: a linearized input's hint
    /// stream (the object the parameter dictionary's <c>/H</c> names) is an ordinary stream, not one
    /// of the categories <see cref="ComputeEmitSet"/> excludes, so it survives as orphaned dead
    /// weight — nothing in the output references it once the parameter dictionary that pointed at it
    /// is gone. Harmless (it is just bytes nothing reads) but deliberately not cleaned up; finding it
    /// would mean recognising a linearized layout well enough to name its hint stream specifically,
    /// which is more machinery than a single, one-off dead object justifies.
    /// </para>
    /// <para>
    /// <strong>Signatures.</strong> Re-serialising the object graph invalidates every digital
    /// signature it carries by construction — a fresh <c>/ByteRange</c> no longer names the region
    /// the original signature was computed over. This method throws
    /// <see cref="InvalidOperationException"/> when the source document has one or more signatures
    /// unless <see cref="PdfSaveDecryptedOptions.AllowInvalidatingSignatures"/> is set. Detection
    /// UNIONS two independent sources, neither of which is complete on its own: the public
    /// <see cref="Signatures"/> property's <c>/AcroForm</c>/<c>/Fields</c> walk (which needs an
    /// <c>/AcroForm</c> entry and a field-tree path to the signature at all), and a direct recursive
    /// scan of every object this method is about to emit for the same structural shape
    /// <see cref="DecryptObjectGraph"/> already uses to exempt a signature's <c>/Contents</c> from
    /// decryption on read — recursing into nested dictionaries and arrays exactly as that method
    /// does, since ISO 32000-2 Table 226 does not require a field's <c>/V</c> to be an indirect
    /// reference the way it requires <c>/Lock</c> and <c>/SV</c> to be, so a signature dictionary can
    /// legally sit INLINE inside its field, reachable only by recursing rather than as its own
    /// top-level emit-set entry. Neither source is redundant with the other: the field-tree walk
    /// finds a signature the object scan cannot reach without an <c>/AcroForm</c> path to it, and the
    /// object scan finds one the field-tree walk cannot reach without a well-formed field tree naming
    /// it — a signature-shaped dictionary present in the document but not linked from
    /// <c>/AcroForm</c> at all is exactly such a case. Even with the opt-in set, a signature's own
    /// <c>/Contents</c> is copied verbatim: if the source
    /// encrypted that field —
    /// qpdf does this by default even for a document this library refuses to both sign and encrypt
    /// itself — the ciphertext lands where the DER signature bytes belong, permanently. This is not a
    /// decryption defect; the signature was already invalid the moment the byte range it was computed
    /// over stopped existing.
    /// </para>
    /// <para>
    /// <strong>Not addressed, by design.</strong> Following #186's own security analysis: a
    /// <c>/Perms</c>-restricted document (view-only, no printing, and so on) is rewritten anyway. At
    /// revision ≤ 4 the owner password recovers the same file key as the user password (Algorithm 7);
    /// at revision ≥ 5 <c>/UE</c> and <c>/OE</c> unwrap the same key either way — so a caller who can
    /// already open the document already holds every byte the owner does, and refusing here would
    /// protect nothing while breaking the common case of a merely permission-restricted file.
    /// </para>
    /// <para>
    /// <strong>Cost.</strong> The whole object graph is force-resolved and held in memory at once
    /// (the same whole-file design <see cref="PdfReader"/> already uses), and the output is built in
    /// an internal buffer before anything reaches <paramref name="destination"/> — so peak memory is
    /// roughly three times the input file's size (source bytes, resolved object graph, output
    /// buffer). A failure during serialisation (a malformed object, the signature guard, and so on)
    /// therefore leaves <paramref name="destination"/> completely untouched. The final copy from the
    /// buffer to <paramref name="destination"/> is NOT covered by that guarantee: a failure or a
    /// cancelled <see cref="System.Threading.CancellationToken"/> partway through it can leave a
    /// genuinely-plaintext, truncated prefix on the stream. A caller writing to a file and wanting an
    /// all-or-nothing result on disk should write to a temporary file and rename it into place only
    /// after this method returns.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The document has one or more signatures and <see cref="PdfSaveDecryptedOptions.AllowInvalidatingSignatures"/>
    /// was not set.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An object the cross-reference table declares could not be resolved. The message names the
    /// object number.
    /// </exception>
    /// <exception cref="UnsupportedPdfFeatureException">
    /// A stream's <c>/Filter</c> (or <c>/DecodeParms</c>) beginning with <c>/Crypt</c> is an indirect
    /// reference — rewriting it risks corrupting another object sharing the same array.
    /// </exception>
    public void SaveDecrypted(Stream destination) => SaveDecrypted(destination, new PdfSaveDecryptedOptions());

    /// <summary>
    /// Writes a decrypted copy of this document to <paramref name="destination"/>, honouring
    /// <paramref name="options"/>. See <see cref="SaveDecrypted(Stream)"/> for the full semantics.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The document has one or more signatures and <see cref="PdfSaveDecryptedOptions.AllowInvalidatingSignatures"/>
    /// was not set.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An object the cross-reference table declares could not be resolved. The message names the
    /// object number.
    /// </exception>
    /// <exception cref="UnsupportedPdfFeatureException">
    /// A stream's <c>/Filter</c> (or <c>/DecodeParms</c>) beginning with <c>/Crypt</c> is an indirect
    /// reference — rewriting it risks corrupting another object sharing the same array.
    /// </exception>
    public void SaveDecrypted(Stream destination, PdfSaveDecryptedOptions options)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        using var buffer = SerializeDecrypted(options);
        buffer.Position = 0;
        buffer.CopyTo(destination);
    }

    /// <summary>
    /// Asynchronous twin of <see cref="SaveDecrypted(Stream)"/>. See its remarks for the full
    /// semantics this follows.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public Task SaveDecryptedAsync(Stream destination) =>
        SaveDecryptedAsync(destination, new PdfSaveDecryptedOptions());

    /// <summary>
    /// Asynchronously writes a decrypted copy of this document to <paramref name="destination"/>,
    /// honouring <paramref name="options"/>. See <see cref="SaveDecrypted(Stream)"/> for the full
    /// semantics.
    ///
    /// <para>
    /// Serialisation is CPU-bound (an object-graph walk and re-encoding, not I/O), so it runs on a
    /// thread-pool thread via <see cref="Task.Run(Action)"/> against an in-memory buffer; the buffer
    /// is then copied to <paramref name="destination"/> with an asynchronous write.
    /// <paramref name="cancellationToken"/> is honoured before serialisation starts and during the
    /// final copy, but does not abort serialisation already in progress. A token already cancelled
    /// before serialisation starts leaves <paramref name="destination"/> untouched; one that fires
    /// DURING the final copy can leave a genuinely-plaintext, truncated prefix already written —
    /// see <see cref="SaveDecrypted(Stream)"/>'s own "Cost" remarks for what that means for a
    /// caller writing to a file.
    /// </para>
    /// <para>
    /// The only overload of this method carrying a default argument (the public-API analyzer forbids
    /// more than one) — call it with two arguments for the common case
    /// (<c>SaveDecryptedAsync(destination, options)</c>) or all three to also control cancellation.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The document has one or more signatures and <see cref="PdfSaveDecryptedOptions.AllowInvalidatingSignatures"/>
    /// was not set.
    /// </exception>
    public async Task SaveDecryptedAsync(
        Stream destination, PdfSaveDecryptedOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        using var buffer = await Task.Run(() => SerializeDecrypted(options), cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        await buffer.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    // ── Emit-set computation ─────────────────────────────────────────────────

    /// <summary>
    /// The object numbers <see cref="SaveDecrypted(Stream)"/> writes to the output: every number the
    /// merged cross-reference table declares (<see cref="ObjectNumbers"/>), minus the trailer's own
    /// <c>/Encrypt</c> object (#186's blocking security requirement — see
    /// <see cref="_encryptObjectNumber"/>'s own doc comment for why removing the trailer entry is not
    /// enough), minus every object-stream container (an entry with
    /// <see cref="XrefEntryKind.InObjectStream"/> names its container in
    /// <see cref="XrefEntry.ObjStmObjectNumber"/>, and a container with no live member left pointing
    /// at it is still excluded, since checking every live top-level stream's own <c>/Type</c> for
    /// <c>/ObjStm</c> catches both shapes in one pass — a container is never anything else), minus
    /// every cross-reference stream (<see cref="IsCrossReferenceStream"/>, ISO 32000-2 §7.5.8.2
    /// forbids encrypting one, so it is never ciphertext to begin with, but it is still not a
    /// meaningful object in a classic-xref rewrite), and minus the document's own linearization
    /// parameter dictionary (a non-stream dictionary carrying the key <c>/Linearized</c> — its byte
    /// offsets describe the INPUT file's layout and are simply wrong once rewritten; a stream that
    /// merely claims <c>/Type /XRef</c> without living at an offset this reader actually parsed a
    /// cross-reference stream from is ordinary content and stays in the set).
    /// <para>
    /// Every object number this method visits is force-resolved to decide its category, which is
    /// also where a malformed object surfaces: an object the cross-reference table declares but that
    /// cannot actually be parsed throws <see cref="InvalidDataException"/> naming the object number,
    /// the same way <see cref="SaveDecrypted(Stream)"/>'s own emission loop does for an object stream
    /// member — this reader never leaves unread content out of a full save silently (#186).
    /// </para>
    /// </summary>
    internal HashSet<int> ComputeEmitSet()
    {
        ThrowIfDisposed();

        var emit = new HashSet<int>(_xref.Keys);

        if (_encryptObjectNumber is { } encryptObjectNumber)
            emit.Remove(encryptObjectNumber);

        foreach (var objectNumber in _xref.Keys)
        {
            var entry = _xref[objectNumber];
            if (entry.Kind != XrefEntryKind.Uncompressed)
                continue; // An /ObjStm member re-emits top-level; it belongs in the set.

            if (IsCrossReferenceStream(objectNumber))
            {
                emit.Remove(objectNumber);
                continue;
            }

            try
            {
                var stream = ResolveStream(objectNumber);
                if (stream is not null)
                {
                    if (stream.Dictionary.Get(PdfName.Type) is PdfName typeName && typeName.Equals(_objStmTypeName))
                        emit.Remove(objectNumber);
                    continue;
                }

                if (Resolve(objectNumber) is PdfDictionary dict && dict.Get(_linearizedParamKey) is not null)
                    emit.Remove(objectNumber);
            }
            catch (InvalidDataException ex)
            {
                throw WrapResolveFailure(objectNumber, ex);
            }
        }

        return emit;
    }

    /// <summary>
    /// Whether any object in <paramref name="emitSet"/> — or anything reachable from one by direct
    /// nesting, not just the object itself — is something <see cref="IsSignatureDictionary"/>
    /// recognises as a signature dictionary. One of the two independent sources the signature-policy
    /// guard unions; see the "Signatures" remarks on <see cref="SaveDecrypted(Stream)"/> for why
    /// neither source alone is complete.
    /// </summary>
    /// <remarks>
    /// Recurses into nested dictionaries and arrays exactly the way <see cref="DecryptObjectGraph"/>
    /// does when it decides the <c>/Contents</c> exemption on read — deliberately not just checking
    /// each top-level object, since ISO 32000-2 Table 226 lets a field's <c>/V</c> be a direct
    /// (inline) dictionary rather than requiring an indirect reference the way <c>/Lock</c> and
    /// <c>/SV</c> in that same table do. An earlier version of this method checked only the top-level
    /// resolved value of each emit-set entry, which missed exactly that inline shape: a signature
    /// dictionary sitting directly inside its field's <c>/V</c>, never itself an <c>/AcroForm</c>
    /// entry's own object number. Indirect references are NOT followed here — an indirect target is
    /// its own emit-set entry and gets checked on its own turn by the caller's loop, so following it
    /// again here would only repeat work, not find anything new. Every candidate object is
    /// force-resolved by <see cref="SerializeDecrypted"/>'s own emission loop regardless, so this
    /// scan costs nothing beyond the recursion itself: a signature-shaped dictionary found here is a
    /// cache hit when the loop reaches its containing object, not a second parse.
    /// </remarks>
    private bool AnyEmittedSignatureDictionary(IEnumerable<int> emitSet)
    {
        foreach (var objectNumber in emitSet)
        {
            try
            {
                var stream = ResolveStream(objectNumber);
                if (stream is not null)
                {
                    // A signature VALUE dictionary is never itself a stream — its /Contents is a
                    // string, not stream data — but nothing stops a stream's OWN dictionary from
                    // nesting one inline the same way a field's /V can, so this still recurses
                    // rather than skipping the stream outright.
                    if (ContainsSignatureDictionary(stream.Dictionary))
                        return true;
                    continue;
                }

                if (ContainsSignatureDictionary(Resolve(objectNumber)))
                    return true;
            }
            catch (InvalidDataException ex)
            {
                // This scan can be the FIRST resolve attempt for a compressed-object-stream member
                // (ComputeEmitSet's own classification pass only force-resolves top-level
                // Uncompressed entries) — so a malformed one must still fail loud and name itself,
                // not surface as a bare, unattributed exception from here.
                throw WrapResolveFailure(objectNumber, ex);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="obj"/> is a signature dictionary by
    /// <see cref="IsSignatureDictionary"/>'s reading, or contains one nested directly inside a
    /// dictionary value or array element. Does not follow indirect references — see
    /// <see cref="AnyEmittedSignatureDictionary"/> for why not.
    /// </summary>
    private bool ContainsSignatureDictionary(PdfObject? obj)
    {
        switch (obj)
        {
            case PdfDictionary d:
                if (IsSignatureDictionary(d))
                    return true;
                foreach (var kv in d.Entries)
                    if (ContainsSignatureDictionary(kv.Value))
                        return true;
                return false;

            case PdfArray a:
                for (var i = 0; i < a.Count; i++)
                    if (ContainsSignatureDictionary(a[i]))
                        return true;
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Force-resolves <paramref name="objectNumber"/> — as a stream when it is one, otherwise as an
    /// ordinary value — and returns its authoritative post-Restamp generation: the same value
    /// <see cref="EmitObject"/> writes into the output. Internal rather than private purely for the
    /// test suite's generation-preservation assertion (#186's acceptance list); production code
    /// reaches the same information inline through its own resolve calls.
    /// </summary>
    internal int GenerationOf(int objectNumber)
    {
        var stream = ResolveStream(objectNumber);
        if (stream is not null)
            return stream.Generation;

        Resolve(objectNumber);
        return _cache[objectNumber].Generation;
    }

    // ── Core serialiser ──────────────────────────────────────────────────────

    private MemoryStream SerializeDecrypted(PdfSaveDecryptedOptions options)
    {
        var emitSet = ComputeEmitSet();

        // Unions two independent sources — see the "Signatures" remarks above for why neither
        // alone is complete. Signatures.Count is checked first since it is normally cheap (a
        // handful of AcroForm field-tree nodes) and short-circuits the full object scan on the
        // common case where a signature IS reachable that way.
        if (!options.AllowInvalidatingSignatures
            && (Signatures.Count > 0 || AnyEmittedSignatureDictionary(emitSet)))
            throw new InvalidOperationException(
                "This document has one or more digital signatures. Re-serialising it invalidates "
                + "every signature's /ByteRange, so each one would verify as \"document modified "
                + "since signing\" with no way to distinguish that from genuine tampering. Set "
                + "PdfSaveDecryptedOptions.AllowInvalidatingSignatures to accept that outcome.");

        var orderedNumbers = emitSet.Order().ToList();

        var ms = new MemoryStream(Bytes.Length + 4096);
        var writer = new PdfWriter(ms);

        WriteHeader(writer);

        var written = new List<(int ObjectNumber, int Generation, long ByteOffset)>(orderedNumbers.Count);
        foreach (var objectNumber in orderedNumbers)
        {
            var offset = writer.Position;
            var generation = EmitObject(writer, objectNumber);
            written.Add((objectNumber, generation, offset));
        }

        var trailer = BuildTrailer(written);
        IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, written, trailer);

        writer.Flush();
        return ms;
    }

    /// <summary>
    /// Writes the <c>%PDF-M.N</c> header line plus the four-byte binary comment (ISO 32000-2
    /// §7.5.2) that signals an 8-bit-clean transport to route this file untouched, matching
    /// <c>PdfDocument.Save</c>'s own header bytes exactly.
    /// </summary>
    private void WriteHeader(PdfWriter writer)
    {
        var version = TryParseHeaderVersion() ?? "1.7";
        writer.WriteAscii("%PDF-"u8);
        writer.WriteAsciiString(version);
        writer.WriteAscii("\n%"u8);
        writer.WriteRaw([0xE2, 0xE3, 0xCF, 0xD3]);
        writer.WriteAscii("\n"u8);
    }

    /// <summary>
    /// Scans the first kilobyte of the input for its own <c>%PDF-M.N</c> header and returns
    /// <c>"M.N"</c>, or <see langword="null"/> when none is found or it doesn't parse — the input is
    /// untrusted bytes, not a value this reader validated on open (nothing about opening a document
    /// requires reading its header line at all). <see cref="WriteHeader"/> falls back to 1.7 rather
    /// than propagate a malformed or absent header into the output.
    /// </summary>
    private string? TryParseHeaderVersion()
    {
        var span = Bytes.Span;
        var scanLength = Math.Min(span.Length, 1024);
        var needle = "%PDF-"u8;
        var index = span[..scanLength].IndexOf(needle);
        if (index < 0)
            return null;

        var pos = index + needle.Length;
        var majorStart = pos;
        while (pos < span.Length && span[pos] is >= (byte)'0' and <= (byte)'9')
            pos++;
        if (pos == majorStart || pos >= span.Length || span[pos] != (byte)'.')
            return null;

        var majorEnd = pos;
        pos++; // skip '.'
        var minorStart = pos;
        while (pos < span.Length && span[pos] is >= (byte)'0' and <= (byte)'9')
            pos++;
        if (pos == minorStart)
            return null;

        return $"{System.Text.Encoding.ASCII.GetString(span[majorStart..majorEnd])}.{System.Text.Encoding.ASCII.GetString(span[minorStart..pos])}";
    }

    /// <summary>
    /// Emits one object — a stream body via <see cref="EmitStream"/>, or an ordinary value via
    /// <see cref="PdfIndirectObject.WriteTo"/> — and returns the generation it was written at.
    /// </summary>
    private int EmitObject(PdfWriter writer, int objectNumber)
    {
        ParsedStream? stream;
        try
        {
            stream = ResolveStream(objectNumber);
        }
        catch (InvalidDataException ex)
        {
            throw WrapResolveFailure(objectNumber, ex);
        }

        if (stream is not null)
            return EmitStream(writer, objectNumber, stream);

        PdfObject? value;
        try
        {
            value = Resolve(objectNumber);
        }
        catch (InvalidDataException ex)
        {
            throw WrapResolveFailure(objectNumber, ex);
        }

        if (value is null)
            throw ObjectMissingException(objectNumber);

        // Authoritative post-Restamp generation — see the field comment on the reader's own cache
        // for why this is a single dictionary lookup rather than a second trip to the xref table.
        var generation = _cache[objectNumber].Generation;
        new PdfIndirectObject(objectNumber, generation, value).WriteTo(writer);
        writer.WriteAscii("\n"u8);
        return generation;
    }

    /// <summary>
    /// Emits a stream object directly via <see cref="PdfWriter"/> — never through
    /// <c>PdfStream.WriteTo</c>, which re-Flates and would corrupt any stream using a different
    /// filter (or none), and never through <c>RawPdfStream</c>, which cannot express an arbitrary
    /// filter chain. <see cref="DecryptedStreamView"/> yields the body decrypted with every filter
    /// intact, and this method copies that body and every dictionary entry it does not specifically
    /// rewrite (see <see cref="RebuildStreamDictionary"/>) unchanged — nothing here is specific to
    /// any one filter. DCT has a known-answer test pinning byte-for-byte passthrough
    /// (<c>SaveDecryptedTests</c>); JPX, JBIG2 and CCITT follow the identical code path and are not
    /// separately pinned, since nothing about this method distinguishes one opaque filter name from
    /// another.
    /// </summary>
    private int EmitStream(PdfWriter writer, int objectNumber, ParsedStream stream)
    {
        var generation = stream.Generation;

        ParsedStream decrypted;
        try
        {
            decrypted = DecryptedStreamView(stream);
        }
        catch (InvalidDataException ex)
        {
            throw WrapResolveFailure(objectNumber, ex);
        }

        var rebuiltDict = RebuildStreamDictionary(objectNumber, stream.Dictionary, decrypted.RawBody.Length);

        writer.WriteAsciiString($"{objectNumber} {generation} obj\n");
        rebuiltDict.WriteTo(writer);
        writer.WriteAscii("\nstream\n"u8);
        writer.WriteRaw(decrypted.RawBody.Span);
        writer.WriteAscii("\nendstream\nendobj\n"u8);

        return generation;
    }

    /// <summary>
    /// Copies <paramref name="original"/>'s entries, except <c>/Length</c> (always rewritten below,
    /// since AES can shrink a body and an indirect <c>/Length</c> may still name the ciphertext
    /// length) and a leading <c>/Crypt</c> filter (ISO 32000-2 §7.4.10 requires it first when
    /// present) together with its parallel <c>/DecodeParms</c> entry — the crypt filter no longer
    /// exists once the stream is plaintext. Both keys are dropped entirely when <c>/Filter</c> is the
    /// bare name <c>/Crypt</c>; for a filter ARRAY, only the first element is removed from each of
    /// <c>/Filter</c> and <c>/DecodeParms</c>/<c>/DP</c>, and a key is omitted altogether if that
    /// leaves it an empty array.
    /// </summary>
    private PdfDictionary RebuildStreamDictionary(int objectNumber, PdfDictionary original, int newLength)
    {
        var filterRaw = original.Get(PdfName.Filter);
        // Only a FIRST-position /Crypt is stripped, matching ISO 32000-2 §7.4.10's own requirement
        // that /Crypt "shall be the first filter" when present. A /Crypt entry anywhere else in the
        // chain is already malformed under that clause; this leaves it in place rather than guess at
        // a shape the spec does not define. Benign in practice — Table 26's own default for an
        // unresolved crypt filter is Identity, so a mis-placed /Crypt copied into supposedly
        // plaintext output at worst names a no-op filter, not a claim that hides real ciphertext.
        var stripCrypt = CryptFilterResolver.FirstFilterName(original, ResolveMaybe) == "Crypt";

        if (stripCrypt && filterRaw is PdfIndirectReference)
            throw new UnsupportedPdfFeatureException(
                $"Object {objectNumber}: /Filter is an indirect reference to a filter chain "
                + "beginning with /Crypt (ISO 32000-2 §7.4.10), and rewriting it risks corrupting "
                + "another object that shares the same array. SaveDecrypted refuses rather than "
                + "guess. See https://github.com/Tim81/VellumPDF/issues/186.");

        var parmsRaw = original.Get(_decodeParmsKeyForRewrite) ?? original.Get(_dpKeyForRewrite);
        if (stripCrypt && parmsRaw is PdfIndirectReference)
            throw new UnsupportedPdfFeatureException(
                $"Object {objectNumber}: /DecodeParms is an indirect reference paired with a "
                + "/Crypt-first filter chain, and rewriting it risks corrupting another object that "
                + "shares the same array. SaveDecrypted refuses rather than guess. See "
                + "https://github.com/Tim81/VellumPDF/issues/186.");

        var rebuilt = new PdfDictionary();
        foreach (var kv in original.Entries)
        {
            if (kv.Key.Equals(PdfName.Length))
                continue;

            if (stripCrypt && kv.Key.Equals(PdfName.Filter))
            {
                if (kv.Value is PdfArray filterArray)
                {
                    var stripped = RemoveFirstElement(filterArray);
                    if (stripped.Count > 0)
                        rebuilt.Set(kv.Key, stripped);
                }
                // A bare /Crypt name: the key is dropped entirely (no filter remains).
                continue;
            }

            if (stripCrypt && (kv.Key.Equals(_decodeParmsKeyForRewrite) || kv.Key.Equals(_dpKeyForRewrite)))
            {
                if (kv.Value is PdfArray parmsArray)
                {
                    var stripped = RemoveFirstElement(parmsArray);
                    if (stripped.Count > 0)
                        rebuilt.Set(kv.Key, stripped);
                }
                // Parms paired with a bare /Crypt filter name: dropped entirely along with it.
                continue;
            }

            rebuilt.Set(kv.Key, kv.Value);
        }

        rebuilt.Set(PdfName.Length, new PdfInteger(newLength));
        return rebuilt;
    }

    private static PdfArray RemoveFirstElement(PdfArray array)
    {
        var result = new PdfArray();
        for (var i = 1; i < array.Count; i++)
            result.Add(array[i]);
        return result;
    }

    /// <summary>
    /// Builds the output trailer: <c>/Size</c> from the highest emitted object number, <c>/Root</c>
    /// (and <c>/Info</c>, when it names an emitted object) rebuilt with the EMITTED generation rather
    /// than trusted from the input trailer — the #121 C1 lesson <c>AppendRevision</c> already learned
    /// — and <c>/ID</c> carried verbatim when present as an array (ISO 32000-2 §7.6.2: never
    /// encrypted, so nothing about decryption changes it, and carrying it forward keeps the output
    /// tied to the document it came from). <c>/Encrypt</c>, <c>/Prev</c> and <c>/XRefStm</c> are never
    /// written — there is exactly one revision and it is not encrypted.
    /// </summary>
    private PdfDictionary BuildTrailer(IReadOnlyList<(int ObjectNumber, int Generation, long ByteOffset)> written)
    {
        var generationByNumber = new Dictionary<int, int>(written.Count);
        var maxObjectNumber = 0;
        foreach (var w in written)
        {
            generationByNumber[w.ObjectNumber] = w.Generation;
            if (w.ObjectNumber > maxObjectNumber)
                maxObjectNumber = w.ObjectNumber;
        }

        var trailer = new PdfDictionary().Set(PdfName.Size, new PdfInteger(maxObjectNumber + 1));

        if (!Trailer.TryGet(PdfName.Root, out var rootRaw) || rootRaw is not PdfIndirectReference rootRef)
            throw new InvalidDataException("Malformed PDF: trailer does not contain a valid /Root indirect reference.");

        var rootGeneration = generationByNumber.TryGetValue(rootRef.ObjectNumber, out var rg) ? rg : rootRef.Generation;
        trailer.Set(PdfName.Root, new PdfIndirectReference(rootRef.ObjectNumber, rootGeneration));

        if (Trailer.TryGet(PdfName.Info, out var infoRaw)
            && infoRaw is PdfIndirectReference infoRef
            && generationByNumber.TryGetValue(infoRef.ObjectNumber, out var infoGeneration))
        {
            trailer.Set(PdfName.Info, new PdfIndirectReference(infoRef.ObjectNumber, infoGeneration));
        }

        if (Trailer.TryGet(PdfName.ID, out var idRaw) && idRaw is PdfArray idArray)
            trailer.Set(PdfName.ID, idArray);

        return trailer;
    }

    private static InvalidDataException WrapResolveFailure(int objectNumber, Exception inner) =>
        new(
            $"Malformed PDF: object {objectNumber} could not be resolved while preparing a "
            + "decrypted copy. SaveDecrypted force-resolves every object the cross-reference table "
            + "declares, rather than leaving it unread the way lazy resolution would.",
            inner);

    private static InvalidDataException ObjectMissingException(int objectNumber) =>
        new(
            $"Malformed PDF: object {objectNumber} could not be resolved while preparing a "
            + "decrypted copy — its \"N G obj\" header does not match the number the cross-reference "
            + "table declares for it.");
}
