// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Builds a PDF Image XObject dictionary + stream.
/// Supports JPEG (DCTDecode passthrough) and PNG (FlateDecode decode).
/// </summary>
public sealed class PdfImageXObject
{
    public int Width    { get; }
    public int Height   { get; }

    private readonly byte[]           _streamData;
    private readonly PdfName          _filter;
    private readonly ImageColorSpace  _colorSpace;
    private readonly int              _bitsPerComponent;
    private readonly PdfStream?       _sMask; // alpha channel for PNG with transparency

    internal PdfImageXObject(
        int width, int height, byte[] streamData, PdfName filter,
        ImageColorSpace colorSpace, int bitsPerComponent, PdfStream? sMask = null)
    {
        Width             = width;
        Height            = height;
        _streamData       = streamData;
        _filter           = filter;
        _colorSpace       = colorSpace;
        _bitsPerComponent = bitsPerComponent;
        _sMask            = sMask;
    }

    public PdfStream? SMask => _sMask;

    /// <summary>Builds the Image XObject as a PDF stream with inline data.</summary>
    public PdfStream BuildStream()
    {
        // For DCTDecode (JPEG), we pass raw bytes directly — no re-compression.
        // For other filters, PdfStream re-compresses with FlateDecode.
        // We bypass PdfStream's compression for JPEG by using a raw-write path.
        if (_filter == PdfName.DCTDecode)
            return BuildJpegStream();

        // PNG data is already in BGR channel bytes; PdfStream compresses it.
        var stream = new PdfStream(_streamData);
        SetImageDict(stream.Dictionary, sMaskRef: null);
        return stream;
    }

    public PdfStream BuildStreamWithSMask(PdfIndirectReference? sMaskRef)
    {
        if (_filter == PdfName.DCTDecode)
        {
            var s = BuildJpegStream();
            if (sMaskRef is not null) s.Dictionary.Set(new PdfName("SMask"), sMaskRef);
            return s;
        }
        var stream2 = new PdfStream(_streamData);
        SetImageDict(stream2.Dictionary, sMaskRef);
        return stream2;
    }

    private PdfStream BuildJpegStream()
    {
        // Wrap raw JPEG bytes in a stream that skips FlateDecode re-compression.
        // We use a helper that writes the stream with the DCTDecode filter directly.
        var stream = new RawPdfStream(_streamData, _filter);
        SetImageDict(stream.Dictionary, sMaskRef: null);
        return stream;
    }

    private void SetImageDict(PdfDictionary d, PdfIndirectReference? sMaskRef)
    {
        d.Set(PdfName.Type,    new PdfName("XObject"))
         .Set(PdfName.Subtype, new PdfName("Image"))
         .Set(new PdfName("Width"),           new PdfInteger(Width))
         .Set(new PdfName("Height"),          new PdfInteger(Height))
         .Set(new PdfName("ColorSpace"),      ColorSpaceName())
         .Set(new PdfName("BitsPerComponent"), new PdfInteger(_bitsPerComponent));

        if (sMaskRef is not null)
            d.Set(new PdfName("SMask"), sMaskRef);
    }

    private PdfObject ColorSpaceName() => _colorSpace switch
    {
        ImageColorSpace.DeviceGray => new PdfName("DeviceGray"),
        ImageColorSpace.DeviceRgb  => new PdfName("DeviceRGB"),
        ImageColorSpace.DeviceCmyk => new PdfName("DeviceCMYK"),
        _ => new PdfName("DeviceRGB")
    };
}
