// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Core;

namespace VellumPdf.Reader.Fonts;

/// <summary>
/// Decodes a Type1, MMType1 or TrueType simple font (ISO 32000-2 §9.6.5): resolves the font's
/// character-code-to-glyph-name table from its <c>/Encoding</c>, then to Unicode through the Adobe
/// Glyph List, and its per-code advance widths from <c>/Widths</c> or, for a standard 14 font with
/// none, <see cref="SymbolFontMetrics"/>' own name-keyed AFM widths.
/// </summary>
/// <remarks>
/// §9.6.5.4 gives TrueType fonts their own base-encoding rule, distinct from Type1's (§9.6.5.2):
/// initialise from the named encoding or <c>/Differences</c>' own <c>/BaseEncoding</c>, then fill
/// anything still undefined from StandardEncoding. This reader applies that same rule uniformly to
/// Type1, MMType1 and TrueType alike, rather than branching by subtype, since without parsing the
/// font program itself there is no way to tell a Type1 font's built-in encoding from a TrueType
/// one's: both are unavailable data, and the two subclauses converge on the same practical
/// fallback (StandardEncoding for a nonsymbolic font) wherever the font program would
/// otherwise supply an answer this reader cannot. The one step that does branch on the subtype
/// needs no font program: §9.6.5.4's closing rule for a TrueType font whose <c>/Encoding</c> is a
/// dictionary, "Finally, any undefined entries in the table shall be filled using
/// StandardEncoding", is applied after <c>/Differences</c>. The clause's own precondition for
/// building this table at all names "the font descriptor's Nonsymbolic flag" of Table 121, a
/// condition on a descriptor that is present, so this reader runs the fill only when
/// <c>/FontDescriptor</c> and its <c>/Flags</c> both exist. The Table 112 default elsewhere in this
/// class treats a missing descriptor as nonsymbolic, and that default is not extended here: Table
/// 109 makes <c>/FontDescriptor</c> required, optional only in PDF 1.0 to 1.7 for the standard 14,
/// so its absence on any other font is a producer defect, and running a fill the clause conditions
/// on the descriptor would be papering over it. Which flag decides the state follows §9.8.2, "A PDF
/// processor should always check the Symbolic flag to determine whether the state is Symbolic or
/// NonSymbolic": the fill runs when the Symbolic flag is clear. Table 121 requires the two flags to
/// be complementary ("This flag and the Nonsymbolic flag shall not both be set or both be clear"),
/// so the two readings agree on every conformant descriptor and differ only on one whose flags
/// disagree, where the Symbolic flag wins here as it does when the constructor classifies the
/// font at step 3. The only cells the
/// fill can change are the twelve StandardEncoding cells MacRomanEncoding leaves undefined
/// (<c>SimpleFontEncodingsTests</c> pins the set; WinAnsiEncoding leaves none, and a dictionary
/// without <c>/BaseEncoding</c> starts from StandardEncoding already), fewer when
/// <c>/Differences</c> has named one of them, and it is skipped for a
/// <c>/BaseEncoding /MacExpertEncoding</c>, whose table this reader carries as all-null
/// (<see cref="SimpleFontEncodings.MacExpert"/>), so "undefined" cannot be told from "not
/// transcribed". §9.6.5.2 states no such rule for Type1 fonts, and none is applied.
/// <para>
/// Every dictionary entry this class reads, wherever it is read, goes through this class's own
/// <c>Resolve</c> helper before its type is tested (one hop through
/// <see cref="PdfDocumentReader.ResolveValue"/>, with a dangling reference or a resolved
/// <see cref="PdfNull"/>, direct or reached through that hop, normalised to
/// <see langword="null"/> and treated as absent per §7.3.7), with one exception: an
/// element of <c>/Differences</c> is read raw (§9.6.5, step 5 below), because §7.3.10 permits an
/// indirect reference there and this reader deliberately does not resolve one, recording that as a
/// reader limitation (<see cref="PdfReaderDiagnosticCode.FontEncodingMalformed"/>) rather than
/// silently supporting or silently rejecting it.
/// </para>
/// <para>
/// A <c>/Differences</c> name longer than <see cref="AdobeGlyphList.MaxGlyphNameLength"/> reports
/// that same reader limitation and leaves the code undefined, rather than keeping whatever name
/// the base encoding had assigned there: the oversized name still occupies its slot in the
/// sequence (the running code still advances past it), so refusing to record it erases the base
/// encoding's own glyph at that code, it does not preserve it.
/// </para>
/// <para>
/// ISO 32000-2 §9.6.5 states the ordering rule for <c>/Differences</c> sequences verbatim: "These
/// sequences may be specified in any order but shall not overlap." This reader does not enforce
/// that rule: two sequences that assign the same code are applied in array order, so a later one
/// silently overwrites an earlier one's name there, with no diagnostic for the overlap itself.
/// </para>
/// <para>
/// §9.6.5.4 also states, verbatim: "When the font has no Encoding entry, or the font descriptor's
/// Symbolic flag is set (in which case the Encoding entry is ignored), this shall occur: ..." (the
/// steps that follow need a (3, 0) or (1, 0) cmap subtable from the font program, which this
/// reader does not read). For a TrueType font whose descriptor sets the Symbolic flag but which
/// still carries a dictionary <c>/Encoding</c>, this reader honours the entry rather than ignoring
/// it: the clause's own alternative needs font-program data this reader has no access to, and the
/// clause's preceding paragraph already readmits a symbolic font that names MacRomanEncoding or
/// WinAnsiEncoding, so following a dictionary the file went to the trouble of writing is closer to
/// what the font program would have produced than discarding it.
/// </para>
/// </remarks>
internal sealed class SimpleFontReader : PdfFontReader
{
    private static readonly PdfName _fontDescriptorKey = new("FontDescriptor");
    private static readonly PdfName _flagsKey = new("Flags");
    private static readonly PdfName _baseEncodingKey = new("BaseEncoding");
    private static readonly PdfName _differencesKey = new("Differences");
    private static readonly PdfName _firstCharKey = new("FirstChar");
    private static readonly PdfName _lastCharKey = new("LastChar");
    private static readonly PdfName _widthsKey = new("Widths");
    private static readonly PdfName _missingWidthKey = new("MissingWidth");
    private static readonly PdfName _toUnicodeKey = new("ToUnicode");

