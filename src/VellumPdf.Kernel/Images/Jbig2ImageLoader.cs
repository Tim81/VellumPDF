// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Creates a PDF Image XObject from JBIG2 bilevel image data (ITU-T T.88 / ISO/IEC 14492).
///
/// <para><b>Organisations:</b> PDF <c>/JBIG2Decode</c> uses the <i>embedded</i> organisation (T.88
/// D.3): no file header; the segments of the page go in the image stream; the global segments (page
/// association 0, such as symbol dictionaries, pattern dictionaries and tables) go in a separate stream referenced as
/// <c>/DecodeParms &lt;&lt; /JBIG2Globals &lt;ref&gt; &gt;&gt;</c>. The input is that form, or the same
/// segments after the 8-byte ID string <c>97 4A 42 32 0D 0A 1A 0A</c>, which is stripped.</para>
///
/// <para><b>A file with the T.88 Annex D file header is refused unless its bytes after the ID string
/// read as segments.</b> Only the ID string is stripped. The flags byte and page count that Annex D (D.4.2,
/// D.4.3) puts after it are not read as header fields, so a file that carries them, or is in the
/// random-access organisation, is refused with <see cref="InvalidDataException"/> in both
/// <see cref="ImageDecodeMode"/>s unless those bytes happen to read as a valid segment sequence.
/// Pass the segments without the file header.</para>
///
/// <para><b>One page:</b> a PDF image is one page, so only the first page of a multi-page stream is
/// embedded. The first page is the one named by the page association of the first
/// page-information segment (type 48). Its segments are kept, with the segments that belong to no
/// page (page association 0), and the segments of every other page are dropped. The width and height
/// are the first page's. A first page-information segment with page association 0 names no page, so
/// every segment is kept. <see cref="ImageDecodeMode.DecodeToRaster"/> still decodes the regions of
/// the other pages and discards the result, so a later page with a region this loader cannot decode, or
/// whose data length is unknown, refuses the file there. <b>Do not pass</b> a multi-page stream: the
/// other pages are lost without a word, and 3.0 refuses it.</para>
///
/// <para><b>Unknown segment length:</b> a segment whose data length field is 0xFFFFFFFF (ITU-T T.88
/// 7.2.7) is not embedded, because this loader does not scan the data for the end marker that clause
/// describes and so cannot say where such a segment ends.
/// <see cref="ImageDecodeMode.Passthrough"/> drops it, which can leave a page blank without a word;
/// <b>do not pass</b> such a segment, because 3.0 refuses it and nothing reports it.
/// <see cref="ImageDecodeMode.DecodeToRaster"/> refuses an unknown length on an
/// immediate, lossless or intermediate generic region (types 36, 38 and 39) with
/// <see cref="InvalidDataException"/> naming the segment. A stream takes the rest of the data as an
/// unknown-length segment's own, so what follows one is not seen.</para>
///
/// <para><b>Polarity:</b> <see cref="ImageDecodeMode.DecodeToRaster"/> stores the decoded bits unchanged as
/// <c>/DeviceGray</c> without <c>/Decode</c>, whose default maps sample 0 to black (ISO 32000-2 8.9.5.2),
/// while T.88 (clause 3, NOTE 1; 5.6) gives 1 as black as the typical reading, so the raster renders as a
/// negative. <see cref="ImageDecodeMode.Passthrough"/> embeds the segments as they are and has no such
/// inversion.</para>
///
/// <para>When the input has no global segments the resulting XObject carries no
/// <c>/JBIG2Globals</c> entry and is self-contained.</para>
///
/// <para><b>Security:</b> every segment offset and length is validated against the input
/// buffer. The segment count is capped and the output pixel count is checked via
/// <see cref="ImageLimits.ValidateDimensions"/>.</para>
/// </summary>
public static class Jbig2ImageLoader
{
    // JBIG2 file ID string (ITU-T T.88 §D.4.1); the flags byte and page count follow it (§D.4.2, §D.4.3).
    private static readonly byte[] FileHeader = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Maximum number of segments accepted from a single JBIG2 file.</summary>
    private const int MaxSegments = 65536;

