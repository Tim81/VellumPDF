// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace VellumPdf.Reader;

/// <summary> One image an <c>ExtractImages</c> call found (#98): an image XObject, an inline image,
/// or a mask reached through another image's <c>/SMask</c> or <c>/Mask</c>. Nothing here is
/// colour-converted or re-encoded: <see cref="Data"/> is either the decoded samples this reader
/// itself produced from a losslessly-decodable filter chain (<see cref="Encoding"/> <see
/// cref="PdfImageEncoding.Raw"/>), or the stored payload of a filter this reader passes through
/// verbatim (DCT, JPX, JBIG2, CCITT).
/// </summary>
public sealed class PdfExtractedImage
{
    /// <summary>The zero-based index of the page this image was found on.</summary>
    public int PageIndex { get; }

    /// <summary>The image XObject's own indirect object number, or <see langword="null"/> for an
    /// inline image, which has none.</summary>
    public int? ObjectNumber { get; }

    /// <summary>The generation of <see cref="ObjectNumber"/>, or <see langword="null"/> when
    /// <see cref="ObjectNumber"/> itself is <see langword="null"/>.</summary>
    public int? Generation { get; }

    /// <summary>Whether this image was drawn as an inline image (ISO 32000-2 §8.9.7) rather than an
    /// image XObject.</summary>
    public bool IsInline { get; }

    /// <summary>Whether <c>/ImageMask</c> is <see langword="true"/> (ISO 32000-2 §8.9.6.2): a
    /// stencil mask painted with the current colour rather than its own samples.</summary>
    public bool IsStencilMask { get; }

    /// <summary>Whether this image was reached through another image's <c>/Mask</c> stream entry
    /// (explicit masking, ISO 32000-2 §8.9.6.3) rather than drawn or found on its own.</summary>
    public bool IsExplicitMask { get; }

    /// <summary>
    /// Whether a soft mask reached through this image's own <c>/SMask</c> also carries a
    /// <c>/Matte</c> entry (ISO 32000-2 §11.6.5.2): this image's own samples are pre-blended with a
    /// matte colour, <c>c' = m + alpha * (c - m)</c>, "a generalization of a technique commonly
    /// called premultiplied alpha". Set on the SOFT MASK instance itself (mirroring
    /// <see cref="IsStencilMask"/> and <see cref="IsExplicitMask"/>, each also a property of the
    /// mask rather than the image it applies to), so it reads
    /// <c>image.SoftMask?.HasMatte == true</c>, not <c>image.HasMatte</c>. A caller that wants to
    /// recover the parent's un-blended colour must undo that blend itself; this reader does not.
    /// </summary>
    public bool HasMatte { get; }

    /// <summary>The image's <c>/Width</c> in samples.</summary>
    public int Width { get; }

    /// <summary>The image's <c>/Height</c> in samples.</summary>
    public int Height { get; }

    /// <summary>
    /// The image's effective bits per component, after the Table 87 filter-forced overrides
    /// (<see cref="PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden"/>) and the
    /// <c>/ImageMask</c> override. 0 for every <see cref="PdfImageEncoding.Jpx"/> image: Table 87
    /// says the dictionary's own <c>/BitsPerComponent</c> "shall be ignored if present" for a
    /// JPXDecode image, and ISO 32000-2 §7.4.9 allows a different depth (1 to 38) per component, so
    /// no single scalar describes it.
    /// </summary>
    public int BitsPerComponent { get; }

    /// <summary>
    /// The image's <c>/SMaskInData</c> value (ISO 32000-2 Table 87: 0, 1, or 2), meaningful only
    /// when <see cref="Encoding"/> is <see cref="PdfImageEncoding.Jpx"/>: a non-zero value there
    /// says the JPX payload's own data carries the soft-mask (1) or premultiplied-alpha (2)
    /// information Table 87 describes, which this reader does not decode out of the payload itself.
    /// Always 0 for every other encoding.
    /// </summary>
    public int SMaskInData { get; }

