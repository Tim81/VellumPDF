// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Why <see cref="ImageDecoder"/> decoded a stream: a drawn image XObject or inline image (
/// <see cref="Image"/>), or one reached through another image's own <c>/SMask</c>
/// (<see cref="SoftMask"/>) or <c>/Mask</c> stream (<see cref="ExplicitMask"/>). Part of the
/// per-call image cache's own key: the same stream drawn once and also used as another image's
/// mask must produce two distinct <see cref="PdfExtractedImage"/> instances, since a mask is
/// validated and flagged differently (<see cref="PdfExtractedImage.IsSoftMask"/>,
/// <see cref="PdfExtractedImage.IsExplicitMask"/>) than a drawn image.
/// </summary>
internal enum ImageRole
{
    Image,
    SoftMask,
    ExplicitMask,
}

/// <summary>The ancillary-stream cache's own key component: which of the three kinds of
/// stream shared across images this decoded byte array is.</summary>
internal enum AncillaryRole
{
    IndexedLookup,
    IccProfile,
    Jbig2Globals,
}

/// <summary>
/// The occurrence counter and aggregate byte budget one <c>ExtractImages</c> call shares across
/// <c>ImageReachabilityWalker</c> and <see cref="ImageDecoder"/>. Two counting sites feed the
/// occurrence counter (the walker, for what the content draws; this decoder, for the derived
/// masks the walker never sees), and two feed the byte counter
/// (<see cref="ColorSpaceReader"/>'s own ancillary-stream decodes, and this decoder's own image
/// buffers), but each counter is checked, and each retained diagnostic reported, in exactly one
/// place, so the two call sites cannot double-report the same condition under one shared
/// <c>(code, null, null)</c> key.
/// </summary>
internal sealed class ImageCallBudget(long maxDecodedBytes, DiagnosticSink diagnostics)
{
    /// <summary>
    /// This reader's own ceiling on image occurrences per call: drawn images, inline
    /// images, and derived masks all share this one count. Not derived from
    /// <see cref="PdfReaderOptions"/>; #376's tighten-only rule already covers the byte ceilings
    /// this budget also enforces (<see cref="PdfReaderOptions.MaxDecodedStreamBytes"/>), and this
    /// reader's own occurrence ceiling is not part of that option's contract.
    /// </summary>
    internal const int MaxImageOccurrencesPerCall = 100_000;

    private readonly long _maxDecodedBytes = maxDecodedBytes;
    private long _bytesRemaining = maxDecodedBytes;
    private int _occurrences;
    private bool _byteBudgetExhausted;
    private bool _occurrenceLimitExhausted;

    /// <summary>Whether the byte budget has already been reported exhausted (510). Checked at the
    /// top of every image this call would otherwise decode, so nothing past the first image that
    /// tripped it does any further work.</summary>
    internal bool IsByteBudgetExhausted => _byteBudgetExhausted;

    /// <summary> Charges <paramref name="bytes"/> against the remaining budget, returning <see
    /// langword="false"/> (and reporting <see
    /// cref="PdfReaderDiagnosticCode.ImageExtractionBudgetExhausted"/> through
    /// <c>ReportRetained</c>, the first time only) when it would not fit.
    /// </summary>
    internal bool TryChargeBytes(long bytes)
    {
        if (_byteBudgetExhausted)
            return false;

        if (bytes > _bytesRemaining)
        {
            _byteBudgetExhausted = true;
            diagnostics.ReportRetained(
                PdfReaderDiagnosticCode.ImageExtractionBudgetExhausted,
                $"The sum of the image buffers this call holds reached the "
                + $"{_maxDecodedBytes / 1024.0 / 1024.0:F0} MiB decode limit; every further image was "
                + "skipped without decoding.");
            return false;
        }

        _bytesRemaining -= bytes;
        return true;
    }

    /// <summary>
    /// Consumes one occurrence slot, returning <see langword="false"/> (and reporting
    /// <see cref="PdfReaderDiagnosticCode.ImageOccurrenceLimitExceeded"/> through
    /// <c>ReportRetained</c>, the first time only) once
    /// <see cref="MaxImageOccurrencesPerCall"/> has been reached.
    /// </summary>
    internal bool TryConsumeOccurrence()
    {
        if (_occurrenceLimitExhausted)
            return false;

        if (_occurrences >= MaxImageOccurrencesPerCall)
        {
            _occurrenceLimitExhausted = true;
            diagnostics.ReportRetained(
                PdfReaderDiagnosticCode.ImageOccurrenceLimitExceeded,
                $"This call reached {MaxImageOccurrencesPerCall:N0} image occurrences (drawn images, "
                + "inline images, and derived masks); every further occurrence was skipped.");
            return false;
        }

        _occurrences++;
        return true;
    }
}