    private const int SymbolicFlagBit = 4; // bit position 3 (ISO 32000-2 Table 121): value 2^(3-1).

    private readonly DiagnosticSink _sink;
    private readonly int? _objectNumber;
    private readonly int? _generation;
    private readonly int? _pageIndex;

    // Empty placeholders: Populate (or Create's own catch block) always replaces all three with a
    // freshly built 256-element array before any caller can observe them, so allocating that size
    // here too would be a second, wasted allocation per font.
    private string?[] _names = [];
    private double[] _widths = [];
    private string?[] _unicode = [];
    private bool _hasToUnicode;
    private bool _hasAnyMappedCode;

    private bool _reported400;
    private bool _reported401;
    private bool _reported402;
    private bool _reportedNoUnicodeOrUnmapped;

    private SimpleFontReader(DiagnosticSink sink, int? objectNumber, int? generation, int? pageIndex)
    {
        _sink = sink;
        _objectNumber = objectNumber;
        _generation = generation;
        _pageIndex = pageIndex;
    }

    /// <inheritdoc />
    public override bool HasToUnicode => _hasToUnicode;

    /// <summary>
    /// Builds a reader for a Type1, MMType1 or TrueType font dictionary. <paramref name="reader"/>
    /// resolves indirect references (see this class's own remarks); <paramref name="objectNumber"/>
    /// and <paramref name="generation"/>, when the font dictionary itself was reached through one,
    /// and <paramref name="pageIndex"/> are attached to every diagnostic this build reports.
    /// </summary>
    internal static SimpleFontReader Create(
        PdfDocumentReader reader, PdfDictionary fontDict, int? objectNumber, int? generation,
        DiagnosticSink sink, int? pageIndex)
    {
        var self = new SimpleFontReader(sink, objectNumber, generation, pageIndex);
        try
        {
            self.Populate(reader, fontDict);
        }
        catch (InvalidDataException)
        {
            // reader.Resolve throws past MaxResolveDepth (PdfDocumentReader.cs). FontFuzzTests
            // covers a wide range of malformed dictionaries (subtypes, /Encoding shapes including
            // an indirect chain, /Differences, /Widths, /ToUnicode shapes) without reaching this
            // catch; it is not itself the proof this clause is the only thing Populate can throw.
            // GetFontReader's own dedicated regression test drives a MaxResolveDepth chain through
            // a font entry instead, since building one needs an object graph parsed from bytes,
            // not a fuzzed in-memory dictionary.
            self._names = new string?[256];
            self._widths = new double[256];
            self._unicode = new string?[256];
            self._hasToUnicode = false;
            self._hasAnyMappedCode = false;
            self.ReportOnce(ref self._reported400, PdfReaderDiagnosticCode.FontUnreadable,
                "building this font hit the reader's own indirect-object resolution depth limit.");
        }
        return self;
    }

