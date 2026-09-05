// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// The stopping filter's canonical Table 6 name, its <c>/DecodeParms</c> entry (values unresolved:
/// see <c>ImageDecoder</c>'s own remarks on why it re-resolves them), and the bytes decoded up to
/// that filter. <see cref="Succeeded"/> is false when a filter threw or a decode bound was hit; the
/// underlying condition has already been reported through the diagnostics sink by then, so a caller
/// adds no second report of it (#98).
/// </summary>
internal readonly record struct ImageDecodeResult(
    byte[] Data, PdfName? ImageFilter, PdfDictionary? ImageFilterParms, bool Succeeded);

/// <summary> Applies PDF filter chains to stream bodies (ISO 32000-2 §7.4). Handles FlateDecode,
/// LZWDecode, ASCIIHexDecode, ASCII85Decode, RunLengthDecode and their predictors. Image filters
/// (DCTDecode, JPXDecode, JBIG2Decode, CCITTFaxDecode) are recognised but left undecoded by <see
/// cref="TryDecode"/> and <see cref="Decode"/>: callers receive a false fullyDecoded flag.
/// <c>DecodeForImage</c> (#98) is the one caller that wants the bytes up to that filter instead of
/// a stop signal.
/// </summary>
internal static class PdfFilters
{
    private static readonly PdfName _dp = new("DecodeParms");
    private static readonly PdfName _dp2 = new("DP");
    private static readonly PdfName _predictor = new("Predictor");
    private static readonly PdfName _columns = new("Columns");
    private static readonly PdfName _colors = new("Colors");
    private static readonly PdfName _bpc = new("BitsPerComponent");
    private static readonly PdfName _earlyChange = new("EarlyChange");

    private static readonly HashSet<string> _imageFilters =
    [
        "DCTDecode", "DCT",
        "JPXDecode",
        "JBIG2Decode",
        "CCITTFaxDecode", "CCF",
    ];

    /// <summary> Names, without decoding anything, the image filter that would stop a full decode
    /// of <paramref name="dictionary"/>'s own filter chain (<c>ImageFilter</c>, or <see
    /// langword="null"/> when every filter in the chain is one <see cref="Decode"/> can consume in
    /// full, in which case the image reaches the <c>Raw</c> path) and the chain's own last filter
    /// (<c>LastFilterName</c>, or <see langword="null"/> for no filter at all). Used by
    /// <c>ImageDecoder</c> (#98): both size and depth have to be settled before any decode is
    /// attempted, so they cannot wait for <c>DecodeForImage</c> itself to run. Any diagnostic
    /// <see cref="GetFilterList"/> reports here (a malformed
    /// <c>/Filter</c> shape) is deduped against the identical report <see cref="DecodeCore"/> makes
    /// for the same object when the decode runs later, so nothing is reported twice.
    /// </summary>
    internal static (PdfName? ImageFilter, PdfName? LastFilterName) PeekFilterInfo(
        PdfDictionary dictionary, Func<PdfObject?, PdfObject?>? resolve, DiagnosticSink? diagnostics,
        int? objectNumber, int? generation)
    {
        var filters = GetFilterList(dictionary, resolve, diagnostics, objectNumber, generation);
        PdfName? imageFilter = null;
        foreach (var f in filters)
        {
            if (_imageFilters.Contains(f.Value))
            {
                imageFilter = f;
                break;
            }
        }
        var last = filters.Count > 0 ? filters[^1] : null;
        return (imageFilter, last);
    }

    /// <summary>
    /// Tries to decode the full filter chain for <paramref name="stream"/>.
    /// Returns true when fully decoded; false when an image filter terminates the chain
    /// (in which case <paramref name="decoded"/> contains the partially decoded bytes
    /// up to and not including the image filter).
    /// </summary>
    /// <param name="stream">The parsed stream whose filter chain is applied.</param>
    /// <param name="decoded">Receives the decoded (or partially decoded) bytes.</param>
    /// <param name="limits">
    /// The resource ceilings for this decode — in particular
    /// <see cref="ReaderLimits.MaxDecodedBytes"/>, the cap FlateDecode, LZWDecode, and
    /// RunLengthDecode enforce on their output. Required, not defaulted: an omitted argument here
    /// used to decode silently at the 512 MiB default even for a caller who had tightened
    /// <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> everywhere else, defeating the point of
    /// the option without ever failing a build. Pass <see cref="ReaderLimits.Defaults"/> explicitly
    /// at a genuine bootstrap call site that has no <see cref="PdfReaderOptions"/> of its own yet.
    /// </param>
    /// <param name="resolve">
    /// Optional indirect-reference resolver. <c>/Filter</c> and <c>/DecodeParms</c> (and their array
    /// elements) may be indirect references — e.g. Ghostscript emits <c>/Filter 12 0 R</c>. When a
    /// resolver is supplied those references are dereferenced; without one only direct values are
    /// honoured (the bootstrap xref-stream path, where the object graph is not yet resolvable).
    /// </param>
    /// <param name="diagnostics">
    /// Optional sink for #385's diagnostics channel. Left null at every bootstrap call site that
    /// runs before a <see cref="PdfDocumentReader"/> (and therefore its sink) exists —
    /// <see cref="XrefParser"/> and <see cref="XrefReconstructor"/> decode the xref stream and
    /// reconstruction candidates before that point. <see cref="PdfDocumentReader"/>'s own call
    /// sites pass its sink.
    /// </param>
    internal static bool TryDecode(
        ParsedStream stream, out byte[] decoded, ReaderLimits limits,
        Func<PdfObject?, PdfObject?>? resolve = null, DiagnosticSink? diagnostics = null)
    {
        var result = DecodeCore(
            stream.Dictionary, stream.RawBody, stream.ObjectNumber, stream.Generation, limits, resolve,
            diagnostics);
        decoded = result.Data;
        return result.ImageFilter is null;
    }