/// <summary> The per-call cache for an Indexed lookup stream, an ICCBased profile stream, or a
/// <c>/JBIG2Globals</c> stream, shared between <see cref="ImageDecoder"/> and <see
/// cref="ColorSpaceReader"/>: each of these is named by object identity and role, decoded once, and
/// charged against <see cref="ImageCallBudget"/> once, however many images reference it.
/// </summary>
internal sealed class AncillaryStreamCache
{
    private readonly Dictionary<(int ObjectNumber, int Generation, AncillaryRole Role), byte[]?> _cache = [];

    /// <summary>
    /// Returns the decoded bytes of <paramref name="stream"/> under <paramref name="role"/>,
    /// decoding and charging <paramref name="budget"/> only on the first call for this
    /// (object, generation, role) triple. Returns <see langword="null"/> when the decode failed or
    /// the byte budget refused the charge; that outcome is cached too, so a later caller does not
    /// retry.
    /// </summary>
    internal byte[]? GetOrDecode(
        PdfDocumentReader reader, ParsedStream stream, AncillaryRole role, ImageCallBudget budget,
        ReaderLimits limits, DiagnosticSink diagnostics)
    {
        var key = (stream.ObjectNumber, stream.Generation, role);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        byte[]? bytes = null;
        if (!budget.IsByteBudgetExhausted)
        {
            var view = reader.DecryptedStreamView(stream);
            var result = PdfFilters.DecodeForImage(view, limits, reader.ResolveMaybe, diagnostics);
            if (result.Succeeded && budget.TryChargeBytes(result.Data.Length))
                bytes = result.Data;
        }

        _cache[key] = bytes;
        return bytes;
    }
}

/// <summary>
/// Turns a <see cref="ParsedStream"/> (an image XObject) or an inline image's dictionary and data
/// into a <see cref="PdfExtractedImage"/> (#98): the ISO 32000-2 §8.9.5/§8.9.7 dictionary rules,
/// Table 87's bit-depth and filter rules, colour space resolution, <c>/Decode</c> exposure,
/// <c>/SMask</c>/<c>/Mask</c>/<c>/SMaskInData</c>, JPX signature sniffing, the per-call image and
/// ancillary-stream caches, and the byte and occurrence accounting this reader's own checking
/// order requires. One instance per <c>ExtractImages</c> call.
/// </summary>
internal sealed class ImageDecoder
{
    private static readonly PdfName WidthKey = new("Width");
    private static readonly PdfName HeightKey = new("Height");
    private static readonly PdfName BitsPerComponentKey = new("BitsPerComponent");
    private static readonly PdfName ImageMaskKey = new("ImageMask");
    private static readonly PdfName InterpolateKey = new("Interpolate");
    private static readonly PdfName SMaskInDataKey = new("SMaskInData");
    private static readonly PdfName DecodeKey = new("Decode");
    private static readonly PdfName SMaskKey = new("SMask");
    private static readonly PdfName MaskKey = new("Mask");
    private static readonly PdfName MatteKey = new("Matte");
    private static readonly PdfName DecodeParmsKey = new("DecodeParms");
    private static readonly PdfName Jbig2GlobalsKey = new("JBIG2Globals");
    private static readonly PdfName ColorTransformKey = new("ColorTransform");
    private static readonly PdfName KKey = new("K");
    private static readonly PdfName ColumnsKey = new("Columns");
    private static readonly PdfName RowsKey = new("Rows");
    private static readonly PdfName BlackIs1Key = new("BlackIs1");
    private static readonly PdfName EncodedByteAlignKey = new("EncodedByteAlign");
    private static readonly PdfName EndOfLineKey = new("EndOfLine");
    private static readonly PdfName EndOfBlockKey = new("EndOfBlock");
    private static readonly PdfName DamagedRowsBeforeErrorKey = new("DamagedRowsBeforeError");

