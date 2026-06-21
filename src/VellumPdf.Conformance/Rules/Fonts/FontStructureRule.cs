// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.2 / §6.2.11.3.1 / §6.2.11.3.2 / §6.2.11.6 (Font dictionary structure). Every
/// font dictionary — including the descendant CIDFonts of a composite (Type 0) font — shall carry
/// <c>/Type /Font</c>, a <c>/BaseFont</c> name (except a Type 3 font), and a recognised <c>/Subtype</c>;
/// a non-standard simple font shall carry <c>/FirstChar</c>, <c>/LastChar</c>, and a <c>/Widths</c>
/// array of length <c>LastChar − FirstChar + 1</c>; an embedded <c>/FontFile3</c> program's
/// <c>/Subtype</c> shall be <c>Type1C</c>, <c>CIDFontType0C</c>, or <c>OpenType</c>; a non-symbolic
/// TrueType font's <c>/Encoding</c> shall be MacRoman or WinAnsi and a symbolic one shall have none;
/// an embedded subset Type 1 font's <c>/CharSet</c> (when present) shall list every glyph in the
/// program; an embedded Type 2 CIDFont shall carry a <c>/CIDToGIDMap</c>; and a composite font's
/// CIDFont <c>/CIDSystemInfo</c> and its CMap's <c>/CIDSystemInfo</c> must be compatible.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.11.2 / 6.2.11.3.1 / 6.2.11.3.2 / 6.2.11.6 and ISO 32000-1:2008,
/// 9.6–9.7.
/// Clean-room: derived from the specification text, not from any third-party validation profile.
/// Every check is cross-validated against veraPDF on real embedded fonts — a hand-built compliant
/// simple TrueType (full DejaVu, WinAnsi, correct widths) anchors the positive path, and single
/// malformations of it (a Widths-length mismatch, a missing or non-WinAnsi/MacRoman Encoding, an
/// Encoding on a symbolic font) reproduce clauses 6.2.11.2-6, 6.2.11.6-2, and 6.2.11.6-3; corrupting
/// a composite font's descendant reproduces 6.2.11.2-2 and 6.2.11.3.2-1.
/// <para>
/// Only fonts that a page actually selects via a <c>Tf</c> operator in its content stream are
/// validated (matching veraPDF, which validates only the current graphics state — issue #118).
/// Fonts present in <c>/Resources /Font</c> but never selected are not checked. Fonts used only
/// within form XObjects, Type 3 glyph procedures, or annotation appearance streams are a deferred
/// edge and are not yet detected here.
/// </para>
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
    private static readonly PdfName _fontFile = new("FontFile");
    private static readonly PdfName _fontFile2 = new("FontFile2");
    private static readonly PdfName _fontFile3 = new("FontFile3");
    private static readonly PdfName _charSet = new("CharSet");
    private static readonly PdfName _length1 = new("Length1");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _cidSet = new("CIDSet");
    private static readonly PdfName _firstChar = new("FirstChar");
    private static readonly PdfName _lastChar = new("LastChar");
    private static readonly PdfName _widths = new("Widths");
    private static readonly PdfName _flags = new("Flags");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _baseEncoding = new("BaseEncoding");
    private static readonly PdfName _cidSystemInfo = new("CIDSystemInfo");
    private static readonly PdfName _registry = new("Registry");
    private static readonly PdfName _ordering = new("Ordering");
    private static readonly PdfName _supplement = new("Supplement");
    private static readonly PdfName _cmapName = new("CMapName");

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

    // ISO 32000-1 Table 126: the only /Subtype values permitted on an embedded FontFile3 (CFF /
    // OpenType) program stream (§6.2.11.2-7).
    private static readonly HashSet<string> _fontFile3Subtypes = new(StringComparer.Ordinal)
    {
        "Type1C", "CIDFontType0C", "OpenType",
    };

    // ISO 32000-1 Table 118: the predefined CMaps a composite font's /Encoding may name without
    // embedding the CMap (§6.2.11.3.3-1). Any other name must instead be an embedded CMap stream.
    private static readonly HashSet<string> _predefinedCMaps = new(StringComparer.Ordinal)
    {
        "Identity-H", "Identity-V",
        "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V",
        "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V", "UniGB-UCS2-H", "UniGB-UCS2-V",
        "UniGB-UTF16-H", "UniGB-UTF16-V",
        "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V",
        "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V",
        "UniCNS-UTF16-H", "UniCNS-UTF16-V",
        "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H",
        "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V",
        "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V",
        "UniJIS-UTF16-H", "UniJIS-UTF16-V",
        "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V",
        "KSCpc-EUC-H", "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
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

        foreach (var font in context.EnumerateUsedFonts())
        {
            CheckType(context, font);
            CheckBaseFont(context, font);
            CheckSubtype(context, font);
            CheckSimpleFont(context, font);
            CheckFontFile3Subtype(context, font);
            CheckCMapEncoding(context, font);
            CheckCidSystemInfo(context, font);

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
                        CheckType(context, cidFont);
                        CheckBaseFont(context, cidFont);
                        CheckSubtype(context, cidFont);
                        CheckCidToGidMap(context, cidFont);
                        CheckCidSet(context, cidFont);
                        CheckFontFile3Subtype(context, cidFont);
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
        else
            CheckType1CharSet(context, font, subtype, baseFont);
    }

    // §6.2.11.4.2-1: if an embedded subset Type 1 font's FontDescriptor carries a /CharSet string, it
    // shall list the character names of every glyph present in the font program. The program glyph
    // names come from the eexec-decrypted /CharStrings via the in-process Type1 parser; .notdef is not
    // required to be listed (subsetters vary on whether they include it).
    private void CheckType1CharSet(PreflightContext context, PdfDictionary font, string subtype, string? baseFont)
    {
        if (subtype is not ("Type1" or "MMType1"))
            return;
        if (baseFont is null || !IsSubsetTag(baseFont))
            return; // the CharSet-completeness constraint applies only to subset fonts.
        if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return;
        if (CharSetNames(context, descriptor) is not { } declared)
            return; // /CharSet is optional.
        if (context.ResolveStream(descriptor.Get(_fontFile)) is not { } fontFile
            || context.DecodeStream(fontFile) is not { } programBytes)
            return; // only an embedded Type 1 program (a /FontFile) is constrained here.
        var length1 = context.Resolve(fontFile.Dictionary.Get(_length1)) is PdfInteger l ? (int)l.Value : -1;
        if (Type1Glyphs.TryEnumerate(programBytes, length1) is not { Count: > 0 } programGlyphs)
            return; // an unparseable program degrades to no finding rather than a false positive.

        foreach (var glyph in programGlyphs)
            if (glyph != ".notdef" && !declared.Contains(glyph))
            {
                Report(context, "6.2.11.4.2-charset", "ISO 19005-2:2011, 6.2.11.4.2",
                    "An embedded subset Type 1 font's /CharSet does not list every glyph present in the "
                    + $"font program (e.g. /{glyph} is missing), which §6.2.11.4.2 requires.");
                return;
            }
    }

    // Parses a FontDescriptor /CharSet string ("/name/name…") into its set of glyph names, or null
    // when no /CharSet is present.
    private static HashSet<string>? CharSetNames(PreflightContext context, PdfDictionary descriptor)
    {
        var raw = context.Resolve(descriptor.Get(_charSet)) switch
        {
            PdfLiteralString s => s.Bytes,
            PdfHexString h => h.Bytes,
            _ => (ReadOnlyMemory<byte>?)null,
        };
        if (raw is not { } bytes)
            return null;
        var text = System.Text.Encoding.Latin1.GetString(bytes.Span);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
            set.Add(part.Trim());
        return set;
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

            // §6.2.11.6-4: the embedded program's cmap shall contain exactly one subtable, or contain
            // the Microsoft Symbol (3,0) encoding.
            if (EmbeddedTrueTypeProgram(context, font) is { } program
                && program.CmapSubtableCount != 1 && !program.HasSymbolCmap)
                Report(context, "6.2.11.6-symbolic-cmap", "ISO 19005-2:2011, 6.2.11.6",
                    "A symbolic TrueType font's embedded cmap shall have exactly one subtable or include "
                    + "the Microsoft Symbol (3,0) encoding.");
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

        // §6.2.11.6-1: the embedded program's cmap shall provide the subtables needed for glyph
        // lookup — at least one subtable, and more than one when a (3,0) symbol subtable is present
        // (the symbol subtable alone does not suffice for a non-symbolic font).
        if (EmbeddedTrueTypeProgram(context, font) is { } prog
            && !(prog.HasSymbolCmap ? prog.CmapSubtableCount > 1 : prog.CmapSubtableCount > 0))
            Report(context, "6.2.11.6-nonsymbolic-cmap", "ISO 19005-2:2011, 6.2.11.6",
                "A non-symbolic TrueType font's embedded cmap does not provide the encoding subtables "
                + "required for glyph lookup.");
    }

    // §6.2.11.4.2-2: if an embedded subset CIDFontType2 carries a /CIDSet, that bitmap shall identify
    // exactly the CIDs present in the font program. With an Identity /CIDToGIDMap a CID equals its
    // glyph index, so the present CIDs are 0..NumGlyphs−1: bits 0..NumGlyphs−1 shall be set and every
    // higher bit clear. A non-Identity (stream) /CIDToGIDMap would make the present-CID set depend on
    // that map, so the check is skipped there (deferred) to avoid a false positive.
    private void CheckCidSet(PreflightContext context, PdfDictionary cidFont)
    {
        if (context.Resolve(cidFont.Get(PdfName.Subtype)) is not PdfName { Value: "CIDFontType2" })
            return;
        if ((context.Resolve(cidFont.Get(PdfName.BaseFont)) as PdfName)?.Value is not { } baseFont
            || !IsSubsetTag(baseFont))
            return; // the CIDSet-completeness constraint applies only to subset fonts.
        if (context.Resolve(cidFont.Get(_cidToGidMap)) is { } map && map is not PdfName { Value: "Identity" })
            return; // a stream CIDToGIDMap is deferred.
        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor
            || context.ResolveStream(descriptor.Get(_cidSet)) is not { } cidSetStream)
            return; // /CIDSet is optional.
        if (EmbeddedTrueTypeProgram(context, cidFont) is not { } program
            || context.DecodeStream(cidSetStream) is not { } cidSet)
            return;

        if (!CidSetListsAllGlyphs(cidSet, program.NumGlyphs))
            Report(context, "6.2.11.4.2-cidset", "ISO 19005-2:2011, 6.2.11.4.2",
                "An embedded subset CIDFontType2's /CIDSet does not identify exactly the CIDs present in "
                + "the font program (§6.2.11.4.2).");
    }

    // True when every CID in 0..numGlyphs−1 has its bit set and every bit beyond is clear.
    private static bool CidSetListsAllGlyphs(byte[] cidSet, int numGlyphs)
    {
        var totalBits = cidSet.Length * 8;
        for (var i = 0; i < numGlyphs; i++)
            if (i >= totalBits || (cidSet[i / 8] & (0x80 >> (i % 8))) == 0)
                return false;
        for (var i = numGlyphs; i < totalBits; i++)
            if ((cidSet[i / 8] & (0x80 >> (i % 8))) != 0)
                return false;
        return true;
    }

    // A subset font name is "ABCDEF+ActualName": six uppercase letters then '+'.
    private static bool IsSubsetTag(string baseFont)
    {
        if (baseFont.Length < 7 || baseFont[6] != '+')
            return false;
        for (var i = 0; i < 6; i++)
            if (baseFont[i] is < 'A' or > 'Z')
                return false;
        return true;
    }

    // The parsed sfnt program of an embedded TrueType font (its /FontDescriptor /FontFile2), or null
    // when the font is not embedded or the program cannot be parsed.
    private static SfntMetrics? EmbeddedTrueTypeProgram(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (context.ResolveStream(descriptor.Get(_fontFile2)) is not { } stream)
            return null;
        var bytes = context.DecodeStream(stream);
        return bytes is null ? null : SfntMetrics.TryParse(bytes);
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

    // §6.2.11.2-1: every font dictionary (including a composite font's descendant CIDFont) shall
    // carry /Type /Font.
    private void CheckType(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(PdfName.Type)) is not PdfName { Value: "Font" })
            Report(context, "6.2.11.2-type", Clause,
                "A font dictionary has a missing or invalid /Type; it shall be /Font.");
    }

    // §6.2.11.2-3: every font dictionary other than a Type 3 font shall carry a /BaseFont
    // (PostScript) name.
    private void CheckBaseFont(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(PdfName.Subtype)) is PdfName { Value: "Type3" })
            return;
        if (context.Resolve(font.Get(PdfName.BaseFont)) is not PdfName)
            Report(context, "6.2.11.2-basefont", Clause,
                "A non-Type3 font dictionary is missing the required /BaseFont (PostScript) name.");
    }

    // §6.2.11.2-7: an embedded FontFile3 program's /Subtype shall be Type1C (CFF Type 1),
    // CIDFontType0C (CFF CIDFont), or OpenType. A FontFile3 with no /Subtype, or a font that embeds
    // its program via FontFile/FontFile2 instead, is not constrained here.
    private void CheckFontFile3Subtype(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return;
        if (context.ResolveStream(descriptor.Get(_fontFile3)) is not { } fontFile3)
            return;
        if ((context.Resolve(fontFile3.Dictionary.Get(PdfName.Subtype)) as PdfName)?.Value is { } subtype
            && !_fontFile3Subtypes.Contains(subtype))
            Report(context, "6.2.11.2-fontfile3-subtype", Clause,
                $"An embedded FontFile3 font program has /Subtype /{subtype}; PDF/A-2 permits only "
                + "Type1C, CIDFontType0C, or OpenType.");
    }

    // §6.2.11.3.3-1: a composite (Type 0) font's /Encoding shall either name one of the predefined
    // CMaps (ISO 32000-1 Table 118) or be an embedded CMap stream. A non-name /Encoding (a stream or
    // a reference to one) is an embedded CMap and is therefore allowed.
    private void CheckCMapEncoding(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
            return;
        if (context.Resolve(font.Get(_encoding)) is PdfName name && !_predefinedCMaps.Contains(name.Value))
            Report(context, "6.2.11.3.3-cmap-name", "ISO 19005-2:2011, 6.2.11.3.3",
                $"A composite font's /Encoding names the CMap /{name.Value}, which is neither one of the "
                + "predefined CMaps nor an embedded CMap stream.");
    }

    // §6.2.11.3.1-1: the CIDSystemInfo of a composite font's descendant CIDFont and its CMap must be
    // compatible. If /Encoding is Identity-H or Identity-V the check always passes. If /Encoding is any
    // other predefined CMap name (a name that is NOT an indirect reference to a stream) the registry
    // table for the named CMap is not embedded, so this path is deferred — no finding is generated.
    // Only when /Encoding resolves to an embedded CMap stream (an indirect reference) are the two
    // CIDSystemInfo dictionaries compared: Registry and Ordering must be byte-equal, and
    // CIDFont.Supplement must be ≤ CMap.Supplement (all four values must be present).
    // <remarks>
    // Deferred edge: predefined-CMap names other than Identity-H/V (e.g. UniGB-UCS2-H). The rule as
    // implemented therefore covers Identity-H/V (always pass) and embedded-CMap streams (compared).
    // The predefined-name registry table is not in scope for this partial implementation.
    // </remarks>
    private void CheckCidSystemInfo(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
            return;

        var rawEncoding = font.Get(_encoding);
        var encoding = context.Resolve(rawEncoding);

        // Identity-H / Identity-V: always conformant — no check.
        if (encoding is PdfName { Value: "Identity-H" or "Identity-V" })
            return;

        // Any other predefined name (not a stream reference): deferred, no finding.
        if (encoding is PdfName)
            return;

        // Only proceed if /Encoding resolves to an embedded CMap stream.
        if (context.ResolveStream(rawEncoding) is not { } cmapStream)
            return;

        // An embedded CMap whose own /CMapName is Identity-H/V is exempt too — veraPDF keys the
        // exemption on the CMap name, not the /Encoding reference (avoids a false positive on the
        // unusual case of an embedded Identity CMap).
        if (context.Resolve(cmapStream.Dictionary.Get(_cmapName)) is PdfName { Value: "Identity-H" or "Identity-V" })
            return;

        // Read CIDSystemInfo from the CMap stream's dictionary.
        if (context.Resolve(cmapStream.Dictionary.Get(_cidSystemInfo)) is not PdfDictionary cmapSi)
            return;
        var cmapRegistry = PdfStringToLatin1(context, cmapSi.Get(_registry));
        var cmapOrdering = PdfStringToLatin1(context, cmapSi.Get(_ordering));
        var cmapSupplement = (context.Resolve(cmapSi.Get(_supplement)) as PdfInteger)?.Value;

        // Get the descendant CIDFont's CIDSystemInfo.
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont)
            return;
        if (context.Resolve(cidFont.Get(_cidSystemInfo)) is not PdfDictionary cidSi)
            return;
        var cidRegistry = PdfStringToLatin1(context, cidSi.Get(_registry));
        var cidOrdering = PdfStringToLatin1(context, cidSi.Get(_ordering));
        var cidSupplement = (context.Resolve(cidSi.Get(_supplement)) as PdfInteger)?.Value;

        // All four required values must be present.
        if (cmapRegistry is null || cmapOrdering is null || cmapSupplement is null
            || cidRegistry is null || cidOrdering is null || cidSupplement is null)
        {
            Report(context, "6.2.11.3.1-cidsysteminfo", "ISO 19005-2:2011, 6.2.11.3.1",
                "A composite font's CIDSystemInfo or its CMap's CIDSystemInfo is missing a required "
                + "/Registry, /Ordering, or /Supplement entry.");
            return;
        }

        if (!string.Equals(cidRegistry, cmapRegistry, StringComparison.Ordinal)
            || !string.Equals(cidOrdering, cmapOrdering, StringComparison.Ordinal)
            || cidSupplement.Value > cmapSupplement.Value)
        {
            Report(context, "6.2.11.3.1-cidsysteminfo", "ISO 19005-2:2011, 6.2.11.3.1",
                "CIDSystemInfo entries of the CIDFont and CMap dictionaries of a Type 0 font are not "
                + $"compatible (CIDSystemInfo Ordering = {cidOrdering}, CMap Ordering = {cmapOrdering}, "
                + $"CIDSystemInfo Registry = {cidRegistry}, CMap Registry = {cmapRegistry}, "
                + $"CIDSystemInfo Supplement = {cidSupplement}, CMap Supplement = {cmapSupplement}).");
        }
    }

    // Decodes a PDF string object (literal or hex) to a Latin-1 string, or null when absent/not a string.
    private string? PdfStringToLatin1(PreflightContext context, PdfObject? raw)
    {
        var bytes = context.Resolve(raw) switch
        {
            PdfLiteralString s => s.Bytes,
            PdfHexString h => h.Bytes,
            _ => (ReadOnlyMemory<byte>?)null,
        };
        return bytes is { } b ? System.Text.Encoding.Latin1.GetString(b.Span) : null;
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
