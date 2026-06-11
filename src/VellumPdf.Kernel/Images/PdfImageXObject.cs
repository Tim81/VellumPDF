// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Builds a PDF Image XObject dictionary + stream.
/// Supports JPEG (DCTDecode passthrough), CCITTFaxDecode (passthrough),
/// and raw pixel data (FlateDecode).
/// </summary>
public sealed class PdfImageXObject
{
    /// <summary>The image width in pixels.</summary>
    public int Width { get; }
    /// <summary>The image height in pixels.</summary>
    public int Height { get; }

    private readonly byte[] _streamData;
    private readonly PdfName _filter;
    private readonly ImageColorSpace _colorSpace;
    private readonly int _bitsPerComponent;
    private readonly PdfStream? _sMask;
    private readonly int _sMaskBitsPerComponent;
    private readonly PdfDictionary? _decodeParms;
    private readonly byte[]? _jbig2Globals;

    // When true, the stream bytes are passed verbatim (pre-compressed by the
    // encoder named in _filter). When false, PdfStream applies FlateDecode.
    private readonly bool _passthrough;

    internal PdfImageXObject(
        int width, int height, byte[] streamData, PdfName filter,
        ImageColorSpace colorSpace, int bitsPerComponent, PdfStream? sMask = null,
        int sMaskBitsPerComponent = 8, PdfDictionary? decodeParms = null,
        byte[]? jbig2Globals = null)
    {
        Width = width;
        Height = height;
        _streamData = streamData;
        _filter = filter;
        _colorSpace = colorSpace;
        _bitsPerComponent = bitsPerComponent;
        _sMask = sMask;
        _sMaskBitsPerComponent = sMaskBitsPerComponent;
        _decodeParms = decodeParms;
        _jbig2Globals = jbig2Globals;

        // Passthrough: any filter that is not the implicit-FlateDecode sentinel.
        // Loaders pass PdfName.FlateDecode when the bytes are raw pixels that
        // PdfStream should compress. All other named filters mean the bytes are
        // already encoded and must be written verbatim.
        _passthrough = !filter.Equals(PdfName.FlateDecode);
    }

    /// <summary>The soft-mask (alpha channel) stream, or <see langword="null"/> when the image is opaque.</summary>
    public PdfStream? SMask => _sMask;

    /// <summary>
    /// The JBIG2 global segments stream bytes, or <see langword="null"/> when there are no
    /// globally-referenced segments (e.g. a self-contained single generic-region image).
    /// When non-null, <see cref="Document.PdfDocument"/> registers these bytes as a separate
    /// indirect stream object and wires its reference into <c>/DecodeParms /JBIG2Globals</c>.
    /// </summary>
    internal byte[]? Jbig2Globals => _jbig2Globals;

    /// <summary>
    /// The bit depth that the SMask stream was built with. Used by <see cref="Document.PdfDocument"/>
    /// when wiring the SMask image dictionary so that high-bit-depth alpha channels are
    /// emitted at the correct <c>BitsPerComponent</c> rather than silently downsampled to 8.
    /// </summary>
    internal int SMaskBitsPerComponent => _sMaskBitsPerComponent;

    /// <summary>Builds the Image XObject as a PDF stream with inline data.</summary>
    public PdfStream BuildStream()
    {
        if (_passthrough)
            return BuildPassthroughStream(sMaskRef: null, jbig2GlobalsRef: null);

        var stream = new PdfStream(_streamData);
        SetImageDict(stream.Dictionary, sMaskRef: null, jbig2GlobalsRef: null);
        return stream;
    }

    /// <summary>Builds the Image XObject stream, wiring <paramref name="sMaskRef"/> as the /SMask reference when supplied.</summary>
    public PdfStream BuildStreamWithSMask(PdfIndirectReference? sMaskRef)
    {
        if (_passthrough)
            return BuildPassthroughStream(sMaskRef, jbig2GlobalsRef: null);

        var stream = new PdfStream(_streamData);
        SetImageDict(stream.Dictionary, sMaskRef, jbig2GlobalsRef: null);
        return stream;
    }

    /// <summary>
    /// Builds the Image XObject stream, wiring <paramref name="sMaskRef"/> as the /SMask
    /// reference and <paramref name="jbig2GlobalsRef"/> as the /DecodeParms /JBIG2Globals
    /// reference when supplied. Used by <see cref="Document.PdfDocument"/> when registering
    /// a JBIG2 image that has global segments.
    /// </summary>
    internal PdfStream BuildStreamWithSMaskAndJbig2Globals(
        PdfIndirectReference? sMaskRef,
        PdfIndirectReference? jbig2GlobalsRef)
    {
        if (_passthrough)
            return BuildPassthroughStream(sMaskRef, jbig2GlobalsRef);

        var stream = new PdfStream(_streamData);
        SetImageDict(stream.Dictionary, sMaskRef, jbig2GlobalsRef);
        return stream;
    }

    private PdfStream BuildPassthroughStream(
        PdfIndirectReference? sMaskRef,
        PdfIndirectReference? jbig2GlobalsRef)
    {
        // Write raw bytes verbatim with the named filter (DCTDecode, CCITTFaxDecode, …).
        var stream = new RawPdfStream(_streamData, _filter);
        SetImageDict(stream.Dictionary, sMaskRef, jbig2GlobalsRef);
        return stream;
    }

    private void SetImageDict(
        PdfDictionary d,
        PdfIndirectReference? sMaskRef,
        PdfIndirectReference? jbig2GlobalsRef)
    {
        d.Set(PdfName.Type, new PdfName("XObject"))
         .Set(PdfName.Subtype, new PdfName("Image"))
         .Set(new PdfName("Width"), new PdfInteger(Width))
         .Set(new PdfName("Height"), new PdfInteger(Height))
         .Set(new PdfName("ColorSpace"), ColorSpaceName())
         .Set(new PdfName("BitsPerComponent"), new PdfInteger(_bitsPerComponent));

        if (_decodeParms is not null || jbig2GlobalsRef is not null)
        {
            // Loaders hand us a fresh per-image /DecodeParms dictionary and PdfDocument builds
            // each image exactly once, so adding the /JBIG2Globals indirect reference in place is
            // safe. (For a JBIG2 image with globals, _decodeParms is always non-null.)
            var dp = _decodeParms ?? new PdfDictionary();
            if (jbig2GlobalsRef is not null)
                dp.Set(new PdfName("JBIG2Globals"), jbig2GlobalsRef);
            d.Set(new PdfName("DecodeParms"), dp);
        }

        if (sMaskRef is not null)
            d.Set(new PdfName("SMask"), sMaskRef);
    }

    private PdfObject ColorSpaceName() => _colorSpace switch
    {
        ImageColorSpace.DeviceGray => new PdfName("DeviceGray"),
        ImageColorSpace.DeviceRgb => new PdfName("DeviceRGB"),
        ImageColorSpace.DeviceCmyk => new PdfName("DeviceCMYK"),
        _ => new PdfName("DeviceRGB")
    };
}