    // ISO/IEC 15444-1's own signature box, twelve bytes, and the SOC-then-SIZ marker pair that
    // opens a bare JPEG 2000 codestream with no box structure around it. Neither string occurs in
    // ISO 32000-2 itself (§7.4.9 only requires "a full JPX file structure"); both come from the
    // JPEG 2000 standard, which ISO 32000-2 normatively references there. Kernel's
    // JpxImageLoader.cs holds the same two constants for its own, unrelated purpose (reading a JPX
    // file to determine its dimensions), not shared with this reader.
    private static readonly byte[] Jp2Signature =
        [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
    private static readonly byte[] BareCodestreamStart = [0xFF, 0x4F, 0xFF, 0x51];

    private readonly PdfDocumentReader _reader;
    private readonly ReaderLimits _limits;
    private readonly AncillaryStreamCache _ancillaryCache = new();
    private readonly ColorSpaceReader _colorSpaceReader;
    private readonly Dictionary<(int ObjectNumber, int Generation, ImageRole Role), PdfExtractedImage?> _imageCache = [];

    /// <summary>The occurrence and byte budget this call shares with
    /// <c>ImageReachabilityWalker</c>.</summary>
    internal ImageCallBudget Budget { get; }

    internal ImageDecoder(PdfDocumentReader reader, ReaderLimits limits, DiagnosticSink diagnostics)
    {
        _reader = reader;
        _limits = limits;
        Budget = new ImageCallBudget(limits.MaxDecodedBytes, diagnostics);
        _colorSpaceReader = new ColorSpaceReader(reader, _ancillaryCache, Budget, limits);
    }

    /// <summary>Decodes a drawn image XObject. The caller (<c>ImageReachabilityWalker</c>) has
    /// already consumed one occurrence slot from <see cref="Budget"/> for it.</summary>
    internal PdfExtractedImage? Decode(
        ParsedStream stream, PdfDictionary? resources, int pageIndex, DiagnosticSink diagnostics) =>
        DecodeXObjectCore(stream, resources, pageIndex, ImageRole.Image, diagnostics);

    /// <summary>Decodes an inline image. <paramref name="data"/> is the walker's own copy (inline
    /// images bypass the image cache: they have no object identity).</summary>
    internal PdfExtractedImage? DecodeInline(
        PdfDictionary dictionary, byte[] data, PdfDictionary? resources, int pageIndex,
        DiagnosticSink diagnostics)
    {
        if (Budget.IsByteBudgetExhausted)
            return null;

        return BuildImage(
            dictionary, objectNumber: null, generation: null, isInline: true, resources, pageIndex,
            ImageRole.Image, diagnostics, data.Length,
            () => PdfFilters.DecodeForImage(dictionary, data, _limits, _reader.ResolveMaybe, diagnostics));
    }

    private PdfExtractedImage? DecodeXObjectCore(
        ParsedStream stream, PdfDictionary? resources, int pageIndex, ImageRole role,
        DiagnosticSink diagnostics)
    {
        var key = (stream.ObjectNumber, stream.Generation, role);
        if (_imageCache.TryGetValue(key, out var cached))
            return cached;

        PdfExtractedImage? result = null;
        if (!Budget.IsByteBudgetExhausted && (role == ImageRole.Image || Budget.TryConsumeOccurrence()))
        {
            result = BuildImage(
                stream.Dictionary, stream.ObjectNumber, stream.Generation, isInline: false, resources,
                pageIndex, role, diagnostics, stream.RawBody.Length,
                () => PdfFilters.DecodeForImage(
                    _reader.DecryptedStreamView(stream), _limits, _reader.ResolveMaybe, diagnostics));
        }

        _imageCache[key] = result;
        return result;
    }

    // Shared by a drawn/inline image and by a mask reached through it (role decides which). The
    // checks below run in a fixed order because each gates the next: dimensions are read before
    // anything is allocated from them, the raw-body size is checked before any decode is
    // attempted, and the byte budget is checked before the filter chain runs.
    // passthroughBodyLength is the stored body length for that raw-body size check on a non-Raw
    // encoding, taken from the stream's RawBody (never the decrypted view, so this check never
    // pays a decrypt copy just to fail it) or, for an inline image, its already-plaintext data.
    // decodeBytes runs the filter chain, only once the budget check passes.
    private PdfExtractedImage? BuildImage(
        PdfDictionary dict, int? objectNumber, int? generation, bool isInline, PdfDictionary? resources,
        int pageIndex, ImageRole role, DiagnosticSink diagnostics, long passthroughBodyLength,
        Func<ImageDecodeResult> decodeBytes)
    {
        // ── dictionary ───────────────────────────────────────────────────────────────────────────
        var width = ReadPositiveInt(dict, WidthKey);
        var height = ReadPositiveInt(dict, HeightKey);
        if (width is null || height is null)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                "/Width or /Height is missing, not an integer, or outside 1..int.MaxValue (ISO "
                + "32000-2 Table 87); the image was skipped.",
                objectNumber, generation, pageIndex);
            return null;
        }