    // Segment types (ITU-T T.88 §7.4).
    private const int SegmentTypeSymbolDictionary = 0;
    private const int SegmentTypeIntermediateTextRegion = 4;
    private const int SegmentTypeImmediateTextRegion = 6;
    private const int SegmentTypeImmediateLosslessTextRegion = 7;
    private const int SegmentTypePatternDictionary = 16;
    private const int SegmentTypeIntermediateHalftoneRegion = 20;
    private const int SegmentTypeImmediateHalftoneRegion = 22;
    private const int SegmentTypeImmediateLosslessHalftoneRegion = 23;
    private const int SegmentTypeIntermediateGenericRegion = 36;
    private const int SegmentTypeImmediateGenericRegion = 38;
    private const int SegmentTypeImmediateLosslessGenericRegion = 39;
    private const int SegmentTypeIntermediateGenericRefinementRegion = 40;
    private const int SegmentTypeImmediateGenericRefinementRegion = 42;
    private const int SegmentTypeImmediateLosslessGenericRefinementRegion = 43;
    private const int SegmentTypePageInformation = 48;
    private const int SegmentTypeEndOfPage = 49;
    private const int SegmentTypeEndOfStripe = 50;
    private const int SegmentTypeEndOfFile = 51;
    private const int SegmentTypeProfiles = 52;
    private const int SegmentTypeTables = 53;
    private const int SegmentTypeExtension = 62;

    /// <summary>
    /// Loads a JBIG2 image and creates a <c>/JBIG2Decode</c> PDF Image XObject using default
    /// options (passthrough, with globals partitioned out if present).
    /// </summary>
    /// <param name="jbig2">
    /// The JBIG2 segments in embedded form, with or without the 8-byte ID string in front.
    /// </param>
    /// <returns>
    /// A <see cref="PdfImageXObject"/> with <c>/Filter /JBIG2Decode</c>.
    /// When global segments are present, <see cref="PdfImageXObject.Jbig2Globals"/> is
    /// non-null and will be wired into <c>/DecodeParms /JBIG2Globals</c> automatically
    /// by <see cref="Document.PdfDocument"/> when the document is written.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="jbig2"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">
    /// Thrown on malformed or truncated input, and for a file with the T.88 Annex D file header whose
    /// bytes after the ID string do not read as segments; see the remarks on this type. A segment whose
    /// data length is unknown (0xFFFFFFFF, ITU-T T.88 7.2.7) is dropped, not refused, because this
    /// loader does not scan for the end marker 7.2.7 describes and so cannot say where the segment
    /// ends. <b>Do not pass</b> such a segment: the image is embedded without it, which can leave no
    /// region and a blank image, and nothing reports it yet; 3.0 refuses it. Only the first page of a
    /// multi-page stream is embedded, unless the first page-information segment has page association 0,
    /// which keeps every segment.
    /// </exception>
    public static PdfImageXObject Load(byte[] jbig2)
        => Load(jbig2, ImageLoadOptions.Default);

