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

    // When true, the stream bytes are passed verbatim (pre-compressed by the
    // encoder named in _filter). When false, PdfStream applies FlateDecode.
    private readonly bool _passthrough;

    internal PdfImageXObject(
        int width, int height, byte[] streamData, PdfName filter,
        ImageColorSpace colorSpace, int bitsPerComponent, PdfStream? sMask = null,
        int sMaskBitsPerComponent = 8, PdfDictionary? decodeParms = null)
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

        // Passthrough: any filter that is not the implicit-FlateDecode sentinel.
        // Loaders pass PdfName.FlateDecode when the bytes are raw pixels that
        // PdfStream should compress. All other named filters mean the bytes are
        // already encoded and must be written verbatim.
        _passthrough = !filter.Equals(PdfName.FlateDecode);
    }

    /// <summary>The soft-mask (alpha channel) stream, or <see langword="null"/> when the image is opaque.</summary>
    public PdfStream? SMask => _sMask;

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
            return BuildPassthroughStream(sMaskRef: null);

        var stream = new PdfStream(_streamData);
        SetImageDict(stream.Dictionary, sMaskRef: null);
        return stream;
    }

    /// <summary>Builds the Image XObject stream, wiring <paramref name="sMaskRef"/> as the /SMask reference when supplied.</summary>
    public PdfStream BuildStreamWithSMask(PdfIndirectReference? sMaskRef)
    {
        if (_passthrough)
            return BuildPassthroughStream(sMaskRef);

        var stream = new PdfStream(_streamData);
        SetImageDict(stream.Dictionary, sMaskRef);
        return stream;
    }

    private PdfStream BuildPassthroughStream(PdfIndirectReference? sMaskRef)
    {
        // Write raw bytes verbatim with the named filter (DCTDecode, CCITTFaxDecode, …).
        var stream = new RawPdfStream(_streamData, _filter);
        SetImageDict(stream.Dictionary, sMaskRef);
        return stream;
    }

    private void SetImageDict(PdfDictionary d, PdfIndirectReference? sMaskRef)
    {
        d.Set(PdfName.Type, new PdfName("XObject"))
         .Set(PdfName.Subtype, new PdfName("Image"))
         .Set(new PdfName("Width"), new PdfInteger(Width))
         .Set(new PdfName("Height"), new PdfInteger(Height))
         .Set(new PdfName("ColorSpace"), ColorSpaceName())
         .Set(new PdfName("BitsPerComponent"), new PdfInteger(_bitsPerComponent));

        if (_decodeParms is not null)
            d.Set(new PdfName("DecodeParms"), _decodeParms);

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