    private void Populate(PdfDocumentReader reader, PdfDictionary fontDict)
    {
        // Step 2: /BaseFont. A name longer than Standard14Names could ever resolve is no more
        // usable than a missing or wrong-typed one: it never selects a standard 14 font, and
        // quoting it whole in a diagnostic would be the unbounded-allocation risk
        // DiagnosticExcerpt exists to avoid, so both report the same 400 message.
        var baseFontResolved = Resolve(reader, fontDict.Get(PdfName.BaseFont));
        string? afmName = null;
        if (baseFontResolved is PdfName baseFontName && baseFontName.Value.Length <= AdobeGlyphList.MaxGlyphNameLength)
        {
            Standard14Names.TryResolve(baseFontName.Value, out var resolved);
            afmName = resolved.Length == 0 ? null : resolved;
        }
        else
        {
            var excerpt = baseFontResolved is PdfName oversized
                ? DiagnosticExcerpt.Quote(oversized.Value)
                : "(not a name)";
            ReportOnce(ref _reported400, PdfReaderDiagnosticCode.FontUnreadable,
                $"has no usable /BaseFont: {excerpt}.");
        }

        // Step 3: symbolic. A /Flags of the wrong type (Table 121 requires an integer; a real
        // is the one a producer is most likely to write by mistake) is treated the same as an
        // absent one, silently: this class has four diagnostic codes, all for the font
        // dictionary itself, and none of them fits a malformed descriptor entry, so a fifth code
        // is not added here for a producer defect this reader has never observed in practice.
        var descriptor = Resolve(reader, fontDict.Get(_fontDescriptorKey)) as PdfDictionary;
        var resolvedFlags = descriptor is not null
            ? Resolve(reader, descriptor.Get(_flagsKey)) as PdfInteger
            : null;
        bool symbolic;
        if (resolvedFlags is not null)
        {
            symbolic = (resolvedFlags.Value & SymbolicFlagBit) != 0;
        }
        else
        {
            symbolic = afmName is "Symbol" or "ZapfDingbats";
        }

        // §9.6.5.4's own StandardEncoding fill (step 5) is conditioned on "the font descriptor's
        // Nonsymbolic flag ... is set" (see the class remarks): a condition on a present
        // descriptor, unlike the Table 112 default above, which treats a missing descriptor as
        // nonsymbolic. Table 109 makes /FontDescriptor required, optional only in PDF 1.0 to 1.7
        // for the standard 14, so its absence on any other font is a producer defect this reader
        // does not paper over by running the fill. With a descriptor present, the state is read
        // from the Symbolic flag, as step 3 read it, per §9.8.2's "A PDF processor should always
        // check the Symbolic flag"; a descriptor whose two flags disagree is not read differently
        // here than there.
        var descriptorNonsymbolic = resolvedFlags is not null && !symbolic;

        // Step 4: base table, then /Differences. Symbol and ZapfDingbats get no special path
        // here: their built-in encodings are the Table 112 default base encoding (the "font's
        // built-in encoding" case), and §9.6.5.2 says an /Encoding entry, "if present, shall
        // override a Type 1 font's mapping from character codes to character names", so a named
        // /Encoding or a /Differences array applies to them exactly as to any other font.
        var table = ResolveEncodingTable(
            reader, fontDict, symbolic, afmName, out var encodingDict, out var standardFillAllowed);
        if (encodingDict is not null)
        {
            ApplyDifferences(reader, encodingDict, table);

            // Step 5: §9.6.5.4's closing rule (see the class remarks for its exact scope and for
            // why this needs a present descriptor rather than "!symbolic" alone).
            var trueType = Resolve(reader, fontDict.Get(PdfName.Subtype)) is PdfName { Value: "TrueType" };
            if (trueType && descriptorNonsymbolic && standardFillAllowed)
                FillUndefinedFromStandard(table);
        }

        _names = table;

        // Step 6/7: widths.
        var descriptorMissingWidth = 0.0;
        if (descriptor is not null && Resolve(reader, descriptor.Get(_missingWidthKey)) is { } mw)
        {
            descriptorMissingWidth = mw switch { PdfInteger i => i.Value, PdfReal r => r.Value, _ => 0.0 };
        }

        var widths = new double[256];
        Array.Fill(widths, descriptorMissingWidth);
        var usesAfmWidths = BuildWidths(reader, fontDict, widths, afmName);
        _widths = widths;

        // Step 8: Unicode, per code, from the glyph name.
        var unicode = new string?[256];
        var zapf = afmName == "ZapfDingbats";
        for (var code = 0; code < 256; code++)
        {
            var name = table[code];
            if (name is null)
                continue;

            if (zapf && ZapfDingbatsGlyphList.TryMap(name, out var zapfUnicode))
                unicode[code] = zapfUnicode;
            else if (AdobeGlyphList.TryMapToUnicode(name, out var aglUnicode))
                unicode[code] = aglUnicode;
        }
        _unicode = unicode;
        _hasAnyMappedCode = Array.Exists(unicode, u => u is not null);

        // Step 9: AFM widths, only when /Widths itself was absent (step 7 deferred this here,
        // since a text font's width needs this step's own Unicode table).
        if (usesAfmWidths)
            FillAfmWidths(afmName!, table, widths, descriptorMissingWidth);

        // /ToUnicode: recorded only, not parsed yet (see PdfFontReader's doc). A stream object is
        // always indirect (§7.3.8.1), and PdfDocumentReader.Resolve hands back a stream object's
        // dictionary rather than a stream, so the reference is followed with ResolveStream; the
        // direct PdfStream arm serves dictionaries built in memory, which a parsed file never
        // produces.
        _hasToUnicode = fontDict.Get(_toUnicodeKey) switch
        {
            PdfIndirectReference toUnicodeRef => reader.ResolveStream(toUnicodeRef) is not null,
            PdfStream => true,
            _ => false,
        };

        // Step 10: 403, reported once, right here; 404 is decided lazily in TryDecodeNext, using
        // _hasAnyMappedCode computed above so that check costs nothing per decoded byte.
        if (!_hasToUnicode && !_hasAnyMappedCode)
        {
            ReportOnce(ref _reportedNoUnicodeOrUnmapped, PdfReaderDiagnosticCode.FontNoUnicodeRoute,
                "no code in this font has a route to Unicode: no /ToUnicode stream, and no glyph "
                + "name the Adobe Glyph List (or the ZapfDingbats list) maps.");
        }
    }