        var isStencilMask = ResolveEntry(dict, ImageMaskKey) is PdfBoolean { Value: true };
        var dictionaryBpc = ResolveEntry(dict, BitsPerComponentKey) is PdfInteger bpcInt ? (int)bpcInt.Value : (int?)null;

        var (imageFilter, lastFilterName) =
            PdfFilters.PeekFilterInfo(dict, _reader.ResolveMaybe, diagnostics, objectNumber, generation);
        var encoding = imageFilter?.Value switch
        {
            "DCTDecode" or "DCT" => PdfImageEncoding.Jpeg,
            "JPXDecode" => PdfImageEncoding.Jpx,
            "JBIG2Decode" => PdfImageEncoding.Jbig2,
            "CCITTFaxDecode" or "CCF" => PdfImageEncoding.CcittFax,
            _ => PdfImageEncoding.Raw,
        };

        int bitsPerComponent;
        if (isStencilMask)
        {
            if (dictionaryBpc is not null && dictionaryBpc != 1)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden,
                    $"/ImageMask true with /BitsPerComponent {dictionaryBpc}; Table 87 requires 1 "
                    + "for an image mask, so it was forced to 1.",
                    objectNumber, generation, pageIndex);
            }
            bitsPerComponent = 1;

            if (dict.Get(PdfName.ColorSpace) is not null || dict.Get(MaskKey) is not null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                    "/ImageMask true also carries /ColorSpace or /Mask, neither of which Table 87 "
                    + "permits on an image mask; both were ignored.",
                    objectNumber, generation, pageIndex);
            }
        }
        else if (encoding == PdfImageEncoding.Jpx)
        {
            // Table 87: "shall be ignored if present"; §7.4.9 allows 1 to 38 bits, possibly
            // different per component, so no single scalar describes it.
            bitsPerComponent = 0;
        }
        else
        {
            if (dictionaryBpc is not (1 or 2 or 4 or 8 or 16))
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                    "/BitsPerComponent is required (ISO 32000-2 Table 87) and is missing, not an "
                    + "integer, or outside {1, 2, 4, 8, 16}; the image was skipped.",
                    objectNumber, generation, pageIndex);
                return null;
            }
            bitsPerComponent = dictionaryBpc.Value;

            if (encoding is PdfImageEncoding.CcittFax or PdfImageEncoding.Jbig2)
            {
                if (bitsPerComponent != 1)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden,
                        "CCITTFaxDecode and JBIG2Decode always deliver 1-bit samples (ISO 32000-2 "
                        + "Table 87); /BitsPerComponent was overridden to 1.",
                        objectNumber, generation, pageIndex);
                    bitsPerComponent = 1;
                }
            }
            else if (encoding == PdfImageEncoding.Jpeg
                || (encoding == PdfImageEncoding.Raw && lastFilterName?.Value is "RunLengthDecode" or "RL"))
            {
                if (bitsPerComponent != 8)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden,
                        "DCTDecode, and a chain whose last filter is RunLengthDecode, always deliver "
                        + "8-bit samples (ISO 32000-2 Table 87); /BitsPerComponent was overridden to 8.",
                        objectNumber, generation, pageIndex);
                    bitsPerComponent = 8;
                }
            }

            var operativeParms = ReadOperativeDecodeParms(dict);
            if (operativeParms?.Get(BitsPerComponentKey) is PdfInteger parmsBpc
                && (int)parmsBpc.Value != bitsPerComponent)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                    $"/DecodeParms /BitsPerComponent ({(int)parmsBpc.Value}) disagrees with the image "
                    + $"dictionary's own /BitsPerComponent ({bitsPerComponent}); the image "
                    + "dictionary's value decides the sample layout (ISO 32000-2 §8.9.3).",
                    objectNumber, generation, pageIndex);
            }
        }

        // ── colour space ─────────────────────────────────────────────────────────────────────────
        PdfImageColorSpace? colorSpace = null;
        if (!isStencilMask)
        {
            var csRaw = dict.Get(PdfName.ColorSpace);
            if (csRaw is null)
            {
                if (encoding != PdfImageEncoding.Jpx)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ImageColorSpaceUnsupported,
                        "/ColorSpace is required (ISO 32000-2 Table 87) and is absent.",
                        objectNumber, generation, pageIndex);
                    if (encoding == PdfImageEncoding.Raw)
                        return null;
                }
                // Jpx with no /ColorSpace: null, no diagnostic. §7.4.9 says the JPEG 2000 data
                // itself carries it.
            }
            else
            {
                colorSpace = _colorSpaceReader.Read(csRaw, resources, diagnostics, objectNumber, generation, pageIndex);
                if (colorSpace is null && encoding == PdfImageEncoding.Raw)
                    return null; // ColorSpaceReader already reported 501.
            }
        }

        // ── per-image size bound, before any decode ──────────────────────────────────────────────
        long rowBytes = 0;
        long expectedRawLength = 0;
        if (encoding == PdfImageEncoding.Raw)
        {
            var componentCount = isStencilMask ? 1 : colorSpace!.ComponentCount;
            // Cannot overflow long: 2^31 (Width) * 64 (max ComponentCount) * 16 (max bpc) is about
            // 2^41, far under long.MaxValue.
            rowBytes = ((long)width.Value * componentCount * bitsPerComponent + 7) / 8;
            if (rowBytes > 0 && height.Value > _limits.MaxDecodedBytes / rowBytes)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageLimitExceeded,
                    "This image's decoded sample buffer would exceed the configured decode limit; "
                    + "it was skipped before any decode was attempted.",
                    objectNumber, generation, pageIndex);
                return null;
            }
            expectedRawLength = rowBytes * height.Value;
        }
        else if (passthroughBodyLength > _limits.MaxDecodedBytes)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageLimitExceeded,
                "This image's stored payload exceeds the configured decode limit; it was skipped "
                + "before any decode was attempted.",
                objectNumber, generation, pageIndex);
            return null;
        }

        // ── aggregate budget, before the decode this image would otherwise pay for ───────────────
        var chargeSize = encoding == PdfImageEncoding.Raw ? expectedRawLength : passthroughBodyLength;
        if (!Budget.TryChargeBytes(chargeSize))
            return null; // 510 already reported by TryChargeBytes, once per call.

        // ── bytes ────────────────────────────────────────────────────────────────────────────────
        var decodeResult = decodeBytes();
        if (!decodeResult.Succeeded)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageDataUnreadable,
                "The image's filter chain could not be decoded; see the accompanying diagnostic for "
                + "why. The image was skipped.",
                objectNumber, generation, pageIndex);
            return null;
        }
        var data = decodeResult.Data;

        string fileExtension;
        if (encoding == PdfImageEncoding.Jpx)
        {
            if (StartsWith(data, Jp2Signature))
            {
                fileExtension = ".jp2";
            }
            else if (StartsWith(data, BareCodestreamStart))
            {
                fileExtension = ".j2k";
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageJpxSignatureUnrecognised,
                    "The JPXDecode payload is a bare JPEG 2000 codestream, not the full file "
                    + "structure ISO 32000-2 §7.4.9 requires; returned as a .j2k codestream.",
                    objectNumber, generation, pageIndex);
            }
            else
            {
                fileExtension = ".jp2";
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageJpxSignatureUnrecognised,
                    "The JPXDecode payload begins with neither the ISO/IEC 15444-1 signature box "
                    + "nor a bare codestream's SOC marker; returned as .jp2 unchanged.",
                    objectNumber, generation, pageIndex);
            }
        }
        else
        {
            fileExtension = encoding switch
            {
                PdfImageEncoding.Jpeg => ".jpg",
                PdfImageEncoding.Jbig2 => ".jb2",
                PdfImageEncoding.CcittFax => ".ccitt",
                _ => ".bin",
            };
        }

        // ── short buffer (Raw only) ──────────────────────────────────────────────────────────────
        if (encoding == PdfImageEncoding.Raw && data.Length < expectedRawLength)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageSampleDataShort,
                $"The decoded sample buffer ({data.Length} bytes) is shorter than "
                + $"rowBytes * Height ({expectedRawLength} bytes, ISO 32000-2 §8.9.3); the image is "
                + "kept unpadded.",
                objectNumber, generation, pageIndex);
        }

        // ── /Decode ──────────────────────────────────────────────────────────────────────────────
        var decodeArray = ReadDecodeArray(dict, isStencilMask, colorSpace, objectNumber, generation, pageIndex, diagnostics);

        // ── /SMask, /Mask, /SMaskInData (skipped for a mask itself: decoded at depth 1 only,
        // so a mask's own masks are ignored) ──────────────────────────────────────────────────────
        var sMaskInData = 0;
        PdfExtractedImage? softMask = null;
        PdfExtractedImage? explicitMask = null;
        if (role == ImageRole.Image)
        {
            var sMaskInDataRaw = ResolveEntry(dict, SMaskInDataKey);
            if (sMaskInDataRaw is PdfInteger smidInt)
            {
                var v = (int)smidInt.Value;
                if (v is 0 or 1 or 2)
                {
                    sMaskInData = v;
                }
                else
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                        $"/SMaskInData {v} is outside {{0, 1, 2}} (ISO 32000-2 Table 87); treated as 0.",
                        objectNumber, generation, pageIndex);
                }
            }
            else if (sMaskInDataRaw is not null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                    "/SMaskInData is present but not an integer (ISO 32000-2 Table 87); treated as 0.",
                    objectNumber, generation, pageIndex);
            }

            var smaskRaw = dict.Get(SMaskKey);
            if (smaskRaw is not null)
            {
                if (sMaskInData != 0)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ImageMaskInvalid,
                        "/SMaskInData is non-zero, and Table 87 says \"If this entry has a non-zero "
                        + "value, SMask shall not be specified\"; the /SMask entry was dropped.",
                        objectNumber, generation, pageIndex);
                }
                else if (smaskRaw is PdfIndirectReference smaskRef
                    && _reader.ResolveStream(smaskRef) is { } smaskStream)
                {
                    softMask = ReadSoftMask(smaskStream, resources, pageIndex, diagnostics);
                }
            }

            var maskRaw = dict.Get(MaskKey);
            if (maskRaw is PdfIndirectReference maskRef && _reader.ResolveStream(maskRef) is { } maskStream)
            {
                if (ResolveEntry(maskStream.Dictionary, ImageMaskKey) is PdfBoolean { Value: true })
                {
                    explicitMask = DecodeXObjectCore(maskStream, resources, pageIndex, ImageRole.ExplicitMask, diagnostics);
                }
                else
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ImageMaskInvalid,
                        "/Mask names a stream that is not itself an image mask (ISO 32000-2 §8.9.6.3 "
                        + "requires /ImageMask true); the /Mask entry was dropped.",
                        objectNumber, generation, pageIndex);
                }
            }
            else if (maskRaw is not null and not PdfArray)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageMaskInvalid,
                    "/Mask is neither a stream reference nor an array; it was ignored.",
                    objectNumber, generation, pageIndex);
            }
            // A /Mask array is colour key masking (§8.9.6.4), a distinct mechanism from the
            // stream-based stencil and explicit masks this type exposes; it is left unread here,
            // with no diagnostic, rather than misreported as one of those two.
        }

        // ── parameters ───────────────────────────────────────────────────────────────────────────
        PdfJbig2Parameters? jbig2 = null;
        PdfCcittFaxParameters? ccittFax = null;
        PdfDctParameters? dct = null;
        var operativeParmsForStep11 = ReadOperativeDecodeParms(dict);

        if (encoding == PdfImageEncoding.Jbig2)
        {
            var globals = ReadOnlyMemory<byte>.Empty;
            if (operativeParmsForStep11?.Get(Jbig2GlobalsKey) is PdfIndirectReference globalsRef
                && _reader.ResolveStream(globalsRef) is { } globalsStream)
            {
                var bytes = _ancillaryCache.GetOrDecode(
                    _reader, globalsStream, AncillaryRole.Jbig2Globals, Budget, _limits, diagnostics);
                if (bytes is not null)
                    globals = bytes;
            }
            jbig2 = new PdfJbig2Parameters(globals);
        }
        else if (encoding == PdfImageEncoding.CcittFax)
        {
            ccittFax = ReadCcittParameters(operativeParmsForStep11, objectNumber, generation, pageIndex, diagnostics);
        }
        else if (encoding == PdfImageEncoding.Jpeg)
        {
            int? colorTransform = null;
            var ctRaw = operativeParmsForStep11?.Get(ColorTransformKey) is { } ctObj
                ? _reader.ResolveValue(ctObj) : null;
            if (ctRaw is PdfInteger ctInt)
            {
                var v = (int)ctInt.Value;
                if (v is 0 or 1)
                {
                    colorTransform = v;
                }
                else
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                        $"/DecodeParms /ColorTransform {v} is outside {{0, 1}} (ISO 32000-2 Table 13); "
                        + "treated as absent.",
                        objectNumber, generation, pageIndex);
                }
            }
            else if (ctRaw is not null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                    "/DecodeParms /ColorTransform is present but not an integer (ISO 32000-2 Table "
                    + "13); treated as absent.",
                    objectNumber, generation, pageIndex);
            }
            dct = new PdfDctParameters(colorTransform);
        }

        return new PdfExtractedImage(
            pageIndex: pageIndex,
            objectNumber: objectNumber,
            generation: generation,
            isInline: isInline,
            isStencilMask: isStencilMask,
            isExplicitMask: role == ImageRole.ExplicitMask,
            hasMatte: role == ImageRole.SoftMask && ResolveEntry(dict, MatteKey) is not null,
            width: width.Value,
            height: height.Value,
            bitsPerComponent: bitsPerComponent,
            sMaskInData: sMaskInData,
            colorSpace: colorSpace,
            decode: decodeArray,
            encoding: encoding,
            data: data,
            fileExtension: fileExtension,
            softMask: softMask,
            explicitMask: explicitMask,
            jbig2: jbig2,
            ccittFax: ccittFax,
            dct: dct,
            interpolate: ResolveEntry(dict, InterpolateKey) is PdfBoolean { Value: true },
            isSoftMask: role == ImageRole.SoftMask);
    }

    // Table 143 (ISO 32000-2 §11.6.5.2) restricts what a soft mask may itself be: its own
    // /ImageMask must be false or absent, its own /Mask and /SMask must be absent, and its
    // /ColorSpace must be DeviceGray. The last two are checked here, against the raw stream and the
    // built candidate respectively, because BuildImage itself (called with role SoftMask) does not
    // recurse into a mask's own masks at all (a mask is decoded at depth 1 only), so nothing inside
    // it could ever discover a violation of "its own /Mask and /SMask must be absent" on its own.
    private PdfExtractedImage? ReadSoftMask(
        ParsedStream smaskStream, PdfDictionary? resources, int pageIndex, DiagnosticSink diagnostics)
    {
        if (smaskStream.Dictionary.Get(SMaskKey) is not null || smaskStream.Dictionary.Get(MaskKey) is not null)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageMaskInvalid,
                "/SMask itself carries /Mask or /SMask, which Table 143 requires to be absent on a "
                + "soft mask; the /SMask entry was dropped.",
                smaskStream.ObjectNumber, smaskStream.Generation, pageIndex);
            return null;
        }

        var candidate = DecodeXObjectCore(smaskStream, resources, pageIndex, ImageRole.SoftMask, diagnostics);
        if (candidate is null)
            return null; // Already reported by BuildImage itself.

        if (candidate.IsStencilMask || candidate.ColorSpace?.Family != PdfImageColorSpaceFamily.DeviceGray)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageMaskInvalid,
                "/SMask must have /ColorSpace DeviceGray and /ImageMask false or absent (ISO "
                + "32000-2 Table 143); the /SMask entry was dropped.",
                smaskStream.ObjectNumber, smaskStream.Generation, pageIndex);
            return null;
        }

        return candidate;
    }

    private PdfCcittFaxParameters ReadCcittParameters(
        PdfDictionary? parms, int? objectNumber, int? generation, int pageIndex, DiagnosticSink diagnostics)
    {
        int ReadInt(PdfName key, int def)
        {
            var raw = parms?.Get(key) is { } v ? _reader.ResolveValue(v) : null;
            if (raw is PdfInteger i)
                return (int)i.Value;
            if (raw is not null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                    $"/DecodeParms /{key.Value} is present but not an integer (ISO 32000-2 Table 11); "
                    + $"the default ({def}) was used.",
                    objectNumber, generation, pageIndex);
            }
            return def;
        }

        bool ReadBool(PdfName key, bool def)
        {
            var raw = parms?.Get(key) is { } v ? _reader.ResolveValue(v) : null;
            if (raw is PdfBoolean b)
                return b.Value;
            if (raw is not null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDictionaryInvalid,
                    $"/DecodeParms /{key.Value} is present but not a boolean (ISO 32000-2 Table 11); "
                    + $"the default ({def}) was used.",
                    objectNumber, generation, pageIndex);
            }
            return def;
        }

        return new PdfCcittFaxParameters(
            k: ReadInt(KKey, 0),
            columns: ReadInt(ColumnsKey, 1728),
            rows: ReadInt(RowsKey, 0),
            blackIs1: ReadBool(BlackIs1Key, false),
            encodedByteAlign: ReadBool(EncodedByteAlignKey, false),
            endOfLine: ReadBool(EndOfLineKey, false),
            endOfBlock: ReadBool(EndOfBlockKey, true),
            damagedRowsBeforeError: ReadInt(DamagedRowsBeforeErrorKey, 0));
    }

    private IReadOnlyList<double>? ReadDecodeArray(
        PdfDictionary dict, bool isStencilMask, PdfImageColorSpace? colorSpace, int? objectNumber,
        int? generation, int pageIndex, DiagnosticSink diagnostics)
    {
        var raw = dict.Get(DecodeKey);
        if (raw is null)
            return null;

        var resolved = _reader.ResolveValue(raw);
        if (resolved is not PdfArray arr)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageDecodeArrayInvalid,
                "/Decode is present but is not an array (ISO 32000-2 §8.9.5.2); exposed as null.",
                objectNumber, generation, pageIndex);
            return null;
        }

        // Table 88 gives Indexed and an image mask a fixed length of 2, regardless of the base
        // space's own component count; every other family needs 2 * ComponentCount. When the
        // colour space itself could not be determined (a passthrough image with ColorSpace null),
        // there is no basis to check the length against, so the array is exposed unchecked.
        int? expectedLength = isStencilMask || colorSpace?.Family == PdfImageColorSpaceFamily.Indexed
            ? 2
            : colorSpace?.ComponentCount * 2;

        // Checked before any per-element work, not after: a /Decode array whose count already
        // disagrees with the known expected length is rejected without reading or allocating for
        // its (possibly hostile-sized) element count.
        if (expectedLength is int len && arr.Count != len)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.ImageDecodeArrayInvalid,
                $"/Decode has {arr.Count} elements; ISO 32000-2 §8.9.5.2 requires exactly {len} "
                + "for this image's colour space. Exposed as null.",
                objectNumber, generation, pageIndex);
            return null;
        }

        // No pre-sized capacity: when expectedLength is unknown (colour space not determined),
        // arr.Count is not otherwise bounded here, so the list grows incrementally rather than
        // committing one allocation sized directly from it.
        var values = new List<double>();
        foreach (var element in EnumerateArray(arr))
        {
            var resolvedElement = _reader.ResolveValue(element);
            if (resolvedElement is PdfInteger i)
                values.Add(i.Value);
            else if (resolvedElement is PdfReal r)
                values.Add(r.Value);
            else
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.ImageDecodeArrayInvalid,
                    "/Decode contains a non-numeric element (ISO 32000-2 §8.9.5.2); exposed as null.",
                    objectNumber, generation, pageIndex);
                return null;
            }
        }

        return values;
    }

    // The image dictionary's own /DecodeParms (never /DP: an inline image's abbreviation is already
    // expanded by the interpreter before this decoder ever sees the dictionary, and an XObject
    // dictionary must spell the key out in full). An array is aligned with /Filter positionally;
    // this reader's own image-parameter reads (BitsPerComponent cross-check, CCITT/DCT/JBIG2
    // parameters) all concern the filter that shapes the image data, which for the short
    // filter chains an image dictionary carries is the LAST element.
    private PdfDictionary? ReadOperativeDecodeParms(PdfDictionary dict)
    {
        var raw = dict.Get(DecodeParmsKey);
        if (raw is null)
            return null;

        var resolved = _reader.ResolveValue(raw);
        if (resolved is PdfDictionary d)
            return d;
        if (resolved is PdfArray arr && arr.Count > 0)
            return _reader.ResolveValue(arr[^1]) as PdfDictionary;
        return null;
    }

    private PdfObject? ResolveEntry(PdfDictionary dict, PdfName key) =>
        dict.Get(key) is { } raw ? _reader.ResolveValue(raw) : null;

    private int? ReadPositiveInt(PdfDictionary dict, PdfName key)
    {
        var raw = ResolveEntry(dict, key);
        return raw is PdfInteger i && i.Value is >= 1 and <= int.MaxValue ? (int)i.Value : null;
    }

    private static IEnumerable<PdfObject> EnumerateArray(PdfArray array)
    {
        for (var i = 0; i < array.Count; i++)
            yield return array[i];
    }

    private static bool StartsWith(ReadOnlyMemory<byte> data, byte[] prefix) =>
        data.Length >= prefix.Length && data.Span[..prefix.Length].SequenceEqual(prefix);
}
