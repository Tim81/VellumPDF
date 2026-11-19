// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Creates a PDF Image XObject from JBIG2 bilevel image data (ITU-T T.88 / ISO/IEC 14492).
///
/// <para><b>File organisations:</b> A standalone <c>.jbig2</c> or <c>.jb2</c> file starts with a
/// file header (ITU-T T.88 Annex D.4): the 8-byte ID string <c>97 4A 42 32 0D 0A 1A 0A</c>, a flags
/// byte, and a 4-byte page count unless bit 1 of the flags says the count is unknown. Bit 0 of the
/// flags selects the organisation. In the <i>sequential</i> organisation (D.1) each segment's header
/// is followed by its data. In the <i>random-access</i> organisation (D.2) all the segment headers
/// come first, the last of them the end-of-file segment, and the data parts follow in the same
/// order. Both are read. PDF <c>/JBIG2Decode</c> uses the <i>embedded</i> organisation (D.3):
/// no file header; page-associated segments go in the image stream; globally-referenced segments
/// (symbol dictionaries, pattern dictionaries, tables) go in a separate stream referenced as
/// <c>/DecodeParms &lt;&lt; /JBIG2Globals &lt;ref&gt; &gt;&gt;</c>. A random-access file is written out in
/// that embedded form, each header joined to its own data.</para>
///
/// <para><b>One page:</b> a PDF image is one page, so only the first page of a multi-page file is
/// embedded. The first page is the one named by the page association of the first
/// page-information segment (type 48). Its segments are kept, with the segments that belong to no
/// page (page association 0), and the segments of every other page are dropped. The width and height
/// are the first page's. The page count in a file header is not checked against the pages the file
/// holds. A first page-information segment with page association 0 names no page, so every segment is
/// kept.</para>
///
/// <para><see cref="ImageDecodeMode.DecodeToRaster"/> still decodes the regions of the other pages and
/// discards the result, so a later page with a region this loader cannot decode, or whose data length
/// is unknown, refuses the file there. <b>Do not pass</b> a multi-page file without the T.88 Annex D
/// header: the other pages are lost without a word, and 3.0 refuses it. A multi-page file with that
/// header is refused with <see cref="InvalidDataException"/> naming the page count, in both
/// modes.</para>
///
/// <para><b>A file with the Annex D header is loaded only if it complies with T.88, and only in
/// <see cref="ImageDecodeMode.Passthrough"/>.</b> A file that breaks T.88 is refused with
/// <see cref="InvalidDataException"/> naming the clause: a page-information segment that is not on
/// page 1 (7.2.6, 7.3.2), a segment on any other page (7.2.6), a first segment on page 1 that is not
/// the page information (7.4.8), a region on no page (7.3.2), a header page count other than 1
/// (D.4.3), segment numbers that do not increase through the file (7.2, D.1, D.2; a repeated number
/// is refused too, though T.88 does not say numbers are unique), a reserved segment type (7.3), or a
/// page that does not end with exactly one end-of-page segment (7.4.9). <see cref="ImageDecodeMode.DecodeToRaster"/> refuses every such file, compliant or not,
/// because the raster it writes would render as a negative: it stores the decoded bits unchanged as
/// <c>/DeviceGray</c> without <c>/Decode</c>, where T.88 reads 1 as black. A file without the header
/// decodes to the same raster, with the same inversion.</para>
///
/// <para><b>Unknown segment length:</b> a segment whose data length field is 0xFFFFFFFF (ITU-T T.88
/// 7.2.7) is not embedded in a file without the Annex D header, because this loader does not scan the
/// data for the end marker that clause describes and so cannot say where such a segment ends.
/// <see cref="ImageDecodeMode.Passthrough"/> drops it, which can leave a page blank without a word;
/// <b>do not pass</b> such a segment, because 3.0 refuses it and the diagnostics channel (#607) will
/// report it before then. In a file with the Annex D header, a segment of unknown length on any page,
/// or of no page, is refused with <see cref="InvalidDataException"/> naming it, in both modes.
/// <see cref="ImageDecodeMode.DecodeToRaster"/> refuses an unknown length on an immediate, lossless or
/// intermediate generic region (types 36, 38 and 39), with a message naming the segment. A
/// sequential or embedded stream takes the rest of the data as an unknown-length segment's own, so
/// what follows one is not seen. A random-access file cannot use an unknown length on any page,
/// because its data parts cannot be located without it, and says so without naming the segment
/// type.</para>
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
    /// The JBIG2 bytes: a standalone file in either organisation of ITU-T T.88 Annex D, sequential or
    /// random-access, with its file header (the 8-byte ID string, a flags byte, and a 4-byte page
    /// count unless the flags say it is unknown); or data already in embedded form, without a file
    /// header. The ID string followed directly by segments loads too.
    /// </param>
    /// <returns>
    /// A <see cref="PdfImageXObject"/> with <c>/Filter /JBIG2Decode</c>.
    /// When global segments are present, <see cref="PdfImageXObject.Jbig2Globals"/> is
    /// non-null and will be wired into <c>/DecodeParms /JBIG2Globals</c> automatically
    /// by <see cref="Document.PdfDocument"/> when the document is written.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="jbig2"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">
    /// Thrown on malformed or truncated input. In a file without the Annex D header, a segment whose
    /// data length is unknown (0xFFFFFFFF, ITU-T T.88 7.2.7) is dropped, not refused, because this
    /// loader does not scan for the end marker 7.2.7 describes and so cannot say where the segment
    /// ends. <b>Do not pass</b> such a segment: the image is embedded without it, which can leave no
    /// region and a blank image, and nothing reports it yet; 3.0 refuses it. In a file with the Annex D
    /// header, a segment of unknown length on any page, or of no page, is refused with
    /// <see cref="InvalidDataException"/> naming it. Only the first page of a multi-page file is
    /// embedded, unless the first page-information segment has page association 0, which keeps every
    /// segment. A file with the Annex D header and more than one page is refused, naming the page
    /// count, and so is one that does not comply with T.88 (see the remarks on this type).
    /// </exception>
    public static PdfImageXObject Load(byte[] jbig2)
        => Load(jbig2, ImageLoadOptions.Default);

    /// <summary>
    /// Loads a JBIG2 image and creates a PDF Image XObject using the specified options.
    /// </summary>
    /// <param name="jbig2">
    /// The JBIG2 bytes: a standalone file in either organisation of ITU-T T.88 Annex D, sequential or
    /// random-access, with its file header (the 8-byte ID string, a flags byte, and a 4-byte page
    /// count unless the flags say it is unknown); or data already in embedded form, without a file
    /// header. The ID string followed directly by segments loads too.
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
    /// <exception cref="InvalidDataException">
    /// Thrown on malformed or truncated input. Under <see cref="ImageDecodeMode.DecodeToRaster"/> it
    /// is also thrown for a generic region whose data length is unknown (0xFFFFFFFF, ITU-T T.88
    /// 7.2.7), which this loader cannot decode; the message names the segment number. Under
    /// <see cref="ImageDecodeMode.Passthrough"/> such a segment is dropped in a file without the Annex D
    /// header; see <see cref="Load(byte[])"/>. Only the first page of a multi-page file is embedded,
    /// under either mode, unless the first page-information segment has page association 0, which keeps
    /// every segment. Under <see cref="ImageDecodeMode.DecodeToRaster"/> the other pages are decoded and
    /// discarded, so a region on them that cannot be decoded refuses the file. A file with the Annex D
    /// header and more than one page is refused in both modes, naming the page count; every file with
    /// that header is refused under <see cref="ImageDecodeMode.DecodeToRaster"/>, and in either mode
    /// when it does not comply with T.88; see the remarks on this type.
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

        // The segments start after a file header, if there is one: the ID string, then a flags byte,
        // then a page count unless the flags say it is unknown (T.88 D.4). Embedded-form input has no
        // file header at all. Which of those a file is cannot be read off the bytes alone: the ID
        // string followed straight away by segments is indistinguishable from a flags byte followed by
        // more of the same. Each reading is tried in turn, and the first that yields a
        // page-information segment wins.
        InvalidDataException? leadFailure = null;
        var failed = new List<(string Reading, string Message)>();
        var lastReading = "";
        List<SegmentHeader>? segments = null;
        var dataStart = 0;
        var width = 0;
        var height = 0;
        var firstPage = 0;

        foreach (var (start, randomAccess) in CandidateLayouts(jbig2))
        {
            var reading = DescribeReading(jbig2, start, randomAccess);
            try
            {
                var candidate = jbig2.AsSpan(start);
                var parsed = randomAccess ? ParseRandomAccessSegments(candidate) : ParseSegments(candidate);
                (width, height, firstPage) = ReadPageInfo(parsed, candidate);
                segments = parsed;
                dataStart = start;
                break;
            }
            catch (InvalidDataException ex)
            {
                // Lead with the last reading's failure: with a well-formed header, that reading is
                // the last one tried and is the one that applies.
                leadFailure = ex;
                lastReading = reading;
                failed.Add((reading, ex.Message));
            }
        }

        if (segments is null)
        {
            // One reading tried: its own message. Several: the last reading's, then which readings were
            // tried, so a broken older file is not described only in the terms of the newer
            // random-access one.
            throw failed.Count <= 1
                ? leadFailure!
                : new InvalidDataException(
                    $"{leadFailure!.Message} The data was read as "
                    + string.Join(", then as ", failed.Select(f => f.Reading == lastReading
                        ? f.Reading
                        : $"{f.Reading}, which failed with: {f.Message}"))
                    + ".",
                    leadFailure);
        }

        var data = jbig2.AsSpan(dataStart);

        // T.88 7.2.7 allows an unknown length only on an immediate generic region, and this loader
        // cannot place where such a segment ends, so dropping it would embed an image with no
        // region or, for a segment of any other type, accept a file that is not compliant. In a
        // file with the Annex D header it is refused, on every page and in both modes. The same
        // goes for a page count above one; see below.
        if (dataStart > FileHeader.Length)
        {
            foreach (var seg in segments)
                if (!seg.HasKnownLength)
                    throw UnknownLength(seg);

            // More than one page is refused too, in both modes: a PDF image is one page, and
            // embedding the first and dropping the rest is not correct handling.
            var pages = segments.Count(seg => seg.Type == SegmentTypePageInformation);
            if (pages > 1)
            {
                throw new InvalidDataException(
                    $"JBIG2: the file holds {pages} pages, and a PDF image holds one. " +
                    "A file with the T.88 Annex D header and more than one page is not embedded.");
            }

            // Only a file that complies with T.88 loads; a file that does not comply is refused.
            RequireCompliantSinglePage(jbig2, dataStart, segments);

            // The raster this mode writes is not correct for these files: it stores the decoded bits,
            // where T.88 reads 1 as black (clause 3 NOTE 1, 5.6), as /DeviceGray 1 bpc with no
            // /Decode, whose default maps sample 0 to black (ISO 32000-2 8.9.5.2). A page that
            // renders as a negative is not correct handling of compliant input, so it is refused.
            if (options.DecodeMode == ImageDecodeMode.DecodeToRaster)
            {
                throw new InvalidDataException(
                    "JBIG2: a file with the T.88 Annex D header is not decoded to a raster, because this " +
                    "loader's raster output for such a file is not yet correct (it renders inverted). " +
                    "Use ImageDecodeMode.Passthrough.");
            }
        }

        ImageLimits.ValidateDimensions("JBIG2", width, height);

        return options.DecodeMode == ImageDecodeMode.DecodeToRaster
            ? DecodeToRaster(segments, data, width, height, firstPage, decodeOtherPages: dataStart <= FileHeader.Length)
            : BuildPassthrough(segments, data, width, height, firstPage);
    }

    /// <summary>
    /// Refuses a file with the T.88 Annex D header that does not comply with the standard in a way that
    /// matters to a one-page embedding. Only compliant input is accepted.
    /// </summary>
    private static void RequireCompliantSinglePage(
        byte[] jbig2, int dataStart, List<SegmentHeader> segments)
    {
        // D.4.3: a page count that is present "must equal the number of pages contained in the file".
        // This file holds one page at most, so a present count other than 1 is wrong. The count is
        // present exactly when the segments start 4 bytes after the flags byte (D.4.2 bit 1).
        if (dataStart == FileHeader.Length + 1 + 4)
        {
            var count = ReadUInt32(jbig2, FileHeader.Length + 1);
            if (count != 1)
                throw new InvalidDataException(
                    $"JBIG2: the file header says {count} pages, and a PDF image holds one (T.88 D.4.3).");
        }

        var firstAssociated = true;
        var firstPageInformation = true;
        var endOfPageCount = 0;
        var lastPagedType = -1;
        long previousNumber = -1;
        foreach (var seg in segments)
        {
            // 7.2 introduction, D.1 and D.2: "no segment may precede a segment having a lower number
            // than it". T.88 does not say whether two segments may share a number; a referred-to
            // segment is named by its number alone, so a repeat is refused too.
            var number = unchecked((uint)seg.Number);
            if (number <= previousNumber)
                throw new InvalidDataException(
                    $"JBIG2: segment {number} follows segment {previousNumber}, but segment numbers must " +
                    "increase through the file (T.88 7.2, D.1, D.2).");
            previousNumber = number;

            // 7.3: "All other segment types are reserved and must not be used."
            if (!IsAllowedSegmentType(seg.Type))
                throw new InvalidDataException(
                    $"JBIG2: segment {number} has type {seg.Type}, which T.88 reserves (T.88 7.3).");

            // 7.2.6: "The first page must be numbered 1"; this file has one page, so 0 or 1 only.
            if (seg.PageAssociation is not (0 or 1))
                throw new InvalidDataException(
                    $"JBIG2: segment {seg.Number} is associated with page {seg.PageAssociation}, but the " +
                    "file's first page is page 1 and it holds no other (T.88 7.2.6).");

            if (seg.Type == SegmentTypePageInformation)
            {
                // 7.3.2: a page information segment must be associated with a page, and 7.2.6 numbers
                // the first page 1.
                if (seg.PageAssociation != 1 && firstPageInformation)
                    throw new InvalidDataException(
                        $"JBIG2: the first page-information segment ({seg.Number}) is not on page 1 " +
                        "(T.88 7.2.6, 7.3.2).");
                firstPageInformation = false;
            }

            if (seg.PageAssociation != 0)
            {
                // 7.4.8: "The first segment that is associated with any page must be a page information
                // segment."
                if (firstAssociated && seg.Type != SegmentTypePageInformation)
                    throw new InvalidDataException(
                        $"JBIG2: segment {seg.Number} (type {seg.Type}) is the first segment on page 1, " +
                        "and it is not the page-information segment (T.88 7.4.8).");
                firstAssociated = false;
                lastPagedType = seg.Type;
                if (seg.Type == SegmentTypeEndOfPage) endOfPageCount++;
            }
            else if (IsRegionSegment(seg.Type))
            {
                // 7.3.2: "Every region segment must be associated with some page".
                throw new InvalidDataException(
                    $"JBIG2: region segment {seg.Number} (type {seg.Type}) is not associated with any " +
                    "page (T.88 7.3.2).");
            }
        }

        // 7.4.9: "The last segment that is associated with any page must be an end of page segment.
        // Each page must have exactly one end of page segment associated with it."
        if (endOfPageCount != 1 || lastPagedType != SegmentTypeEndOfPage)
            throw new InvalidDataException(
                "JBIG2: page 1 must end with exactly one end-of-page segment, and the last segment " +
                "associated with a page must be that segment (T.88 7.4.9).");
    }

    private static bool IsAllowedSegmentType(int type) => type is
        0 or 4 or 6 or 7 or 16 or 20 or 22 or 23 or 36 or 38 or 39 or 40 or 42 or 43
        or 48 or 49 or 50 or 51 or 52 or 53 or 54 or 62;

    private static bool IsRegionSegment(int type) => type is
        SegmentTypeIntermediateTextRegion or SegmentTypeImmediateTextRegion
        or SegmentTypeImmediateLosslessTextRegion or SegmentTypeIntermediateHalftoneRegion
        or SegmentTypeImmediateHalftoneRegion or SegmentTypeImmediateLosslessHalftoneRegion
        or SegmentTypeIntermediateGenericRegion or SegmentTypeImmediateGenericRegion
        or SegmentTypeImmediateLosslessGenericRegion or SegmentTypeIntermediateGenericRefinementRegion
        or SegmentTypeImmediateGenericRefinementRegion or SegmentTypeImmediateLosslessGenericRefinementRegion;

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
        /// <summary>Byte offset in the stripped data span just past the segment header.</summary>
        /// <remarks>
        /// Equal to <see cref="DataOffset"/> in the sequential and embedded organisations, where a
        /// segment's data follows its header. In the random-access organisation (ITU-T T.88 D.2) all
        /// the headers come first and the data parts after them, so the two are far apart, and a
        /// segment cannot be copied as one contiguous range.
        /// </remarks>
        public readonly int HeaderEnd;
        /// <summary>Byte offset in the stripped data span where segment data begins.</summary>
        public readonly int DataOffset;
        /// <summary>Segment data length in bytes. -1 = unknown (variable-length).</summary>
        public readonly long DataLength;

        public SegmentHeader(int number, int type, int pageAssociation,
            int headerStart, int headerEnd, int dataOffset, long dataLength)
        {
            Number = number;
            Type = type;
            PageAssociation = pageAssociation;
            HeaderStart = headerStart;
            HeaderEnd = headerEnd;
            DataOffset = dataOffset;
            DataLength = dataLength;
        }

        public bool HasKnownLength => DataLength >= 0;
    }

    // ── Segment header parsing (ITU-T T.88 §7.2) ─────────────────────────────

    /// <summary>One segment header as it is read, before it is known where the segment's data is.</summary>
    private readonly record struct RawHeader(
        int Number, int Type, int PageAssociation, int HeaderStart, int HeaderEnd, long DataLength);

    /// <summary>
    /// Reads one segment header (§7.2) at <paramref name="pos"/> and leaves <paramref name="pos"/> just
    /// past it. Shared by the sequential organisation, where the data follows, and the random-access
    /// one, where it does not.
    /// </summary>
    private static RawHeader ReadSegmentHeader(ReadOnlySpan<byte> data, ref int pos)
    {
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

        return new RawHeader(segNumber, segType, pageAssociation, headerStart, pos, dataLength);
    }

    /// <summary>
    /// Parses the sequential and embedded organisations (§D.1, §D.3), where each segment's header is
    /// followed by its data.
    /// </summary>
    private static List<SegmentHeader> ParseSegments(ReadOnlySpan<byte> data)
    {
        var segments = new List<SegmentHeader>();
        var pos = 0;

        while (pos < data.Length)
        {
            if (segments.Count >= MaxSegments)
                throw new InvalidDataException(
                    $"JBIG2: segment count exceeds the safety limit of {MaxSegments}.");

            var h = ReadSegmentHeader(data, ref pos);
            var dataOffset = pos;

            if (h.DataLength >= 0)
            {
                if (dataOffset + h.DataLength > data.Length)
                    throw new InvalidDataException(
                        $"JBIG2: segment {h.Number} data length {h.DataLength} " +
                        $"at offset {dataOffset} exceeds buffer size {data.Length}.");
                pos += (int)h.DataLength;
            }
            else
            {
                // Variable-length segment: treat as consuming the remaining buffer.
                pos = data.Length;
            }

            segments.Add(new SegmentHeader(
                h.Number, h.Type, h.PageAssociation,
                h.HeaderStart, h.HeaderEnd, dataOffset, h.DataLength));
        }

        return segments;
    }

    /// <summary>
    /// Parses the random-access organisation (§D.2): a run of segment headers that ends with the
    /// end-of-file segment's header, then the data parts of every segment in the same order. The
    /// end-of-file segment is what tells a reader the headers are over, so a file without one cannot
    /// be read.
    /// </summary>
    private static List<SegmentHeader> ParseRandomAccessSegments(ReadOnlySpan<byte> data)
    {
        var headers = new List<RawHeader>();
        var pos = 0;

        while (true)
        {
            if (headers.Count >= MaxSegments)
                throw new InvalidDataException(
                    $"JBIG2: segment count exceeds the safety limit of {MaxSegments}.");
            if (pos >= data.Length)
                throw new InvalidDataException(
                    "JBIG2: a random-access file must end its segment headers with an end-of-file segment (type 51).");

            RawHeader h;
            try
            {
                h = ReadSegmentHeader(data, ref pos);
            }
            catch (InvalidDataException ex)
            {
                // Without the end-of-file header a reader cannot tell where the headers stop, and runs
                // on into the data parts and reads them as headers, so the fault it finds is a long
                // way from the cause. Say what the file is missing.
                throw new InvalidDataException(
                    "JBIG2: the segment headers of a random-access file end with an end-of-file segment " +
                    $"(type 51), and the headers here are malformed or run out before one: {ex.Message}", ex);
            }

            headers.Add(h);
            if (h.Type == SegmentTypeEndOfFile) break;
        }

        // The data parts follow the last header, one after another in header order.
        var segments = new List<SegmentHeader>(headers.Count);
        var dataPos = (long)pos;
        foreach (var h in headers)
        {
            // An unknown length is only meaningful where the data follows its header and the reader
            // can scan for the end marker; here nothing says where the next data part starts.
            if (h.DataLength < 0)
                throw new InvalidDataException(
                    $"JBIG2: segment {h.Number} has an unknown data length, which a random-access file cannot use.");
            if (dataPos + h.DataLength > data.Length)
                throw new InvalidDataException(
                    $"JBIG2: segment {h.Number} data length {h.DataLength} " +
                    $"at offset {dataPos} exceeds buffer size {data.Length}.");

            segments.Add(new SegmentHeader(
                h.Number, h.Type, h.PageAssociation,
                h.HeaderStart, h.HeaderEnd, (int)dataPos, h.DataLength));
            dataPos += h.DataLength;
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
            if (seg.HeaderStart < 0 || seg.HeaderEnd > data.Length || segEnd > data.Length)
                continue; // safety guard against corrupt entries

            // The embedded organisation a PDF stores is header then data. In a random-access file the
            // two are far apart, so they are joined here rather than sliced as one range.
            var segBytes = new byte[(seg.HeaderEnd - seg.HeaderStart) + (int)seg.DataLength];
            data[seg.HeaderStart..seg.HeaderEnd].CopyTo(segBytes);
            data.Slice(seg.DataOffset, (int)seg.DataLength).CopyTo(segBytes.AsSpan(seg.HeaderEnd - seg.HeaderStart));

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
        int width, int height, int firstPage, bool decodeOtherPages)
    {
        // Reject segment types that require symbol/pattern/refinement decoding.
        foreach (var seg in segments)
        {
            if (!decodeOtherPages && !BelongsToPage(seg, firstPage)) continue;

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
            if (!onPage && !decodeOtherPages) continue;

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

    /// <summary>
    /// The places the segments can start, in the order they are tried, as (offset, random-access).
    /// </summary>
    /// <remarks>
    /// With the ID string, the reading with the first segment directly after it comes first. A valid
    /// file in that form can have a first segment number whose high byte looks like a flags byte, so
    /// trying the Annex D reading first misreads it. Only when that reading fails, and the
    /// byte after the ID string is a flags byte whose reserved bits 4 to 7 are zero (T.88 D.4.2), is the file header read as the standard says:
    /// bit 0 clear is the random-access organisation, bit 1 set means there is no 4-byte page count.
    /// Without the ID string the bytes are the embedded organisation.
    /// </remarks>
    private static (int Start, bool RandomAccess)[] CandidateLayouts(byte[] jbig2)
    {
        if (!StartsWithFileHeader(jbig2))
            return [(0, false)];

        var idLength = FileHeader.Length;
        if (jbig2.Length > idLength)
        {
            var flags = jbig2[idLength];
            if ((flags & 0xF0) == 0)
            {
                var sequential = (flags & 0x01) != 0;
                var pageCountPresent = (flags & 0x02) == 0;
                var start = idLength + 1 + (pageCountPresent ? 4 : 0);
                if (start <= jbig2.Length)
                    return [(idLength, false), (start, !sequential)];
            }
        }

        return [(idLength, false)];
    }

    private static string DescribeReading(byte[] jbig2, int start, bool randomAccess) =>
        start == 0 ? "the embedded organisation, without a file header"
        : randomAccess ? "a random-access file (T.88 D.2)"
        : start == FileHeader.Length ? "the ID string followed directly by segments"
        : "a sequential file (T.88 D.1)";

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