    // standardFillAllowed is true for an encoding dictionary whose base table is one this reader
    // transcribes in full, so that its null cells are the "undefined entries" §9.6.5.4 speaks of;
    // it is false for a name /Encoding (the clause's fill belongs to the dictionary case only) and
    // for a /BaseEncoding /MacExpertEncoding (see the class remarks).
    private string?[] ResolveEncodingTable(
        PdfDocumentReader reader, PdfDictionary fontDict, bool symbolic, string? afmName,
        out PdfDictionary? encodingDict, out bool standardFillAllowed)
    {
        encodingDict = null;
        standardFillAllowed = false;
        var encoding = Resolve(reader, fontDict.Get(PdfName.Encoding));
        switch (encoding)
        {
            case null:
                return TableDefault(symbolic, afmName);

            case PdfName named:
                if (SimpleFontEncodings.TryGetNamed(named.Value, out var byName))
                    return byName.ToArray();
                ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                    $"/Encoding names an encoding this reader does not know: "
                    + $"{DiagnosticExcerpt.Quote(named.Value)}.");
                return TableDefault(symbolic, afmName);

            case PdfDictionary dict:
                encodingDict = dict;
                standardFillAllowed = true;
                var baseEncoding = Resolve(reader, dict.Get(_baseEncodingKey));
                if (baseEncoding is null)
                    return TableDefault(symbolic, afmName);
                if (baseEncoding is PdfName baseName && SimpleFontEncodings.TryGetNamed(baseName.Value, out var baseTable))
                {
                    standardFillAllowed = baseName.Value != "MacExpertEncoding";
                    return baseTable.ToArray();
                }
                if (baseEncoding is PdfIndirectReference)
                {
                    // A reference here has already been through one Resolve hop and is STILL a
                    // reference: a second link in the chain, which this reader does not follow
                    // (see the class remarks). Naming it "an encoding this reader does not know"
                    // would be wrong: it names no encoding at all, resolved or not.
                    ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                        "/Encoding's /BaseEncoding is an indirect reference this reader does not "
                        + "follow past one hop.");
                    return TableDefault(symbolic, afmName);
                }
                ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                    "/Encoding's /BaseEncoding names an encoding this reader does not know.");
                return TableDefault(symbolic, afmName);

            default:
                ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                    "/Encoding is neither a known encoding name nor an encoding dictionary.");
                return TableDefault(symbolic, afmName);
        }
    }

    // Table 112's default base encoding is the font program's built-in encoding for an embedded
    // font or a symbolic one, and StandardEncoding for a nonsymbolic one. This reader never
    // parses a font program, so the built-in encoding is known only for the two standard 14
    // fonts Annex D.5 and D.6 print it for; every other symbolic font gets an all-null table,
    // and whether the font is embedded plays no part.
    private static string?[] TableDefault(bool symbolic, string? afmName) => afmName switch
    {
        "Symbol" => SymbolFontMetrics.SymbolEncoding.ToArray(),
        "ZapfDingbats" => SymbolFontMetrics.ZapfDingbatsEncoding.ToArray(),
        _ => symbolic ? new string?[256] : SimpleFontEncodings.Standard.ToArray(),
    };

    private static void FillUndefinedFromStandard(string?[] table)
    {
        var standard = SimpleFontEncodings.Standard;
        for (var code = 0; code < 256; code++)
            table[code] ??= standard[code];
    }

    private void ApplyDifferences(PdfDocumentReader reader, PdfDictionary encodingDict, string?[] table)
    {
        var resolved = Resolve(reader, encodingDict.Get(_differencesKey));
        if (resolved is null)
            return; // absent (see this class's Resolve helper): omitted, null, or a dangling reference.

        if (resolved is not PdfArray differences)
        {
            ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                $"/Differences is present but not an array: {DescribeNonArrayType(resolved)}.");
            return;
        }

        var code = 0;
        for (var i = 0; i < differences.Count; i++)
        {
            // Raw, not resolved: §7.3.10 permits an indirect reference here, and this reader
            // deliberately does not follow one; see the class doc's own remarks.
            var element = differences[i];
            switch (element)
            {
                case PdfInteger codeInt:
                    if (codeInt.Value is < 0 or > 255)
                    {
                        ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                            $"/Differences sets the current code to {codeInt.Value}, outside 0..255.");
                        return; // the rest of the array is ignored.
                    }
                    code = (int)codeInt.Value;
                    break;

                case PdfName glyphName:
                    if (code > 255)
                    {
                        ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                            "/Differences assigns a name past code 255.");
                        return; // stop.
                    }
                    if (glyphName.Value.Length > AdobeGlyphList.MaxGlyphNameLength)
                    {
                        // The flag is tested before the message is built, not just inside
                        // ReportOnce, because this is the one Report call in this class reachable
                        // from an unbounded loop: building the interpolated message and the
                        // DiagnosticExcerpt.Quote call for every oversized element, only to have
                        // ReportOnce discard all but the first, is an allocation a 100,000-element
                        // array should not have to pay for.
                        if (!_reported401)
                        {
                            ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                                $"/Differences names a glyph longer than {AdobeGlyphList.MaxGlyphNameLength} characters: "
                                + $"{DiagnosticExcerpt.Quote(glyphName.Value)}.");
                        }
                        table[code] = null; // the code stays undefined; see this class's own doc.
                    }
                    else
                    {
                        // A later assignment overwrites an earlier one at the same code. ISO
                        // 32000-2 §9.6.5 forbids overlapping sequences; this reader allows the
                        // overwrite and reports nothing for it (see the class doc's own remarks).
                        table[code] = glyphName.Value;
                    }
                    code++;
                    break;

                default:
                    // §9.6.5.1 permits only integers and names in this array; a direct real,
                    // string, boolean, dictionary or nested array is not "unresolved", it is of a
                    // type the clause does not permit there. An indirect reference is the one
                    // shape §7.3.10 does permit, that this reader still does not follow.
                    var kind = element is PdfIndirectReference
                        ? "an indirect reference, which this reader does not follow inside /Differences"
                        : $"an element that is neither an integer nor a name ({DescribeNonArrayType(element)})";
                    ReportOnce(ref _reported401, PdfReaderDiagnosticCode.FontEncodingMalformed,
                        $"/Differences contains {kind}.");
                    // Stop applying the array at the first element this reader cannot interpret,
                    // rather than resuming after it with the running code unchanged: that
                    // resumption is what let a later name silently overwrite an earlier one's
                    // code.
                    return;
            }
        }
    }

    // Names a resolved value's type for the 401 message reported when /Differences is present but
    // not an array, or contains an element of a type §9.6.5.1 does not permit there: a name or
    // keyword goes through DiagnosticExcerpt, matching every other Report call in this class.
    private static string DescribeNonArrayType(PdfObject value) => value switch
    {
        PdfDictionary => "a dictionary",
        PdfIndirectReference => "an indirect reference this reader does not follow past one hop",
        PdfName n => $"the name {DiagnosticExcerpt.Quote(n.Value)}",
        PdfInteger i => $"the integer {i.Value}",
        // Invariant so the message does not change with the host culture's decimal separator.
        PdfReal r => $"the number {r.Value.ToString(CultureInfo.InvariantCulture)}",
        PdfBoolean b => $"the boolean {(b.Value ? "true" : "false")}",
        PdfLiteralString or PdfHexString => "a string",
        PdfStream => "a stream",
        _ => "a value of a type this reader does not recognise",
    };

    /// <summary>Returns <see langword="true"/> when /Widths was absent, meaning step 9's AFM fill
    /// applies (only for a standard 14 or aliased font; any other font keeps MissingWidth
    /// everywhere and reports 402).</summary>
    private bool BuildWidths(PdfDocumentReader reader, PdfDictionary fontDict, double[] widths, string? afmName)
    {
        var widthsResolved = Resolve(reader, fontDict.Get(_widthsKey));
        if (widthsResolved is null)
        {
            if (afmName is not null)
                return true;

            ReportOnce(ref _reported402, PdfReaderDiagnosticCode.FontWidthsMalformed,
                "has no /Widths and is not a standard 14 font.");
            return false;
        }

        var firstCharResolved = Resolve(reader, fontDict.Get(_firstCharKey));
        var lastCharResolved = Resolve(reader, fontDict.Get(_lastCharKey));
        if (firstCharResolved is not PdfInteger first || first.Value is < 0 or > 255
            || lastCharResolved is not PdfInteger last || last.Value is < 0 or > 255
            || first.Value > last.Value)
        {
            ReportOnce(ref _reported402, PdfReaderDiagnosticCode.FontWidthsMalformed,
                "/FirstChar or /LastChar is missing, mistyped, out of range, or FirstChar exceeds LastChar.");
            return false;
        }

        if (widthsResolved is not PdfArray widthsArray)
        {
            ReportOnce(ref _reported402, PdfReaderDiagnosticCode.FontWidthsMalformed,
                "/Widths is not an array.");
            return false;
        }

        var firstChar = (int)first.Value;
        var span = (int)(last.Value - first.Value + 1);
        var usable = Math.Min(widthsArray.Count, span);
        var malformed = widthsArray.Count < span;
        for (var i = 0; i < usable; i++)
        {
            var element = Resolve(reader, widthsArray[i]);
            switch (element)
            {
                case PdfInteger wi: widths[firstChar + i] = wi.Value; break;
                case PdfReal wr: widths[firstChar + i] = wr.Value; break;
                default: malformed = true; break;
            }
        }

        if (malformed)
        {
            ReportOnce(ref _reported402, PdfReaderDiagnosticCode.FontWidthsMalformed,
                "/Widths is shorter than LastChar - FirstChar + 1, or contains a non-number element.");
        }
        return false;
    }

    // Name-keyed for every one of the fourteen standard 14 fonts (SymbolFontMetrics' own
    // generator reads all fourteen AFM files), so this lookup needs no Unicode round trip and no
    // dependence on whether the glyph's Unicode value happens to fall inside WinAnsiEncoding: a
    // text font's own AFM lists a width for every glyph name it defines, encodable in WinAnsi or
    // not. The trade this keying makes: a /Differences name absent from the font's own AFM (a
    // uniXXXX name, say, which no AFM's own N record ever uses) keeps MissingWidth rather than
    // falling back through a Unicode round trip that might have found it. Glyph-name keying
    // matches the AFM's own key, and a uniXXXX name in /Differences on a non-embedded standard 14
    // font is a producer choice this AFM lookup was never going to be able to serve either way.
    private static void FillAfmWidths(string afmName, string?[] table, double[] widths, double missingWidth)
    {
        var byName = SymbolFontMetrics.TryGetTextFontWidths(afmName, out var textWidths)
            ? textWidths
            : afmName == "Symbol" ? SymbolFontMetrics.SymbolWidths : SymbolFontMetrics.ZapfDingbatsWidths;
        for (var code = 0; code < 256; code++)
        {
            var name = table[code];
            if (name is null)
                continue;
            widths[code] = byName.TryGetValue(name, out var w) ? w : missingWidth;
        }
    }

    /// <inheritdoc />
    public override bool TryDecodeNext(ReadOnlySpan<byte> bytes, ref int offset, out DecodedGlyph glyph)
    {
        if (offset >= bytes.Length)
        {
            glyph = default;
            return false;
        }

        var code = bytes[offset];
        offset++;

        // 404 is withheld while the font names a /ToUnicode stream this reader does not parse
        // yet: that stream has priority over the glyph-name route (§9.10.2) and may map the code.
        var unicode = _unicode[code];
        if (unicode is null && !_reportedNoUnicodeOrUnmapped && _hasAnyMappedCode && !_hasToUnicode)
        {
            ReportOnce(ref _reportedNoUnicodeOrUnmapped, PdfReaderDiagnosticCode.UnmappedGlyphs,
                "decoded a glyph whose code has no Unicode mapping, though other codes in this font do.");
        }

        glyph = new DecodedGlyph(code, 1, _widths[code], unicode, code == 32);
        return true;
    }

    private void ReportOnce(ref bool flag, PdfReaderDiagnosticCode code, string message)
    {
        if (flag)
            return;
        flag = true;
        _sink.Report(code, message, _objectNumber, _generation, _pageIndex);
    }

    /// <summary>
    /// Null-tolerant single-hop resolution through <paramref name="reader"/>. Also normalises a
    /// resolved <see cref="PdfNull"/>, direct or reached through the one hop, to
    /// <see langword="null"/>: ISO 32000-2 §7.3.7 states, verbatim, "A dictionary entry whose
    /// value is null (see 7.3.9, "Null object") shall be treated the same as if the entry does
    /// not exist", and every entry this class reads goes through this one helper, so that rule
    /// applies uniformly to <c>/Encoding</c>, <c>/Widths</c>, <c>/FirstChar</c>, <c>/LastChar</c>,
    /// <c>/BaseFont</c>, <c>/FontDescriptor</c>, <c>/MissingWidth</c>, <c>/BaseEncoding</c>,
    /// <c>/Flags</c> and <c>/Differences</c> from this one site.
    /// </summary>
    private static PdfObject? Resolve(PdfDocumentReader reader, PdfObject? raw)
    {
        if (raw is null)
            return null;
        var resolved = reader.ResolveValue(raw);
        return resolved is PdfNull ? null : resolved;
    }
}