    /// <summary> The image's colour space, or <see langword="null"/> when none could be determined
    /// (Pattern, an unsupported or malformed space, or one absent where required; see <see
    /// cref="PdfReaderDiagnosticCode.ImageColorSpaceUnsupported"/>) or when none applies (a stencil
    /// mask; a <see cref="PdfImageEncoding.Jpx"/> image with no <c>/ColorSpace</c>, whose colour
    /// space the JPEG 2000 data itself carries per ISO 32000-2 §7.4.9).
    /// </summary>
    public PdfImageColorSpace? ColorSpace { get; }

    /// <summary> The image's <c>/Decode</c> array (ISO 32000-2 §8.9.5.2), exposed exactly as
    /// written and never applied to <see cref="Data"/>: this reader hands back stored or decoded
    /// samples, not remapped ones. <see langword="null"/> when absent or malformed (<see
    /// cref="PdfReaderDiagnosticCode.ImageDecodeArrayInvalid"/>). For a <see
    /// cref="PdfImageEncoding.Jpx"/> image with no <c>/ColorSpace</c>, Table 87 says this array
    /// "shall be ignored unless ImageMask is true" even when present, but it is still exposed here.
    /// </summary>
    public IReadOnlyList<double>? Decode { get; }

    /// <summary>How <see cref="Data"/> is shaped.</summary>
    public PdfImageEncoding Encoding { get; }

    /// <summary> The image's bytes: decoded samples (ISO 32000-2 §8.9.3 row layout) for <see
    /// cref="PdfImageEncoding.Raw"/>, or the stored, still-encoded payload for every other <see
    /// cref="Encoding"/>. Never colour-converted, never re-encoded, never padded: a short
    /// <c>Raw</c> buffer (<see cref="PdfReaderDiagnosticCode.ImageSampleDataShort"/>) is returned
    /// as short as it was decoded.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary> A file extension for <see cref="Data"/>, including the leading dot: <c>.bin</c>
    /// for <see cref="PdfImageEncoding.Raw"/> (a caller cannot losslessly serialise samples into a
    /// file format from this property alone; use <see cref="TryEncodePng"/> for that), <c>.jpg</c>
    /// for <see cref="PdfImageEncoding.Jpeg"/>, <c>.jb2</c> for <see
    /// cref="PdfImageEncoding.Jbig2"/> (§7.4.7's embedded-organisation segment sequence, not a
    /// standalone loadable JBIG2 file: the file header, page association, and end-of-file segments
    /// are absent, and <see cref="Jbig2"/>'s own <see cref="PdfJbig2Parameters.Globals"/> travels
    /// as a separate buffer, not appended to this one), <c>.ccitt</c> for <see
    /// cref="PdfImageEncoding.CcittFax"/>, and, for <see cref="PdfImageEncoding.Jpx"/>, <c>.jp2</c>
    /// when the payload begins with the ISO/IEC 15444-1 JP2 signature box and <c>.j2k</c> when it
    /// instead begins with a bare codestream's SOC marker (see <see
    /// cref="PdfReaderDiagnosticCode.ImageJpxSignatureUnrecognised"/> for both, and for the third
    /// shape that also reports it and falls back to <c>.jp2</c>).
    /// </summary>
    public string FileExtension { get; }

    /// <summary>
    /// The soft mask reached through this image's own <c>/SMask</c> (ISO 32000-2 §11.6.5.2), or
    /// <see langword="null"/> when absent or invalid
    /// (<see cref="PdfReaderDiagnosticCode.ImageMaskInvalid"/>). Also present in its own right in
    /// <see cref="PdfImageExtractionResult.Images"/>: immediately after this image, the first time
    /// a document-level call reaches it, or every time a page-level call does (per-call dedupe
    /// applies to this entry the same way it applies to any other).
    /// </summary>
    public PdfExtractedImage? SoftMask { get; }

    /// <summary>
    /// The explicit mask reached through this image's own <c>/Mask</c> stream entry (ISO 32000-2
    /// §8.9.6.3), or <see langword="null"/> when absent or invalid, or when this image's own
    /// <c>/Mask</c> is instead an array; colour key masking (§8.9.6.4) is not exposed here.
    /// </summary>
    public PdfExtractedImage? ExplicitMask { get; }