    /// <summary>
    /// Loads a JBIG2 image and creates a PDF Image XObject using the specified options.
    /// </summary>
    /// <param name="jbig2">
    /// The JBIG2 segments in embedded form, with or without the 8-byte ID string in front.
    /// </param>
    /// <param name="options">
    /// Options controlling how the image is mapped into the XObject.
    /// <list type="bullet">
    ///   <item><see cref="ImageDecodeMode.Passthrough"/> (default) — parse + partition into
    ///   page stream and optional globals stream; emit as <c>/JBIG2Decode</c>.</item>
    ///   <item><see cref="ImageDecodeMode.DecodeToRaster"/> — decode the JBIG2 to a 1-bpp
    ///   raster and emit via <c>/FlateDecode</c>. Only MMR-coded immediate generic region
    ///   segments are supported; arithmetic-coded, symbol-dictionary, text-region,
    ///   halftone-region, and refinement segments throw <see cref="NotSupportedException"/>.</item>
    /// </list>
    /// </param>
    /// <returns>A <see cref="PdfImageXObject"/> with the appropriate filter.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="jbig2"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <see langword="null"/>, because the decode mode is read from it
    /// (<c>ParamName</c> <c>options</c>). A call with null data and null options reports the data.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown on malformed or truncated input, and for a file with the T.88 Annex D file header whose
    /// bytes after the ID string do not read as segments; see the remarks on this type. Under
    /// <see cref="ImageDecodeMode.DecodeToRaster"/> it is also thrown for a generic region whose data
    /// length is unknown (0xFFFFFFFF, ITU-T T.88 7.2.7), which this loader cannot decode; the message
    /// names the segment number. Under <see cref="ImageDecodeMode.Passthrough"/> such a segment is
    /// dropped; see <see cref="Load(byte[])"/>. Only the first page of a multi-page stream is embedded,
    /// under either mode, unless the first page-information segment has page association 0, which keeps
    /// every segment. Under <see cref="ImageDecodeMode.DecodeToRaster"/> the other pages are decoded and
    /// discarded, so a region on them that cannot be decoded refuses the file.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown under <see cref="ImageDecodeMode.DecodeToRaster"/> when the file contains
    /// segment types that are not supported for in-process decoding (symbol dictionaries,
    /// text regions, halftone regions, refinement regions, or arithmetic-coded generic regions).
    /// </exception>
    public static PdfImageXObject Load(byte[] jbig2, ImageLoadOptions options)
    {
        if (jbig2 is null || jbig2.Length == 0)
            throw new ArgumentException("JBIG2 data must be non-empty.", nameof(jbig2));
        ArgumentNullException.ThrowIfNull(options);

        // Only the ID string is stripped; embedded-form input has none. What follows is read as
        // segments, so a file whose Annex D flags byte and page count do not read that way is
        // refused, as on 2.3.3.
        var dataStart = StartsWithFileHeader(jbig2) ? FileHeader.Length : 0;
        var data = jbig2.AsSpan(dataStart);

        var segments = ParseSegments(data);
        var (width, height, firstPage) = ReadPageInfo(segments, data);

        ImageLimits.ValidateDimensions("JBIG2", width, height);

        return options.DecodeMode == ImageDecodeMode.DecodeToRaster
            ? DecodeToRaster(segments, data, width, height, firstPage)
            : BuildPassthrough(segments, data, width, height, firstPage);
    }

    // ── Parsed segment metadata ───────────────────────────────────────────────

    private readonly struct SegmentHeader
    {
        /// <summary>Segment number (§7.2.1).</summary>
        public readonly int Number;
        /// <summary>Segment type (§7.2.2 bits 0-5).</summary>
        public readonly int Type;
        /// <summary>Page association (§7.2.6). 0 = globally referenced.</summary>
        public readonly int PageAssociation;
        /// <summary>Byte offset in the stripped data span where segment header begins.</summary>
        public readonly int HeaderStart;
        /// <summary>Byte offset in the stripped data span where segment data begins.</summary>
        public readonly int DataOffset;
        /// <summary>Segment data length in bytes. -1 = unknown (variable-length).</summary>
        public readonly long DataLength;

        public SegmentHeader(int number, int type, int pageAssociation,
            int headerStart, int dataOffset, long dataLength)
        {
            Number = number;
            Type = type;
            PageAssociation = pageAssociation;
            HeaderStart = headerStart;
            DataOffset = dataOffset;
            DataLength = dataLength;
        }

        public bool HasKnownLength => DataLength >= 0;
    }

    // ── Segment header parsing (ITU-T T.88 §7.2) ─────────────────────────────