    /// <summary>Returns decoded bytes or null if an image filter prevents full decode.</summary>
    /// <param name="stream">The parsed stream whose filter chain is applied.</param>
    /// <param name="limits">The decode ceiling to enforce; see <see cref="TryDecode"/>.</param>
    /// <param name="resolve">Optional indirect-reference resolver for
    /// <c>/Filter</c>/<c>/DecodeParms</c>; see <see cref="TryDecode"/>.</param>
    /// <param name="diagnostics">Optional diagnostics sink; see <see cref="TryDecode"/>.</param>
    internal static byte[]? Decode(
        ParsedStream stream, ReaderLimits limits, Func<PdfObject?, PdfObject?>? resolve = null,
        DiagnosticSink? diagnostics = null)
    {
        if (!TryDecode(stream, out var decoded, limits, resolve, diagnostics))
            return null;
        return decoded;
    }

    // Shared throwing core behind TryDecode and DecodeForImage (#98). Runs the filter chain exactly
    // as TryDecode always has, up to and not including the first image filter, and lets every
    // InvalidDataException the chain raises escape uncaught: TryDecode's own throw is load-bearing
    // (11 call sites across XrefParser, XrefReconstructor, PdfDocumentReader and their own tests
    // assert it), so this core must not swallow anything TryDecode itself did not.
    private static ImageDecodeResult DecodeCore(
        PdfDictionary dictionary, ReadOnlyMemory<byte> rawBody, int? objectNumber, int? generation,
        ReaderLimits limits, Func<PdfObject?, PdfObject?>? resolve, DiagnosticSink? diagnostics)
    {
        var filters = GetFilterList(dictionary, resolve, diagnostics, objectNumber, generation);
        var parms = GetParmsList(dictionary, filters.Count, resolve, diagnostics, objectNumber, generation);

        var data = rawBody.ToArray();
        PdfName? imageFilter = null;
        PdfDictionary? imageFilterParms = null;

        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            var p = i < parms.Count ? parms[i] : null;

            if (_imageFilters.Contains(f.Value))
            {
                imageFilter = f;
                imageFilterParms = p;
                break;
            }

            data = ApplyFilter(f, p, data, limits.MaxDecodedBytes, diagnostics, objectNumber, generation);
        }