    /// <summary>The JBIG2 parameters, when <see cref="Encoding"/> is
    /// <see cref="PdfImageEncoding.Jbig2"/>; otherwise <see langword="null"/>.</summary>
    public PdfJbig2Parameters? Jbig2 { get; }

    /// <summary>The CCITT fax parameters, when <see cref="Encoding"/> is
    /// <see cref="PdfImageEncoding.CcittFax"/>; otherwise <see langword="null"/>.</summary>
    public PdfCcittFaxParameters? CcittFax { get; }

    /// <summary>The DCT parameters, when <see cref="Encoding"/> is
    /// <see cref="PdfImageEncoding.Jpeg"/>; otherwise <see langword="null"/>.</summary>
    public PdfDctParameters? Dct { get; }

    /// <summary>
    /// Whether <see cref="TryEncodePng"/> can losslessly encode this image: it requires
    /// <see cref="Encoding"/> <see cref="PdfImageEncoding.Raw"/>, a <see cref="BitsPerComponent"/>
    /// of 1, 2, 4, 8, or 16, a <see cref="Data"/> at least as long as this image's own expected
    /// sample length, and a colour space (or stencil-mask shape) PNG itself can represent: grey at
    /// any of those depths, RGB at 8 or 16, or an Indexed image over a grey or RGB base at 8 bits
    /// or below. This answers a question about this API's own capability, not about the document: a
    /// passthrough (DCT, JPX, JBIG2, CCITT) image is never eligible, since re-encoding its payload
    /// would not be lossless.
    /// </summary>
    public bool CanEncodePng => PngEncoder.CanEncode(this);

    /// <summary> Encodes this image as a PNG (ISO/IEC 15948), losslessly, when <see
    /// cref="CanEncodePng"/> is <see langword="true"/>; otherwise returns <see langword="false"/>
    /// with <paramref name="png"/> <see langword="null"/>. An image mask (<see
    /// cref="IsStencilMask"/>) encodes as PNG colour type 0 (greyscale) at bit depth 1, its stored
    /// samples unchanged: <c>/Decode</c> is never applied, so under the default <c>[0 1]</c> a
    /// stored sample of 0 is the painted area (ISO 32000-2 §8.9.6.2) and renders black in the PNG,
    /// while an explicit <c>[1 0]</c> reverses that meaning with no change to the bytes written.
    /// </summary>
    public bool TryEncodePng([NotNullWhen(true)] out byte[]? png) => PngEncoder.TryEncode(this, out png);

    /// <summary> Encodes this image as a PNG with <see cref="SoftMask"/> interleaved as an alpha
    /// channel (PNG colour type 4 or 6), losslessly, when all of the following hold: <see
    /// cref="SoftMask"/> is not <see langword="null"/>; its own <see cref="Encoding"/> is <see
    /// cref="PdfImageEncoding.Raw"/>; its own <see cref="HasMatte"/> is <see langword="false"/> (a
    /// matte means the parent's samples are already pre-blended per ISO 32000-2 §11.6.5.2, and PNG
    /// alpha is not premultiplied, so interleaving would write the wrong pixels); its <see
    /// cref="Width"/> and <see cref="Height"/> equal this image's own (Table 143 permits a
    /// differently sized soft mask, but resampling one onto the other's grid would not be lossless,
    /// and this method does not resample); it has a single colour component; its own sample depth
    /// equals the PNG output depth (8 or 16) this image maps to, which for an Indexed image is not
    /// the same as its own <see cref="BitsPerComponent"/> (the index depth, not the sample depth);
    /// ISO/IEC 15948's IHDR table permits an alpha-carrying colour type only at 8 or 16 bits, so a
    /// grey image mapping to colour type 0 at 1, 2, or 4 bits (see <see cref="TryEncodePng"/>) is
    /// never eligible here, whatever its own soft mask looks like; and its own <see cref="Decode"/>
    /// is either absent or <c>[0 1]</c> (Table 143's own default), since this method interleaves
    /// the mask's stored bytes unchanged and a non-default array would invert the resulting alpha
    /// channel. Otherwise returns <see langword="false"/> with <paramref name="png"/> <see
    /// langword="null"/>, including every case <see cref="CanEncodePng"/> itself is false.
    /// </summary>
    /// <remarks>
    /// Allocates <c>Width * (channels + 1) * (bitDepth / 8) * Height</c> bytes for the interleaved
    /// row buffer before compression, plus 57 bytes of chunk overhead and whatever zlib itself
    /// costs; <c>channels</c> is 1 for a grey image, 3 for RGB or Indexed (an Indexed image's own
    /// indices are expanded to 8-bit RGB triples first, up to 32 times <see cref="Data"/>'s own
    /// length at 1 bit per index). This is independent of <see cref="Data"/>'s own length, unlike
    /// <see cref="TryEncodePng"/>'s bound.
    /// </remarks>
    public bool TryEncodePngWithAlpha([NotNullWhen(true)] out byte[]? png) =>
        PngEncoder.TryEncodeWithAlpha(this, out png);

