// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.2 / §6.2.11.3.2 / §6.2.11.6 (Font dictionary structure). Every font dictionary
/// — including the descendant CIDFonts of a composite (Type 0) font — shall have a recognised
/// <c>/Subtype</c>; a non-standard simple font shall carry <c>/FirstChar</c>, <c>/LastChar</c>, and a
/// <c>/Widths</c> array of length <c>LastChar − FirstChar + 1</c>; a non-symbolic TrueType font's
/// <c>/Encoding</c> shall be MacRoman or WinAnsi and a symbolic one shall have none; and an embedded
/// Type 2 CIDFont shall carry a <c>/CIDToGIDMap</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.11.2 / 6.2.11.3.2 / 6.2.11.6 and ISO 32000-1:2008, 9.6–9.7.
/// Clean-room: derived from the specification text, not from any third-party validation profile.
/// Every check is cross-validated against veraPDF on real embedded fonts — a hand-built compliant
/// simple TrueType (full DejaVu, WinAnsi, correct widths) anchors the positive path, and single
/// malformations of it (a Widths-length mismatch, a missing or non-WinAnsi/MacRoman Encoding, an
/// Encoding on a symbolic font) reproduce clauses 6.2.11.2-6, 6.2.11.6-2, and 6.2.11.6-3; corrupting
/// a composite font's descendant reproduces 6.2.11.2-2 and 6.2.11.3.2-1.
/// <para>
/// Deferred to the font-<em>program</em> parser: glyph presence (§6.2.11.4.1), glyph-width
/// consistency (§6.2.11.5), and the embedded-cmap requirements (§6.2.11.6 t1/t4). Embedding itself is
/// enforced by <see cref="FontEmbeddingRule"/>.
/// </para>
/// </remarks>
internal sealed class FontStructureRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.2-font-subtype";

    public string Clause => "ISO 19005-2:2011, 6.2.11.2";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _firstChar = new("FirstChar");
    private static readonly PdfName _lastChar = new("LastChar");
    private static readonly PdfName _widths = new("Widths");
    private static readonly PdfName _flags = new("Flags");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _baseEncoding = new("BaseEncoding");

    private const int SymbolicFlag = 1 << 2; // ISO 32000-1 Table 121, bit 3 (Symbolic).

    private static readonly HashSet<string> _validSubtypes = new(StringComparer.Ordinal)
    {
        "Type0", "Type1", "MMType1", "Type3", "TrueType", "CIDFontType0", "CIDFontType2",
    };

    private static readonly HashSet<string> _simpleSubtypes = new(StringComparer.Ordinal)
    {
        "Type1", "MMType1", "TrueType",
    };

    private static readonly HashSet<string> _trueTypeEncodings = new(StringComparer.Ordinal)
    {
        "MacRomanEncoding", "WinAnsiEncoding",
    };

    // The 14 standard fonts are exempt from the FirstChar/LastChar/Widths requirements (§6.2.11.2).
    private static readonly HashSet<string> _standard14 = new(StringComparer.Ordinal)
    {
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats",
    };

    public void Evaluate(PreflightContext context)
    {
        var checkedFonts = new HashSet<int>();

        foreach (var font in context.EnumerateFonts())
        {
            CheckSubtype(context, font);
            CheckSimpleFont(context, font);

            // A composite font's descendant CIDFont is itself a font dictionary (§6.2.11.2 applies).
            if (context.Resolve(font.Get(PdfName.Subtype)) is PdfName { Value: "Type0" }
                && context.Resolve(font.Get(_descendantFonts)) is PdfArray descendants)
            {
                for (var i = 0; i < descendants.Count; i++)
                {
                    if (descendants[i] is PdfIndirectReference r && !checkedFonts.Add(r.ObjectNumber))
                        continue;
                    if (context.Resolve(descendants[i]) is PdfDictionary cidFont)
                    {
                        CheckSubtype(context, cidFont);
                        CheckCidToGidMap(context, cidFont);
                    }
                }
            }
        }
    }

    private void CheckSimpleFont(PreflightContext context, PdfDictionary font)
    {
        var subtype = (context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value;
        if (subtype is null || !_simpleSubtypes.Contains(subtype))
            return;

        var baseFont = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
        CheckSimpleFontMetrics(context, font, subtype, baseFont);
        if (subtype == "TrueType")
            CheckTrueTypeEncoding(context, font);
    }

    // §6.2.11.2-4/-5/-6: a non-standard simple font shall carry FirstChar, LastChar, and a Widths
    // array whose length is LastChar − FirstChar + 1.
    private void CheckSimpleFontMetrics(PreflightContext context, PdfDictionary font, string subtype, string? baseFont)
    {
        if (baseFont is not null && _standard14.Contains(StripSubsetPrefix(baseFont)))
            return; // a standard-14 font is exempt (and is separately required to embed, §6.3).

        var first = context.Resolve(font.Get(_firstChar)) as PdfInteger;
        var last = context.Resolve(font.Get(_lastChar)) as PdfInteger;
        var widths = context.Resolve(font.Get(_widths)) as PdfArray;
        if (first is null || last is null || widths is null)
        {
            Report(context, "6.2.11.2-metrics", Clause,
                $"The simple font /{baseFont ?? subtype} is missing /FirstChar, /LastChar, or /Widths.");
            return;
        }

        var expected = last.Value - first.Value + 1;
        if (expected < 0 || widths.Count != expected)
            Report(context, "6.2.11.2-widths", Clause,
                $"The simple font /{baseFont ?? subtype} has a /Widths array of {widths.Count} entries; "
                + $"§6.2.11.2 requires LastChar − FirstChar + 1 = {expected}.");
    }

    // §6.2.11.6-2/-3: a non-symbolic TrueType font's /Encoding shall be MacRomanEncoding or
    // WinAnsiEncoding (a name, or an encoding dictionary with such a BaseEncoding); a symbolic
    // TrueType font shall not carry an /Encoding entry at all.
    private void CheckTrueTypeEncoding(PreflightContext context, PdfDictionary font)
    {
        var symbolic = context.Resolve(font.Get(_fontDescriptor)) is PdfDictionary descriptor
            && context.Resolve(descriptor.Get(_flags)) is PdfInteger flags
            && (flags.Value & SymbolicFlag) != 0;

        var encoding = context.Resolve(font.Get(_encoding));

        if (symbolic)
        {
            if (font.Get(_encoding) is not null)
                Report(context, "6.2.11.6-symbolic-encoding", "ISO 19005-2:2011, 6.2.11.6",
                    "A symbolic TrueType font carries an /Encoding entry, which is not permitted in PDF/A-2.");
            return;
        }

        // Non-symbolic: the (base) encoding name shall be MacRomanEncoding or WinAnsiEncoding.
        var encodingName = encoding switch
        {
            PdfName name => name.Value,
            PdfDictionary dict => (context.Resolve(dict.Get(_baseEncoding)) as PdfName)?.Value,
            _ => null,
        };
        if (encodingName is null || !_trueTypeEncodings.Contains(encodingName))
            Report(context, "6.2.11.6-nonsymbolic-encoding", "ISO 19005-2:2011, 6.2.11.6",
                "A non-symbolic TrueType font shall use MacRomanEncoding or WinAnsiEncoding "
                + $"({(encodingName is null ? "no usable Encoding" : $"/{encodingName}")} found).");
    }

    // §6.2.11.3.2: an embedded Type 2 CIDFont shall carry a /CIDToGIDMap (Identity or a stream).
    private void CheckCidToGidMap(PreflightContext context, PdfDictionary cidFont)
    {
        if (context.Resolve(cidFont.Get(PdfName.Subtype)) is not PdfName { Value: "CIDFontType2" })
            return;
        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor
            || context.ResolveStream(descriptor.Get(_fontFile2)) is null)
            return; // only embedded CIDFontType2 fonts are constrained here (embedding is §6.3).
        if (cidFont.Get(_cidToGidMap) is null)
            context.Report(
                "ISO19005-2:6.2.11.3.2-cidtogidmap",
                "ISO 19005-2:2011, 6.2.11.3.2",
                PreflightSeverity.Error,
                "An embedded CIDFontType2 is missing the required /CIDToGIDMap entry.");
    }

    private void CheckSubtype(PreflightContext context, PdfDictionary font)
    {
        var subtype = (context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value;
        if (subtype is null || !_validSubtypes.Contains(subtype))
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "A font dictionary has an invalid or missing /Subtype "
                + $"({(subtype is null ? "absent" : $"/{subtype}")}); it shall be one of the font subtypes "
                + "defined in ISO 32000-1.");
    }

    // A subset font name is "ABCDEF+ActualName"; strip the tag for the standard-14 comparison.
    private static string StripSubsetPrefix(string baseFont)
        => baseFont.Length > 7 && baseFont[6] == '+' ? baseFont[7..] : baseFont;

    private static void Report(PreflightContext context, string ruleSuffix, string clause, string message)
        => context.Report($"ISO19005-2:{ruleSuffix}", clause, PreflightSeverity.Error, message);
}