    private static List<SegmentHeader> ParseSegments(ReadOnlySpan<byte> data)
    {
        var segments = new List<SegmentHeader>();
        var pos = 0;

        while (pos < data.Length)
        {
            if (segments.Count >= MaxSegments)
                throw new InvalidDataException(
                    $"JBIG2: segment count exceeds the safety limit of {MaxSegments}.");

            var headerStart = pos;

            // §7.2.1 Segment number — 4 bytes, big-endian.
            if (pos + 4 > data.Length)
                throw new InvalidDataException(
                    $"JBIG2: truncated segment number at offset {pos}.");
            var segNumber = ReadInt32(data, pos);
            pos += 4;

            // §7.2.2 Segment header flags — 1 byte.
            if (pos >= data.Length)
                throw new InvalidDataException("JBIG2: truncated segment flags.");
            var flags = data[pos];
            var segType = flags & 0x3F;
            var pageAssocSizeFlag = (flags >> 6) & 1; // 0 = 1-byte, 1 = 4-byte page assoc
            pos++;

            // §7.2.4 Referred-to segment count and retention flags.
            // The count is held in the TOP 3 bits of the first byte (the low 5 bits are
            // retention flags). When those top bits are all 1 (value 7) the field is the
            // long form: a 4-byte big-endian value whose low 29 bits hold the count,
            // followed by ceil((count + 1) / 8) retention-flag bytes.
            if (pos >= data.Length)
                throw new InvalidDataException("JBIG2: truncated referred-to segment count.");
            var refFlagByte = data[pos];
            int refCount = (refFlagByte >> 5) & 0x7;

            if (refCount == 7)
            {
                // Long form: the count byte is the first of a 4-byte field.
                if (pos + 4 > data.Length)
                    throw new InvalidDataException("JBIG2: truncated referred-to segment count (long form).");
                refCount = (int)(ReadUInt32(data, pos) & 0x1FFF_FFFF);
                pos += 4;
                // Retention flags: ceil((refCount + 1) / 8) bytes.
                var retentionBytes = (refCount + 8) / 8;
                if (pos + retentionBytes > data.Length)
                    throw new InvalidDataException("JBIG2: truncated referred-to retention flags.");
                pos += retentionBytes;
            }
            else
            {
                // Short form: single byte (count in top 3 bits, retention flags in low 5 bits).
                pos++;
            }

            // §7.2.5 Referred-to segment numbers.
            // Each entry is 1, 2, or 4 bytes depending on the current segment number.
            int refEntryBytes = segNumber <= 256 ? 1 : segNumber <= 65536 ? 2 : 4;
            var refSectionBytes = (long)refCount * refEntryBytes;
            if (pos + refSectionBytes > data.Length)
                throw new InvalidDataException("JBIG2: truncated referred-to segment numbers.");
            pos += (int)refSectionBytes;

            // §7.2.6 Segment page association.
            int pageAssociation;
            if (pageAssocSizeFlag == 1)
            {
                if (pos + 4 > data.Length)
                    throw new InvalidDataException("JBIG2: truncated page association.");
                pageAssociation = ReadInt32(data, pos);
                pos += 4;
            }
            else
            {
                if (pos >= data.Length)
                    throw new InvalidDataException("JBIG2: truncated page association.");
                pageAssociation = data[pos];
                pos++;
            }

            // §7.2.7 Segment data length — 4 bytes, big-endian.
            // 0xFFFF_FFFF means unknown (variable-length segment).
            if (pos + 4 > data.Length)
                throw new InvalidDataException("JBIG2: truncated segment data length.");
            var rawLength = ReadUInt32(data, pos);
            pos += 4;

            long dataLength = rawLength == 0xFFFF_FFFF ? -1L : (long)rawLength;

            var dataOffset = pos;

            if (dataLength >= 0)
            {
                if (dataOffset + dataLength > data.Length)
                    throw new InvalidDataException(
                        $"JBIG2: segment {segNumber} data length {dataLength} " +
                        $"at offset {dataOffset} exceeds buffer size {data.Length}.");
                pos += (int)dataLength;
            }
            else
            {
                // Variable-length segment: treat as consuming the remaining buffer.
                pos = data.Length;
            }

            segments.Add(new SegmentHeader(
                segNumber, segType, pageAssociation,
                headerStart, dataOffset, dataLength));
        }

        return segments;
    }

    // ── Page-info segment (type 48) ───────────────────────────────────────────

