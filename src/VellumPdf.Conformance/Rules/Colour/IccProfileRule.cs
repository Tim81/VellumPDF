// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.2.4.2-1 (ICCBased colour space profile validity). The ICC profile embedded as
/// the stream of an <c>ICCBased</c> colour space shall conform to a permitted version of the ICC
/// specification: the profile's device class shall be one of <c>prtr</c>, <c>mntr</c>, <c>scnr</c>,
/// or <c>spac</c>; the data colour space shall be one of <c>RGB </c>, <c>CMYK</c>,
/// <c>GRAY</c>, or <c>Lab </c>; and the ICC major version shall be less than 5.
/// </summary>
/// <remarks>
/// <para>
/// The ICC profile header is 128 bytes (ISO 15076-1 §7.2). The fields checked are:
/// <list type="bullet">
///   <item>bytes 36–39: file signature <c>acsp</c> (sanity check; if absent or profile shorter than
///   128 bytes, the profile is skipped defensively — no spurious finding)</item>
///   <item>byte 8: ICC major version (treat the raw byte value as the integer major version;
///   version &lt; 5 ⇔ byte[8] &lt; 5)</item>
///   <item>bytes 12–15: profile/device class (4 ASCII characters)</item>
///   <item>bytes 16–19: data colour space (4 ASCII characters, space-padded)</item>
/// </list>
/// </para>
/// <para>
/// <b>Scoping (derived empirically from veraPDF 1.30.2):</b> veraPDF checks only ICCBased colour
/// spaces that are <em>selected</em> by page content via a <c>cs</c> or <c>CS</c> operator and
/// resolved through the page's <c>/Resources /ColorSpace</c> dictionary — it does not flag a
/// colour space that is merely present in resources but never used. This rule matches that behaviour
/// to avoid false positives: only colour spaces whose resource key appears in
/// <see cref="ContentStreamUsage.Analyze"/>'s <c>SelectedColorSpaces</c> set are validated.
/// </para>
/// <para>
/// <b>Stream decoding:</b> ICCBased streams are typically <c>FlateDecode</c>-compressed; the ICC
/// profile bytes are the decoded stream data, obtained via <see cref="PreflightContext.DecodeStream"/>.
/// </para>
/// <para>
/// <b>Defensive design:</b> a profile shorter than 128 bytes, missing the <c>acsp</c> signature,
/// or otherwise undecodable produces no finding. Each offending profile object is reported at most
/// once (deduplicated by stream object number) even when the colour space is referenced by multiple
/// pages. The §6.2.3 <c>DestOutputProfile</c> rule already checks the output-intent ICC stream;
/// that stream is not an ICCBased colour space and is not in scope here.
/// </para>
/// <para>
/// Authored from ISO 19005-2:2011, §6.2.4.2 and ISO 15076-1 (ICC.1). Clean-room: derived from
/// the specification text and empirical veraPDF oracle probing, not from any third-party validation
/// profile.
/// </para>
/// </remarks>
internal sealed class IccProfileRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.4.2-1";

    public string Clause => "ISO 19005-2:2011, 6.2.4.2";

    private static readonly PdfName _colorSpace = new("ColorSpace");

    // Allowed device-class values (4-byte ASCII, ISO 15076-1 §7.2.4).
    private static readonly HashSet<string> _allowedDeviceClasses = new(StringComparer.Ordinal)
    {
        "prtr", "mntr", "scnr", "spac",
    };

    // Allowed data colour-space values (4-byte ASCII, space-padded, ISO 15076-1 §7.2.4).
    private static readonly HashSet<string> _allowedColorSpaces = new(StringComparer.Ordinal)
    {
        "RGB ", "CMYK", "GRAY", "Lab ",
    };

    public void Evaluate(PreflightContext context)
    {
        // Profile streams may be shared across pages; report each offending stream once.
        var reported = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_colorSpace)) is not PdfDictionary colorSpaces)
                continue;

            // Scope to colour spaces that are selected by page content (cs/CS operators).
            // veraPDF does not flag an ICCBased space that is present but not used — verified
            // empirically against veraPDF 1.30.2 (STEP-0 probes 13–14).
            var selected = ContentStreamUsage.Analyze(context, page).SelectedColorSpaces;
            if (selected.Count == 0)
                continue;

            foreach (var entry in colorSpaces.Entries)
            {
                if (!selected.Contains(entry.Key.Value))
                    continue;

                // An ICCBased colour space is the 2-element array [/ICCBased <stream>].
                if (context.Resolve(entry.Value) is not PdfArray csArray || csArray.Count < 2)
                    continue;
                if (context.Resolve(csArray[0]) is not PdfName { Value: "ICCBased" })
                    continue;

                // The second element of the array is an indirect reference to the ICC stream.
                var streamRef = csArray[1];
                if (streamRef is not PdfIndirectReference iccRef)
                    continue;

                // Deduplicate: each profile stream is checked and reported at most once.
                if (!reported.Add(iccRef.ObjectNumber))
                    continue;

                var stream = context.ResolveStream(streamRef);
                if (stream is null)
                    continue;

                // Decode the stream — ICCBased streams are typically FlateDecode-compressed.
                var icc = context.DecodeStream(stream);

                // Defensive: skip profiles that cannot be decoded or are too short.
                // An undecodable or truncated profile is not flagged — better to under-detect
                // than to false-positive on something we cannot reliably parse.
                if (icc is null || icc.Length < 128)
                    continue;

                // Sanity check: bytes 36–39 shall be the ICC file signature 'acsp'
                // (ISO 15076-1 §7.2.4). If absent, the bytes are not a recognisable ICC profile.
                if (icc[36] != (byte)'a' || icc[37] != (byte)'c'
                    || icc[38] != (byte)'s' || icc[39] != (byte)'p')
                    continue;

                // byte[8]: ICC major version. The raw byte value equals the major version integer
                // (e.g. 0x02 = version 2, 0x04 = version 4, 0x05 = version 5). Confirmed
                // empirically: byte[8]=4 → PASS; byte[8]=5 → FAIL (STEP-0 probes 10–11).
                var majorVersion = icc[8];

                // bytes 12–15: profile/device class (4 ASCII characters).
                var deviceClass = System.Text.Encoding.Latin1.GetString(icc, 12, 4);

                // bytes 16–19: data colour space (4 ASCII characters, space-padded).
                var dataColorSpace = System.Text.Encoding.Latin1.GetString(icc, 16, 4);

                // The test condition (from veraPDF 1.30.2 profile): the conjunction of all three
                // constraints must hold; if any fails, the profile is non-conformant.
                var isValid =
                    _allowedDeviceClasses.Contains(deviceClass)
                    && _allowedColorSpaces.Contains(dataColorSpace)
                    && majorVersion < 5;

                if (!isValid)
                {
                    context.Report(
                        RuleId,
                        Clause,
                        PreflightSeverity.Error,
                        $"The embedded ICC profile (Device Class = {deviceClass.TrimEnd()}, "
                        + $"color space = {dataColorSpace}, version = {majorVersion}.x) "
                        + "is either invalid or does not satisfy PDF 1.7 requirements.",
                        objectRef: $"{iccRef.ObjectNumber} 0 R");
                }
            }
        }
    }
}