        return new ImageDecodeResult(data, imageFilter, imageFilterParms, Succeeded: true);
    }

    /// <summary> Decodes an image XObject's filter chain up to (not including) the image filter
    /// that stops it, for <c>ImageDecoder</c> (#98). Unlike <see cref="TryDecode"/>, never throws:
    /// an <see cref="InvalidDataException"/> from <see cref="DecodeCore"/> (a filter this reader
    /// does not implement, a decompression bomb, an invalid predictor parameter) is caught here and
    /// reported through <see cref="ImageDecodeResult.Succeeded"/> instead, since the underlying
    /// condition has already been reported through <paramref name="diagnostics"/> by one of <see
    /// cref="DecodeCore"/>'s own call sites (<see
    /// cref="PdfReaderDiagnosticCode.UnknownFilter"/>-class codes); this method adds no second
    /// report of the same condition.
    /// </summary>
    /// <param name="stream">The image XObject stream, already decrypted (see
    /// <c>PdfDocumentReader.DecryptedStreamView</c>); this method never reads <see
    /// cref="ParsedStream.RawBody"/> from an encrypted stream directly.</param>
    /// <param name="limits">The decode ceiling to enforce; see <see cref="TryDecode"/>.</param>
    /// <param name="resolve">Optional indirect-reference resolver for
    /// <c>/Filter</c>/<c>/DecodeParms</c>.</param>
    /// <param name="diagnostics">Optional diagnostics sink.</param>
    internal static ImageDecodeResult DecodeForImage(
        ParsedStream stream, ReaderLimits limits, Func<PdfObject?, PdfObject?>? resolve,
        DiagnosticSink? diagnostics)
    {
        // Charged before any copy: Filters.cs's own body-length copy below (inside DecodeCore) is
        // unconditional, and for a DCTDecode/JPXDecode/JBIG2Decode/CCITTFaxDecode stream whose
        // image filter comes first that copy is never otherwise bounded by MaxDecodedBytes (that
        // bound is enforced only inside InflateFlate/DecodeLzw/DecodeRunLength, none of which this
        // stream's chain ever reaches).
        if (stream.RawBody.Length > limits.MaxDecodedBytes)
            return new ImageDecodeResult([], null, null, Succeeded: false);

        try
        {
            return DecodeCore(
                stream.Dictionary, stream.RawBody, stream.ObjectNumber, stream.Generation, limits,
                resolve, diagnostics);
        }
        catch (InvalidDataException)
        {
            return new ImageDecodeResult([], null, null, Succeeded: false);
        }
    }

    /// <summary> The inline-image overload of <c>DecodeForImage</c>.
    /// <see cref="ParsedStream"/>'s own constructor requires a non-null object number and
    /// generation (its remarks warn that a synthesised (0, 0) decrypts under the wrong per-object
    /// key), and an inline image has none, so it is decoded directly from its dictionary and
    /// already-plaintext data instead of a fabricated stream (content streams are decrypted whole
    /// before this interpreter ever sees them).
    /// </summary>
    /// <param name="dictionary">The inline image's key/value pairs, abbreviations already
    /// expanded.</param>
    /// <param name="data">The image's own bytes between <c>ID</c> and <c>EI</c>.</param>
    /// <param name="limits">The decode ceiling to enforce; see <see cref="TryDecode"/>.</param>
    /// <param name="resolve">Optional indirect-reference resolver.</param>
    /// <param name="diagnostics">Optional diagnostics sink.</param>
    internal static ImageDecodeResult DecodeForImage(
        PdfDictionary dictionary, ReadOnlyMemory<byte> data, ReaderLimits limits,
        Func<PdfObject?, PdfObject?>? resolve, DiagnosticSink? diagnostics)
    {
        if (data.Length > limits.MaxDecodedBytes)
            return new ImageDecodeResult([], null, null, Succeeded: false);

        try
        {
            return DecodeCore(dictionary, data, null, null, limits, resolve, diagnostics);
        }
        catch (InvalidDataException)
        {
            return new ImageDecodeResult([], null, null, Succeeded: false);
        }
    }

    private static byte[] ApplyFilter(
        PdfName filter, PdfDictionary? parms, byte[] input, long maxDecodedBytes,
        DiagnosticSink? diagnostics, int? objectNumber, int? generation)
    {
        if (filter.Value is "FlateDecode" or "Fl")
        {
            var raw = InflateFlate(input, maxDecodedBytes, diagnostics, objectNumber, generation);
            return ApplyPredictor(parms, raw);
        }
        if (filter.Value is "LZWDecode" or "LZW")
        {
            var earlyChange = 1;
            if (parms?.Get(_earlyChange) is PdfInteger ec)
                earlyChange = (int)ec.Value;
            var raw = DecodeLzw(input, earlyChange, maxDecodedBytes, diagnostics, objectNumber, generation);
            return ApplyPredictor(parms, raw);
        }
        if (filter.Value is "ASCIIHexDecode" or "AHx")
            return DecodeAsciiHex(input);
        if (filter.Value is "ASCII85Decode" or "A85")
            return DecodeAscii85(input);
        if (filter.Value is "RunLengthDecode" or "RL")
            return DecodeRunLength(input, maxDecodedBytes, diagnostics, objectNumber, generation);

        // /Crypt (ISO 32000-2 §7.4.10) is a no-op at this layer: PdfDocumentReader.GetDecodedStreamData
        // resolves which crypt filter method a stream's own /Crypt entry (or the document-wide /StmF)
        // names, via CryptFilterResolver, and performs the actual decryption BEFORE handing the body
        // to this filter chain — that is what design decision #1 in #97 requires, since decryption
        // changes a stream's length and StreamRule/HexStringRule need RawBody to stay the verbatim
        // file bytes. By the time /Crypt is encountered here, whatever it named has already happened
        // (or, for /Identity, correctly not happened); passing the bytes through unchanged is correct
        // either way — it is never this method's job to decrypt.
        if (filter.Value == "Crypt")
            return input;

        // Reported before the throw, not instead of it (#385 routing is observe-only): a caller
        // that catches the InvalidDataException upstream and keeps the reader alive still sees this
        // in PdfDocumentReader.Diagnostics, and the reader gave up on the object either way, which
        // is what makes this severity Error rather than Warning. The name is excerpted, not
        // interpolated whole: a /Filter name has no length bound (Annex C.1), this
        // dictionary's filter object is dereferenced once and shared by every stream that
        // resolves to it, and a diagnostic is retained for the reader's lifetime, so an
        // attacker- or corruption-sized name would become a comparably sized permanent allocation
        // once per (code, object, page) the sink's dedupe key admits, per stream (#402 round 8; the
        // throw below keeps the whole name, since it is transient and AddElement replaces its
        // Message before any caller sees it).
        diagnostics?.Report(
            PdfReaderDiagnosticCode.UnknownFilter,
            $"Unknown PDF filter: /{DiagnosticExcerpt.Quote(filter.Value)}.",
            objectNumber, generation);
        throw new InvalidDataException($"Unknown PDF filter: /{filter.Value}");
    }

    // ── FlateDecode ──────────────────────────────────────────────────────────

    internal static byte[] InflateFlate(
        byte[] input, long maxDecodedBytes,
        DiagnosticSink? diagnostics = null, int? objectNumber = null, int? generation = null)
    {
        // FlateDecode is zlib (RFC 1950), but some producers emit raw deflate. Use the 2-byte
        // header as a fast-path hint for which to try first, then fall back to the other on a
        // format error — so neither a header-less raw-deflate stream nor a zlib stream is rejected.
        // The fallback must NEVER swallow the decompression-size cap (that would mask a bomb and
        // double-decompress), so the cap is thrown as a distinct exception type that is re-thrown.
        var primaryIsZlib = LooksLikeZlib(input);
        try
        {
            return Inflate(MakeDecompressor(input, primaryIsZlib), maxDecodedBytes);
        }
        catch (DecompressionLimitExceededException ex)
        {
            // A decompression bomb: surface it, never retry.
            ReportDecodedStreamLimitExceeded(diagnostics, objectNumber, generation, ex.Message);
            throw new InvalidDataException(ex.Message);
        }
        catch (Exception primaryError) when (primaryError is not OutOfMemoryException)
        {
            // Format error on the primary decoder — retry with the other (handles header-less
            // raw deflate vs. zlib-wrapped). Still never swallow the size cap. OutOfMemoryException
            // is excluded so a real OOM is not masked as malformed input or retried.
            try
            {
                return Inflate(MakeDecompressor(input, !primaryIsZlib), maxDecodedBytes);
            }
            catch (DecompressionLimitExceededException ex)
            {
                ReportDecodedStreamLimitExceeded(diagnostics, objectNumber, generation, ex.Message);
                throw new InvalidDataException(ex.Message);
            }
            catch (Exception inner)
            {
                // Normalise any BCL decode failure (InvalidDataException, IOException, …) to a
                // single InvalidDataException so callers see a consistent malformed-input signal.
                throw new InvalidDataException("FlateDecode: failed to decompress stream body.", inner);
            }
        }
    }

    /// <summary>
    /// Reports #385's <see cref="PdfReaderDiagnosticCode.DecodedStreamLimitExceeded"/> before the
    /// three decompression bomb guards (FlateDecode, LZWDecode, RunLengthDecode) re-throw as
    /// <see cref="InvalidDataException"/> — observe-only, the throw still happens either way.
    /// </summary>
    private static void ReportDecodedStreamLimitExceeded(
        DiagnosticSink? diagnostics, int? objectNumber, int? generation, string message) =>
        diagnostics?.Report(PdfReaderDiagnosticCode.DecodedStreamLimitExceeded, message, objectNumber, generation);

    private static Stream MakeDecompressor(byte[] input, bool zlib) => zlib
        ? new ZLibStream(new MemoryStream(input), CompressionMode.Decompress)
        : new DeflateStream(new MemoryStream(input), CompressionMode.Decompress);

    private static bool LooksLikeZlib(byte[] input)
    {
        // RFC 1950: low nibble of CMF is the compression method (8 = deflate), and the 16-bit
        // CMF/FLG header is a multiple of 31.
        if (input.Length < 2)
            return false;
        var cmf = input[0];
        var flg = input[1];
        return (cmf & 0x0F) == 8 && (((cmf << 8) | flg) % 31) == 0;
    }

    private static byte[] Inflate(Stream decompressor, long maxDecodedBytes)
    {
        using var decoStream = decompressor;
        var ms = new MemoryStream();
        var buf = new byte[65536];
        long total = 0;
        int read;
        while ((read = decoStream.Read(buf, 0, buf.Length)) > 0)
        {
            total += read;
            if (total > maxDecodedBytes)
                throw new DecompressionLimitExceededException(
                    $"Decompressed stream size exceeds {maxDecodedBytes} bytes ({maxDecodedBytes / 1024.0 / 1024.0:F2} MiB) cap.");
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Internal signal that decompression exceeded the decode's <see cref="ReaderLimits.MaxDecodedBytes"/>
    /// cap. A distinct type lets <see cref="InflateFlate"/> distinguish the bomb guard from an
    /// ordinary format error so it re-throws (as <see cref="InvalidDataException"/>) instead of
    /// retrying the other decoder.
    /// </summary>
    private sealed class DecompressionLimitExceededException(string message) : Exception(message);

    // ── Predictors ───────────────────────────────────────────────────────────

    private static byte[] ApplyPredictor(PdfDictionary? parms, byte[] data)
    {
        if (parms is null) return data;
        if (parms.Get(_predictor) is not PdfInteger predObj) return data;

        var predictor = (int)predObj.Value;
        if (predictor == 1) return data; // None

        var columns = parms.Get(_columns) is PdfInteger col ? col.Value : 1;
        var colors = parms.Get(_colors) is PdfInteger clr ? clr.Value : 1;
        var bpc = parms.Get(_bpc) is PdfInteger b ? b.Value : 8;

        // Guard untrusted predictor parameters: out-of-range values could overflow the row-size
        // computation to a negative/huge array length (an uncaught OverflowException or an
        // allocation-amplification DoS) instead of a clean InvalidDataException.
        // Cap columns so that columns*colors*bpc (max 1M*32*16 = 512M) cannot overflow a 32-bit int.
        if (columns is < 1 or > (1 << 20) || colors is < 1 or > 32 || bpc is not (1 or 2 or 4 or 8 or 16))
            throw new InvalidDataException(
                $"FlateDecode predictor: invalid Columns/Colors/BitsPerComponent ({columns}/{colors}/{bpc}).");

        if (predictor == 2)
            return ApplyTiffPredictor2(data, (int)columns, (int)colors, (int)bpc);

        if (predictor >= 10 && predictor <= 15)
            return ApplyPngPredictor(data, (int)columns, (int)colors, (int)bpc);

        return data;
    }

    // TIFF predictor 2 (ISO 32000-2 §7.4.4.4): horizontal differencing per colour component. Each
    // sample is the sum, modulo 2^BitsPerComponent, of the still-differenced value read and every
    // prior instance of that same component earlier in the row: "The TIFF function group shall
    // predict each colour component from the prior instance of that component" (§7.4.4.4). A row's
    // own leading `colors` samples have no such prior instance and are left as read. Rows never
    // predict across each other: each restarts at its own first `colors` samples, the same rule
    // for every supported bit depth (1, 2, 4, 8, 16).
    private static byte[] ApplyTiffPredictor2(byte[] data, int columns, int colors, int bpc)
    {
        // "A row shall occupy a whole number of bytes, rounded up if necessary" (§7.4.4.4).
        var rowBytes = (columns * colors * bpc + 7) / 8;
        if (data.Length == 0 || rowBytes == 0) return data;
        var rows = data.Length / rowBytes;
        var result = new byte[rows * rowBytes];

        if (bpc == 8)
        {
            for (var row = 0; row < rows; row++)
            {
                var src = row * rowBytes;
                var dst = row * rowBytes;
                for (var i = 0; i < rowBytes; i++)
                {
                    var prev = i >= colors ? result[dst + i - colors] : (byte)0;
                    result[dst + i] = (byte)(data[src + i] + prev);
                }
            }
            return result;
        }

        if (bpc == 16)
        {
            // No padding at 16 bits: columns*colors*16 is always a multiple of 8, so rowBytes is
            // exactly samplesPerRow * 2 with nothing left over to preserve untouched.
            var samplesPerRow = columns * colors;
            for (var row = 0; row < rows; row++)
            {
                var rowStart = row * rowBytes;
                var decoded = new ushort[samplesPerRow];
                for (var i = 0; i < samplesPerRow; i++)
                {
                    // "units of 16 bits shall be given with the most significant byte first"
                    // (ISO 32000-2 §8.9.3).
                    var raw = (ushort)((data[rowStart + i * 2] << 8) | data[rowStart + i * 2 + 1]);
                    var prev = i >= colors ? decoded[i - colors] : (ushort)0;
                    decoded[i] = (ushort)(raw + prev);
                }
                for (var i = 0; i < samplesPerRow; i++)
                {
                    result[rowStart + i * 2] = (byte)(decoded[i] >> 8);
                    result[rowStart + i * 2 + 1] = (byte)decoded[i];
                }
            }
            return result;
        }

        // bpc 1, 2, 4: unpack one byte per component value (never bpc/8 bytes, which is 0 at every
        // sub-byte depth), predict, and repack "from high-order to low-order bits" (§7.4.4.4). A
        // row whose sample count does not fill its last byte carries padding bits past the last
        // sample; those are copied through untouched (result starts as a copy of data) and never
        // read as part of any sample, so they cannot be accumulated into or overwritten below.
        var samplesPerRow2 = columns * colors;
        var mask = (1 << bpc) - 1;
        var samplesPerByte = 8 / bpc;
        var samples = new byte[samplesPerRow2];
        for (var row = 0; row < rows; row++)
        {
            var rowStart = row * rowBytes;
            Array.Copy(data, rowStart, result, rowStart, rowBytes);

            for (var i = 0; i < samplesPerRow2; i++)
            {
                var byteIndex = rowStart + i / samplesPerByte;
                var shift = 8 - bpc - (i % samplesPerByte) * bpc;
                samples[i] = (byte)((data[byteIndex] >> shift) & mask);
            }

            for (var i = 0; i < samplesPerRow2; i++)
            {
                if (i >= colors)
                    samples[i] = (byte)((samples[i] + samples[i - colors]) & mask);

                var byteIndex = rowStart + i / samplesPerByte;
                var shift = 8 - bpc - (i % samplesPerByte) * bpc;
                result[byteIndex] = (byte)((result[byteIndex] & ~(mask << shift)) | (samples[i] << shift));
            }
        }
        return result;
    }

    private static byte[] ApplyPngPredictor(byte[] data, int columns, int colors, int bpc)
    {
        var rowBytes = (columns * colors * bpc + 7) / 8;
        var stride = rowBytes + 1; // +1 for the per-row filter byte
        if (data.Length == 0 || rowBytes == 0) return data;

        var rows = data.Length / stride;
        var result = new byte[rows * rowBytes];
        var prev = new byte[rowBytes];

        var bpp = Math.Max(1, colors * bpc / 8);

        for (var row = 0; row < rows; row++)
        {
            var filterType = data[row * stride];
            var srcStart = row * stride + 1;
            var dstStart = row * rowBytes;

            var dst = result.AsSpan(dstStart, rowBytes);
            data.AsSpan(srcStart, rowBytes).CopyTo(dst);

            switch (filterType)
            {
                case 0: // None
                    break;
                case 1: // Sub
                    for (var x = bpp; x < rowBytes; x++)
                        dst[x] = (byte)(dst[x] + dst[x - bpp]);
                    break;
                case 2: // Up
                    for (var x = 0; x < rowBytes; x++)
                        dst[x] = (byte)(dst[x] + prev[x]);
                    break;
                case 3: // Average
                    for (var x = 0; x < rowBytes; x++)
                    {
                        var a = x >= bpp ? dst[x - bpp] : (byte)0;
                        dst[x] = (byte)(dst[x] + (a + prev[x]) / 2);
                    }
                    break;
                case 4: // Paeth
                    for (var x = 0; x < rowBytes; x++)
                    {
                        var a = x >= bpp ? dst[x - bpp] : (byte)0;
                        var b = prev[x];
                        var c = x >= bpp ? prev[x - bpp] : (byte)0;
                        dst[x] = (byte)(dst[x] + PaethPredictor(a, b, c));
                    }
                    break;
                default:
                    throw new InvalidDataException(
                        $"PNG predictor: unsupported row filter type {filterType}.");
            }

            dst.CopyTo(prev.AsSpan());
        }
        return result;
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    // ── LZWDecode ────────────────────────────────────────────────────────────

    private static byte[] DecodeLzw(
        byte[] input, int earlyChange, long maxDecodedBytes,
        DiagnosticSink? diagnostics = null, int? objectNumber = null, int? generation = null)
    {
        const int ClearCode = 256;
        const int EoiCode = 257;

        var output = new MemoryStream();
        var table = new List<byte[]>(4096);
        int codeSize = 9;
        byte[]? prevEntry = null;

        void ResetTable()
        {
            table.Clear();
            for (var i = 0; i < 256; i++)
                table.Add([(byte)i]);
            table.Add([]); // 256 = clear
            table.Add([]); // 257 = EOI
            codeSize = 9;
            prevEntry = null;
        }

        ResetTable();

        var bitPos = 0L;
        var inputLen = (long)input.Length * 8;

        long ReadCode()
        {
            var code = 0L;
            for (var bit = 0; bit < codeSize; bit++)
            {
                if (bitPos >= inputLen) return EoiCode;
                var byteIdx = (int)(bitPos / 8);
                var bitIdx = 7 - (int)(bitPos % 8);
                if ((input[byteIdx] & (1 << bitIdx)) != 0)
                    code |= 1L << (codeSize - 1 - bit);
                bitPos++;
            }
            return code;
        }

        void MaybeGrow()
        {
            // EarlyChange=1: grow when table size equals (1<<codeSize)-1
            // EarlyChange=0: grow when table size equals (1<<codeSize)
            var threshold = earlyChange == 1
                ? (1 << codeSize) - 1
                : (1 << codeSize);

            if (table.Count >= threshold && codeSize < 12)
                codeSize++;
        }

        while (true)
        {
            MaybeGrow();
            var code = (int)ReadCode();

            if (code == EoiCode) break;
            if (code == ClearCode)
            {
                ResetTable();
                continue;
            }

            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
            }
            else if (code == table.Count && prevEntry is not null)
            {
                // Special case: code not yet in table — entry = prevEntry + prevEntry[0]
                entry = [.. prevEntry, prevEntry[0]];
            }
            else
            {
                throw new InvalidDataException($"LZWDecode: invalid code {code} at table size {table.Count}.");
            }

            if (output.Length + entry.Length > maxDecodedBytes)
            {
                var message =
                    $"LZWDecode: decompressed size exceeds {maxDecodedBytes} bytes ({maxDecodedBytes / 1024.0 / 1024.0:F2} MiB) cap.";
                ReportDecodedStreamLimitExceeded(diagnostics, objectNumber, generation, message);
                throw new InvalidDataException(message);
            }

            output.Write(entry);

            if (prevEntry is not null && table.Count < 4096)
                table.Add([.. prevEntry, entry[0]]);

            prevEntry = entry;
        }

        return output.ToArray();
    }

    // ── ASCIIHexDecode ───────────────────────────────────────────────────────

    private static byte[] DecodeAsciiHex(byte[] input)
    {
        var output = new MemoryStream();
        var i = 0;
        while (i < input.Length)
        {
            var b = input[i++];
            if (b == (byte)'>') break; // EOD
            if (IsWhitespace(b)) continue;
            var hi = HexDigit(b);
            if (hi < 0)
                throw new InvalidDataException($"ASCIIHexDecode: invalid hex byte 0x{b:X2}.");

            byte lo = 0;
            // Find next non-whitespace
            while (i < input.Length && IsWhitespace(input[i])) i++;
            if (i < input.Length && input[i] != (byte)'>')
            {
                var lb = input[i++];
                var ld = HexDigit(lb);
                if (ld < 0)
                    throw new InvalidDataException($"ASCIIHexDecode: invalid hex byte 0x{lb:X2}.");
                lo = (byte)ld;
            }
            output.WriteByte((byte)((hi << 4) | lo));
        }
        return output.ToArray();
    }

    // ── ASCII85Decode ────────────────────────────────────────────────────────

    private static byte[] DecodeAscii85(byte[] input)
    {
        var output = new MemoryStream();
        var i = 0;
        Span<byte> group = stackalloc byte[5];
        while (i < input.Length)
        {
            var b = input[i];
            if (IsWhitespace(b)) { i++; continue; }

            // EOD marker '~>'
            if (b == (byte)'~')
            {
                if (i + 1 < input.Length && input[i + 1] == (byte)'>') break;
                throw new InvalidDataException("ASCII85Decode: invalid '~' not followed by '>'.");
            }

            if (b == (byte)'z')
            {
                // 'z' encodes four zero bytes
                output.Write([0, 0, 0, 0]);
                i++;
                continue;
            }

            // Collect up to 5 chars in the range '!'(33) to 'u'(117)
            var count = 0;
            while (count < 5 && i < input.Length)
            {
                var cb = input[i];
                if (IsWhitespace(cb)) { i++; continue; }
                if (cb == (byte)'~') break;
                if (cb < 33 || cb > 117)
                    throw new InvalidDataException($"ASCII85Decode: invalid character 0x{cb:X2}.");
                group[count++] = (byte)(cb - 33);
                i++;
            }

            if (count == 0) continue;

            // A final group must hold 2..5 characters; a single trailing character is invalid and
            // would otherwise emit one spurious byte.
            if (count == 1)
                throw new InvalidDataException("ASCII85Decode: final group has a single character.");

            // Pad to 5 with 'u' value = 84
            for (var p = count; p < 5; p++)
                group[p] = 84;

            var val = (long)group[0] * 52200625L
                    + (long)group[1] * 614125L
                    + (long)group[2] * 7225L
                    + (long)group[3] * 85L
                    + group[4];

            if (val > 0xFFFFFFFFL)
                throw new InvalidDataException("ASCII85Decode: group value out of range.");

            // Emit count-1 bytes
            var bytesToEmit = count - 1;
            output.WriteByte((byte)((val >> 24) & 0xFF));
            if (bytesToEmit >= 2) output.WriteByte((byte)((val >> 16) & 0xFF));
            if (bytesToEmit >= 3) output.WriteByte((byte)((val >> 8) & 0xFF));
            if (bytesToEmit >= 4) output.WriteByte((byte)(val & 0xFF));
        }
        return output.ToArray();
    }

    // ── RunLengthDecode ──────────────────────────────────────────────────────

    private static byte[] DecodeRunLength(
        byte[] input, long maxDecodedBytes,
        DiagnosticSink? diagnostics = null, int? objectNumber = null, int? generation = null)
    {
        var output = new MemoryStream();
        var i = 0;
        while (i < input.Length)
        {
            var length = input[i++];
            if (length == 128) break; // EOD
            if (length < 128)
            {
                // literal: copy (length+1) bytes
                var count = length + 1;
                if (i + count > input.Length)
                    throw new InvalidDataException("RunLengthDecode: literal run extends past end of input.");
                if (output.Length + count > maxDecodedBytes)
                {
                    var message =
                        $"RunLengthDecode: decompressed size exceeds {maxDecodedBytes} bytes ({maxDecodedBytes / 1024.0 / 1024.0:F2} MiB) cap.";
                    ReportDecodedStreamLimitExceeded(diagnostics, objectNumber, generation, message);
                    throw new InvalidDataException(message);
                }
                output.Write(input, i, count);
                i += count;
            }
            else
            {
                // repeat: 257 - length copies of next byte
                var count = 257 - length;
                if (i >= input.Length)
                    throw new InvalidDataException("RunLengthDecode: repeat run missing data byte.");
                var b = input[i++];
                if (output.Length + count > maxDecodedBytes)
                {
                    var message =
                        $"RunLengthDecode: decompressed size exceeds {maxDecodedBytes} bytes ({maxDecodedBytes / 1024.0 / 1024.0:F2} MiB) cap.";
                    ReportDecodedStreamLimitExceeded(diagnostics, objectNumber, generation, message);
                    throw new InvalidDataException(message);
                }
                for (var j = 0; j < count; j++)
                    output.WriteByte(b);
            }
        }
        return output.ToArray();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PdfObject? Deref(Func<PdfObject?, PdfObject?>? resolve, PdfObject? obj)
        => resolve is null ? obj : resolve(obj);

    private static List<PdfName> GetFilterList(
        PdfDictionary dict, Func<PdfObject?, PdfObject?>? resolve,
        DiagnosticSink? diagnostics, int? objectNumber, int? generation)
    {
        // /Filter only. /F in a stream dictionary is the (external) file specification,
        // not a filter abbreviation, so it must not be consulted here.
        // /Filter (and each array element) may be an indirect reference — resolve when able.
        var filterObj = Deref(resolve, dict.Get(PdfName.Filter));
        // An indirect /Filter that fails to resolve dereferences to the null object (ISO 32000-2
        // §7.3.10), and a null-valued dictionary entry is equivalent to the entry being absent
        // (§7.3.9). So a null here means "this stream declares no filter", not "an error
        // occurred" — the stream is handed back unfiltered, per spec, not flagged. See #373.
        if (filterObj is null) return [];
        if (filterObj is PdfName n) return [n];
        if (filterObj is PdfNull)
        {
            // An EXPLICIT /Filter null, as opposed to the key being absent — still "no filter"
            // under §7.3.9's equivalence, but distinct enough from an ordinary omission (a
            // producer wrote something here) that it is worth an Info-level note rather than
            // passing through in total silence.
            diagnostics?.Report(
                PdfReaderDiagnosticCode.FilterNull,
                "/Filter is explicitly null; treated as absent per ISO 32000-2 §7.3.9.",
                objectNumber, generation);
            return [];
        }
        if (filterObj is PdfArray arr)
        {
            var list = new List<PdfName>(arr.Count);
            for (var i = 0; i < arr.Count; i++)
            {
                var element = Deref(resolve, arr[i]);
                if (element is PdfName fn)
                {
                    list.Add(fn);
                    continue;
                }

                // #373: a non-name element is dropped from the chain rather than applied — this
                // records that a producer wrote something the chain silently skips, which
                // previously left no trace at all.
                diagnostics?.Report(
                    PdfReaderDiagnosticCode.FilterArrayElementNotName,
                    $"/Filter array element {i} did not resolve to a name; dropped from the chain.",
                    objectNumber, generation);
            }
            return list;
        }

        diagnostics?.Report(
            PdfReaderDiagnosticCode.FilterValueMalformed,
            $"/Filter resolved to a {filterObj.GetType().Name}, neither a name, an array, nor null; treated as absent.",
            objectNumber, generation);
        return [];
    }

    private static List<PdfDictionary?> GetParmsList(
        PdfDictionary dict, int filterCount, Func<PdfObject?, PdfObject?>? resolve,
        DiagnosticSink? diagnostics, int? objectNumber, int? generation)
    {
        var pObj = Deref(resolve, dict.Get(_dp) ?? dict.Get(_dp2));
        // An explicit /DecodeParms null is equivalent to the entry being absent (ISO 32000-2
        // §7.3.9), same as the array-element case just below treats a null element — so it is
        // handled here rather than falling into the catch-all, which would otherwise report
        // DecodeParmsMalformed with a message that contradicts the very rule this branch follows.
        if (pObj is null or PdfNull)
        {
            var list = new List<PdfDictionary?>(filterCount);
            for (var i = 0; i < filterCount; i++) list.Add(null);
            return list;
        }
        if (pObj is PdfDictionary pd) return [pd];
        if (pObj is PdfArray arr)
        {
            var list = new List<PdfDictionary?>(arr.Count);
            for (var i = 0; i < arr.Count; i++)
            {
                var element = Deref(resolve, arr[i]);
                if (element is PdfDictionary d)
                {
                    list.Add(d);
                    continue;
                }

                // A non-dictionary, non-null element supplies no parameters for its filter — same
                // "treated as absent" outcome as the catch-all below, reported here because it is
                // the array-element shape rather than the whole entry that is malformed.
                if (element is not (null or PdfNull))
                {
                    diagnostics?.Report(
                        PdfReaderDiagnosticCode.DecodeParmsMalformed,
                        $"/DecodeParms array element {i} did not resolve to a dictionary; treated as no parameters.",
                        objectNumber, generation);
                }
                list.Add(null);
            }
            return list;
        }

        diagnostics?.Report(
            PdfReaderDiagnosticCode.DecodeParmsMalformed,
            $"/DecodeParms resolved to a {pObj.GetType().Name}, neither a dictionary, an array, nor null; treated as absent.",
            objectNumber, generation);
        return [];
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