    /// <summary>
    /// Reads the first page-information segment: the page size, and the page association that names
    /// the first page.
    /// </summary>
    private static (int width, int height, int page) ReadPageInfo(
        List<SegmentHeader> segments, ReadOnlySpan<byte> data)
    {
        foreach (var seg in segments)
        {
            if (seg.Type != SegmentTypePageInformation) continue;

            // Page info data layout (§7.4.8):
            // Width(4) Height(4) XResolution(4) YResolution(4) Flags(1) StripingInfo(2) = 19 bytes.
            if (!seg.HasKnownLength || seg.DataLength < 19)
                throw new InvalidDataException("JBIG2: page-information segment data too short.");

            var off = seg.DataOffset;
            if (off + 8 > data.Length)
                throw new InvalidDataException("JBIG2: truncated page-information segment data.");

            var width = ReadInt32(data, off);
            var height = ReadInt32(data, off + 4);

            if (width <= 0 || height <= 0)
                throw new InvalidDataException(
                    $"JBIG2: invalid page dimensions {width}×{height}.");

            return (width, height, seg.PageAssociation);
        }

        throw new InvalidDataException("JBIG2: no page-information segment (type 48) found.");
    }

    /// <summary>
    /// The refusal of a generic region whose data length is 0xFFFFFFFF (§7.2.7) under
    /// <c>DecodeToRaster</c>. Only an immediate generic region may carry that length, and its end is
    /// found only by scanning the data for a marker that depends on the coding (ITU-T T.88 7.2.7):
    /// 0xFF 0xAC for template-based arithmetic coding or 0x00 0x00 for MMR, each followed by a
    /// four-byte row count. This loader does not scan, so it cannot decode such a segment.
    /// </summary>
    private static InvalidDataException UnknownLength(in SegmentHeader seg)
        => new(
            $"JBIG2: segment {seg.Number} (type {seg.Type}) has an unknown data length " +
            "(0xFFFFFFFF, ITU-T T.88 7.2.7), which this loader cannot use: it does not scan the data " +
            "for the end marker that clause describes, so it can neither partition the segment into " +
            "a page stream and a globals stream nor decode it.");

    /// <summary>
    /// Whether a segment is part of the page being embedded: it is associated with that page, or with
    /// no page at all (association 0, the globally referenced segments). A first page-information
    /// segment that itself has association 0 names no page, so no page is selected and every segment
    /// is kept.
    /// </summary>
    private static bool BelongsToPage(in SegmentHeader seg, int page)
        => page == 0 || seg.PageAssociation == page || seg.PageAssociation == 0;

    // ── Passthrough: partition segments into page stream + globals stream ─────

    private static PdfImageXObject BuildPassthrough(
        List<SegmentHeader> segments, ReadOnlySpan<byte> data,
        int width, int height, int firstPage)
    {
        var globalBuf = new MemoryStream();
        var pageBuf = new MemoryStream();

        foreach (var seg in segments)
        {
            // File-framing segments have no place in the PDF embedded organisation (ISO 32000-1
            // §7.4.7): the end-of-file (51) and end-of-page (49) segments are dropped. End-of-stripe
            // (50) is deliberately retained — for a striped page it records the stripe's end row,
            // which is image data the decoder needs, not file framing.
            if (seg.Type is SegmentTypeEndOfFile or SegmentTypeEndOfPage)
                continue;

            // A PDF image is one page; the rest of a multi-page file is not embedded.
            if (!BelongsToPage(seg, firstPage))
                continue;

            // A segment of unknown length (§7.2.7) is dropped. This loader does not scan for the end
            // marker 7.2.7 describes (0xFF 0xAC for arithmetic coding, 0x00 0x00 for MMR, each
            // followed by a row count; only an immediate generic region may have one), so it cannot
            // say where the segment ends. Dropping it can leave an image with no region, which a
            // reader draws blank. The file is valid, so it is not refused; 3.0 refuses it.
            if (!seg.HasKnownLength)
                continue;

            var segEnd = seg.DataOffset + (int)seg.DataLength;
            if (seg.HeaderStart < 0 || segEnd > data.Length)
                continue; // safety guard against corrupt entries

            var segBytes = data[seg.HeaderStart..segEnd].ToArray();

            if (IsGlobalSegment(seg))
                globalBuf.Write(segBytes);
            else
                pageBuf.Write(segBytes);
        }

        var pageBytes = pageBuf.ToArray();
        var globalsBytes = globalBuf.Length > 0 ? globalBuf.ToArray() : null;

        // Pass no /DecodeParms here: when global segments exist, PdfDocument supplies a
        // /DecodeParms << /JBIG2Globals … >> dictionary while wiring the side-stream reference;
        // when there are none, the image needs no /DecodeParms at all (emitting an empty
        // << >> dictionary is unnecessary clutter).
        return new PdfImageXObject(
            width, height, pageBytes,
            PdfName.JBIG2Decode,
            ImageColorSpace.DeviceGray,
            bitsPerComponent: 1,
            decodeParms: null,
            jbig2Globals: globalsBytes);
    }