    /// <summary>
    /// <see cref="Data"/>'s expected length for a <see cref="PdfImageEncoding.Raw"/> image:
    /// <c>rowBytes * Height</c>, where <c>rowBytes</c> is derived from <see cref="Width"/>,
    /// <see cref="BitsPerComponent"/>, and this image's own component count (1 for a stencil mask,
    /// otherwise <see cref="ColorSpace"/>'s own <see cref="PdfImageColorSpace.ComponentCount"/>).
    /// 0 for every other <see cref="Encoding"/>, which has no sample geometry to derive a length
    /// from. Internal pending a maintainer decision on whether to promote it before #187 freezes
    /// this package's surface (the value is derivable from already-public members, so this is a
    /// convenience rather than new information).
    /// </summary>
    internal long ExpectedSampleDataLength
    {
        get
        {
            if (Encoding != PdfImageEncoding.Raw)
                return 0;
            var componentCount = IsStencilMask ? 1 : ColorSpace?.ComponentCount ?? 0;
            var rowBytes = ((long)Width * componentCount * BitsPerComponent + 7) / 8;
            return rowBytes * Height;
        }
    }

    /// <summary> The image's <c>/Interpolate</c> value (ISO 32000-2 Table 87), default <see
    /// langword="false"/>. Kept internal: no consumer of this release's public surface needs it,
    /// and a future release can widen it without breaking one that already does.
    /// </summary>
    internal bool Interpolate { get; }

    /// <summary>
    /// Whether this instance is itself a soft mask reached through another image's <c>/SMask</c>
    /// (as opposed to a drawn image or an explicit mask). Kept internal for the same reason as
    /// <see cref="Interpolate"/>.
    /// </summary>
    internal bool IsSoftMask { get; }

    internal PdfExtractedImage(
        int pageIndex, int? objectNumber, int? generation, bool isInline, bool isStencilMask,
        bool isExplicitMask, bool hasMatte, int width, int height, int bitsPerComponent,
        int sMaskInData, PdfImageColorSpace? colorSpace, IReadOnlyList<double>? decode,
        PdfImageEncoding encoding, ReadOnlyMemory<byte> data, string fileExtension,
        PdfExtractedImage? softMask, PdfExtractedImage? explicitMask, PdfJbig2Parameters? jbig2,
        PdfCcittFaxParameters? ccittFax, PdfDctParameters? dct, bool interpolate, bool isSoftMask)
    {
        PageIndex = pageIndex;
        ObjectNumber = objectNumber;
        Generation = generation;
        IsInline = isInline;
        IsStencilMask = isStencilMask;
        IsExplicitMask = isExplicitMask;
        HasMatte = hasMatte;
        Width = width;
        Height = height;
        BitsPerComponent = bitsPerComponent;
        SMaskInData = sMaskInData;
        ColorSpace = colorSpace;
        Decode = decode;
        Encoding = encoding;
        Data = data;
        FileExtension = fileExtension;
        SoftMask = softMask;
        ExplicitMask = explicitMask;
        Jbig2 = jbig2;
        CcittFax = ccittFax;
        Dct = dct;
        Interpolate = interpolate;
        IsSoftMask = isSoftMask;
    }
}
