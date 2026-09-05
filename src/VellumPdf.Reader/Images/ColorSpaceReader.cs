// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Turns a raw <c>/ColorSpace</c> value (name, array, or reference) into a
/// <see cref="PdfImageColorSpace"/> (ISO 32000-2 §8.6), or <see langword="null"/> with
/// <see cref="PdfReaderDiagnosticCode.ImageColorSpaceUnsupported"/> reported. One instance per
/// <c>ExtractImages</c> call, sharing <see cref="ImageDecoder"/>'s own ancillary-stream cache and
/// byte budget so an Indexed lookup or ICC profile stream named by many images is decoded, and
/// charged, once.
/// </summary>
internal sealed class ColorSpaceReader(
    PdfDocumentReader reader, AncillaryStreamCache ancillaryCache, ImageCallBudget budget, ReaderLimits limits)
{
    // ISO 32000-2 §8.6.6.5 allows DeviceN "an arbitrary number" of colourants; 32 (the figure Annex
    // C.2 Table C.1 gives, informative, and marked as a PREVIOUS PDF version's own recommendation)
    // is not this reader's cap. This reader's own ceiling instead matches
    // ContentInterpreter.MaxOperandsPerOperator (64) and its recorded reasoning there: an scn call
    // against a DeviceN space needs one operand per colourant, so a legal call with 32 or more
    // colourants needs 33 or more operands, and two files in one repository must not read one
    // clause oppositely.
    private const int MaxDeviceNComponents = 64;

    private static readonly PdfName IccBasedTag = new("ICCBased");
    private static readonly PdfName IndexedTag = new("Indexed");
    private static readonly PdfName CalGrayTag = new("CalGray");
    private static readonly PdfName CalRgbTag = new("CalRGB");
    private static readonly PdfName LabTag = new("Lab");
    private static readonly PdfName SeparationTag = new("Separation");
    private static readonly PdfName DeviceNTag = new("DeviceN");

    /// <summary>
    /// Resolves <paramref name="csValue"/> against <paramref name="resources"/>' own
    /// <c>/ColorSpace</c> subdictionary when it names a resource rather than a device space,
    /// exactly the lookup ISO 32000-2 §8.9.7 provides for an inline image's <c>/CS</c>, and, per
    /// §8.6.1, a leniency (not a conformance path) for an image XObject's own <c>/ColorSpace</c>,
    /// which "shall always be defined directly as a PDF object, not by an entry in the ColorSpace
    /// resource subdictionary". Producers write it anyway, so it is attempted either way; when it
    /// succeeds, no diagnostic is raised for having taken the leniency.
    /// </summary>
    internal PdfImageColorSpace? Read(
        PdfObject? csValue, PdfDictionary? resources, DiagnosticSink diagnostics, int? objectNumber,
        int? generation, int pageIndex) =>
        ReadCore(csValue, resources, diagnostics, objectNumber, generation, pageIndex, allowResourceLookup: true);

    private PdfImageColorSpace? ReadCore(
        PdfObject? csValue, PdfDictionary? resources, DiagnosticSink diagnostics, int? objectNumber,
        int? generation, int pageIndex, bool allowResourceLookup)
    {
        if (csValue is null)
        {
            Report(diagnostics, objectNumber, generation, pageIndex, "/ColorSpace is absent.");
            return null;
        }
        var resolved = reader.ResolveValue(csValue);

        if (resolved is PdfName name)
        {
            if (name.Value == "DeviceGray")
                return new PdfImageColorSpace(PdfImageColorSpaceFamily.DeviceGray, 1);
            if (name.Equals(PdfName.DeviceRGB))
                return new PdfImageColorSpace(PdfImageColorSpaceFamily.DeviceRgb, 3);
            if (name.Value == "DeviceCMYK")
                return new PdfImageColorSpace(PdfImageColorSpaceFamily.DeviceCmyk, 4);

            if (name.Value == "Pattern")
            {
                Report(diagnostics, objectNumber, generation, pageIndex,
                    "/ColorSpace names Pattern, which ISO 32000-2 Table 88 marks \"Not permitted "
                    + "with images\".");
                return null;
            }

            // A resource name is resolved exactly one hop: the entry it names is read here, but if
            // THAT entry is itself just a bare, non-device name, this does not loop back into
            // another resource lookup for it (allowResourceLookup: false below); a /CS0 entry
            // whose own value is the name /CS0 terminates at 501 rather than recursing forever.
            if (allowResourceLookup && resources is not null
                && TryGetColorSpaceResourceEntry(resources, name) is { } entry)
            {
                return ReadCore(entry, resources, diagnostics, objectNumber, generation, pageIndex, allowResourceLookup: false);
            }

            Report(diagnostics, objectNumber, generation, pageIndex,
                $"/ColorSpace names '/{DiagnosticExcerpt.Quote(name.Value)}', which is not a device "
                + "colour space and could not be resolved from the applicable /Resources "
                + "/ColorSpace subdictionary (ISO 32000-2 §8.6.1).");
            return null;
        }

        if (resolved is PdfArray arr && arr.Count > 0 && reader.ResolveValue(arr[0]) is PdfName tag)
        {
            if (tag.Equals(IccBasedTag))
                return ReadIccBased(arr, diagnostics, objectNumber, generation, pageIndex);
            if (tag.Equals(IndexedTag))
                return ReadIndexed(arr, resources, diagnostics, objectNumber, generation, pageIndex);
            if (tag.Equals(CalGrayTag))
                return new PdfImageColorSpace(PdfImageColorSpaceFamily.CalGray, 1);
            if (tag.Equals(CalRgbTag))
                return new PdfImageColorSpace(PdfImageColorSpaceFamily.CalRgb, 3);
            if (tag.Equals(LabTag))
                return new PdfImageColorSpace(PdfImageColorSpaceFamily.Lab, 3);
            if (tag.Equals(SeparationTag))
                return new PdfImageColorSpace(PdfImageColorSpaceFamily.Separation, 1);
            if (tag.Equals(DeviceNTag))
                return ReadDeviceN(arr, diagnostics, objectNumber, generation, pageIndex);
            if (tag.Value == "Pattern")
            {
                Report(diagnostics, objectNumber, generation, pageIndex,
                    "/ColorSpace is a Pattern array, which ISO 32000-2 Table 88 marks \"Not "
                    + "permitted with images\".");
                return null;
            }

            Report(diagnostics, objectNumber, generation, pageIndex,
                $"/ColorSpace array names '/{DiagnosticExcerpt.Quote(tag.Value)}', which this reader "
                + "does not recognise as an image colour space family.");
            return null;
        }

        Report(diagnostics, objectNumber, generation, pageIndex,
            "/ColorSpace did not resolve to a device name, a resource name, or a recognised colour "
            + "space array.");
        return null;
    }

    private PdfImageColorSpace? ReadIccBased(
        PdfArray arr, DiagnosticSink diagnostics, int? objectNumber, int? generation, int pageIndex)
    {
        if (arr.Count < 2 || arr[1] is not PdfIndirectReference profileRef
            || reader.ResolveStream(profileRef) is not { } profileStream)
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                "ICCBased color space does not name a usable profile stream (ISO 32000-2 §8.6.5.5).");
            return null;
        }

        var componentCount = ResolveEntry(profileStream.Dictionary, PdfName.N) is PdfInteger n
            ? (int)n.Value
            : 0;
        if (componentCount is not (1 or 3 or 4))
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                $"ICCBased color space's own /N is {componentCount}, but ISO 32000-2 §8.6.5.5 "
                + "requires 1, 3, or 4.");
            return null;
        }

        var profileBytes = ancillaryCache.GetOrDecode(
            reader, profileStream, AncillaryRole.IccProfile, budget, limits, diagnostics);

        return new PdfImageColorSpace(
            PdfImageColorSpaceFamily.IccBased, componentCount,
            iccProfile: profileBytes ?? ReadOnlyMemory<byte>.Empty);
    }

    private PdfImageColorSpace? ReadIndexed(
        PdfArray arr, PdfDictionary? resources, DiagnosticSink diagnostics, int? objectNumber,
        int? generation, int pageIndex)
    {
        if (arr.Count != 4)
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                "Indexed color space array does not have the four elements ISO 32000-2 §8.6.6.3 "
                + "requires: [/Indexed base hival lookup].");
            return null;
        }

        // §8.6.6.3: "The base colour space ... shall not be a Pattern space or another Indexed
        // space." Checked against the raw element first, cheaply, before resolving it as a full
        // colour space, so a directly-nested /Indexed base is refused without a second full read.
        var baseRaw = reader.ResolveValue(arr[1]);
        if (baseRaw is PdfArray baseArr && baseArr.Count > 0 && reader.ResolveValue(baseArr[0]) is PdfName baseTag
            && (baseTag.Equals(IndexedTag) || baseTag.Value == "Pattern"))
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                "Indexed color space's base is itself Indexed or Pattern, which ISO 32000-2 "
                + "§8.6.6.3 forbids.");
            return null;
        }
        if (baseRaw is PdfName baseName && baseName.Value == "Pattern")
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                "Indexed color space's base is Pattern, which ISO 32000-2 §8.6.6.3 forbids.");
            return null;
        }

        var baseSpace = ReadCore(arr[1], resources, diagnostics, objectNumber, generation, pageIndex, allowResourceLookup: true);
        if (baseSpace is null)
            return null; // Already reported by the recursive call.
        if (baseSpace.Family == PdfImageColorSpaceFamily.Indexed)
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                "Indexed color space's base resolved to another Indexed space, which ISO 32000-2 "
                + "§8.6.6.3 forbids.");
            return null;
        }

        var hivalRaw = reader.ResolveValue(arr[2]);
        if (hivalRaw is not PdfInteger hivalInt || hivalInt.Value is < 0 or > 255)
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                "Indexed color space's hival is missing, not an integer, or outside 0..255 (ISO "
                + "32000-2 §8.6.6.3).");
            return null;
        }
        var hival = (int)hivalInt.Value;

        var expectedLength = (long)(hival + 1) * baseSpace.ComponentCount;
        var lookupBytes = ReadLookupBytes(arr[3], diagnostics, pageIndex);
        if (lookupBytes is null || lookupBytes.Length < expectedLength)
        {
            // This reader's own policy, not a clause citation: §8.6.6.3 says the table "shall be"
            // this long but gives a reader no recovery path, and a table shorter than that cannot
            // be indexed safely.
            Report(diagnostics, objectNumber, generation, pageIndex,
                $"Indexed color space's lookup table is shorter than the {expectedLength} bytes "
                + "its base and hival require; this reader does not extrapolate a short table.");
            return null;
        }
        if (lookupBytes.Length > expectedLength)
            lookupBytes = lookupBytes[..(int)expectedLength]; // Longer: truncated, no diagnostic.

        return new PdfImageColorSpace(
            PdfImageColorSpaceFamily.Indexed, 1, @base: baseSpace, highValue: hival, lookup: lookupBytes);
    }

    private byte[]? ReadLookupBytes(PdfObject rawUnresolved, DiagnosticSink diagnostics, int pageIndex)
    {
        if (rawUnresolved is PdfIndirectReference reference && reader.ResolveStream(reference) is { } stream)
        {
            return ancillaryCache.GetOrDecode(reader, stream, AncillaryRole.IndexedLookup, budget, limits, diagnostics);
        }

        return reader.ResolveValue(rawUnresolved) switch
        {
            PdfLiteralString s => s.Bytes.ToArray(),
            PdfHexString h => h.Bytes.ToArray(),
            _ => null,
        };
    }

    private PdfImageColorSpace? ReadDeviceN(
        PdfArray arr, DiagnosticSink diagnostics, int? objectNumber, int? generation, int pageIndex)
    {
        if (arr.Count < 2 || reader.ResolveValue(arr[1]) is not PdfArray names)
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                "DeviceN color space does not name a colourant array (ISO 32000-2 §8.6.6.5).");
            return null;
        }

        if (names.Count is < 1 or > MaxDeviceNComponents)
        {
            Report(diagnostics, objectNumber, generation, pageIndex,
                $"DeviceN color space names {names.Count} colourants; this reader's own cap is "
                + $"{MaxDeviceNComponents} (matching ContentInterpreter's own MaxOperandsPerOperator "
                + "and its reading of ISO 32000-2 §8.6.6.5 against Annex C.2's informative Table "
                + "C.1, not a requirement of the standard, which allows an arbitrary number).");
            return null;
        }

        return new PdfImageColorSpace(PdfImageColorSpaceFamily.DeviceN, names.Count);
    }

    // §8.6.1: an inline image's /CS may name a key in the current resource dictionary's own
    // /ColorSpace subdictionary; §8.9.7 provides the identical lookup for an image XObject as a
    // producer leniency this reader honours without treating it as itself a conformance defect.
    private PdfObject? TryGetColorSpaceResourceEntry(PdfDictionary resources, PdfName name)
    {
        var categoryRaw = resources.Get(PdfName.ColorSpace);
        if (categoryRaw is null)
            return null;
        if (reader.ResolveValue(categoryRaw) is not PdfDictionary category)
            return null;
        var entry = category.Get(name);
        return entry is null or PdfNull ? null : entry;
    }

    private PdfObject? ResolveEntry(PdfDictionary dict, PdfName key) =>
        dict.Get(key) is { } raw ? reader.ResolveValue(raw) : null;

    private static void Report(
        DiagnosticSink diagnostics, int? objectNumber, int? generation, int pageIndex, string message) =>
        diagnostics.Report(
            PdfReaderDiagnosticCode.ImageColorSpaceUnsupported, message, objectNumber, generation, pageIndex);
}