    /// <summary>
    /// Returns true when a segment should be placed in the JBIG2Globals side-stream.
    /// Global segments have page association 0 and carry inter-page shared resources
    /// (symbol/pattern dictionaries, tables, extension segments).
    /// </summary>
    private static bool IsGlobalSegment(in SegmentHeader seg)
    {
        if (seg.PageAssociation != 0)
            return false;

        return seg.Type is SegmentTypeSymbolDictionary
            or SegmentTypePatternDictionary
            or SegmentTypeTables
            or SegmentTypeProfiles
            or SegmentTypeExtension;
    }

    // ── Decode-to-raster ─────────────────────────────────────────────────────

    private static PdfImageXObject DecodeToRaster(
        List<SegmentHeader> segments, ReadOnlySpan<byte> data,
        int width, int height, int firstPage)
    {
        // Every segment is looked at, whatever page it is on, as the class remarks say.
        // Reject segment types that require symbol/pattern/refinement decoding.
        foreach (var seg in segments)
        {
            switch (seg.Type)
            {
                case SegmentTypeSymbolDictionary:
                case SegmentTypeIntermediateTextRegion:
                case SegmentTypeImmediateTextRegion:
                case SegmentTypeImmediateLosslessTextRegion:
                case SegmentTypePatternDictionary:
                case SegmentTypeIntermediateHalftoneRegion:
                case SegmentTypeImmediateHalftoneRegion:
                case SegmentTypeImmediateLosslessHalftoneRegion:
                case SegmentTypeIntermediateGenericRefinementRegion:
                case SegmentTypeImmediateGenericRefinementRegion:
                case SegmentTypeImmediateLosslessGenericRefinementRegion:
                    throw new NotSupportedException(
                        $"JBIG2 DecodeToRaster does not support segment type {seg.Type} " +
                        "(symbol dictionaries, text regions, halftone regions, and " +
                        "refinement regions are not supported; use Passthrough instead).");
            }
        }

        var rowBytes = (width + 7) / 8;
        var outputSize = (long)rowBytes * height;
        if (outputSize > (long)ImageLimits.MaxPixels / 8 + 1)
            throw new InvalidDataException("JBIG2: decoded raster would exceed the safety limit.");

        var raster = new byte[(int)outputSize];
        byte[]? discarded = null;

        foreach (var seg in segments)
        {
            var onPage = BelongsToPage(seg, firstPage);

            if (seg.Type != SegmentTypeImmediateGenericRegion
                && seg.Type != SegmentTypeImmediateLosslessGenericRegion
                && seg.Type != SegmentTypeIntermediateGenericRegion)
                continue;

            // The one place this mode already refused an unknown length, as "data too short". Every
            // other type is as it was: an unsupported one throws NotSupportedException above, and a
            // segment the raster does not use is ignored whatever its length.
            if (!seg.HasKnownLength)
                throw UnknownLength(seg);

            if (seg.DataLength < 18)
                throw new InvalidDataException(
                    $"JBIG2: generic region segment {seg.Number} data too short.");

            // The regions of the other pages are decoded so that one this loader cannot decode is
            // refused, and the result is thrown away.
            byte[] target = raster;
            if (!onPage)
                target = discarded ??= new byte[raster.Length];

            DecodeGenericRegion(seg, data, target, width, height, rowBytes);
        }

        return new PdfImageXObject(
            width, height, raster,
            PdfName.FlateDecode,
            ImageColorSpace.DeviceGray,
            bitsPerComponent: 1);
    }

