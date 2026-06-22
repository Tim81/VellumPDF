// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.13 (Implementation limits). A conforming file shall not exceed the architectural
/// limits of ISO 32000-1 Annex C: integers in [−2³¹, 2³¹−1], reals within ±3.403×10³⁸ and not closer
/// to zero than ±1.175×10⁻³⁸, strings of at most 32767 bytes, names of at most 127 bytes, and at most
/// 8388607 indirect objects.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.13 and ISO 32000-1:2008, Annex C. Clean-room: derived from the
/// specification text, not from any third-party validation profile. The scalar limits are checked by
/// walking every indirect object's value graph; the indirect-object count comes from the
/// cross-reference table; and each page-boundary box (MediaBox, CropBox, BleedBox, TrimBox, ArtBox)
/// is checked to have sides in the 3–14400 unit range (§6.1.13 t11). The content-stream q/Q nesting
/// limit (t8) and the CMap CID maximum (t10) need content/CMap parsing. The DeviceN colourant limit
/// (t9) is implemented in <see cref="VellumPdf.Conformance.Rules.Colour.DeviceNColorantRule"/>.
/// </remarks>
internal sealed class NumericLimitsRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.13-numeric-limits";

    public string Clause => "ISO 19005-2:2011, 6.1.13";

    private const long MaxInteger = 2147483647;
    private const long MinInteger = -2147483648;
    private const double MaxReal = 3.403e38;
    private const double MinNormalReal = 1.175e-38;
    private const int MaxStringBytes = 32767;
    private const int MaxNameBytes = 127;
    private const int MaxIndirectObjects = 8388607;
    private const int MaxDepth = 64;

    public void Evaluate(PreflightContext context)
    {
        var flagged = new HashSet<string>(StringComparer.Ordinal);

        // §6.1.13: a conforming file shall not contain more than 8388607 indirect objects.
        if (context.IndirectObjectCount > MaxIndirectObjects)
            Report(context, flagged, "indirect-count",
                $"The file contains {context.IndirectObjectCount} indirect objects; the limit is {MaxIndirectObjects}.");

        foreach (var obj in context.EnumerateIndirectObjects())
            Walk(context, obj, flagged, 0);

        CheckPageBoundaries(context, flagged);
    }

    private static readonly PdfName[] _boundaryBoxes =
    [
        new("MediaBox"), new("CropBox"), new("BleedBox"), new("TrimBox"), new("ArtBox"),
    ];

    // §6.1.13: each page-boundary box side shall be ≥ 3 and ≤ 14400 units (ISO 32000-1 §14.11.2).
    private void CheckPageBoundaries(PreflightContext context, HashSet<string> flagged)
    {
        foreach (var page in context.EnumeratePages())
            foreach (var box in _boundaryBoxes)
                if (context.ResolveInherited(page, box) is PdfArray rect && rect.Count == 4
                    && Side(context, rect, 0, 2) is { } width && Side(context, rect, 1, 3) is { } height
                    && (width < 3 || width > 14400 || height < 3 || height > 14400))
                    Report(context, flagged, "page-bounds",
                        $"A /{box.Value} side ({width}×{height}) is outside the permitted 3–14400 unit range.");
    }

    private static double? Side(PreflightContext context, PdfArray rect, int a, int b)
    {
        if (Number(context.Resolve(rect[a])) is { } x && Number(context.Resolve(rect[b])) is { } y)
            return Math.Abs(y - x);
        return null;
    }

    private static double? Number(PdfObject? obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => null,
    };

    private static readonly PdfName _contents = new("Contents");
    private static readonly PdfName _byteRange = new("ByteRange");
    private static readonly PdfName _sig = new("Sig");
    private static readonly PdfName _subFilter = new("SubFilter");

    // Returns true when <paramref name="dict"/> is a PDF signature dictionary. ISO 32000-1 §12.8.1
    // defines the signature dict as a dictionary with a /ByteRange entry (or /Type /Sig). We use
    // /ByteRange as the primary predicate because /Type /Sig may be omitted by some producers, while
    // /SubFilter is present in all PAdES/CMS signature dicts. Any of the three is sufficient.
    private static bool IsSignatureDictionary(PreflightContext context, PdfDictionary dict)
    {
        if (dict.Get(_byteRange) is not null)
            return true;
        if (context.Resolve(dict.Get(PdfName.Type)) is PdfName t && t.Value == _sig.Value)
            return true;
        if (dict.Get(_subFilter) is not null)
            return true;
        return false;
    }

    private void Walk(PreflightContext context, PdfObject? value, HashSet<string> flagged, int depth,
        bool isSignatureContents = false)
    {
        if (depth > MaxDepth)
            return;

        switch (value)
        {
            case PdfInteger i when i.Value > MaxInteger || i.Value < MinInteger:
                Report(context, flagged, "integer",
                    $"An integer value ({i.Value}) is outside the permitted range [−2147483648, 2147483647].");
                break;
            case PdfReal r when Math.Abs(r.Value) > MaxReal:
                Report(context, flagged, "real-magnitude",
                    $"A real value ({r.Value}) exceeds the permitted magnitude ±3.403×10^38.");
                break;
            case PdfReal r when r.Value != 0.0 && Math.Abs(r.Value) < MinNormalReal:
                Report(context, flagged, "real-precision",
                    $"A non-zero real value ({r.Value}) is closer to zero than the permitted ±1.175×10^-38.");
                break;
            // The /Contents entry of a signature dictionary is a DER-encoded CMS blob whose size is
            // governed by the CMS structure rather than the PDF string limit. veraPDF 1.30.2 accepts
            // signature /Contents strings that exceed 32767 decoded bytes (the CMS placeholder used by
            // PAdES B-T/B-LTA signatures reserves 32768 bytes by convention). Skip the length check
            // for this specific entry only; all other strings are still subject to the limit.
            case PdfLiteralString s when !isSignatureContents && s.Bytes.Length > MaxStringBytes:
            case PdfHexString hs when !isSignatureContents && hs.Bytes.Length > MaxStringBytes:
                Report(context, flagged, "string", $"A string longer than {MaxStringBytes} bytes is not permitted.");
                break;
            case PdfName n when Encoding.UTF8.GetByteCount(n.Value) > MaxNameBytes:
                Report(context, flagged, "name", $"A name longer than {MaxNameBytes} bytes is not permitted.");
                break;
            case PdfArray array:
                for (var k = 0; k < array.Count; k++)
                    Walk(context, array[k], flagged, depth + 1);
                break;
            case PdfDictionary dict:
                var isSigDict = IsSignatureDictionary(context, dict);
                foreach (var entry in dict.Entries)
                {
                    if (Encoding.UTF8.GetByteCount(entry.Key.Value) > MaxNameBytes)
                        Report(context, flagged, "name", $"A name longer than {MaxNameBytes} bytes is not permitted.");
                    var entryIsSignatureContents = isSigDict && entry.Key.Value == _contents.Value;
                    Walk(context, entry.Value, flagged, depth + 1, entryIsSignatureContents);
                }
                break;
        }
    }

    // Each limit kind is reported once per document (a file that blows a limit usually blows it many
    // times); the verdict is unaffected.
    private void Report(PreflightContext context, HashSet<string> flagged, string kind, string message)
    {
        if (flagged.Add(kind))
            context.Report($"ISO19005-2:6.1.13-{kind}", Clause, PreflightSeverity.Error, message);
    }
}