    /// <summary>
    /// Decodes one JBIG2 immediate generic region (§7.4.6) into <paramref name="raster"/>.
    /// Only MMR (T.6) coding is supported; arithmetic-coded regions throw.
    /// </summary>
    private static void DecodeGenericRegion(
        in SegmentHeader seg, ReadOnlySpan<byte> data,
        byte[] raster, int pageWidth, int pageHeight, int pageRowBytes)
    {
        // Region segment information field (§7.4.1): 4+4+4+4+1 = 17 bytes.
        // Generic region segment flags (§7.4.6.4): 1 byte. Total = 18 bytes.
        var off = seg.DataOffset;
        if (off + 18 > data.Length)
            throw new InvalidDataException(
                $"JBIG2: generic region segment {seg.Number} is truncated.");

        var regionWidth = ReadInt32(data, off);
        var regionHeight = ReadInt32(data, off + 4);
        var regionX = ReadInt32(data, off + 8);
        var regionY = ReadInt32(data, off + 12);
        // data[off + 16] = region combination flags (§7.4.1 table 22) — not needed here.
        var grFlags = data[off + 17];

        // grFlags bit 0: 1 = MMR (T.6), 0 = arithmetic (MQ-coder).
        if ((grFlags & 0x01) == 0)
        {
            throw new NotSupportedException(
                $"JBIG2 DecodeToRaster: segment {seg.Number} uses arithmetic (MQ) coding. " +
                "Only MMR-coded generic regions are supported for in-process decoding; " +
                "use Passthrough for arithmetic-coded JBIG2 files.");
        }

        if (regionWidth <= 0 || regionHeight <= 0)
            throw new InvalidDataException(
                $"JBIG2: generic region {seg.Number} has invalid dimensions " +
                $"{regionWidth}×{regionHeight}.");

        if ((long)regionX + regionWidth > pageWidth
            || (long)regionY + regionHeight > pageHeight)
            throw new InvalidDataException(
                $"JBIG2: generic region {seg.Number} at ({regionX},{regionY}) " +
                $"size {regionWidth}×{regionHeight} extends outside page " +
                $"({pageWidth}×{pageHeight}).");

        var mmrOff = off + 18;
        var mmrLen = (int)(seg.DataLength - 18);
        if (mmrOff + mmrLen > data.Length)
            throw new InvalidDataException(
                $"JBIG2: MMR data for segment {seg.Number} extends beyond the buffer.");

        var mmrData = data.Slice(mmrOff, mmrLen);

        var regionRowBytes = (regionWidth + 7) / 8;
        var regionRaster = MmrDecoder.Decode(mmrData, regionWidth, regionHeight, regionRowBytes);

        BlitRegion(
            regionRaster, regionWidth, regionHeight, regionRowBytes,
            raster, pageWidth, pageHeight, pageRowBytes,
            regionX, regionY);
    }

    // ── Region blit ───────────────────────────────────────────────────────────

    private static void BlitRegion(
        byte[] src, int srcWidth, int srcHeight, int srcRowBytes,
        byte[] dst, int dstWidth, int dstHeight, int dstRowBytes,
        int dstX, int dstY)
    {
        for (var row = 0; row < srcHeight; row++)
        {
            var dstRow = dstY + row;
            if ((uint)dstRow >= (uint)dstHeight) continue;
            for (var col = 0; col < srcWidth; col++)
            {
                var dstCol = dstX + col;
                if ((uint)dstCol >= (uint)dstWidth) continue;

                var srcByte = row * srcRowBytes + col / 8;
                var srcBit = 7 - (col % 8);
                var bit = (src[srcByte] >> srcBit) & 1;

                if (bit == 1)
                {
                    var dstByte = dstRow * dstRowBytes + dstCol / 8;
                    var dstBit = 7 - (dstCol % 8);
                    dst[dstByte] |= (byte)(1 << dstBit);
                }
            }
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static bool StartsWithFileHeader(byte[] data)
    {
        if (data.Length < FileHeader.Length) return false;
        for (var i = 0; i < FileHeader.Length; i++)
            if (data[i] != FileHeader[i]) return false;
        return true;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16)
         | (data[offset + 2] << 8) | data[offset + 3];

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
