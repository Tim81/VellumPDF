// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>
/// The cross-validation corpus. Each fixture is a real, complete PDF produced by VellumPdf's own
/// writer (so veraPDF can parse it), differing from a known-good baseline only by a single
/// same-length byte edit — keeping the in-process verdict and the veraPDF verdict comparable.
/// </summary>
/// <remarks>
/// Most fixtures derive from one PDF/A-2b baseline and apply in-place, same-length edits so
/// cross-reference offsets stay valid; this anchors the gate on the byte-level structural and
/// metadata rules. A few fixtures are instead whole writer-produced documents (e.g.
/// <c>pdfa2b-link</c>, which exercises the §6.3 annotation rule). Further object-graph
/// rules (fonts, output intents, blend modes, actions, logical structure) and the 2u/2a/UA flavours
/// are the next expansion: each needs a writer-produced document veraPDF agrees on, so the
/// cross-validation gate (CI) is what confirms each new fixture's expected verdict.
/// </remarks>
public static class OracleCorpus
{
    public static IReadOnlyList<OracleFixture> All { get; } = Build();

    public static OracleFixture ByName(string name) => All.Single(f => f.Name == name);

    /// <summary>Public accessor for the §7.1-3 untagged-real-content violation fixture (for unit tests).</summary>
    internal static byte[] Ua1UntaggedRealContentPublic() => Ua1UntaggedRealContent();

    private static IReadOnlyList<OracleFixture> Build()
    {
        // One baseline, cloned per fixture so the documents are byte-identical except for the edit.
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);

        return
        [
            new OracleFixture("pdfa2b-compliant", Clone(baseline),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            new OracleFixture("pdfa2b-bad-conformance", Edit(baseline, CorruptXmpConformance),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            new OracleFixture("pdfa2b-bad-part", Edit(baseline, CorruptXmpPart),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            new OracleFixture("pdfa2b-bad-binary-marker", Edit(baseline, CorruptBinaryMarker),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A plain (non-PDF/A) document validated as PDF/A-2b: non-compliant.
            new OracleFixture("plain-not-pdfa", WriterPdf(VellumPdf.Document.PdfConformance.None),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A PDF/A-2b document carrying a writer-produced /Link annotation. The writer sets /F 4
            // (Print) per §6.3.2, so the Link is conformant — Link is exempt from the appearance-stream
            // requirement but must still satisfy the flag requirements. End-to-end guard that the
            // writer emits conformant Link annotations, cross-checked by veraPDF.
            new OracleFixture("pdfa2b-link", WriterPdfWithLink(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A PDF/A-2b document that draws text with a non-embedded standard-14 font. PDF/A requires
            // every font embedded (ISO 19005-2 §6.2.11.4.1 / §6.3.4), so both veraPDF and the in-process
            // FontEmbeddingRule reject it. Cross-validates the font-embedding rule's negative path.
            // Uses only the built-in standard-14 metrics, so no external font asset is needed.
            new OracleFixture("pdfa2b-nonembedded-font", WriterPdfNonEmbeddedFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A real embedded TrueType whose descendant CIDFont carries an invalid /Subtype
            // (§6.2.11.2-2). veraPDF and the in-process FontStructureRule both reject it.
            new OracleFixture("pdfa2b-bad-font-subtype", WriterPdfWithBadFontSubtype(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A real embedded CIDFontType2 with its /CIDToGIDMap removed (§6.2.11.3.2-1). veraPDF and
            // the in-process FontStructureRule both reject it.
            new OracleFixture("pdfa2b-no-cidtogidmap", WriterPdfWithoutCidToGidMap(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A composite font's descendant CIDFont with its /Type /Font entry removed (§6.2.11.2-1).
            // veraPDF and the in-process FontStructureRule both reject it.
            new OracleFixture("pdfa2b-font-no-type", WriterPdfWithoutFontType(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A simple embedded TrueType font with its /BaseFont (PostScript name) removed
            // (§6.2.11.2-3). veraPDF and the in-process FontStructureRule both reject it.
            new OracleFixture("pdfa2b-font-no-basefont", WriterPdfWithoutBaseFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A composite font whose /Encoding names a non-predefined, non-embedded CMap
            // (§6.2.11.3.3-1). veraPDF and the in-process FontStructureRule both reject it.
            new OracleFixture("pdfa2b-bad-cmap-name", WriterPdfWithBadCMapName(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A composite font shown a glyph index beyond the embedded program's glyph count
            // (§6.2.11.4.1-2). veraPDF and the in-process GlyphPresenceRule both reject it.
            new OracleFixture("pdfa2b-glyph-not-present", WriterPdfWithOutOfRangeGlyph(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A composite font shown glyph index 0 — a reference to .notdef (§6.2.11.8-1). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-notdef-glyph", WriterPdfDrawingNotdef(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A composite font with its /W array removed, so the drawn glyphs' declared widths fall to
            // /DW and no longer match the embedded program (§6.2.11.5-1). veraPDF and the in-process
            // rule both reject it.
            new OracleFixture("pdfa2b-glyph-width", WriterPdfWithBadGlyphWidth(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A hand-built but fully conformant simple WinAnsi TrueType font (full DejaVu, correct
            // widths) — the regression guard that the new simple-font checks do not false-positive.
            new OracleFixture("pdfa2b-simple-font", SimpleTrueTypeFont(_ => { }, encoding: new PdfName("WinAnsiEncoding")),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // The same simple font with a /Widths length that does not equal LastChar−FirstChar+1
            // (§6.2.11.2-6). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-font-widths",
                SimpleTrueTypeFont(f => f.Set(new PdfName("LastChar"), new PdfInteger(66)), encoding: new PdfName("WinAnsiEncoding")),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A non-symbolic simple TrueType font with no /Encoding entry (§6.2.11.6-2). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-font-no-encoding", SimpleTrueTypeFont(_ => { }, flags: 32, encoding: null),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A symbolic simple TrueType font carrying an /Encoding entry (§6.2.11.6-3). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-font-symbolic-encoding",
                SimpleTrueTypeFont(_ => { }, flags: 4, encoding: new PdfName("WinAnsiEncoding")),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A symbolic simple TrueType font (no /Encoding) whose embedded program has a 5-subtable
            // cmap with no Microsoft Symbol (3,0) encoding (§6.2.11.6-4). veraPDF and the in-process
            // FontStructureRule both reject it.
            new OracleFixture("pdfa2b-font-symbolic-cmap",
                SimpleTrueTypeFont(_ => { }, flags: 4, encoding: null),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A non-symbolic simple TrueType font whose embedded program's cmap is a single (3,0)
            // symbol subtable — insufficient for a non-symbolic font (§6.2.11.6-1). veraPDF and the
            // in-process rule both reject it.
            new OracleFixture("pdfa2b-font-nonsymbolic-cmap", SimpleTrueTypeFontSymbolOnlyCmap(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A hand-built classic-cross-reference PDF where the xref keyword and the first subsection
            // header are separated by a space rather than a single EOL (§6.1.4-2). veraPDF and the
            // in-process CrossReferenceRule both reject it.
            new OracleFixture("pdfa2b-xref-bad-eol", AssembleClassicXref(corruptXrefEol: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same classic-cross-reference document, uncorrupted — both validators accept it (the
            // no-false-positive guard for §6.1.4-2/§6.1.7.1-2, and that a hand-built classic xref is
            // conformant).
            new OracleFixture("pdfa2b-classic-xref", AssembleClassicXref(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A stream whose 'stream' keyword is followed by a lone CR instead of CRLF or LF
            // (§6.1.7.1-2). veraPDF and the in-process StreamRule both reject it.
            new OracleFixture("pdfa2b-stream-bad-eol", AssembleClassicXref(corruptStreamEol: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A stream whose 'endstream' keyword is preceded by a space instead of an EOL marker
            // (§6.1.7.1-2). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-endstream-bad-eol", AssembleClassicXref(corruptEndstreamEol: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An indirect object whose object and generation numbers are separated by two spaces
            // rather than a single white-space (§6.1.9-1). veraPDF and the in-process ObjectLayoutRule
            // both reject it.
            new OracleFixture("pdfa2b-obj-bad-spacing", AssembleClassicXref(corruptObjSpacing: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A hexadecimal string with an odd number of hex digits (§6.1.6-1). veraPDF and the
            // in-process HexStringRule both reject it. (§6.1.6-2, a non-hex digit, is not fixtured: the
            // reader rejects an invalid hex digit while parsing, so such a file cannot be opened to
            // validate — HexStringRule still flags it best-effort when the document does parse.)
            new OracleFixture("pdfa2b-hex-odd", AssembleClassicXref(injectOddHex: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An embedded subset CIDFontType2 with a /CIDSet that does not identify the present CIDs
            // (§6.2.11.4.2-2). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-cidset-incomplete", WriterPdfWithCidSet(complete: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same font with a /CIDSet that marks exactly CIDs 0..NumGlyphs−1 — both validators
            // accept it (the no-false-positive guard for §6.2.11.4.2-2).
            new OracleFixture("pdfa2b-cidset-complete", WriterPdfWithCidSet(complete: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // An embedded subset Type 1 font (real Noto Sans Shavian PFB) whose FontDescriptor /CharSet
            // lists every glyph in the program — both validators accept it (the no-false-positive guard
            // for §6.2.11.4.2-1).
            new OracleFixture("pdfa2b-type1-charset-complete", WriterPdfWithType1CharSet(complete: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // The same font with one present glyph omitted from /CharSet (§6.2.11.4.2-1). veraPDF and
            // the in-process FontStructureRule both reject it.
            new OracleFixture("pdfa2b-type1-charset-incomplete", WriterPdfWithType1CharSet(complete: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A PDF/A-2b document that draws text with a properly embedded (subset) TrueType font via
            // the Type0/CIDFontType2/Identity-H path. veraPDF accepts it (all §6.2.11.x font checks
            // pass) and so does the in-process validator — the positive end-to-end font fixture.
            new OracleFixture("pdfa2b-embedded-font", WriterPdfWithEmbeddedFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A PDF/A-2b document drawing text with a properly embedded OpenType-CFF font via the
            // Type0/CIDFontType0 path, so its descendant carries /FontFile3 /Subtype /OpenType. Both
            // veraPDF and the in-process validator accept it — the positive guard that the §6.2.11.2-7
            // FontFile3 /Subtype check does not false-positive on a conformant CFF program.
            new OracleFixture("pdfa2b-embedded-cff-font", WriterPdfWithEmbeddedCffFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // The same embedded OpenType-CFF font with its /FontFile3 /Subtype changed from /OpenType
            // to /Type2 (§6.2.11.2-7). veraPDF and the in-process FontStructureRule both reject it.
            new OracleFixture("pdfa2b-cff-bad-fontfile3-subtype", WriterPdfWithBadFontFile3Subtype(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A PDF/A-2b document drawing text with a standard-14 font (Helvetica) via the
            // VellumPdf.Fonts.Standard14 substitution package, which embeds a metric-compatible
            // Liberation font. Proves the substitution path yields conformant PDF/A text out-of-the-box.
            new OracleFixture("pdfa2b-standard14-substitute", WriterPdfWithStandard14Substitute(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // PDF/A-2u: embedded-font text with a ToUnicode CMap. Cross-validates the unicode rule
            // set (ToUnicodeRule) and the part-2u XMP identification against veraPDF's 2u profile.
            new OracleFixture("pdfa2u-embedded-font", WriterPdfEmbeddedText(VellumPdf.Document.PdfConformance.PdfA2u),
                Conformance.PdfConformance.PdfA2U, "2u", ExpectedCompliant: true),

            // PDF/A-2a: a tagged document (marked content + a Document→P structure tree). Cross-
            // validates the logical-structure rule (§6.8) and the part-2a identification.
            new OracleFixture("pdfa2a-tagged", WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: true),

            // PDF/UA-1: a tagged document with /Lang, document title and DisplayDocTitle. Cross-
            // validates the whole UA rule set (metadata, tagging, lang, title, tab order).
            new OracleFixture("pdfua1-tagged", WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // NEGATIVE object-graph fixtures: a conformant baseline with a single forbidden construct
            // injected via an incremental update, so each is non-compliant for exactly one reason and
            // both validators agree. These give the object-graph rules their first NEGATIVE veraPDF
            // cross-validation.

            // A forbidden /JavaScript action on the catalog /OpenAction (ISO 19005-2 §6.5.1).
            new OracleFixture("pdfa2b-javascript-action", WriterPdfWithJavaScriptAction(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A forbidden multimedia annotation subtype (/Movie) on the page (ISO 19005-2 §6.3.1).
            new OracleFixture("pdfa2b-movie-annotation", WriterPdfWithMovieAnnotation(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A PDF/A-2a document with /Lang and a title but NO tagged content, so it has no
            // structure tree — the one violation. Cross-validates the logical-structure rule (§6.8).
            new OracleFixture("pdfa2a-no-structure", WriterPdfMissingStructure(VellumPdf.Document.PdfConformance.PdfA2a),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: false),

            // §6.7.3.4-1 VIOLATION: a StructElem with /S /MyCustomTag and NO /RoleMap entry.
            // veraPDF fires 6.7.3.4-1 (isNotMappedToStandardType == true).
            // In-process: A2aStructureTypeRule fires "ISO19005-2:6.7.3.4-1".
            new OracleFixture("pdfa2a-nonstandard-type-unmapped",
                Pdfa2aNonStandardTypeUnmapped(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: false),

            // §6.7.3.4-1 FP-safety: StructElem /S /MyCustomTag with /RoleMap /MyCustomTag /Div.
            // veraPDF PASSES (isNotMappedToStandardType == false).
            // In-process: A2aStructureTypeRule must NOT fire.
            new OracleFixture("pdfa2a-nonstandard-type-rolemapped",
                Pdfa2aNonStandardTypeRoleMapped(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: true),

            // §6.7.3.4-2 VIOLATION: /RoleMap /Foo /Bar /Bar /Foo with a StructElem /S /Foo.
            // veraPDF fires 6.7.3.4-2 (circularMappingExist == true on the element).
            // In-process: A2aStructureTypeRule fires "ISO19005-2:6.7.3.4-2".
            new OracleFixture("pdfa2a-circular-rolemap",
                Pdfa2aCircularRoleMap(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: false),

            // §6.7.3.4-3 VIOLATION: /RoleMap /P /MyNonStd with a StructElem /S /P. Standard type
            // /P is remapped to non-standard /MyNonStd. veraPDF fires 6.7.3.4-3.
            // In-process: A2aStructureTypeRule fires "ISO19005-2:6.7.3.4-3".
            new OracleFixture("pdfa2a-standard-type-remap-nonstd",
                Pdfa2aStandardTypeRemapNonStd(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: false),

            // §6.7.3.4-3 FP-safety: /RoleMap /P /Div (standard remapped to another standard).
            // veraPDF PASSES (empirically confirmed). In-process: A2aStructureTypeRule must NOT fire.
            new OracleFixture("pdfa2a-standard-type-remap-std",
                Pdfa2aStandardTypeRemapStd(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: true),

            // §6.7.3.4-3 FP-safety (multi-hop): /RoleMap /P /Foo  /Foo /Span — standard /P remapped
            // through a NON-standard intermediate /Foo that itself resolves to the standard type
            // /Span. veraPDF resolves the FULL chain and PASSES (exit 0, no 6.7.3.4 failure —
            // empirically confirmed). Guards against a regression to the immediate-target check,
            // which would over-reject this document. In-process: A2aStructureTypeRule must NOT fire.
            new OracleFixture("pdfa2a-standard-type-remap-multihop",
                Pdfa2aStandardTypeRemapMultihop(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: true),

            // §6.7.4-1 VIOLATION: document catalog /Lang (invalid!!bad) — not a valid RFC 3066 tag.
            // veraPDF fires 6.7.4-1. In-process: A2aLangSyntaxRule fires "ISO19005-2:6.7.4-1".
            new OracleFixture("pdfa2a-bad-catalog-lang",
                Pdfa2aBadCatalogLang(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: false),

            // §6.7.4-1 VIOLATION: StructElem /Lang (xyz!!bad) — bad tag on a structure element.
            // veraPDF fires 6.7.4-1. In-process: A2aLangSyntaxRule fires "ISO19005-2:6.7.4-1".
            new OracleFixture("pdfa2a-bad-structelem-lang",
                Pdfa2aBadStructElemLang(),
                Conformance.PdfConformance.PdfA2A, "2a", ExpectedCompliant: false),

            // §6.7.4-1 FP-safety: catalog /Lang (en-US) — valid BCP-47 tag.
            // veraPDF PASSES. In-process: A2aLangSyntaxRule must NOT fire.
            // (re-uses the pdfa2a-tagged fixture via WriterPdfTagged which sets Language = "en-US")

            // The same for PDF/UA-1: lang + title present but no structure tree, isolating the
            // tagging requirement (§7.1). Cross-validates the UA tagging rule's negative path.
            new OracleFixture("pdfua1-no-structure", WriterPdfMissingStructure(VellumPdf.Document.PdfConformance.PdfUA1),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-4 (Suspects): the compliant tagged baseline has no /Suspects entry — both accept.
            new OracleFixture("pdfua1-suspects-absent", WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §7.1-4 (Suspects): /MarkInfo /Suspects = true is forbidden; both veraPDF and the in-process
            // UaSuspectsRule reject it.
            new OracleFixture("pdfua1-suspects-true", WriterUa1WithSuspectsTrue(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-4 (Suspects): /MarkInfo /Suspects = false is explicitly permitted (same as absent).
            // Both veraPDF and the in-process rule accept this document.
            new OracleFixture("pdfua1-suspects-false", WriterUa1WithSuspectsFalse(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §6.1-1 (File header): the compliant tagged baseline has a well-formed %PDF-1.7 header
            // with no trailing characters — both validators accept it.
            new OracleFixture("pdfua1-header-valid", WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §6.1-1 (File header): replacing the version digit '7' with '8' (in-place, same-length
            // so xref offsets remain valid) produces a header "%PDF-1.8" with a digit outside the
            // permitted 0–7 range. Both veraPDF (6.1-1) and the in-process UaFileHeaderRule reject it.
            new OracleFixture("pdfua1-header-bad-digit", WriterUa1WithBadHeaderDigit(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §6.1-1 (File header): a trailing character after the version digit but before the EOL.
            // Replacing the header-terminating LF (index 8) with a space (in-place, same-length) makes
            // line 1 read "%PDF-1.7 %…" so the regex /^%PDF-1\.[0-7]$/ no longer matches. This is the
            // case that exercises the rule's distinguishing EOL check (vs the bad-digit case above):
            // veraPDF 1.30.2 rejects it for 6.1-1 (empirically confirmed), and the in-process
            // UaFileHeaderRule flags the non-EOL byte immediately after the version digit.
            new OracleFixture("pdfua1-header-trailing-char", WriterUa1WithTrailingHeaderChar(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.2-29 (Lang syntax): a catalog /Lang of "fr-FR" is a syntactically valid BCP-47 tag.
            // Both validators accept it.
            new OracleFixture("pdfua1-lang-valid-fr", WriterUa1WithLang("fr-FR"),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §7.2-29 (Lang syntax): a catalog /Lang of "not!!valid" is not a valid BCP-47 tag.
            // Both veraPDF (7.2-29) and the in-process UaLangSyntaxRule reject it.
            new OracleFixture("pdfua1-lang-invalid", WriterUa1WithLang("not!!valid"),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.2-29 (Lang syntax): an empty catalog /Lang string is not a valid BCP-47 tag.
            // Both veraPDF (7.2-29) and the in-process UaLangSyntaxRule reject it.
            // (Note: veraPDF 1.30.2 empirically rejects empty /Lang for 7.2-29 — not exempt.)
            new OracleFixture("pdfua1-lang-empty", WriterUa1WithEmptyLang(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // A non-standard blend mode in an /ExtGState resource that the page never applies (no `gs`
            // operator). §6.4 governs only the current blend mode, so both veraPDF and the in-process
            // rule accept it — the regression guard for the content-stream usage scoping (#127).
            new OracleFixture("pdfa2b-unused-blendmode", WriterPdfWithUnusedBlendMode(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A GTS_PDFA1 output intent with no DestOutputProfile, on a page that paints device colour.
            // The output-intent requirement applies, so both veraPDF and in-process reject it (#128).
            new OracleFixture("pdfa2b-devicecolour-no-profile", WriterPdfMalformedOutputIntent(deviceColour: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same malformed output intent, but on a page that paints no colour. veraPDF tolerates
            // it (no device colour ⇒ no output-intent requirement) and so must the in-process rule —
            // the regression guard for the device-colour scoping (#128).
            new OracleFixture("pdfa2b-nocolour-no-profile", WriterPdfMalformedOutputIntent(deviceColour: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // Device colour painted with NO output intent at all. §6.2.4.3 requires one, so both veraPDF
            // and the in-process rule reject it — the first negative coverage of the new
            // device-colour-requires-an-output-intent check (#122).
            new OracleFixture("pdfa2b-devicecolour-no-outputintent", WriterPdfDeviceColourNoOutputIntent(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An interactive form dictionary carrying an /XFA entry, which PDF/A-2 forbids (§6.4.2).
            // Both veraPDF and the in-process XfaRule reject it (#122).
            new OracleFixture("pdfa2b-xfa", WriterPdfWithXfa(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An integer beyond 2147483647 (§6.1.13-1). veraPDF and the in-process NumericLimitsRule
            // both reject it.
            new OracleFixture("pdfa2b-oversized-integer", WriterPdfWithCatalogEntry("VellumBig", new PdfInteger(9999999999L)),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A name longer than 127 bytes (§6.1.13-4). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-oversized-name", WriterPdfWithCatalogEntry("VellumName", new PdfName(new string('A', 200))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A page whose /MediaBox is 1×1 units, below the 3-unit minimum (§6.1.13-11). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-tiny-mediabox", WriterPdfWithTinyMediaBox(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // Data after the final %%EOF marker (§6.1.3-3). veraPDF and the in-process rule both
            // reject it.
            new OracleFixture("pdfa2b-trailing-data", AppendTrailingBytes(WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b)),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A stream whose /Length does not match its body length (§6.1.7.1-1). veraPDF and the
            // in-process StreamRule both reject it.
            new OracleFixture("pdfa2b-bad-stream-length", WriterPdfWithBadStreamLength(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A drawn PostScript XObject (/Subtype /PS invoked by `Do`), which PDF/A-2 forbids
            // outright (§6.2.9). Both veraPDF (clause 6.2.9-3) and the in-process ForbiddenXObjectRule
            // reject it — the first negative cross-validation of the XObject rule.
            new OracleFixture("pdfa2b-postscript-xobject", WriterPdfWithDrawnPostScriptXObject(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A drawn Image XObject whose /Interpolate is true (§6.2.8). Both veraPDF (clause 6.2.8-3)
            // and the in-process ForbiddenXObjectRule reject it.
            new OracleFixture("pdfa2b-image-interpolate", WriterPdfWithDrawnInterpolatedImage(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A drawn Image XObject carrying the /OPI key (§6.2.8-2). veraPDF and the in-process rule
            // both reject it — cross-validation of the forbidden image-key family.
            new OracleFixture("pdfa2b-image-opi", WriterPdfWithDrawnImageOpi(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A drawn form XObject carrying the /OPI key (§6.2.9-1). veraPDF (clause 6.2.9-1) and the
            // in-process rule both reject it.
            new OracleFixture("pdfa2b-form-opi", WriterPdfWithDrawnFormOpi(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A drawn reference XObject — a form XObject with a /Ref key (§6.2.9-2). veraPDF (clause
            // 6.2.9-2) and the in-process rule both reject it.
            new OracleFixture("pdfa2b-reference-xobject", WriterPdfWithDrawnReferenceXObject(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A catalog carrying the /NeedsRendering key (§6.4.2-2). veraPDF and the in-process
            // InteractiveFormRule both reject it.
            new OracleFixture("pdfa2b-needs-rendering", WriterPdfWithNeedsRendering(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An interactive form dictionary with /NeedAppearances true (§6.4.1-3). veraPDF and the
            // in-process rule both reject it.
            new OracleFixture("pdfa2b-need-appearances", WriterPdfWithNeedAppearances(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An external stream — a stream dictionary carrying the /F key (§6.1.7.1). veraPDF and the
            // in-process StreamRule both reject it.
            new OracleFixture("pdfa2b-external-stream", WriterPdfWithExternalStream(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An /AA additional-actions dictionary on the catalog (§6.5.2-1). veraPDF and the
            // in-process ActionRule both reject it.
            new OracleFixture("pdfa2b-catalog-aa", WriterPdfWithCatalogAdditionalActions(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A disallowed named action (/N /GoForward) on the catalog /OpenAction (§6.5.1-2). veraPDF
            // and the in-process ActionRule both reject it.
            new OracleFixture("pdfa2b-named-action", WriterPdfWithDisallowedNamedAction(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An applied ExtGState carrying a /TR transfer function (§6.2.5-1). veraPDF and the
            // in-process GraphicsStateRule both reject it.
            new OracleFixture("pdfa2b-transfer-function", WriterPdfWithTransferFunction(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An applied ExtGState whose halftone has an invalid /HalftoneType (§6.2.5-4). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-halftone-type", WriterPdfWithBadHalftoneType(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An applied ExtGState whose halftone carries a /HalftoneName (§6.2.5-5). veraPDF and the
            // in-process rule both reject it.
            new OracleFixture("pdfa2b-halftone-name", WriterPdfWithHalftoneName(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An applied ExtGState whose type-1 halftone carries a /TransferFunction (§6.2.5-6). veraPDF
            // and the in-process GraphicsStateRule both reject it.
            new OracleFixture("pdfa2b-halftone-transfer", WriterPdfWithHalftoneTransferFunction(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A non-standard rendering intent set by the `ri` content operator (§6.2.6). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-rendering-intent", WriterPdfWithBadRenderingIntent(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An XMP packet whose <?xpacket?> header declares a forbidden 'bytes' pseudo-attribute
            // (§6.6.2.1-2). veraPDF and the in-process MetadataRule both reject it.
            new OracleFixture("pdfa2b-xmp-bytes-attr", WriterPdfWithXmpHeader(" bytes=\"100\""),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An XMP packet serialised as UTF-16 rather than UTF-8 (§6.6.2.1-5). veraPDF and the
            // in-process rule both reject it.
            new OracleFixture("pdfa2b-xmp-utf16", WriterPdfWithUtf16Xmp(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A well-formed PDF/A extension schema declaring a custom property. Both veraPDF and the
            // in-process ExtensionSchemaRule accept it — the regression guard that a valid extension
            // schema is not falsely rejected (§6.6.2.3.3).
            new OracleFixture("pdfa2b-extension-schema", WriterPdfWithMetadata(Encoding.UTF8.GetBytes(ExtensionSchemaXmp2b(ValidPropertyFields))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // The same valid extension schema serialised with the equivalent rdf:Description blank-node
            // form instead of rdf:parseType="Resource". veraPDF normalises the RDF and accepts it, and
            // the in-process rule (which unwraps a lone rdf:Description) must too — the regression guard
            // that the §6.6.2.3.2/§6.6.2.3.3 checks do not over-reject the alternate serialisation.
            new OracleFixture("pdfa2b-extension-schema-rdf-description",
                WriterPdfWithMetadata(Encoding.UTF8.GetBytes(RdfDescriptionExtensionSchemaXmp2b())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // An extension-schema property whose 'category' is neither internal nor external
            // (§6.6.2.3.3-9). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-extension-schema-bad-category",
                WriterPdfWithMetadata(Encoding.UTF8.GetBytes(ExtensionSchemaXmp2b(
                    "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>"
                    + "<pdfaProperty:category>bogus</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>"))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An extension schema container carrying a child element (pdfaSchema:bogusField) whose name
            // is not in the allowed set for the pdfaSchema container (§6.6.2.3.2-1). veraPDF and the
            // in-process ExtensionSchemaRule both reject it. The existing pdfa2b-extension-schema
            // fixture (ExpectedCompliant: true) serves as the no-false-positive guard.
            new OracleFixture("pdfa2b-extension-schema-undefined-field",
                WriterPdfWithMetadata(Encoding.UTF8.GetBytes(UndefinedSchemaFieldXmp2b())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A custom XMP property in a namespace that is neither predefined nor declared by an
            // extension schema (§6.6.2.3.1). veraPDF and the in-process PropertyUsageRule both reject it.
            new OracleFixture("pdfa2b-undeclared-xmp-property", WriterPdfWithMetadata(Encoding.UTF8.GetBytes(CustomPropertyXmp2b())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An XMP packet drawing on several predefined schemas (Dublin Core, XMP Basic, Adobe PDF,
            // TIFF, EXIF, plus an xmpMM struct value) — the regression guard that predefined-schema
            // properties are not falsely rejected as undeclared (§6.6.2.3.1).
            new OracleFixture("pdfa2b-rich-xmp", WriterPdfWithMetadata(Encoding.UTF8.GetBytes(RichXmp2b())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // The pdfaid properties bound to a non-canonical prefix ('aid' instead of 'pdfaid')
            // (§6.6.4-4/-5). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-pdfaid-prefix", WriterPdfWithMetadata(Encoding.UTF8.GetBytes(AltPrefixXmp2b())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The pdfaid 'amd' and 'corr' identification properties bound to a non-canonical prefix
            // (§6.6.4-6/-7, alongside -4/-5). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-pdfaid-amd-corr-prefix", WriterPdfWithMetadata(Encoding.UTF8.GetBytes(AmdCorrAliasedXmp2b())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A GTS_PDFX output intent carrying the /DestOutputProfileRef key (§6.2.3-3). veraPDF and
            // the in-process OutputIntentRule both reject it.
            new OracleFixture("pdfa2b-pdfx-dest-output-profile-ref", WriterPdfWithCatalogEntry("OutputIntents",
                new PdfArray([new PdfDictionary()
                    .Set(PdfName.Type, new PdfName("OutputIntent")).Set(new PdfName("S"), new PdfName("GTS_PDFX"))
                    .Set(new PdfName("DestOutputProfileRef"), new PdfDictionary())])),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An extension schema declaring a custom value type (pdfaType) whose first field omits the
            // mandatory 'type' name (§6.6.2.3.3-11). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-extension-valuetype-bad", WriterPdfWithMetadata(Encoding.UTF8.GetBytes(BadValueTypeXmp2b())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A catalog carrying the /Requirements key (§6.11-1). veraPDF and the in-process
            // CatalogRestrictionsRule both reject it.
            new OracleFixture("pdfa2b-requirements", WriterPdfWithCatalogEntry("Requirements",
                new PdfArray([new PdfDictionary()
                    .Set(PdfName.Type, new PdfName("Requirement")).Set(new PdfName("S"), new PdfName("EnableJavaScripts"))])),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An /AlternatePresentations entry in the document name dictionary (§6.10-1). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-alternate-presentations", WriterPdfWithCatalogEntry("Names",
                new PdfDictionary().Set(new PdfName("AlternatePresentations"), new PdfDictionary())),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A /PresSteps entry on a page dictionary (§6.10-2). veraPDF and the in-process rule both
            // reject it.
            new OracleFixture("pdfa2b-pres-steps", WriterPdfWithPresSteps(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A minimal but valid optional-content configuration (/OCProperties with a named /D config
            // and one OCG). Both veraPDF and the in-process OptionalContentRule accept it — the
            // regression guard that a valid optional-content dictionary is not falsely rejected (§6.9).
            new OracleFixture("pdfa2b-optional-content", WriterPdfWithOptionalContent(NamedConfig()),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // An optional-content configuration dictionary with no /Name (§6.9-1). veraPDF and the
            // in-process rule both reject it.
            new OracleFixture("pdfa2b-oc-no-name", WriterPdfWithOptionalContent(new PdfDictionary()),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An optional-content configuration dictionary carrying an /AS entry (§6.9-4). veraPDF and
            // the in-process rule both reject it.
            new OracleFixture("pdfa2b-oc-as", WriterPdfWithOptionalContent(NamedConfig().Set(new PdfName("AS"),
                new PdfArray([new PdfDictionary()
                    .Set(new PdfName("Event"), new PdfName("View"))
                    .Set(new PdfName("OCGs"), new PdfArray([]))
                    .Set(new PdfName("Category"), new PdfArray([new PdfName("View")]))]))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // Two optional-content configuration dictionaries (/D and a /Configs entry) sharing the
            // same /Name (§6.9-2). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-oc-dup-name", WriterPdfWithOptionalContentGroups(
                (_, _) => NamedConfig(), (_, _) => new PdfArray([NamedConfig()])),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A /D configuration dictionary whose /Order array omits one of the file's OCGs (§6.9-3).
            // veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-oc-order-incomplete", WriterPdfWithOptionalContentGroups(
                (ocg1, _) => NamedConfig().Set(new PdfName("Order"), new PdfArray([new PdfIndirectReference(ocg1)]))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same /Order referencing every OCG (§6.9-3 satisfied) — the no-false-positive guard.
            new OracleFixture("pdfa2b-oc-order-complete", WriterPdfWithOptionalContentGroups(
                (ocg1, ocg2) => NamedConfig().Set(new PdfName("Order"),
                    new PdfArray([new PdfIndirectReference(ocg1), new PdfIndirectReference(ocg2)]))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A catalog /Perms permissions dictionary carrying a key other than /UR3 or /DocMDP
            // (§6.1.12-1). veraPDF and the in-process PermissionsRule both reject it.
            new OracleFixture("pdfa2b-permissions-bad-key", WriterPdfWithCatalogEntry("Perms",
                new PdfDictionary().Set(new PdfName("Foo"), new PdfDictionary().Set(PdfName.Type, new PdfName("Sig")))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A valid embedded-file specification: the attachment IS a compliant PDF/A-2b document and
            // the filespec carries both /F and /UF. Both validators accept it — the no-false-positive
            // guard for the §6.8 embedded-file rule (and §6.8-5 stays satisfied).
            new OracleFixture("pdfa2b-embedded-file", WriterPdfWithEmbeddedFile(includeUf: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // The same embedded-file specification missing /UF (§6.8-2). veraPDF and the in-process
            // EmbeddedFileRule both reject it.
            new OracleFixture("pdfa2b-embedded-file-no-uf", WriterPdfWithEmbeddedFile(includeUf: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An untyped (/Type omitted) file specification reached through the /Names /EmbeddedFiles
            // name tree, missing /UF (§6.8-2). veraPDF and the in-process rule both reject it — the
            // name-tree identification path, which catches filespecs that omit /Type /Filespec.
            new OracleFixture("pdfa2b-embedded-file-untyped-no-uf", WriterPdfWithEmbeddedFile(includeUf: false, typed: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An /EF key on a page dictionary, which is NOT a file specification. Both validators accept
            // it — the regression guard that the §6.8 rule keys on genuine file specifications, not on
            // bare /EF presence (an adversarial false-positive that was found and fixed).
            new OracleFixture("pdfa2b-ef-on-page", WriterPdfWithEfOnPage(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A file specification whose /EF /F stream is a plain (non-PDF/A) PDF: it carries the
            // %PDF- header but has no pdfaid identification. §6.8-5 requires the embedded file to be
            // a valid PDF/A-1 or PDF/A-2 document, so both veraPDF (clause 6.8, testNumber 5) and the
            // in-process EmbeddedFilePdfaRule reject it. The filespec carries both /F and /UF so only
            // §6.8-5 is the isolating violation. Chosen as a clear-cut negative so the in-process and
            // veraPDF verdicts cannot diverge due to the parity gap.
            new OracleFixture("pdfa2b-embedded-nonpdfa", WriterPdfWithNonPdfAEmbeddedFile(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A Link annotation whose flags set the ToggleNoView bit (§6.3.2-2). veraPDF and the
            // in-process AnnotationRule both reject it.
            new OracleFixture("pdfa2b-annot-togglenoview", WriterPdfWithAnnotFlags(1 << 2 | 1 << 8),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A Text annotation whose appearance dictionary (/AP) carries a /D entry besides /N
            // (§6.3.3-2). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-annot-ap-extra-key", WriterPdfWithApExtraKey(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A Widget button field whose /AP /N is a stream instead of an appearance sub-dictionary
            // (§6.3.3-3). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-btn-appearance-stream", WriterPdfWithAppearanceKind("Widget", "Btn", nAsSubDictionary: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same Widget button with /AP /N a sub-dictionary (§6.3.3-3 satisfied) — a guard.
            new OracleFixture("pdfa2b-btn-appearance-dict", WriterPdfWithAppearanceKind("Widget", "Btn", nAsSubDictionary: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A Widget text field (/FT /Tx) whose /AP /N is a sub-dictionary instead of a stream
            // (§6.3.3-4). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-tx-appearance-dict", WriterPdfWithAppearanceKind("Widget", "Tx", nAsSubDictionary: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A /Popup annotation whose /AP /N is a sub-dictionary instead of a stream (§6.3.3-4).
            // veraPDF flags it (Popup has no appearance-kind exemption) and so does the in-process rule.
            new OracleFixture("pdfa2b-popup-appearance-dict", WriterPdfWithAppearanceKind("Popup", null, nAsSubDictionary: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same /Popup with a stream /AP /N — both validators accept it (the regression guard
            // that a Popup keeps its flag/appearance-presence exemptions after the §6.3.3-4 fix).
            new OracleFixture("pdfa2b-popup-appearance-stream", WriterPdfWithAppearanceKind("Popup", null, nAsSubDictionary: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A /Hide action on the catalog /OpenAction (§6.5.1-1). /Hide is one of the action types
            // the deny-list previously missed; the allow-list rejects it, as does veraPDF.
            new OracleFixture("pdfa2b-hide-action", WriterPdfWithOpenAction(
                new PdfDictionary().Set(new PdfName("S"), new PdfName("Hide"))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An unknown/vendor action type on the catalog /OpenAction (§6.5.1-1). veraPDF rejects any
            // action type outside the permitted seven; the allow-list does too (a deny-list would not).
            new OracleFixture("pdfa2b-unknown-action", WriterPdfWithOpenAction(
                new PdfDictionary().Set(new PdfName("S"), new PdfName("VellumFoo"))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // An action dictionary with no /S action-type key (§6.5.1-1). veraPDF rejects it (the
            // permitted-type test fails when /S is absent); the in-process rule now does too — a false
            // negative found by adversarial review and fixed.
            new OracleFixture("pdfa2b-action-no-s", WriterPdfWithOpenAction(
                new PdfDictionary().Set(PdfName.Type, new PdfName("Action"))),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A page content stream that nests 29 q/Q pairs — one beyond the §6.1.13-8 limit of 28.
            // veraPDF (rule Op_q_gsave, test number 8) and the in-process GraphicsStateNestingRule both
            // reject it. The 29 matching Q operators are appended so the stream is well-formed otherwise.
            new OracleFixture("pdfa2b-q-nesting-too-deep", WriterPdfWithDeepQNesting(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A conformant writer baseline with an unreferenced stream whose /Filter is /LZWDecode
            // (ISO 19005-2 §6.1.7.2-1). The stream is not reachable from the document tree, so no
            // decoder is invoked — veraPDF still flags the forbidden filter name (CosFilter check),
            // and so does the in-process StreamRule (which walks the full xref keyspace).
            new OracleFixture("pdfa2b-lzw-filter", WriterPdfWithLzwStream(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // A DeviceN colour space in the first page's /Resources /ColorSpace with 33 colourant
            // names — one above the §6.1.13-9 limit of 32 — that the page paints via `/CS0 cs`. The
            // tint-transform function is a Type 4 PostScript calculator that pops all 33 inputs and
            // pushes 4 (C M Y K) zero outputs, so veraPDF parses it and cleanly reports 6.1.13-9
            // rather than erroring on the function. Both veraPDF (clause 6.1.13, test 9) and the
            // in-process DeviceNColorantRule reject it.
            new OracleFixture("pdfa2b-devicen-33-colourants", WriterPdfWithDeviceN33Colourants(paint: true),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same 33-colourant DeviceN present in /Resources but never painted by content. veraPDF
            // only flags §6.1.13-9 for a colour space that is actually used, so it accepts this file —
            // and so must the in-process rule (the no-false-positive guard for the usage scoping).
            new OracleFixture("pdfa2b-devicen-33-unused", WriterPdfWithDeviceN33Colourants(paint: false),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // An unembedded simple Type1 font (/Helvetica) added to the first page's /Resources /Font
            // under the key /F99, with NO page content selecting it (no `Tf` operator referencing /F99).
            // veraPDF accepts this document — fonts not selected by page content are not validated.
            // The in-process validator must now also accept it (font rules scope to used fonts only,
            // issue #118). This is the regression guard that unused fonts do not produce a false positive.
            new OracleFixture("pdfa2b-unused-nonembedded-font", WriterPdfWithUnusedNonEmbeddedFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A USED DeviceN colour space with 2 spot colourants (/Spot1 /Spot2) where /Spot2 is absent
            // from the /Colorants dictionary (ISO 19005-2 §6.2.4.4-1). veraPDF flags clause 6.2.4.4
            // testNumber 1 and the in-process DeviceNColorantsRule must agree. The negative oracle
            // cross-validates the new rule against veraPDF.
            new OracleFixture("pdfa2b-devicen-missing-colorant",
                WriterPdfWithDeviceNMissingColorant(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same DeviceN with both /Spot1 and /Spot2 present in /Colorants. Both veraPDF and the
            // in-process rule accept it — the no-false-positive guard for §6.2.4.4-1.
            new OracleFixture("pdfa2b-devicen-complete-colorants",
                WriterPdfWithDeviceNCompleteColorants(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // A page content stream containing the unknown keyword 'zz', which is not defined in
            // ISO 32000-1 (§6.2.2-1). veraPDF flags clause 6.2.2 testNumber 1 and the in-process
            // ContentStreamOperatorRule agrees. The hand-built PDF produces exactly one failing rule
            // (confirmed empirically against veraPDF 1.30.2 before implementing the rule).
            new OracleFixture("pdfa2b-unknown-operator",
                HandBuiltPdfWithContent("q zz Q"),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same PDF with the unknown operator bracketed inside BX...EX. §6.2.2-1 explicitly
            // prohibits unknown operators even within compatibility sections. veraPDF and the in-process
            // rule both reject it — confirming the BX/EX brackets do not exempt the operator.
            new OracleFixture("pdfa2b-unknown-operator-in-bxex",
                HandBuiltPdfWithContent("BX zz EX"),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: false),

            // The same structure with only ISO 32000-1 operators inside BX...EX. Both veraPDF and the
            // in-process rule accept it — the no-false-positive guard for the BX/EX handling.
            new OracleFixture("pdfa2b-bxex-standard-operators",
                HandBuiltPdfWithContent("BX q Q EX"),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

            // ── PDF/UA-1 Batch A2 fixtures ──────────────────────────────────────────────────────────

            // §7.18.2-1 (TrapNet annotation): a visible TrapNet annotation inside the crop box is
            // forbidden. Both veraPDF (clause 7.18.2, testNumber 1) and the in-process
            // UaTrapNetAnnotRule reject it.
            new OracleFixture("pdfua1-trapnet-visible",
                WriterUa1WithTrapNetAnnotation(hidden: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.18.2-1 (TrapNet hidden): a TrapNet annotation with the Hidden flag set (F & 2) is
            // exempt — the veraPDF predicate passes and so does the in-process rule.
            // (The document still fails other UA rules like 7.18.3-1 tab-order; this fixture is
            // validated per-clause via --format xml to confirm 7.18.2-1 is NOT among the failures.)
            new OracleFixture("pdfua1-trapnet-hidden",
                WriterUa1WithTrapNetAnnotation(hidden: true),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.18.5-2 (Link annotation without /Contents): a Link without a /Contents entry fails
            // 7.18.5-2. Both veraPDF and the in-process UaLinkAnnotRule reject it.
            new OracleFixture("pdfua1-link-no-contents",
                WriterUa1WithLinkAnnotation(includeContents: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.18.5-2 (Link annotation with /Contents): a Link with a non-empty /Contents entry
            // passes 7.18.5-2. Both veraPDF and the in-process rule agree.
            // (The document still fails 7.18.5-1 and 7.18.3-1 — structure rules — confirmed via XML.)
            new OracleFixture("pdfua1-link-with-contents",
                WriterUa1WithLinkAnnotation(includeContents: true),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.18.1-2 (Non-Widget annotation without /Contents or /Alt): a Text annotation with
            // neither /Contents nor /Alt fails 7.18.1-2. Both veraPDF and the in-process
            // UaAnnotContentsRule reject it.
            new OracleFixture("pdfua1-annot-no-contents",
                WriterUa1WithTextAnnotation(includeContents: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.18.1-2 (Non-Widget annotation with /Contents): a Text annotation with a non-empty
            // /Contents passes 7.18.1-2. (Still fails 7.18.1-1 and 7.18.3-1 — structure rules.)
            new OracleFixture("pdfua1-annot-with-contents",
                WriterUa1WithTextAnnotation(includeContents: true),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.18.2-1 / §7.18.5-2 / §7.18.1-2 (outside crop box): a Link annotation whose /Rect
            // is entirely outside the page's MediaBox is exempt from ALL §7.18 requirements.
            // veraPDF passes 7.18.5-2 for such a Link even without /Contents — confirmed empirically.
            // The overall document still has other failures (7.18.3-1 tab order); this is a
            // per-clause guard that only 7.18.5-2 and 7.18.1-2 do NOT fire.
            new OracleFixture("pdfua1-link-outside-cropbox",
                WriterUa1WithAnnotOutsideCropBox(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.20-1 (reference XObject): a drawn Form XObject with a /Ref entry fails 7.20-1.
            // Both veraPDF (clause 7.20, testNumber 1) and the in-process UaReferenceXObjectRule
            // reject it.
            new OracleFixture("pdfua1-ref-xobject",
                WriterUa1WithReferenceXObject(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.11-1 (embedded file without /UF): a file-specification with /EF but no /UF fails
            // 7.11-1. Both veraPDF (clause 7.11, testNumber 1) and the in-process
            // UaEmbeddedFileRule reject it.
            new OracleFixture("pdfua1-embedded-no-uf",
                WriterUa1WithEmbeddedFile(includeUf: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.11-1 (embedded file with /F and /UF): a file-specification with /EF, /F, and /UF
            // passes 7.11-1. Both veraPDF and the in-process rule accept it.
            new OracleFixture("pdfua1-embedded-with-uf",
                WriterUa1WithEmbeddedFile(includeUf: true),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §7.10-1 (OC config without /Name): an optional-content configuration dictionary
            // without a /Name fails 7.10-1. Both veraPDF and the in-process UaOptionalContentRule
            // reject it.
            new OracleFixture("pdfua1-oc-no-name",
                WriterUa1WithOptionalContent(hasName: false, hasAs: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.10-2 (OC config with /AS): an optional-content configuration dictionary with an
            // /AS entry fails 7.10-2. Both veraPDF and the in-process rule reject it.
            new OracleFixture("pdfua1-oc-with-as",
                WriterUa1WithOptionalContent(hasName: true, hasAs: true),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.10 (OC config valid): a configuration with a non-empty /Name and no /AS passes
            // 7.10-1 and 7.10-2. Both veraPDF and the in-process rule accept it.
            new OracleFixture("pdfua1-oc-valid",
                WriterUa1WithOptionalContent(hasName: true, hasAs: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §7.15-1 (dynamic XFA): an AcroForm with an XFA stream containing
            // xdp:xdp > config > acrobat > acrobat7 > dynamicRender = "required"
            // fails 7.15-1. Both veraPDF and the in-process UaXfaRule reject it.
            new OracleFixture("pdfua1-xfa-dynamic",
                WriterUa1WithXfa(dynamic: true),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.15-1 (static XFA): the same structure with dynamicRender = "forbidden" passes
            // 7.15-1. Both veraPDF and the in-process rule accept it (the document may still fail
            // other rules if the XFA template triggers them; these are confirmed via XML).
            new OracleFixture("pdfua1-xfa-static",
                WriterUa1WithXfa(dynamic: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // ── Batch A3 — font clause §7.21 fixtures ────────────────────────────────────────────

            // §7.21.3.2-1: embedded CIDFontType2 must carry /CIDToGIDMap.
            // The baseline document has a real Type0/CIDFontType2 font with /CIDToGIDMap /Identity.
            // veraPDF accepts the baseline and rejects the variant with /CIDToGIDMap removed.
            new OracleFixture("pdfua1-cidtogidmap-compliant",
                Ua1TaggedWithEmbeddedFont(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),
            new OracleFixture("pdfua1-cidtogidmap-missing",
                Ua1EmbeddedFontNoCidToGidMap(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.6-3: a symbolic TrueType font (Flags bit 3 = Symbolic) must have no /Encoding.
            // The failing fixture adds /Encoding /WinAnsiEncoding to a symbolic (Flags=4) TrueType
            // font. DejaVu has 5 cmap subtables but NO (3,0) — both veraPDF and the in-process
            // UaSymbolicFontRule+UaTrueTypeCmapRule fire 7.21.6-3 and 7.21.6-4.
            // The 7.21.6-3 FP guard (symbolic, no encoding) is tested as a unit test only.
            // The 7.21.6-4 FP guard (1 cmap, (3,0) present) is Ua1SymbolicFontNoEncodingOneCmap.
            new OracleFixture("pdfua1-symbolic-font-with-encoding",
                Ua1SymbolicFontWithEncoding(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // ── Batch A6 — §7.21.6-1/-2/-4 TrueType cmap / Differences-compliance fixtures ─────────

            // §7.21.6-1 VIOLATION: a non-symbolic TrueType font (Flags=32, WinAnsiEncoding) whose
            // embedded cmap has been patched to a single Microsoft Symbol (3,0) subtable. veraPDF
            // fires 7.21.6-1 (isSymbolic==false, cmap30Present==true, nrCmaps==1 → not > 1).
            // In-process: UaTrueTypeCmapRule fires 7.21.6-1.
            new OracleFixture("pdfua1-nonsymb-symbol-only-cmap",
                Ua1NonSymbolicTrueTypeSymbolOnlyCmap(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.6-2 VIOLATION: a non-symbolic TrueType font with /Differences containing /BADNAME_XYZ
            // (not in the Adobe Glyph List). veraPDF fires 7.21.6-2 (differencesAreUnicodeCompliant==false).
            // In-process: UaTrueTypeCmapRule fires 7.21.6-2.
            new OracleFixture("pdfua1-nonsymb-bad-differences",
                Ua1NonSymbolicTrueTypeBadDifferences(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.6-1 and §7.21.6-2 VIOLATION: a non-symbolic TrueType font with AGL-compliant
            // /Differences (/Alpha at code 65) but whose cmap is patched to symbol-only (3,0).
            // veraPDF fires both 7.21.6-1 (symbol-only cmap for non-symbolic font) and 7.21.6-2
            // (differencesAreUnicodeCompliant requires the (3,1) cmap).
            // In-process: UaTrueTypeCmapRule fires both 7.21.6-1 and 7.21.6-2.
            new OracleFixture("pdfua1-nonsymb-agl-diff-bad-cmap",
                Ua1NonSymbolicTrueTypeAglDiffBadCmap(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // (§7.21.7-2 ToUnicode-forbidden-value fixtures were removed together with the rule: the
            // whole-CMap scan over-rejected an UNUSED forbidden mapping that veraPDF — which validates
            // only glyphs actually shown — accepts. Deferred pending shown-glyph-code extraction.)

            // ── Batch A4 — font clause §7.21 fixtures ────────────────────────────────────────────

            // §7.21.3.3-1: a composite font's /Encoding must be a predefined CMap name or an embedded
            // CMap stream. Using a non-predefined name (/FooBarCMap) triggers both veraPDF and the
            // in-process UaCMapRule. The compliant path uses the existing baseline (Identity-H).
            new OracleFixture("pdfua1-bad-cmap-name",
                Ua1BadCMapName(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.4.2-1: an embedded subset Type1 font with /CharSet that omits a glyph.
            // The Noto Sans Shavian PFB is embedded in a UA-1-tagged document using the invisible-draw
            // trick (Tr 3). veraPDF fires 7.21.4.2-1 (and 7.1-3 / 7.21.7-1 for the untagged content
            // and missing ToUnicode — deferred infrastructure gaps). In-process: 7.21.4.2-1 fires.
            // The no-false-positive guard (complete CharSet) is an in-process unit test only: the
            // compliant-Type1 document would pass our rules but fail other unimplemented UA-1 rules,
            // so a veraPDF-gated oracle fixture would show a verdict mismatch — unit test is sufficient.
            new OracleFixture("pdfua1-type1-charset-incomplete",
                WriterPdfWithType1CharSetUa1(complete: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.4.2-2: an embedded subset CIDFontType2 with /CIDSet must list all present CIDs.
            // The DejaVu subset from Ua1TaggedWithEmbeddedFont() gets a /CIDSet injected. When
            // incomplete (single zero byte) veraPDF fires 7.21.4.2-2; when correct it accepts.
            new OracleFixture("pdfua1-cidset-complete",
                WriterPdfWithCidSetUa1(complete: true),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),
            new OracleFixture("pdfua1-cidset-incomplete",
                WriterPdfWithCidSetUa1(complete: false),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // ── Batch A5a — §7.21.4.1-1 rendering-mode-scoped font embedding fixtures ────────────

            // §7.21.4.1-1 VIOLATION: a non-embedded simple TrueType font drawn with a visible
            // rendering mode (default Tr 0). veraPDF fires 7.21.4.1-1 (containsFontFile == false
            // AND renderingMode != 3) and the in-process UaFontEmbeddingRule must agree.
            new OracleFixture("pdfua1-nonembedded-font-visible",
                Ua1NonEmbeddedFontVisibleDraw(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.4.1-1 COMPLIANT (Tr 3 exemption) and §7.21.4.1-1 COMPLIANT (embedded font):
            // These fixtures are tested as unit tests only (not oracle fixtures): the compliant
            // documents fail OTHER UA-1 rules (7.1-3 untagged text), so veraPDF reports non-compliant
            // for those reasons while the in-process validator (which defers 7.1-3) reports compliant
            // — the verdicts diverge for rule-gap reasons unrelated to §7.21.4.1-1. The unit tests verify
            // the specific absence of the §7.21.4.1-1 finding, matching the Batch A4 pattern used
            // for the Type1-CharSet-complete and CIDSet-complete FP-guards.

            // ── Batch A5b — §7.21.8-1 .notdef + §7.21.7-2 forbidden-ToUnicode ──────────────────────

            // §7.21.8-1 VIOLATION: appends a content stream that shows glyph index 0 (0x0000 = .notdef)
            // with the existing Identity-H CIDFontType2 font. veraPDF fires 7.21.8-1 (.notdef
            // referenced). In-process: UaNotdefGlyphRule must also fire.
            // (The extra untagged content stream also triggers 7.1-3 in veraPDF; the fixture is
            // ExpectedCompliant: false so any non-compliance satisfies the oracle.)
            new OracleFixture("pdfua1-notdef-glyph",
                Ua1PdfDrawingNotdef(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.7-2 VIOLATION: the Type0 font's /ToUnicode maps a SHOWN code to U+0000. veraPDF
            // fires 7.21.7-2. In-process: UaToUnicodeForbiddenRule must also fire.
            // (The extra untagged content stream also triggers 7.1-3; fixture is non-compliant overall.)
            new OracleFixture("pdfua1-tounicode-forbidden-shown",
                Ua1ToUnicodeForbiddenShownCode(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.7-2 REGRESSION GUARD: the Type0 font's /ToUnicode maps an UNUSED code (0xFFFF)
            // to U+0000; the SHOWN code (0x0041) maps to U+0041 (valid). veraPDF must NOT fire
            // 7.21.7-2 (only shown codes are checked). In-process: UaToUnicodeForbiddenRule must also
            // NOT fire. Both verdicts diverge on unrelated rules (untagged content → 7.1-3 in veraPDF,
            // which the in-process validator defers), so this is tested as a unit test only — see
            // UaToUnicodeUnusedBadMapping_DoesNotFire72172() in PdfPreflightTests.cs.

            // ── PDF/UA-1 Batch A5c — §7.21.4.1-2 glyph presence (Tr-3-exempt) fixtures ─────────────

            // §7.21.4.1-2 violation: a composite Identity-H CIDFontType2 font shows GID 0xEA60
            // (60000, beyond the embedded program's glyph count) in a VISIBLE rendering mode (Tr 0).
            // veraPDF fires clause 7.21.4.1-2 (isGlyphPresent == false, renderingMode != 3).
            // In-process: UaGlyphPresenceRule fires.
            new OracleFixture("pdfua1-glyph-not-present",
                Ua1OutOfRangeGlyphVisible(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.21.4.1-2 compliant baseline: the standard UA-1 tagged baseline draws only glyphs
            // that are present in the embedded subset (in-range GIDs). Both veraPDF and the in-process
            // UaGlyphPresenceRule accept it — the positive control confirming no false positive on
            // a well-formed embedded-font document.
            new OracleFixture("pdfua1-glyph-presence-compliant",
                Ua1TaggedWithEmbeddedFont(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §7.21.4.1-2 FP-SAFETY guard (Tr-3 exemption): the SAME out-of-range GID (0xEA60) is
            // drawn ONLY with text rendering mode 3 (invisible text). veraPDF does NOT fire
            // 7.21.4.1-2 (renderingMode == 3 → exempt per predicate). In-process: UaGlyphPresenceRule
            // must also NOT fire. The overall document fails other UA rules (7.21.7-1, 7.21.8-1)
            // that veraPDF fires but the in-process validator defers — verdicts diverge on unrelated
            // rules, so this is validated per-clause in a unit test only; see
            // UaOutOfRangeGlyphInvisible_DoesNotFire7214121() in PdfPreflightTests.cs.
            // (Not in the oracle All list to avoid the in-process / veraPDF verdict mismatch.)

            // ── PDF/UA-1 Batch A5d — §7.21.5-1 glyph width consistency (Tr-3-exempt) ─────────────

            // §7.21.5-1 violation: the UA-1 tagged baseline's CIDFontType2 has /W removed so all
            // shown glyphs fall to /DW=1000, while the hmtx advances differ by more than 1.
            // veraPDF fires clause 7.21.5-1 (19 failed checks, one per shown glyph). In-process:
            // UaGlyphWidthRule fires. Cross-validated against veraPDF 1.30.2.
            new OracleFixture("pdfua1-glyph-width-mismatch",
                Ua1GlyphWidthMismatch(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // ── PDF/UA-1 Batch B1 — structure-tree walker foundation (§7.1) ────────────────────────

            // §7.1-12 VIOLATION: a StructElem (the leaf /P element) has its /P (parent pointer) entry
            // removed. veraPDF cannot trace the content back to the tagged structure tree and fires
            // 7.1-3 (content not tagged) or 7.1-12 — either way, exit 1 = non-compliant. In-process:
            // UaStructElemParentRule fires 7.1-12. Oracle confirms exit 1 (ExpectedCompliant: false).
            new OracleFixture("pdfua1-structelem-missing-parent",
                Ua1StructElemMissingParent(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-6 VIOLATION: a circular RoleMap (/Foo→/Bar→/Foo) plus a StructElem with /S /Foo.
            // veraPDF evaluates circularMappingExist on the /Foo element and fires 7.1-6.
            // In-process: UaRoleMapRule fires "ISO14289-1:7.1-6". Oracle: ExpectedCompliant: false.
            new OracleFixture("pdfua1-circular-rolemap",
                Ua1CircularRoleMap(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-7 VIOLATION: /RoleMap << /Table /Div >> remaps a standard type, plus a StructElem
            // with /S /Table. veraPDF evaluates remappedStandardType on the /Table element and fires
            // 7.1-7. In-process: UaRoleMapRule fires "ISO14289-1:7.1-7". Oracle: ExpectedCompliant: false.
            new OracleFixture("pdfua1-standard-type-remapped",
                Ua1StandardTypeRemapped(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-5 VIOLATION: a StructElem with /S /MyCustomTag and no /RoleMap mapping. veraPDF
            // fires 7.1-5 (isNotMappedToStandardType == true). In-process: UaNonStandardTypeRule fires
            // "ISO14289-1:7.1-5". Oracle: ExpectedCompliant: false.
            new OracleFixture("pdfua1-non-standard-type-unmapped",
                Ua1NonStandardTypeUnmapped(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // ── Batch B10 — §7.4.2-1 heading-nesting + §7.5-1/-2 connected-header fixtures ──────────

            // §7.4.2-1 VIOLATION: H1 then H3 (skipping H2). veraPDF fires 7.4.2-1
            // (hasCorrectNestingLevel == false on the H3 element). In-process: UaHeadingNestingRule fires.
            new OracleFixture("pdfua1-heading-skip-h1-h3",
                Ua1HeadingsSkip(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.4.2-1 COMPLIANT: H1 then H2 then H3 (no skip). veraPDF does not fire 7.4.2-1.
            // In-process: UaHeadingNestingRule must NOT fire. (The document is otherwise the tagged
            // baseline — all other UA rules pass too.)
            new OracleFixture("pdfua1-heading-no-skip",
                Ua1HeadingsNoSkip(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §7.5-1 VIOLATION: a Table with a TH (no /Scope) and a TD (no /Headers). veraPDF fires
            // 7.5-1 (hasConnectedHeader == false, unknownHeaders == ''). In-process: UaTableHeaderRule fires.
            new OracleFixture("pdfua1-td-no-connected-header",
                Ua1TdNoConnectedHeader(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.5-2 VIOLATION: a TD with /Headers referencing an ID that does not exist on any TH.
            // veraPDF fires 7.5-2 (hasConnectedHeader == false, unknownHeaders != ''). In-process: fires.
            new OracleFixture("pdfua1-td-unknown-header-id",
                Ua1TdUnknownHeaderId(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.5 COMPLIANT: a Table with TH /Scope /Column and TD (no /Headers). veraPDF does not
            // fire 7.5-1 or 7.5-2. In-process: UaTableHeaderRule must NOT fire.
            // (The document is otherwise the tagged baseline — all other UA rules pass too.)
            new OracleFixture("pdfua1-td-scoped-header",
                Ua1TdScopedHeader(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: true),

            // §7.2-34 VIOLATION: tagged UA-1 with the catalog /Lang key removed entirely (absent,
            // not empty — so gContainsCatalogLang == false in veraPDF terms), with text shows inside
            // a /P BDC that carries no /Lang property. veraPDF fires 7.2-34 (SETextItem natural
            // language cannot be determined). In-process UaMarkedContentLangRule must also fire.
            // NOTE: this doc also fails 7.2-lang (absent /Lang), so veraPDF reports multiple
            // failures; we check only the boolean verdict here. Clause-level evidence:
            // ~/verapdf/verapdf --flavour ua1 --format xml FILE | grep testNumber=\"34\"
            new OracleFixture("pdfua1-mc-text-no-lang",
                Ua1McTextNoLang(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.2-30 VIOLATION: tagged UA-1 with the catalog /Lang key removed entirely (absent,
            // not empty), containing a /Span BDC with /ActualText and no /Lang. veraPDF fires 7.2-30
            // (SEMarkedContent ActualText no lang). In-process UaMarkedContentLangRule must also fire.
            new OracleFixture("pdfua1-mc-span-actualtext-no-lang",
                Ua1McSpanActualTextNoLang(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.2-34 REGRESSION (FP fix): tagged UA-1 with NO catalog /Lang, but the /P struct
            // element (reached via MCID→ParentTree) has /Lang (en-US). veraPDF does NOT fire 7.2-34;
            // the fixed in-process rule must also NOT fire. This fixture pins the confirmed FP fix.
            // NOTE: veraPDF fires 7.2-lang (missing catalog /Lang) and 7.2-33 (XMP x-default title
            // with no catalog /Lang), so the document is non-compliant — but NOT due to 7.2-34.
            new OracleFixture("pdfua1-mc-text-struct-elem-lang",
                Ua1McTextStructElemLang(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-1 VIOLATION: Artifact BMC nested inside a /P BDC whose MCID (0) is linked to a
            // struct element in the /ParentTree. veraPDF fires 7.1-1 (and 7.1-2). In-process
            // UaArtifactTaggingRule must also fire.
            // veraPDF grep: clause="7.1" testNumber="1" status="failed"
            new OracleFixture("pdfua1-artifact-in-tagged",
                Ua1ArtifactInTagged(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-2 VIOLATION: /P BDC with MCID (0) linked to a struct element is nested inside
            // an /Artifact BMC. veraPDF fires 7.1-2. In-process UaArtifactTaggingRule must also fire.
            // veraPDF grep: clause="7.1" testNumber="2" status="failed"
            new OracleFixture("pdfua1-tagged-in-artifact",
                Ua1TaggedInArtifact(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.2-34 REGRESSION GUARD — named-reference BDC FP fix: tagged UA-1 with NO catalog /Lang,
            // content tagged via /P /MC0 BDC (named-reference form — not inline dict), where
            // /Resources/Properties/MC0 = << /MCID 0 >>, and the P struct element has /Lang (en-US).
            // veraPDF does NOT fire 7.2-34 (struct-elem /Lang covers the content via MCID→ParentTree).
            // Pre-fix the in-process rule fired 7.2-34 (false positive — named-ref MCID was not
            // resolved, so struct-elem /Lang was never checked). Post-fix: silent on 7.2-34.
            // Both veraPDF and the in-process validator reject the document for other reasons (7.2-lang,
            // 7.2-33), so ExpectedCompliant: false.
            // Cross-validated against veraPDF 1.30.2: clause 7.2 testNumber 34 is NOT in the failures.
            new OracleFixture("pdfua1-mc-named-ref-struct-elem-lang",
                Ua1McTextNamedRefBdcStructElemLang(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),

            // §7.1-3 VIOLATION: the tagged UA-1 baseline with an additional content stream appended
            // that paints a path (S operator) outside any BDC — untagged real content.
            // veraPDF fires clause 7.1 testNumber 3 (SESimpleContentItem).
            // In-process UaSimpleContentItemRule fires "ISO14289-1:7.1-3".
            new OracleFixture("pdfua1-untagged-real-content",
                Ua1UntaggedRealContent(),
                Conformance.PdfConformance.PdfUA1, "ua1", ExpectedCompliant: false),
        ];
    }

    /// <summary>
    /// Injects a DeviceN colour space with 33 colourant names into the first page's
    /// <c>/Resources /ColorSpace</c> via an incremental update on a conformant baseline. When
    /// <paramref name="paint"/> is true the page also gets a content stream that selects the space
    /// (<c>/CS0 cs 0 … 0 scn</c>) and fills a rectangle; when false the space is present but unused.
    /// The tint-transform function is a Type 4 PostScript calculator that pops all 33 inputs from the
    /// operand stack and pushes four zero literals (one per CMYK channel), so veraPDF can parse and
    /// execute the function and reaches the colourant-count check cleanly.
    /// </summary>
    /// <remarks>
    /// veraPDF only flags ISO 19005-2 §6.1.13-9 when the DeviceN colour space is actually painted by
    /// content; a present-but-unused space is not flagged. The two variants (painted / unused) pin
    /// both sides of that usage scoping against the oracle.
    /// </remarks>
    private static byte[] WriterPdfWithDeviceN33Colourants(bool paint)
    {
        const int n = 33;
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        // Type 4 PostScript calculator: pop all n inputs, push 0 0 0 0 for CMYK.
        // The body "{ pop pop ... pop 0 0 0 0 }" balances the operand stack.
        var tintBody = Encoding.ASCII.GetBytes("{ " + string.Concat(Enumerable.Repeat("pop ", n)) + "0 0 0 0 }");

        // /Domain: [0 1] repeated n times; /Range: [0 1] four times for CMYK.
        var domainValues = Enumerable.Range(0, n * 2).Select(i => (PdfObject)new PdfInteger(i % 2)).ToArray();
        var rangeValues = new PdfObject[] {
            new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1),
            new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1) };

        var tintFuncNum = reader.Size;
        var tintFunc = new PdfStream(tintBody);
        tintFunc.Dictionary
            .Set(new PdfName("FunctionType"), new PdfInteger(4))
            .Set(new PdfName("Domain"), new PdfArray(domainValues))
            .Set(new PdfName("Range"), new PdfArray(rangeValues));

        // Build the colourant-names array: /c0 /c1 ... /c{n-1}.
        var names = new PdfArray(Enumerable.Range(0, n).Select(i => (PdfObject)new PdfName("c" + i)).ToArray());

        // [/DeviceN [/c0../c32] /DeviceCMYK <tintFunc>]
        var csNum = tintFuncNum + 1;
        var csArray = new PdfArray([
            new PdfName("DeviceN"),
            names,
            new PdfName("DeviceCMYK"),
            new PdfIndirectReference(tintFuncNum),
        ]);

        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var newResources = CloneDict(resources);
        newResources.Set(
            new PdfName("ColorSpace"),
            new PdfDictionary().Set(new PdfName("CS0"), new PdfIndirectReference(csNum)));
        newPage.Set(new PdfName("Resources"), newResources);

        var revision = new List<(int, PdfObject)>
        {
            (pageRef.ObjectNumber, newPage), (tintFuncNum, tintFunc), (csNum, csArray),
        };

        if (paint)
        {
            // Activate CS0 as the fill colour space, set all 33 components to 0, fill a rectangle.
            var zeros = string.Join(" ", Enumerable.Repeat("0", n));
            var contentNum = csNum + 1;
            newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));
            revision.Add((contentNum, new PdfStream(Encoding.ASCII.GetBytes($"/CS0 cs {zeros} scn 10 10 50 50 re f"))));
        }

        return reader.AppendRevision(revision);
    }

    /// <summary>
    /// Injects a USED DeviceN colour space with 2 spot colourants (<c>/Spot1 /Spot2</c>) into the
    /// first page's <c>/Resources /ColorSpace</c>. The <c>/Colorants</c> dictionary includes an entry
    /// for <c>/Spot1</c> only; <c>/Spot2</c> is absent. The page paints the space via
    /// <c>/CS0 cs 0 0 scn 10 10 50 50 re f</c>. Both veraPDF (clause 6.2.4.4, testNumber 1) and the
    /// in-process <c>DeviceNColorantsRule</c> flag it.
    /// </summary>
    private static byte[] WriterPdfWithDeviceNMissingColorant()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        // Two-input tint function: pop /Spot1 and /Spot2 inputs → 0 0 0 0 (CMYK).
        var tintBody = Encoding.ASCII.GetBytes("{ pop pop 0 0 0 0 }");
        var tintFuncNum = reader.Size;
        var tintFunc = new PdfStream(tintBody);
        tintFunc.Dictionary
            .Set(new PdfName("FunctionType"), new PdfInteger(4))
            .Set(new PdfName("Domain"), new PdfArray([
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1)]))
            .Set(new PdfName("Range"), new PdfArray([
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1),
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1)]));

        // Single-input tint function for the Spot1 Separation entry in /Colorants.
        var spot1TintNum = tintFuncNum + 1;
        var spot1Tint = new PdfStream(Encoding.ASCII.GetBytes("{ pop 0 0 0 0 }"));
        spot1Tint.Dictionary
            .Set(new PdfName("FunctionType"), new PdfInteger(4))
            .Set(new PdfName("Domain"), new PdfArray([new PdfInteger(0), new PdfInteger(1)]))
            .Set(new PdfName("Range"), new PdfArray([
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1),
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1)]));

        // /Colorants dict: /Spot1 only (Spot2 deliberately absent → violation).
        var spot1Sep = new PdfArray([
            new PdfName("Separation"),
            new PdfName("Spot1"),
            new PdfName("DeviceCMYK"),
            new PdfIndirectReference(spot1TintNum),
        ]);
        var colorantsDict = new PdfDictionary()
            .Set(new PdfName("Spot1"), spot1Sep);

        // Attributes dict with /Colorants.
        var attrsDict = new PdfDictionary()
            .Set(new PdfName("Subtype"), new PdfName("DeviceN"))
            .Set(new PdfName("Colorants"), colorantsDict);

        // DeviceN array: [/DeviceN [/Spot1 /Spot2] /DeviceCMYK <tintFunc> <attrsDict>]
        var csNum = spot1TintNum + 1;
        var csArray = new PdfArray([
            new PdfName("DeviceN"),
            new PdfArray([new PdfName("Spot1"), new PdfName("Spot2")]),
            new PdfName("DeviceCMYK"),
            new PdfIndirectReference(tintFuncNum),
            attrsDict,
        ]);

        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var newResources = CloneDict(resources);
        newResources.Set(
            new PdfName("ColorSpace"),
            new PdfDictionary().Set(new PdfName("CS0"), new PdfIndirectReference(csNum)));
        newPage.Set(new PdfName("Resources"), newResources);

        var contentNum = csNum + 1;
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));
        var content = new PdfStream(Encoding.ASCII.GetBytes("/CS0 cs 0 0 scn 10 10 50 50 re f"));

        return reader.AppendRevision([
            (pageRef.ObjectNumber, newPage),
            (tintFuncNum, tintFunc),
            (spot1TintNum, spot1Tint),
            (csNum, csArray),
            (contentNum, content),
        ]);
    }

    /// <summary>
    /// Same as <see cref="WriterPdfWithDeviceNMissingColorant"/> but with both <c>/Spot1</c> and
    /// <c>/Spot2</c> present in the <c>/Colorants</c> dictionary. Both veraPDF and the in-process
    /// <c>DeviceNColorantsRule</c> accept it — the no-false-positive guard for §6.2.4.4-1.
    /// </summary>
    private static byte[] WriterPdfWithDeviceNCompleteColorants()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        // Two-input tint function for DeviceN → DeviceRGB (3 outputs). Using DeviceRGB as the
        // alternate space avoids the §6.2.4.3-3 violation that arises when DeviceCMYK is used as
        // the alternate space without a CMYK output intent — the writer baseline has an sRGB intent.
        var tintBody = Encoding.ASCII.GetBytes("{ pop pop 0 0 0 }");
        var tintFuncNum = reader.Size;
        var tintFunc = new PdfStream(tintBody);
        tintFunc.Dictionary
            .Set(new PdfName("FunctionType"), new PdfInteger(4))
            .Set(new PdfName("Domain"), new PdfArray([
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1)]))
            .Set(new PdfName("Range"), new PdfArray([
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1),
                new PdfInteger(0), new PdfInteger(1)]));

        // Single-input tint functions for Spot1 and Spot2 Separation entries → DeviceRGB.
        var spot1TintNum = tintFuncNum + 1;
        var spot1Tint = new PdfStream(Encoding.ASCII.GetBytes("{ pop 0 0 0 }"));
        spot1Tint.Dictionary
            .Set(new PdfName("FunctionType"), new PdfInteger(4))
            .Set(new PdfName("Domain"), new PdfArray([new PdfInteger(0), new PdfInteger(1)]))
            .Set(new PdfName("Range"), new PdfArray([
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1),
                new PdfInteger(0), new PdfInteger(1)]));

        var spot2TintNum = spot1TintNum + 1;
        var spot2Tint = new PdfStream(Encoding.ASCII.GetBytes("{ pop 0 0 0 }"));
        spot2Tint.Dictionary
            .Set(new PdfName("FunctionType"), new PdfInteger(4))
            .Set(new PdfName("Domain"), new PdfArray([new PdfInteger(0), new PdfInteger(1)]))
            .Set(new PdfName("Range"), new PdfArray([
                new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1),
                new PdfInteger(0), new PdfInteger(1)]));

        // /Colorants dict: both spots present (Separation → DeviceRGB, matching the DeviceN alternate).
        var colorantsDict = new PdfDictionary()
            .Set(new PdfName("Spot1"), new PdfArray([
                new PdfName("Separation"), new PdfName("Spot1"),
                new PdfName("DeviceRGB"), new PdfIndirectReference(spot1TintNum)]))
            .Set(new PdfName("Spot2"), new PdfArray([
                new PdfName("Separation"), new PdfName("Spot2"),
                new PdfName("DeviceRGB"), new PdfIndirectReference(spot2TintNum)]));

        var attrsDict = new PdfDictionary()
            .Set(new PdfName("Subtype"), new PdfName("DeviceN"))
            .Set(new PdfName("Colorants"), colorantsDict);

        var csNum = spot2TintNum + 1;
        var csArray = new PdfArray([
            new PdfName("DeviceN"),
            new PdfArray([new PdfName("Spot1"), new PdfName("Spot2")]),
            new PdfName("DeviceRGB"),
            new PdfIndirectReference(tintFuncNum),
            attrsDict,
        ]);

        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var newResources = CloneDict(resources);
        newResources.Set(
            new PdfName("ColorSpace"),
            new PdfDictionary().Set(new PdfName("CS0"), new PdfIndirectReference(csNum)));
        newPage.Set(new PdfName("Resources"), newResources);

        var contentNum = csNum + 1;
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));
        var content = new PdfStream(Encoding.ASCII.GetBytes("/CS0 cs 0 0 scn 10 10 50 50 re f"));

        return reader.AppendRevision([
            (pageRef.ObjectNumber, newPage),
            (tintFuncNum, tintFunc),
            (spot1TintNum, spot1Tint),
            (spot2TintNum, spot2Tint),
            (csNum, csArray),
            (contentNum, content),
        ]);
    }

    /// <summary>
    /// Injects an unembedded simple Type 1 font (<c>/Helvetica</c>) into the first page's
    /// <c>/Resources /Font</c> under the key <c>/F99</c> via an incremental update on a conformant
    /// baseline. The page content is NOT changed — no <c>Tf</c> operator references <c>/F99</c> —
    /// so the font is present in resources but never selected by page content.
    /// </summary>
    /// <remarks>
    /// veraPDF only validates fonts that are actually used (selected by the current graphics state).
    /// This fixture has an unembedded font that veraPDF therefore ignores, so it accepts the file.
    /// The in-process validator must agree — this is the regression guard for issue #118 that prevents
    /// unused fonts from triggering a false positive in the font-embedding or font-structure rules.
    /// </remarks>
    private static byte[] WriterPdfWithUnusedNonEmbeddedFont()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        // Build a bare unembedded Type1 font — no /FontDescriptor, no /FontFile.
        var font = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font"))
            .Set(PdfName.Subtype, new PdfName("Type1"))
            .Set(PdfName.BaseFont, new PdfName("Helvetica"));

        var fontNum = reader.Size;

        // Inject the font into the page resources under key /F99, which nothing in the page
        // content ever references with a Tf operator.
        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var newResources = CloneDict(resources);
        newResources.Set(
            PdfName.Font,
            new PdfDictionary().Set(new PdfName("F99"), new PdfIndirectReference(fontNum)));
        newPage.Set(new PdfName("Resources"), newResources);

        // No /Contents update — the page has no content selecting /F99.
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (fontNum, font)]);
    }

    private static byte[] WriterPdfWithDrawnPostScriptXObject()
        => WriterPdfWithDrawnXObject(d => d.Set(PdfName.Subtype, new PdfName("PS")), []);

    private static byte[] WriterPdfWithDrawnInterpolatedImage()
        => WriterPdfWithDrawnXObject(ConfigureImage(d => d.Set(new PdfName("Interpolate"), PdfBoolean.True)), [0]);

    private static byte[] WriterPdfWithDrawnImageOpi()
        => WriterPdfWithDrawnXObject(ConfigureImage(d => d.Set(new PdfName("OPI"), new PdfDictionary())), [0]);

    private static byte[] WriterPdfWithDrawnFormOpi()
        => WriterPdfWithDrawnXObject(ConfigureForm(d => d.Set(new PdfName("OPI"), new PdfDictionary())), []);

    private static byte[] WriterPdfWithDrawnReferenceXObject()
        => WriterPdfWithDrawnXObject(ConfigureForm(d => d.Set(new PdfName("Ref"), new PdfDictionary())), []);

    // A 1×1 DeviceGray image XObject, plus a caller-supplied forbidden key.
    private static Action<PdfDictionary> ConfigureImage(Action<PdfDictionary> extra) => d =>
    {
        d.Set(PdfName.Subtype, new PdfName("Image"))
            .Set(new PdfName("Width"), new PdfInteger(1))
            .Set(new PdfName("Height"), new PdfInteger(1))
            .Set(new PdfName("BitsPerComponent"), new PdfInteger(8))
            .Set(new PdfName("ColorSpace"), new PdfName("DeviceGray"));
        extra(d);
    };

    // A form XObject (with the required /BBox), plus a caller-supplied forbidden key.
    private static Action<PdfDictionary> ConfigureForm(Action<PdfDictionary> extra) => d =>
    {
        d.Set(PdfName.Subtype, new PdfName("Form"))
            .Set(new PdfName("BBox"),
                new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)]));
        extra(d);
    };

    /// <summary>
    /// Injects an XObject (configured by <paramref name="configureXObject"/>, with raw body
    /// <paramref name="body"/>) into the first page's /Resources /XObject as /X0 and draws it from
    /// the page content (<c>/X0 Do</c>), via an incremental update on a conformant baseline. The
    /// drawn invocation is what brings the XObject into both validators' content-usage models.
    /// </summary>
    private static byte[] WriterPdfWithDrawnXObject(Action<PdfDictionary> configureXObject, byte[] body)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        var xobjNum = reader.Size;
        var contentNum = xobjNum + 1;

        var xobject = new PdfStream(body);
        xobject.Dictionary.Set(PdfName.Type, new PdfName("XObject"));
        configureXObject(xobject.Dictionary);

        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var newResources = CloneDict(resources);
        newResources.Set(
            new PdfName("XObject"),
            new PdfDictionary().Set(new PdfName("X0"), new PdfIndirectReference(xobjNum)));
        newPage.Set(new PdfName("Resources"), newResources);
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));

        var content = new PdfStream(Encoding.ASCII.GetBytes("q /X0 Do Q"));
        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (xobjNum, xobject), (contentNum, content)]);
    }

    private static byte[] WriterPdfWithTransferFunction()
        => WriterPdfWithAppliedExtGState(new PdfDictionary()
            .Set(PdfName.Type, new PdfName("ExtGState")).Set(new PdfName("TR"), new PdfName("Identity")));

    private static byte[] WriterPdfWithBadHalftoneType()
        => WriterPdfWithAppliedExtGState(new PdfDictionary()
            .Set(PdfName.Type, new PdfName("ExtGState"))
            .Set(new PdfName("HT"), new PdfDictionary()
                .Set(new PdfName("Type"), new PdfName("Halftone")).Set(new PdfName("HalftoneType"), new PdfInteger(6))));

    private static byte[] WriterPdfWithHalftoneName()
        => WriterPdfWithAppliedExtGState(new PdfDictionary()
            .Set(PdfName.Type, new PdfName("ExtGState"))
            .Set(new PdfName("HT"), new PdfDictionary()
                .Set(new PdfName("Type"), new PdfName("Halftone")).Set(new PdfName("HalftoneType"), new PdfInteger(1))
                .Set(new PdfName("HalftoneName"), new PdfLiteralString(Encoding.ASCII.GetBytes("X")))
                .Set(new PdfName("Frequency"), new PdfInteger(60)).Set(new PdfName("Angle"), new PdfInteger(45))
                .Set(new PdfName("SpotFunction"), new PdfName("SimpleDot"))));

    private static byte[] WriterPdfWithHalftoneTransferFunction()
        => WriterPdfWithAppliedExtGState(new PdfDictionary()
            .Set(PdfName.Type, new PdfName("ExtGState"))
            .Set(new PdfName("HT"), new PdfDictionary()
                .Set(new PdfName("Type"), new PdfName("Halftone")).Set(new PdfName("HalftoneType"), new PdfInteger(1))
                .Set(new PdfName("Frequency"), new PdfInteger(60)).Set(new PdfName("Angle"), new PdfInteger(45))
                .Set(new PdfName("SpotFunction"), new PdfName("SimpleDot"))
                .Set(new PdfName("TransferFunction"), new PdfName("Identity"))));

    // Merges an /ExtGState into the page and applies it (`/GS0 gs`) on a compliant writer baseline.
    private static byte[] WriterPdfWithAppliedExtGState(PdfDictionary gs)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        var gsNum = reader.Size;
        var contentNum = gsNum + 1;
        newPage.Set(
            new PdfName("Resources"),
            new PdfDictionary().Set(
                new PdfName("ExtGState"),
                new PdfDictionary().Set(new PdfName("GS0"), new PdfIndirectReference(gsNum))));
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));
        var content = new PdfStream(Encoding.ASCII.GetBytes("q /GS0 gs Q"));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (gsNum, gs), (contentNum, content)]);
    }

    private static byte[] WriterPdfWithBadRenderingIntent()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        var contentNum = reader.Size;
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));
        var content = new PdfStream(Encoding.ASCII.GetBytes("/FooIntent ri"));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, content)]);
    }

    private static byte[] WriterPdfWithDeepQNesting()
    {
        // 29 nested `q` operators followed by 29 `Q` operators — depth reaches 29, which exceeds
        // the §6.1.13-8 limit of 28. The Q operators close the stack so the stream is otherwise
        // well-formed (no resource mismatches or other violations).
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        var contentNum = reader.Size;
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));
        var streamText = string.Concat(Enumerable.Repeat("q ", 29)) + string.Concat(Enumerable.Repeat("Q ", 29));
        var content = new PdfStream(Encoding.ASCII.GetBytes(streamText));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, content)]);
    }

    private static byte[] WriterPdfWithXmpHeader(string headerExtra)
        => WriterPdfWithMetadata(Encoding.UTF8.GetBytes(Xmp2b(headerExtra)));

    private static byte[] WriterPdfWithUtf16Xmp()
        => WriterPdfWithMetadata(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Xmp2b(""))).ToArray());

    internal const string ValidPropertyFields =
        "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>"
        + "<pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>";

    // A PDF/A-2b XMP packet declaring one valid extension schema with one property carrying the
    // given <paramref name="propertyFields"/> (element serialisation).
    private static string ExtensionSchemaXmp2b(string propertyFields, string schemaExtra = "")
    {
        const string ns =
            "xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
            + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" "
            + "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"";
        return "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>"
            + $"<rdf:Description rdf:about=\"\" {ns}><pdfaExtension:schemas><rdf:Bag>"
            + "<rdf:li rdf:parseType=\"Resource\"><pdfaSchema:schema>S</pdfaSchema:schema>"
            + "<pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
            + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
            + schemaExtra
            + "<pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" + propertyFields
            + "</rdf:li></rdf:Seq></pdfaSchema:property></rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>"
            + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
    }

    // A valid PDF/A extension schema serialised with the equivalent rdf:Description blank-node form
    // (each container's fields wrapped in <rdf:Description> rather than rdf:parseType="Resource").
    // veraPDF normalises both forms and accepts it; the in-process rule must unwrap the Description.
    private static string RdfDescriptionExtensionSchemaXmp2b()
    {
        const string ns =
            "xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
            + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" "
            + "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"";
        return "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>"
            + $"<rdf:Description rdf:about=\"\" {ns}><pdfaExtension:schemas><rdf:Bag>"
            + "<rdf:li><rdf:Description><pdfaSchema:schema>S</pdfaSchema:schema>"
            + "<pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
            + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
            + "<pdfaSchema:property><rdf:Seq><rdf:li><rdf:Description>" + ValidPropertyFields
            + "</rdf:Description></rdf:li></rdf:Seq></pdfaSchema:property>"
            + "</rdf:Description></rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>"
            + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
    }

    // A PDF/A-2b XMP packet using a custom property in a non-predefined, undeclared namespace.
    private static string CustomPropertyXmp2b() => RdfPacket(
        "<rdf:Description rdf:about=\"\" xmlns:ex=\"http://example.com/ns/\"><ex:foo>bar</ex:foo></rdf:Description>");

    // A PDF/A-2b XMP packet drawing on several predefined schemas, including an xmpMM struct value.
    private static string RichXmp2b() => RdfPacket(
        "<rdf:Description rdf:about=\"\" "
        + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" "
        + "xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\" "
        + "xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\" "
        + "xmlns:stRef=\"http://ns.adobe.com/xap/1.0/sType/ResourceRef#\" "
        + "pdf:Producer=\"P\" xmp:CreatorTool=\"T\" tiff:Make=\"M\">"
        + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Title</rdf:li></rdf:Alt></dc:title>"
        + "<exif:ExifVersion>0230</exif:ExifVersion>"
        + "<xmpMM:DerivedFrom rdf:parseType=\"Resource\"><stRef:documentID>d</stRef:documentID></xmpMM:DerivedFrom>"
        + "</rdf:Description>");

    // A PDF/A-2b XMP packet whose pdfaid namespace is bound to the non-canonical prefix 'aid'.
    private static string AltPrefixXmp2b() =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
        + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:aid=\"http://www.aiim.org/pdfa/ns/id/\">"
        + "<aid:part>2</aid:part><aid:conformance>B</aid:conformance></rdf:Description>"
        + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    // The same, additionally carrying the 'amd' and 'corr' identification properties under the
    // non-canonical prefix (so §6.6.4-6/-7 fire alongside -4/-5).
    private static string AmdCorrAliasedXmp2b() =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
        + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:aid=\"http://www.aiim.org/pdfa/ns/id/\">"
        + "<aid:part>2</aid:part><aid:conformance>B</aid:conformance>"
        + "<aid:amd>2010</aid:amd><aid:corr>1</aid:corr></rdf:Description>"
        + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    // A PDF/A-2b XMP packet whose extension schema container carries a bogus child field
    // (pdfaSchema:bogusField) not defined by the PDF/A extension-schema container schema (§6.6.2.3.2-1).
    private static string UndefinedSchemaFieldXmp2b() =>
        ExtensionSchemaXmp2b(
            ValidPropertyFields,
            schemaExtra: "<pdfaSchema:bogusField>x</pdfaSchema:bogusField>");

    // A PDF/A-2b XMP packet declaring a custom value type whose pdfaType omits the 'type' field.
    private static string BadValueTypeXmp2b() => RdfPacket(
        "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
        + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\" "
        + "xmlns:pdfaType=\"http://www.aiim.org/pdfa/ns/type#\" xmlns:pdfaField=\"http://www.aiim.org/pdfa/ns/field#\">"
        + "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">"
        + "<pdfaSchema:schema>S</pdfaSchema:schema><pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
        + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
        + "<pdfaSchema:valueType><rdf:Seq><rdf:li rdf:parseType=\"Resource\">"
        + "<pdfaType:namespaceURI>http://example.com/t/</pdfaType:namespaceURI><pdfaType:prefix>mt</pdfaType:prefix>"
        + "<pdfaType:description>d</pdfaType:description>"
        + "<pdfaType:field><rdf:Seq><rdf:li rdf:parseType=\"Resource\">"
        + "<pdfaField:name>f</pdfaField:name><pdfaField:valueType>Text</pdfaField:valueType><pdfaField:description>d</pdfaField:description>"
        + "</rdf:li></rdf:Seq></pdfaType:field></rdf:li></rdf:Seq></pdfaSchema:valueType>"
        + "</rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>");

    private static string RdfPacket(string extra) =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
        + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
        + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
        + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>"
        + extra + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    // A minimal PDF/A-2b XMP packet, with optional extra text in the <?xpacket?> header.
    private static string Xmp2b(string headerExtra) =>
        $"<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"{headerExtra}?>"
        + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
        + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
        + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
        + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    // Replaces the writer baseline's /Metadata stream with the given XMP bytes via an incremental update.
    private static byte[] WriterPdfWithMetadata(byte[] xmp)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var metaRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("Metadata"))!;
        var stream = new PdfStream(xmp);
        stream.Dictionary.Set(PdfName.Type, new PdfName("Metadata")).Set(PdfName.Subtype, new PdfName("XML"));
        return reader.AppendRevision([(metaRef.ObjectNumber, stream)]);
    }

    private static byte[] WriterPdfWithExternalStream()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var streamNum = reader.Size;
        var stream = new PdfStream([]);
        stream.Dictionary.Set(new PdfName("F"), new PdfLiteralString(Encoding.ASCII.GetBytes("external.dat")));
        return reader.AppendRevision([(streamNum, stream)]);
    }

    private static byte[] WriterPdfWithCatalogAdditionalActions()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        catalog.Set(new PdfName("AA"), new PdfDictionary().Set(new PdfName("WC"), NamedAction("NextPage")));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static byte[] WriterPdfWithOpenAction(PdfDictionary action)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog).Set(new PdfName("OpenAction"), action);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static byte[] WriterPdfWithDisallowedNamedAction()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        catalog.Set(new PdfName("OpenAction"), NamedAction("GoForward"));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static PdfDictionary NamedAction(string name) => new PdfDictionary()
        .Set(new PdfName("S"), new PdfName("Named"))
        .Set(new PdfName("N"), new PdfName(name));

    private static byte[] WriterPdfWithNeedsRendering()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        catalog.Set(new PdfName("NeedsRendering"), PdfBoolean.True);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static byte[] WriterPdfWithNeedAppearances()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var acroForm = new PdfDictionary()
            .Set(new PdfName("Fields"), new PdfArray([]))
            .Set(new PdfName("NeedAppearances"), PdfBoolean.True);
        catalog.Set(new PdfName("AcroForm"), acroForm);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static byte[] WriterPdfWithTinyMediaBox()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        newPage.Set(new PdfName("MediaBox"),
            new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage)]);
    }

    private static byte[] WriterPdfWithBadStreamLength()
    {
        // Bump the leading digit of the first multi-digit /Length so a non-empty stream's declared
        // length no longer matches its body, without changing byte width (xref offsets stay valid).
        var bytes = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        var needle = "/Length "u8.ToArray();
        for (var from = 0; (from = IndexOf(bytes, needle, from)) >= 0; from += needle.Length)
        {
            var s = from + needle.Length;
            var digits = 0;
            while (s + digits < bytes.Length && bytes[s + digits] is >= (byte)'0' and <= (byte)'9')
                digits++;
            if (digits >= 3)
            {
                bytes[s] = bytes[s] == (byte)'9' ? (byte)'1' : (byte)(bytes[s] + 1);
                break;
            }
        }
        return bytes;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match)
                return i;
        }
        return -1;
    }

    private static byte[] AppendTrailingBytes(byte[] pdf)
    {
        var garbage = Encoding.ASCII.GetBytes(" trailing-junk");
        var result = new byte[pdf.Length + garbage.Length];
        pdf.CopyTo(result, 0);
        garbage.CopyTo(result, pdf.Length);
        return result;
    }

    private static byte[] WriterPdfWithCatalogEntry(string key, PdfObject value)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        catalog.Set(new PdfName(key), value);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    // A compliant PDF/A-2b document wrapped as an /EmbeddedFile stream, so an embedding fixture keeps
    // §6.8-5 (the embedded file must itself be PDF/A) satisfied.
    private static PdfStream EmbeddedFileStream()
    {
        var s = new PdfStream(WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b));
        s.Dictionary.Set(PdfName.Type, new PdfName("EmbeddedFile")).Set(PdfName.Subtype, new PdfName("application/pdf"));
        return s;
    }

    // Attaches an embedded file via the catalog /Names /EmbeddedFiles name tree. The filespec carries
    // /F always and /UF when requested, and is typed /Filespec unless <paramref name="typed"/> is
    // false, so the §6.8-2 F/UF requirement can be exercised on both the typed and the name-tree
    // (untyped) identification paths.
    private static byte[] WriterPdfWithEmbeddedFile(bool includeUf, bool typed = true)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);

        var efStreamNum = reader.Size;
        var filespecNum = efStreamNum + 1;

        var filespec = new PdfDictionary()
            .Set(new PdfName("F"), new PdfLiteralString(Encoding.ASCII.GetBytes("attach.pdf")))
            .Set(new PdfName("EF"), new PdfDictionary().Set(new PdfName("F"), new PdfIndirectReference(efStreamNum)));
        if (typed)
            filespec.Set(PdfName.Type, new PdfName("Filespec"));
        if (includeUf)
            filespec.Set(new PdfName("UF"), new PdfLiteralString(Encoding.ASCII.GetBytes("attach.pdf")));

        catalog.Set(new PdfName("Names"), new PdfDictionary()
            .Set(new PdfName("EmbeddedFiles"), new PdfDictionary()
                .Set(new PdfName("Names"), new PdfArray([
                    new PdfLiteralString(Encoding.ASCII.GetBytes("attach.pdf")),
                    new PdfIndirectReference(filespecNum)]))));

        return reader.AppendRevision([
            (rootRef.ObjectNumber, catalog), (efStreamNum, EmbeddedFileStream()), (filespecNum, filespec)]);
    }

    // Attaches a plain (non-PDF/A) PDF as an embedded file via the /Names /EmbeddedFiles name tree.
    // The outer document is a compliant PDF/A-2b; the inner attached PDF has the %PDF- header but
    // carries no pdfaid:part identification — it is not a valid PDF/A-1 or PDF/A-2 document.
    // The filespec carries both /F and /UF so §6.8-2 is satisfied; only §6.8-5 is violated.
    // Clear-cut negative: both the in-process rule and veraPDF agree it is non-compliant.
    private static byte[] WriterPdfWithNonPdfAEmbeddedFile()
    {
        // The inner PDF uses PdfConformance.None — the writer emits a PDF 2.0 header with no XMP
        // pdfaid claim, so it is definitively not a valid PDF/A-1 or PDF/A-2 document.
        var innerBytes = WriterPdf(VellumPdf.Document.PdfConformance.None);

        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);

        var efStreamNum = reader.Size;
        var filespecNum = efStreamNum + 1;

        var efStream = new PdfStream(innerBytes);
        efStream.Dictionary.Set(PdfName.Type, new PdfName("EmbeddedFile"));

        var filespec = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Filespec"))
            .Set(new PdfName("F"), new PdfLiteralString(Encoding.ASCII.GetBytes("plain.pdf")))
            .Set(new PdfName("UF"), new PdfLiteralString(Encoding.ASCII.GetBytes("plain.pdf")))
            .Set(new PdfName("EF"), new PdfDictionary().Set(new PdfName("F"), new PdfIndirectReference(efStreamNum)));

        catalog.Set(new PdfName("Names"), new PdfDictionary()
            .Set(new PdfName("EmbeddedFiles"), new PdfDictionary()
                .Set(new PdfName("Names"), new PdfArray([
                    new PdfLiteralString(Encoding.ASCII.GetBytes("plain.pdf")),
                    new PdfIndirectReference(filespecNum)]))));

        return reader.AppendRevision([
            (rootRef.ObjectNumber, catalog), (efStreamNum, efStream), (filespecNum, filespec)]);
    }

    // Places an /EF key on the first page dictionary — a dictionary that is NOT a file specification.
    // §6.8-2 must not fire here (the regression guard for the bare-/EF false positive).
    private static byte[] WriterPdfWithEfOnPage()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var efStreamNum = reader.Size;
        var newPage = CloneDict(page).Set(new PdfName("EF"),
            new PdfDictionary().Set(new PdfName("F"), new PdfIndirectReference(efStreamNum)));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (efStreamNum, EmbeddedFileStream())]);
    }

    // Attaches a Link annotation with the given annotation flags to the first page. (Link is exempt
    // from the appearance-stream requirement, so the flag value is the only thing under test.)
    private static byte[] WriterPdfWithAnnotFlags(int flags)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var annotNum = reader.Size;
        var annot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot")).Set(PdfName.Subtype, new PdfName("Link"))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(10), new PdfInteger(10), new PdfInteger(50), new PdfInteger(50)]))
            .Set(new PdfName("F"), new PdfInteger(flags));
        var newPage = CloneDict(page).Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotNum)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (annotNum, annot)]);
    }

    // Attaches a Widget (or other-subtype) annotation whose /AP /N is built from the appearance-stream
    // object number, with an AcroForm registration. Used to exercise the §6.3.3-3/-4 N-kind rules.
    private static byte[] WriterPdfWithAppearanceKind(string subtype, string? fieldType, bool nAsSubDictionary)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var (pageRef, page) = FirstPage(reader);
        var apNum = reader.Size;
        var annotNum = apNum + 1;

        var ap = new PdfStream([]);
        ap.Dictionary.Set(PdfName.Type, new PdfName("XObject")).Set(PdfName.Subtype, new PdfName("Form"))
            .Set(new PdfName("BBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)]));
        PdfObject n = nAsSubDictionary
            ? new PdfDictionary().Set(new PdfName("On"), new PdfIndirectReference(apNum)).Set(new PdfName("Off"), new PdfIndirectReference(apNum))
            : new PdfIndirectReference(apNum);

        var annot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot")).Set(PdfName.Subtype, new PdfName(subtype))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(10), new PdfInteger(10), new PdfInteger(50), new PdfInteger(50)]))
            .Set(new PdfName("F"), new PdfInteger(1 << 2))
            .Set(new PdfName("AP"), new PdfDictionary().Set(new PdfName("N"), n));
        if (fieldType is not null)
            annot.Set(new PdfName("FT"), new PdfName(fieldType));
        if (subtype != "Widget")
            annot.Set(new PdfName("Contents"), new PdfLiteralString(Encoding.ASCII.GetBytes("note")));

        var newPage = CloneDict(page).Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotNum)]));
        var catalog = CloneDict(reader.Catalog);
        if (subtype == "Widget")
            catalog.Set(new PdfName("AcroForm"), new PdfDictionary()
                .Set(new PdfName("Fields"), new PdfArray([new PdfIndirectReference(annotNum)])));

        return reader.AppendRevision([
            (rootRef.ObjectNumber, catalog), (pageRef.ObjectNumber, newPage), (apNum, ap), (annotNum, annot)]);
    }

    // Attaches a Text annotation whose /AP appearance dictionary carries a /D entry in addition to /N.
    private static byte[] WriterPdfWithApExtraKey()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var apNum = reader.Size;
        var annotNum = apNum + 1;
        var ap = new PdfStream([]);
        ap.Dictionary.Set(PdfName.Type, new PdfName("XObject")).Set(PdfName.Subtype, new PdfName("Form"))
            .Set(new PdfName("BBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)]));
        var annot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot")).Set(PdfName.Subtype, new PdfName("Text"))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(10), new PdfInteger(10), new PdfInteger(50), new PdfInteger(50)]))
            .Set(new PdfName("F"), new PdfInteger(1 << 2))
            .Set(new PdfName("Contents"), new PdfLiteralString(Encoding.ASCII.GetBytes("note")))
            .Set(new PdfName("AP"), new PdfDictionary()
                .Set(new PdfName("N"), new PdfIndirectReference(apNum))
                .Set(new PdfName("D"), new PdfIndirectReference(apNum)));
        var newPage = CloneDict(page).Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotNum)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (apNum, ap), (annotNum, annot)]);
    }

    // Injects an /OCProperties dictionary with two OCGs, a caller-built /D config and optional
    // /Configs, into the catalog. The builders receive the two OCG object numbers so an /Order array
    // can reference them.
    private static byte[] WriterPdfWithOptionalContentGroups(
        Func<int, int, PdfDictionary> buildD, Func<int, int, PdfArray?>? buildConfigs = null)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var ocg1 = reader.Size;
        var ocg2 = ocg1 + 1;
        PdfDictionary Ocg(string n) => new PdfDictionary()
            .Set(PdfName.Type, new PdfName("OCG")).Set(new PdfName("Name"), new PdfLiteralString(Encoding.ASCII.GetBytes(n)));
        var ocp = new PdfDictionary()
            .Set(new PdfName("OCGs"), new PdfArray([new PdfIndirectReference(ocg1), new PdfIndirectReference(ocg2)]))
            .Set(new PdfName("D"), buildD(ocg1, ocg2));
        if (buildConfigs?.Invoke(ocg1, ocg2) is { } configs)
            ocp.Set(new PdfName("Configs"), configs);
        catalog.Set(new PdfName("OCProperties"), ocp);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog), (ocg1, Ocg("Layer 1")), (ocg2, Ocg("Layer 2"))]);
    }

    private static byte[] WriterPdfWithPresSteps()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        newPage.Set(new PdfName("PresSteps"), new PdfDictionary().Set(PdfName.Type, new PdfName("NavNode")));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage)]);
    }

    private static PdfDictionary NamedConfig()
        => new PdfDictionary().Set(new PdfName("Name"), new PdfLiteralString(Encoding.ASCII.GetBytes("Default")));

    // Injects an /OCProperties dictionary (one OCG plus the given /D configuration) into the catalog
    // via an incremental update on a conformant baseline.
    private static byte[] WriterPdfWithOptionalContent(PdfDictionary defaultConfig)
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var ocgNum = reader.Size;
        var ocg = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("OCG"))
            .Set(new PdfName("Name"), new PdfLiteralString(Encoding.ASCII.GetBytes("Layer 1")));
        catalog.Set(new PdfName("OCProperties"), new PdfDictionary()
            .Set(new PdfName("OCGs"), new PdfArray([new PdfIndirectReference(ocgNum)]))
            .Set(new PdfName("D"), defaultConfig));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog), (ocgNum, ocg)]);
    }

    private static byte[] WriterPdfWithXfa()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var acroForm = new PdfDictionary()
            .Set(new PdfName("Fields"), new PdfArray([]))
            .Set(new PdfName("XFA"), new PdfLiteralString(Encoding.ASCII.GetBytes("<xdp/>")));
        catalog.Set(new PdfName("AcroForm"), acroForm);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static byte[] WriterPdfDeviceColourNoOutputIntent()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var canvas = new PdfCanvas(page);
        canvas.SetFillColorRgb(1, 0, 0).Rectangle(100, 100, 50, 50).Fill();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);

        using var reader = PdfReader.Open(ms.ToArray());
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        // Remove /OutputIntents from the catalog, keeping the device-colour content.
        var catalog = new PdfDictionary();
        foreach (var kv in reader.Catalog.Entries)
            if (kv.Key.Value != "OutputIntents")
                catalog.Set(kv.Key, kv.Value);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static byte[] WriterPdfMalformedOutputIntent(bool deviceColour)
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        if (deviceColour)
        {
            var canvas = new PdfCanvas(page);
            canvas.SetFillColorRgb(1, 0, 0).Rectangle(100, 100, 50, 50).Fill();
            canvas.Finish();
        }
        using var ms = new MemoryStream();
        doc.Save(ms);

        using var reader = PdfReader.Open(ms.ToArray());
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var brokenOi = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("OutputIntent"))
            .Set(new PdfName("S"), new PdfName("GTS_PDFA1"));
        catalog.Set(new PdfName("OutputIntents"), new PdfArray([brokenOi]));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static (PdfIndirectReference Ref, PdfDictionary Dict) FirstPage(PdfDocumentReader reader)
    {
        var pagesRef = (PdfIndirectReference)reader.Catalog.Get(PdfName.Pages)!;
        var pages = (PdfDictionary)reader.Resolve(pagesRef.ObjectNumber)!;
        var kidsObj = pages.Get(new PdfName("Kids"));
        var kids = kidsObj is PdfIndirectReference kr ? (PdfArray)reader.Resolve(kr.ObjectNumber)! : (PdfArray)kidsObj!;
        var pageRef = (PdfIndirectReference)kids[0];
        return (pageRef, (PdfDictionary)reader.Resolve(pageRef.ObjectNumber)!);
    }

    private static byte[] WriterPdfWithUnusedBlendMode()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        // Merge an /ExtGState carrying a non-standard blend mode into the page resources, WITHOUT
        // referencing it from the page content — so it is never the current blend mode.
        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var newResources = CloneDict(resources);
        var gs = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("ExtGState"))
            .Set(new PdfName("BM"), new PdfName("BogusMode"));
        newResources.Set(new PdfName("ExtGState"), new PdfDictionary().Set(new PdfName("GS0"), gs));
        newPage.Set(new PdfName("Resources"), newResources);
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage)]);
    }

    private static byte[] WriterPdfMissingStructure(VellumPdf.Document.PdfConformance conformance)
    {
        // A tagged-conformance document (2a/UA-1) with language and title set but no tagged content,
        // so the writer emits no /StructTreeRoot — non-conformant for lack of a structure tree only.
        using var doc = new PdfDocument { Conformance = conformance, Language = "en-US" };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        doc.AddPage(PageSize.A4);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── PDF/A-2a §6.7.3.4 / §6.7.4 oracle fixture helpers ───────────────────────────────────────

    /// <summary>
    /// §6.7.3.4-1 VIOLATION: injects a StructElem with <c>/S /MyCustomTag</c> and NO <c>/RoleMap</c>
    /// entry. veraPDF fires 6.7.3.4-1 (<c>isNotMappedToStandardType == true</c>).
    /// </summary>
    private static byte[] Pdfa2aNonStandardTypeUnmapped()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var docK = doc.Get(new PdfName("K"));

        var customElemNum = reader.Size;
        var customElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("MyCustomTag"))
            .Set(new PdfName("P"), strRef);

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference(docRef.ObjectNumber)),
            new PdfIndirectReference(customElemNum),
        ]));

        return reader.AppendRevision([
            (docRef.ObjectNumber, newDoc),
            (customElemNum, customElem),
        ]);
    }

    /// <summary>
    /// §6.7.3.4-1 FP-safety: injects a StructElem with <c>/S /MyCustomTag</c> role-mapped to the
    /// standard type <c>/Div</c> via the StructTreeRoot <c>/RoleMap</c>. veraPDF PASSES.
    /// </summary>
    private static byte[] Pdfa2aNonStandardTypeRoleMapped()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var docK = doc.Get(new PdfName("K"));

        var customElemNum = reader.Size;
        var customElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("MyCustomTag"))
            .Set(new PdfName("P"), strRef);

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference(docRef.ObjectNumber)),
            new PdfIndirectReference(customElemNum),
        ]));

        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("MyCustomTag"), new PdfName("Div")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
            (docRef.ObjectNumber, newDoc),
            (customElemNum, customElem),
        ]);
    }

    /// <summary>
    /// §6.7.3.4-2 VIOLATION: <c>/RoleMap &lt;&lt; /Foo /Bar /Bar /Foo &gt;&gt;</c> with a StructElem
    /// <c>/S /Foo</c>. veraPDF fires 6.7.3.4-2 (<c>circularMappingExist == true</c>).
    /// </summary>
    private static byte[] Pdfa2aCircularRoleMap()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var docK = doc.Get(new PdfName("K"));

        var fooElemNum = reader.Size;
        var fooElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("Foo"))
            .Set(new PdfName("P"), strRef);

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference(docRef.ObjectNumber)),
            new PdfIndirectReference(fooElemNum),
        ]));

        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("Foo"), new PdfName("Bar"))
            .Set(new PdfName("Bar"), new PdfName("Foo")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
            (docRef.ObjectNumber, newDoc),
            (fooElemNum, fooElem),
        ]);
    }

    /// <summary>
    /// §6.7.3.4-3 VIOLATION: <c>/RoleMap &lt;&lt; /P /MyNonStd &gt;&gt;</c> with a StructElem
    /// <c>/S /P</c>. Standard type /P is remapped to the non-standard type /MyNonStd.
    /// veraPDF fires 6.7.3.4-3 (<c>remappedStandardType != null</c>).
    /// </summary>
    private static byte[] Pdfa2aStandardTypeRemapNonStd()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        // The baseline already has a Document → P structure. We just add the /RoleMap to remap /P
        // to a non-standard type. The existing /P element satisfies the "element uses /P" condition.
        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("P"), new PdfName("MyNonStd")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
        ]);
    }

    /// <summary>
    /// §6.7.3.4-3 FP-safety: <c>/RoleMap &lt;&lt; /P /Div &gt;&gt;</c> with a StructElem
    /// <c>/S /P</c>. Standard type /P remapped to another STANDARD type /Div.
    /// veraPDF PASSES (empirically confirmed against veraPDF 1.30.2).
    /// </summary>
    private static byte[] Pdfa2aStandardTypeRemapStd()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("P"), new PdfName("Div")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
        ]);
    }

    /// <summary>
    /// §6.7.3.4-3 FP-safety (multi-hop): <c>/RoleMap &lt;&lt; /P /Foo  /Foo /Span &gt;&gt;</c> with a
    /// StructElem <c>/S /P</c>. The standard type /P is remapped through the non-standard intermediate
    /// /Foo, which itself maps to the standard type /Span. veraPDF resolves the full chain and PASSES
    /// (confirmed against veraPDF 1.30.2). The rule must walk the chain — not just the immediate
    /// target — or it raises a false positive here.
    /// </summary>
    private static byte[] Pdfa2aStandardTypeRemapMultihop()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("P"), new PdfName("Foo"))
            .Set(new PdfName("Foo"), new PdfName("Span")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
        ]);
    }

    /// <summary>
    /// §6.7.4-1 VIOLATION: injects a bad <c>/Lang (invalid!!bad)</c> entry into the document
    /// catalog. veraPDF fires 6.7.4-1 (<c>unicodeValue</c> does not match the BCP-47 pattern).
    /// </summary>
    private static byte[] Pdfa2aBadCatalogLang()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        catalog.Set(new PdfName("Lang"), new PdfLiteralString(Encoding.ASCII.GetBytes("invalid!!bad")));
        return reader.AppendRevision([(rootRef.ObjectNumber, (PdfObject)catalog)]);
    }

    /// <summary>
    /// §6.7.4-1 VIOLATION: injects a StructElem with <c>/Lang (xyz!!bad)</c>. The structure
    /// element's /Lang value is not a valid RFC 3066 language tag.
    /// veraPDF fires 6.7.4-1.
    /// </summary>
    private static byte[] Pdfa2aBadStructElemLang()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfA2a);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var docK = doc.Get(new PdfName("K"));

        var badLangElemNum = reader.Size;
        var badLangElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("P"))
            .Set(new PdfName("P"), strRef)
            .Set(new PdfName("Lang"), new PdfLiteralString(Encoding.ASCII.GetBytes("xyz!!bad")));

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference(docRef.ObjectNumber)),
            new PdfIndirectReference(badLangElemNum),
        ]));

        return reader.AppendRevision([
            (docRef.ObjectNumber, newDoc),
            (badLangElemNum, badLangElem),
        ]);
    }

    private static byte[] WriterPdfWithJavaScriptAction()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var jsAction = new PdfDictionary()
            .Set(new PdfName("S"), new PdfName("JavaScript"))
            .Set(new PdfName("JS"), new PdfLiteralString(Encoding.ASCII.GetBytes("app.alert(1);")));
        catalog.Set(new PdfName("OpenAction"), jsAction);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    private static byte[] WriterPdfWithMovieAnnotation()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var pagesRef = (PdfIndirectReference)reader.Catalog.Get(PdfName.Pages)!;
        var pages = (PdfDictionary)reader.Resolve(pagesRef.ObjectNumber)!;
        var kidsObj = pages.Get(new PdfName("Kids"));
        var kids = kidsObj is PdfIndirectReference kr ? (PdfArray)reader.Resolve(kr.ObjectNumber)! : (PdfArray)kidsObj!;
        var pageRef = (PdfIndirectReference)kids[0];

        var page = CloneDict((PdfDictionary)reader.Resolve(pageRef.ObjectNumber)!);
        var annotObjNum = reader.Size;
        var movie = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Movie"))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)]))
            .Set(new PdfName("F"), new PdfInteger(4));
        page.Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotObjNum)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, page), (annotObjNum, movie)]);
    }

    private static PdfDictionary CloneDict(PdfDictionary src)
    {
        var d = new PdfDictionary();
        foreach (var kv in src.Entries)
            d.Set(kv.Key, kv.Value);
        return d;
    }

    private static byte[] WriterPdfEmbeddedText(VellumPdf.Document.PdfConformance conformance)
    {
        using var doc = new PdfDocument { Conformance = conformance, Language = "en-US" };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var handle = doc.EmbedStandard14Font(Standard14.Helvetica);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        DrawGlyphs(canvas, handle, "Unicode-mappable embedded text.");
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] WriterPdfTagged(VellumPdf.Document.PdfConformance conformance)
    {
        using var doc = new PdfDocument { Conformance = conformance, Language = "en-US" };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var handle = doc.EmbedStandard14Font(Standard14.Helvetica);
        doc.RegisterEmbeddedFontUsage(page, handle);

        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        DrawGlyphs(canvas, handle, "Tagged paragraph for accessibility.");
        canvas.EndText();
        canvas.EndMarkedContent();
        canvas.Finish();

        // Minimal valid structure hierarchy: Document → P, the P element bound to the marked content.
        var p = new PdfStructElem("P") { Page = page, Mcid = mcid };
        var root = new PdfStructElem("Document");
        root.AddChild(p);
        doc.RegisterStructElem(root);

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // Builds a tagged PDF/UA-1 baseline and injects /MarkInfo /Suspects = true via an incremental
    // update. The resulting document violates §7.1-4 and is rejected by both validators.
    private static byte[] WriterUa1WithSuspectsTrue()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var markInfo = new PdfDictionary();
        markInfo.Set(new PdfName("Marked"), PdfBoolean.True);
        markInfo.Set(new PdfName("Suspects"), PdfBoolean.True);
        catalog.Set(new PdfName("MarkInfo"), markInfo);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    // The same injection but with /Suspects = false — explicitly permitted (no violation).
    private static byte[] WriterUa1WithSuspectsFalse()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var markInfo = new PdfDictionary();
        markInfo.Set(new PdfName("Marked"), PdfBoolean.True);
        markInfo.Set(new PdfName("Suspects"), PdfBoolean.False);
        catalog.Set(new PdfName("MarkInfo"), markInfo);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    // Replaces the PDF version digit '7' with '8' (in-place, same-length) so the header reads
    // "%PDF-1.8\n" — digit 8 is outside the 0–7 range and violates §6.1-1.
    // An in-place edit preserves all cross-reference offsets; a byte-insertion would shift
    // them and make the file unreadable by the parser.
    private static byte[] WriterUa1WithBadHeaderDigit()
    {
        var bytes = (byte[])WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1).Clone();
        // "%PDF-1.7" = bytes[0..7]; the digit is at index 7.
        if (bytes.Length < 9 || bytes[7] != (byte)'7')
            throw new InvalidOperationException("WriterPdfTagged produced an unexpected header layout.");
        bytes[7] = (byte)'8'; // version digit 8 is out of the permitted 0–7 range
        return bytes;
    }

    // Replaces the header-terminating LF (index 8) with a space (in-place, same-length) so line 1
    // reads "%PDF-1.7 %…" — a trailing character after the version digit, before the EOL. This trips
    // the §6.1-1 "$" anchor (no chars allowed between the digit and the EOL) without disturbing any
    // cross-reference offset. The writer emits "%PDF-1.7\n%<binary marker>\n", so index 8 is the LF;
    // turning it into a space merges the (still comment-prefixed) marker onto line 1, which the
    // reader parses while veraPDF and UaFileHeaderRule both reject the header.
    private static byte[] WriterUa1WithTrailingHeaderChar()
    {
        var bytes = (byte[])WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1).Clone();
        // "%PDF-1.7" = bytes[0..7]; bytes[8] is the header-terminating LF.
        if (bytes.Length < 10 || bytes[7] != (byte)'7' || bytes[8] != (byte)'\n')
            throw new InvalidOperationException("WriterPdfTagged produced an unexpected header layout.");
        bytes[8] = (byte)' '; // a trailing space before the (now-merged) EOL
        return bytes;
    }

    // Builds a tagged PDF/UA-1 baseline and replaces the catalog /Lang with the given value.
    private static byte[] WriterUa1WithLang(string lang)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        catalog.Set(new PdfName("Lang"), new PdfLiteralString(Encoding.ASCII.GetBytes(lang)));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    // Same as WriterUa1WithLang but sets the catalog /Lang to the empty string, exercising the
    // §7.2-29 rejection of empty language tags (empirically confirmed against veraPDF 1.30.2).
    private static byte[] WriterUa1WithEmptyLang()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        catalog.Set(new PdfName("Lang"), new PdfLiteralString(Array.Empty<byte>()));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    // ── PDF/UA-1 Batch A2 fixture helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Injects a TrapNet annotation into the first page of the UA-1 tagged baseline.
    /// When <paramref name="hidden"/> is true, the Hidden flag (F = 2) is set so the annotation
    /// is exempt from §7.18.2-1; when false, the annotation is visible and violates the clause.
    /// </summary>
    private static byte[] WriterUa1WithTrapNetAnnotation(bool hidden)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        var apNum = reader.Size;
        var annotNum = reader.Size + 1;
        // Minimal appearance stream so PDF/A annotation-AP rules do not fire.
        var apStream = new PdfStream(Array.Empty<byte>());
        apStream.Dictionary
            .Set(new PdfName("Type"), new PdfName("XObject"))
            .Set(new PdfName("Subtype"), new PdfName("Form"))
            .Set(new PdfName("BBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(40), new PdfInteger(40)]));
        var annot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("TrapNet"))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(10), new PdfInteger(10), new PdfInteger(50), new PdfInteger(50)]))
            .Set(new PdfName("AP"), new PdfDictionary().Set(new PdfName("N"), new PdfIndirectReference(apNum)));
        if (hidden)
            annot.Set(new PdfName("F"), new PdfInteger(2)); // Hidden flag
        newPage.Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotNum)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (apNum, apStream), (annotNum, annot)]);
    }

    /// <summary>
    /// Injects a Link annotation into the UA-1 tagged baseline. When <paramref name="includeContents"/>
    /// is true, /Contents is set (satisfying §7.18.5-2); when false, it is absent (violation).
    /// </summary>
    private static byte[] WriterUa1WithLinkAnnotation(bool includeContents)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        var annotNum = reader.Size;
        var annot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Link"))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(10), new PdfInteger(10), new PdfInteger(50), new PdfInteger(50)]));
        if (includeContents)
            annot.Set(new PdfName("Contents"), new PdfLiteralString(Encoding.ASCII.GetBytes("Click here")));
        newPage.Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotNum)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (annotNum, annot)]);
    }

    /// <summary>
    /// Injects a Text annotation into the UA-1 tagged baseline. When <paramref name="includeContents"/>
    /// is true, /Contents is set (satisfying §7.18.1-2); when false, it is absent (violation).
    /// </summary>
    private static byte[] WriterUa1WithTextAnnotation(bool includeContents)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        var apNum = reader.Size;
        var annotNum = reader.Size + 1;
        var apStream = new PdfStream(Array.Empty<byte>());
        apStream.Dictionary
            .Set(new PdfName("Type"), new PdfName("XObject"))
            .Set(new PdfName("Subtype"), new PdfName("Form"))
            .Set(new PdfName("BBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(40), new PdfInteger(40)]));
        var annot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Text"))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(10), new PdfInteger(10), new PdfInteger(50), new PdfInteger(50)]))
            .Set(new PdfName("AP"), new PdfDictionary().Set(new PdfName("N"), new PdfIndirectReference(apNum)));
        if (includeContents)
            annot.Set(new PdfName("Contents"), new PdfLiteralString(Encoding.ASCII.GetBytes("A note")));
        newPage.Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotNum)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (apNum, apStream), (annotNum, annot)]);
    }

    /// <summary>
    /// Injects a Link annotation whose /Rect is entirely outside the page's MediaBox (A4 = [0 0 595 842]).
    /// Rect = [600 10 650 50] lies outside the right edge, so the annotation is exempt from §7.18
    /// requirements: veraPDF does not fire 7.18.5-2 or 7.18.1-2 for it (confirmed empirically).
    /// </summary>
    private static byte[] WriterUa1WithAnnotOutsideCropBox()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        var annotNum = reader.Size;
        // /Rect entirely to the right of A4 (width 595) — outside the MediaBox.
        var annot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Link"))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(600), new PdfInteger(10), new PdfInteger(650), new PdfInteger(50)]));
        newPage.Set(new PdfName("Annots"), new PdfArray([new PdfIndirectReference(annotNum)]));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (annotNum, annot)]);
    }

    /// <summary>
    /// Injects a drawn Form XObject with a /Ref entry into the UA-1 tagged baseline, violating §7.20-1.
    /// The /Ref entry makes the XObject a reference XObject, which is forbidden in PDF/UA-1.
    /// veraPDF fires clause 7.20, testNumber 1.
    /// </summary>
    private static byte[] WriterUa1WithReferenceXObject()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);
        var xobjNum = reader.Size;
        var contentNum = reader.Size + 1;
        var xobj = new PdfStream(Array.Empty<byte>());
        xobj.Dictionary
            .Set(new PdfName("Type"), new PdfName("XObject"))
            .Set(new PdfName("Subtype"), new PdfName("Form"))
            .Set(new PdfName("BBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(100), new PdfInteger(100)]))
            .Set(new PdfName("Ref"), new PdfDictionary().Set(new PdfName("Page"), new PdfInteger(0)));
        var content = new PdfStream(Encoding.ASCII.GetBytes("/Fm0 Do"));
        var resources = new PdfDictionary()
            .Set(new PdfName("XObject"), new PdfDictionary().Set(new PdfName("Fm0"), new PdfIndirectReference(xobjNum)));
        newPage.Set(new PdfName("Resources"), resources);
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (xobjNum, xobj), (contentNum, content)]);
    }

    /// <summary>
    /// Injects an embedded file into the UA-1 tagged baseline. When <paramref name="includeUf"/> is
    /// true, /UF is present (satisfying §7.11-1); when false, it is absent (violation).
    /// </summary>
    private static byte[] WriterUa1WithEmbeddedFile(bool includeUf)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var efStreamNum = reader.Size;
        var filespecNum = reader.Size + 1;
        var efStream = new PdfStream(Array.Empty<byte>());
        efStream.Dictionary.Set(new PdfName("Type"), new PdfName("EmbeddedFile"));
        var filespec = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Filespec"))
            .Set(new PdfName("F"), new PdfLiteralString(Encoding.ASCII.GetBytes("attach.txt")))
            .Set(new PdfName("EF"), new PdfDictionary().Set(new PdfName("F"), new PdfIndirectReference(efStreamNum)));
        if (includeUf)
            filespec.Set(new PdfName("UF"), new PdfLiteralString(Encoding.ASCII.GetBytes("attach.txt")));
        catalog.Set(new PdfName("Names"), new PdfDictionary()
            .Set(new PdfName("EmbeddedFiles"), new PdfDictionary()
                .Set(new PdfName("Names"), new PdfArray([
                    new PdfLiteralString(Encoding.ASCII.GetBytes("attach.txt")),
                    new PdfIndirectReference(filespecNum)]))));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog), (efStreamNum, efStream), (filespecNum, filespec)]);
    }

    /// <summary>
    /// Injects an optional-content configuration into the UA-1 tagged baseline. Used to exercise
    /// §7.10-1 (/Name required) and §7.10-2 (/AS forbidden).
    /// </summary>
    private static byte[] WriterUa1WithOptionalContent(bool hasName, bool hasAs)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var ocgNum = reader.Size;
        var ocg = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("OCG"))
            .Set(new PdfName("Name"), new PdfLiteralString(Encoding.ASCII.GetBytes("Layer 1")));
        var ocConfig = new PdfDictionary();
        if (hasName)
            ocConfig.Set(new PdfName("Name"), new PdfLiteralString(Encoding.ASCII.GetBytes("Default")));
        if (hasAs)
            ocConfig.Set(new PdfName("AS"), new PdfArray([new PdfDictionary()
                .Set(new PdfName("Event"), new PdfName("View"))
                .Set(new PdfName("OCGs"), new PdfArray([]))
                .Set(new PdfName("Category"), new PdfArray([new PdfName("View")]))]));
        catalog.Set(new PdfName("OCProperties"), new PdfDictionary()
            .Set(new PdfName("OCGs"), new PdfArray([new PdfIndirectReference(ocgNum)]))
            .Set(new PdfName("D"), ocConfig));
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog), (ocgNum, ocg)]);
    }

    /// <summary>
    /// Injects an XFA stream into the UA-1 tagged baseline. When <paramref name="dynamic"/> is true,
    /// the XFA config specifies <c>dynamicRender = "required"</c> (violates §7.15-1); when false,
    /// <c>dynamicRender = "forbidden"</c> (static XFA — passes §7.15-1).
    /// The XFA config structure follows the veraPDF model:
    /// <c>xdp:xdp &gt; config &gt; acrobat &gt; acrobat7 &gt; dynamicRender</c>.
    /// </summary>
    private static byte[] WriterUa1WithXfa(bool dynamic)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneDict(reader.Catalog);
        var xfaStreamNum = reader.Size;
        var renderValue = dynamic ? "required" : "forbidden";
        var xfaXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"
            + "<config xmlns=\"http://www.xfa.org/schema/xci/1.0/\">"
            + $"<acrobat><acrobat7><dynamicRender>{renderValue}</dynamicRender></acrobat7></acrobat>"
            + "</config></xdp:xdp>";
        var xfaStream = new PdfStream(Encoding.UTF8.GetBytes(xfaXml));
        var acroForm = new PdfDictionary()
            .Set(new PdfName("XFA"), new PdfIndirectReference(xfaStreamNum));
        catalog.Set(new PdfName("AcroForm"), acroForm);
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog), (xfaStreamNum, xfaStream)]);
    }

    private static void DrawGlyphs(PdfCanvas canvas, EmbeddedFontHandle handle, string text)
    {
        var gids = new ushort[text.Length];
        var n = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, n));
    }

    private static byte[] WriterPdfWithStandard14Substitute()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var handle = doc.EmbedStandard14Font(Standard14.Helvetica);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        const string text = "Standard-14 Helvetica, embedded substitute";
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // Builds a PDF/A-2b document that adds a simple WinAnsi TrueType font (the full DejaVu program)
    // drawing the glyph 'A', on top of a compliant writer baseline. The caller mutates the font
    // dictionary to introduce a single malformation; with no mutation the result is fully conformant
    // (the 'A' advance width is computed from the font, so §6.2.11.5 stays satisfied). This is the
    // simple-font analogue of CorruptDescendantFont — the writer itself emits only composite fonts.
    // A non-symbolic simple TrueType font whose embedded program's cmap has been patched down to a
    // single Microsoft Symbol (3,0) subtable — which alone cannot serve a non-symbolic font
    // (§6.2.11.6-1).
    internal static byte[] SimpleTrueTypeFontSymbolOnlyCmap()
    {
        var dejavu = LoadAsset("DejaVuSans.ttf");
        var cmap = SfntTableOffset(dejavu, "cmap");
        dejavu[cmap + 2] = 0;
        dejavu[cmap + 3] = 1;       // numSubtables = 1
        dejavu[cmap + 4] = 0;
        dejavu[cmap + 5] = 3;       // record 0 platform = 3
        dejavu[cmap + 6] = 0;
        dejavu[cmap + 7] = 0;       // record 0 encoding = 0 (Microsoft Symbol)
        return SimpleTrueTypeFont(_ => { }, encoding: new PdfName("WinAnsiEncoding"), fontProgram: dejavu);
    }

    private static int SfntTableOffset(byte[] font, string tag)
    {
        var numTables = (font[4] << 8) | font[5];
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            if (font[rec] == tag[0] && font[rec + 1] == tag[1] && font[rec + 2] == tag[2] && font[rec + 3] == tag[3])
                return (font[rec + 8] << 24) | (font[rec + 9] << 16) | (font[rec + 10] << 8) | font[rec + 11];
        }
        throw new InvalidOperationException($"sfnt table '{tag}' not found.");
    }

    internal static byte[] SimpleTrueTypeFont(
        Action<PdfDictionary> mutate, int flags = 32, PdfName? encoding = null, byte[]? fontProgram = null,
        bool omitBaseFont = false)
    {
        // Width is always measured from the unmodified asset (so a caller-patched program — e.g. a
        // mangled cmap — does not perturb the /Widths and trip §6.2.11.5); only the embedded
        // FontFile2 bytes are overridden.
        var asset = LoadAsset("DejaVuSans.ttf");
        var dejavu = fontProgram ?? asset;
        int widthA;
        using (var measureDoc = new PdfDocument())
            widthA = (int)Math.Round(measureDoc.UseTrueTypeFont(asset).MeasureString("A", 1000));

        using var reader = PdfReader.Open(WriterPdfWithEmbeddedFont());
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontDict = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;

        var fontNum = reader.Size;
        var descNum = fontNum + 1;
        var ffNum = fontNum + 2;
        var contentNum = fontNum + 3;

        var fontFile = new PdfStream(dejavu);
        fontFile.Dictionary.Set(new PdfName("Length1"), new PdfInteger(dejavu.Length));
        var descriptor = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("FontDescriptor")).Set(new PdfName("FontName"), new PdfName("DejaVuSans"))
            .Set(new PdfName("Flags"), new PdfInteger(flags))
            .Set(new PdfName("FontBBox"),
                new PdfArray([new PdfInteger(-1021), new PdfInteger(-463), new PdfInteger(1793), new PdfInteger(1232)]))
            .Set(new PdfName("ItalicAngle"), new PdfInteger(0)).Set(new PdfName("Ascent"), new PdfInteger(928))
            .Set(new PdfName("Descent"), new PdfInteger(-236)).Set(new PdfName("CapHeight"), new PdfInteger(928))
            .Set(new PdfName("StemV"), new PdfInteger(80)).Set(new PdfName("FontFile2"), new PdfIndirectReference(ffNum));
        var simple = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font")).Set(PdfName.Subtype, new PdfName("TrueType"))
            .Set(new PdfName("FirstChar"), new PdfInteger(65)).Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(widthA)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));
        if (!omitBaseFont)
            simple.Set(PdfName.BaseFont, new PdfName("DejaVuSans"));
        if (encoding is not null)
            simple.Set(new PdfName("Encoding"), encoding);
        mutate(simple);

        var newFontDict = CloneDict(fontDict).Set(new PdfName("F1"), new PdfIndirectReference(fontNum));
        var newResources = CloneDict(resources).Set(PdfName.Font, newFontDict);
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));
        var content = new PdfStream(Encoding.ASCII.GetBytes("BT /F1 12 Tf 100 500 Td (A) Tj ET"));

        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (fontNum, simple), (descNum, descriptor), (ffNum, fontFile), (contentNum, content)]);
    }

    private static byte[] WriterPdfWithOutOfRangeGlyph() => WriterPdfDrawingGlyph("EA60"); // 60000, beyond the program

    private static byte[] WriterPdfDrawingNotdef() => WriterPdfDrawingGlyph("0000"); // glyph index 0 = .notdef

    // Appends a content stream that shows the given 2-byte hex glyph index with the embedded
    // Identity-H font, on top of the writer's compliant embedded-font baseline.
    private static byte[] WriterPdfDrawingGlyph(string hexGid)
    {
        using var reader = PdfReader.Open(WriterPdfWithEmbeddedFont());
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var fontName = fonts.Entries.First().Key.Value;

        var contentNum = reader.Size;
        var newPage = CloneDict(page);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));
        var content = new PdfStream(Encoding.ASCII.GetBytes($"BT /{fontName} 12 Tf 72 600 Td <{hexGid}> Tj ET"));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, content)]);
    }

    private static byte[] WriterPdfWithBadFontSubtype()
        => CorruptDescendantFont(d =>
        {
            var c = CloneDict(d);
            c.Set(PdfName.Subtype, new PdfName("BogusType"));
            return c;
        });

    private static byte[] WriterPdfWithoutCidToGidMap()
        => CorruptDescendantFont(d => CloneWithout(d, "CIDToGIDMap"));

    private static byte[] WriterPdfWithoutFontType()
        => CorruptDescendantFont(d => CloneWithout(d, "Type"));

    // Rewrites the embedded Type0 font's /Encoding from /Identity-H to a non-predefined CMap name
    // (§6.2.11.3.3-1), leaving everything else intact.
    private static byte[] WriterPdfWithBadCMapName()
    {
        using var reader = PdfReader.Open(WriterPdfWithEmbeddedFont());
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0Ref = (PdfIndirectReference)fonts.Entries.First().Value;
        var type0 = (PdfDictionary)reader.Resolve(type0Ref.ObjectNumber)!;
        var newType0 = CloneDict(type0).Set(new PdfName("Encoding"), new PdfName("FooBarCMap"));
        return reader.AppendRevision([(type0Ref.ObjectNumber, newType0)]);
    }

    private static byte[] WriterPdfWithoutBaseFont()
        => SimpleTrueTypeFont(_ => { }, encoding: new PdfName("WinAnsiEncoding"), omitBaseFont: true);

    private static byte[] WriterPdfWithBadGlyphWidth()
        => CorruptDescendantFont(d => CloneWithout(d, "W")); // widths fall to /DW, mismatching the program

    private static PdfDictionary CloneWithout(PdfDictionary d, string key)
    {
        var c = new PdfDictionary();
        foreach (var kv in d.Entries)
            if (kv.Key.Value != key)
                c.Set(kv.Key, kv.Value);
        return c;
    }

    // Rebuilds the descendant CIDFont of the writer's embedded-font fixture and returns the
    // incrementally-updated bytes. The base font is a real DejaVu subset veraPDF can parse.
    private static byte[] CorruptDescendantFont(Func<PdfDictionary, PdfDictionary> rebuild)
    {
        var baseline = WriterPdfWithEmbeddedFont();
        using var reader = PdfReader.Open(baseline);
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0 = (PdfDictionary)reader.ResolveValue(fonts.Entries.First().Value)!;
        var descendants = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var descRef = (PdfIndirectReference)descendants[0];
        var descendant = (PdfDictionary)reader.Resolve(descRef.ObjectNumber)!;
        return reader.AppendRevision([(descRef.ObjectNumber, rebuild(descendant))]);
    }

    // Adds a /CIDSet to the embedded subset CIDFontType2's FontDescriptor. When complete, the bitmap
    // marks exactly CIDs 0..NumGlyphs−1 (Identity CIDToGIDMap); otherwise it is a single empty byte,
    // so it fails to identify the present CIDs (§6.2.11.4.2-2).
    private static byte[] WriterPdfWithCidSet(bool complete)
    {
        using var reader = PdfReader.Open(WriterPdfWithEmbeddedFont());
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0 = (PdfDictionary)reader.ResolveValue(fonts.Entries.First().Value)!;
        var descArr = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var cidFont = (PdfDictionary)reader.Resolve(((PdfIndirectReference)descArr[0]).ObjectNumber)!;
        var fdRef = (PdfIndirectReference)cidFont.Get(new PdfName("FontDescriptor"))!;
        var fd = (PdfDictionary)reader.Resolve(fdRef.ObjectNumber)!;
        var ff2 = reader.ResolveStream(((PdfIndirectReference)fd.Get(new PdfName("FontFile2"))!).ObjectNumber)!;
        var program = reader.GetDecodedStreamData(ff2)!;

        var numGlyphs = NumGlyphsOf(program);
        byte[] cidSet;
        if (complete)
        {
            cidSet = new byte[(numGlyphs + 7) / 8];
            for (var i = 0; i < numGlyphs; i++)
                cidSet[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        else
        {
            cidSet = [0x00];
        }

        var cidSetNum = reader.Size;
        var newFd = CloneDict(fd).Set(new PdfName("CIDSet"), new PdfIndirectReference(cidSetNum));
        return reader.AppendRevision([(fdRef.ObjectNumber, newFd), (cidSetNum, new PdfStream(cidSet))]);
    }

    // Rewrites the embedded OpenType-CFF font's /FontFile3 /Subtype from /OpenType to an invalid
    // value (/Type2), preserving the (decoded) program bytes so veraPDF still parses the program and
    // flags only the disallowed subtype (§6.2.11.2-7).
    private static byte[] WriterPdfWithBadFontFile3Subtype()
    {
        using var reader = PdfReader.Open(WriterPdfWithEmbeddedCffFont());
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0 = (PdfDictionary)reader.ResolveValue(fonts.Entries.First().Value)!;
        var descArr = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var cidFont = (PdfDictionary)reader.Resolve(((PdfIndirectReference)descArr[0]).ObjectNumber)!;
        var fd = (PdfDictionary)reader.ResolveValue(cidFont.Get(new PdfName("FontDescriptor"))!)!;
        var ff3Ref = (PdfIndirectReference)fd.Get(new PdfName("FontFile3"))!;
        var ff3 = reader.ResolveStream(ff3Ref.ObjectNumber)!;
        var program = reader.GetDecodedStreamData(ff3)!;

        var newFf3 = new PdfStream(program);
        newFf3.Dictionary.Set(PdfName.Subtype, new PdfName("Type2"));
        return reader.AppendRevision([(ff3Ref.ObjectNumber, newFf3)]);
    }

    // Embeds the Noto Sans Shavian Type 1 program as a subset-tagged simple Type1 font on a
    // compliant PDF/A-2b baseline, with a /CharSet string in the FontDescriptor. When complete the
    // CharSet lists every glyph in the program; otherwise it omits one (§6.2.11.4.2-1).
    private static byte[] WriterPdfWithType1CharSet(bool complete)
    {
        var (fontFile, length1, length2, length3) = Type1FontAsset.ToFontFile();
        var names = Type1Glyphs.TryEnumerate(fontFile, length1)!.OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!complete)
            names.Remove("u1047F"); // drop one present glyph so the CharSet is incomplete.
        var charSet = string.Concat(names.Select(n => "/" + n));

        using var reader = PdfReader.Open(WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b));
        var (pageRef, page) = FirstPage(reader);

        var fontNum = reader.Size;
        var descNum = fontNum + 1;
        var ffNum = fontNum + 2;
        var contentNum = fontNum + 3;

        var program = new PdfStream(fontFile);
        program.Dictionary
            .Set(new PdfName("Length1"), new PdfInteger(length1))
            .Set(new PdfName("Length2"), new PdfInteger(length2))
            .Set(new PdfName("Length3"), new PdfInteger(length3));
        var descriptor = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("FontDescriptor"))
            .Set(new PdfName("FontName"), new PdfName("AAAAAA+NotoSansShavian"))
            .Set(new PdfName("Flags"), new PdfInteger(4)) // symbolic
            .Set(new PdfName("FontBBox"),
                new PdfArray([new PdfInteger(0), new PdfInteger(-502), new PdfInteger(1396), new PdfInteger(1600)]))
            .Set(new PdfName("ItalicAngle"), new PdfInteger(0)).Set(new PdfName("Ascent"), new PdfInteger(1600))
            .Set(new PdfName("Descent"), new PdfInteger(-502)).Set(new PdfName("CapHeight"), new PdfInteger(1600))
            .Set(new PdfName("StemV"), new PdfInteger(80))
            .Set(new PdfName("CharSet"), new PdfLiteralString(Encoding.ASCII.GetBytes(charSet)))
            .Set(new PdfName("FontFile"), new PdfIndirectReference(ffNum));
        var font = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font")).Set(PdfName.Subtype, new PdfName("Type1"))
            .Set(PdfName.BaseFont, new PdfName("AAAAAA+NotoSansShavian"))
            .Set(new PdfName("FirstChar"), new PdfInteger(32)).Set(new PdfName("LastChar"), new PdfInteger(32))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(1000)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));

        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var newResources = CloneDict(resources)
            .Set(PdfName.Font, new PdfDictionary().Set(new PdfName("F1"), new PdfIndirectReference(fontNum)));
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);
        // veraPDF only validates a font's /CharSet when the font is actually used, so select it.
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));
        // Render the glyph invisibly (text rendering mode 3): the font counts as used — so veraPDF
        // validates its /CharSet — but the §6.2.11.5 width check is skipped, keeping this fixture
        // about /CharSet alone. Code 32 maps to /uni00A0 via the program's built-in encoding.
        var content = new PdfStream(Encoding.ASCII.GetBytes("BT 3 Tr /F1 12 Tf 72 700 Td (\x20) Tj ET"));

        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (fontNum, font), (descNum, descriptor), (ffNum, program),
                (contentNum, content)]);
    }

    private static int NumGlyphsOf(byte[] program)
    {
        var maxp = SfntTableOffset(program, "maxp");
        return (program[maxp + 4] << 8) | program[maxp + 5];
    }

    // Embeds the SourceSans3 OpenType-CFF program (so the writer emits a CIDFontType0 descendant
    // with /FontFile3 /Subtype /OpenType), drawing a short string. The CFF analogue of
    // WriterPdfWithEmbeddedFont — the positive baseline for the FontFile3 /Subtype check (§6.2.11.2-7).
    private static byte[] WriterPdfWithEmbeddedCffFont()
    {
        var otf = LoadAsset("SourceSans3-ExtraLight.otf");
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var handle = doc.UseTrueTypeFont(otf);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        const string text = "Embedded Source Sans 3 CFF subset";
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] WriterPdfWithEmbeddedFont()
    {
        var ttf = LoadAsset("DejaVuSans.ttf");
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var handle = doc.UseTrueTypeFont(ttf);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        const string text = "Embedded DejaVu Sans subset";
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] LoadAsset(string logicalName)
    {
        using var s = typeof(OracleCorpus).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded asset '{logicalName}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] WriterPdfNonEmbeddedFont()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage(PageSize.A4);
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("Non-embedded standard-14 font").EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] WriterPdfWithLink()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(72, 72, 200, 90),
            Uri = "https://example.com/",
        });
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // A hand-built, classic-cross-reference-table PDF/A-2b document (catalog, pages, page, XMP
    // metadata), so the cross-reference and object byte layout is under our control. veraPDF accepts
    // it as compliant. When <paramref name="corruptXrefEol"/> is set, the single EOL between the xref
    // keyword and the first subsection header is replaced by a space (same length, so offsets stay
    // valid), violating §6.1.4-2.
    internal static byte[] AssembleClassicXref(
        bool corruptXrefEol = false, bool corruptStreamEol = false, bool corruptEndstreamEol = false,
        bool corruptObjSpacing = false, bool injectOddHex = false)
    {
        var xmp = Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // An odd-length hexadecimal string (3 hex digits) violates §6.1.6-1 when injected.
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                + (injectOddHex ? " /VellumHex <ABC>" : "") + " >>",
        };
        var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.7\n%âãÏÓ\n");
        var size = objs.Length + 2; // 3 objects + metadata object + the free (object 0) entry
        var offsets = new int[size];
        for (var i = 0; i < objs.Length; i++)
        {
            offsets[i + 1] = (int)ms.Position;
            // The xref records the start of each object, so a doubled space inside the header keeps
            // the offsets valid while violating §6.1.9-1 (a single white-space is required).
            var sep = corruptObjSpacing && i + 1 == 3 ? "  " : " ";
            W($"{i + 1}{sep}0 obj\n{objs[i]}\nendobj\n");
        }
        offsets[4] = (int)ms.Position;
        W($"4 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n");
        ms.Write(xmp, 0, xmp.Length);
        W("\nendstream\nendobj\n");
        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var i = 1; i < size; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R "
            + "/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        var pdf = ms.ToArray();
        if (corruptXrefEol)
            ReplaceSameLength(pdf, "xref\n0 "u8, "xref 0 "u8);
        if (corruptStreamEol) // 'stream' followed by a lone CR instead of CRLF/LF (§6.1.7.1-2)
            ReplaceSameLength(pdf, "stream\n"u8, "stream\r"u8);
        if (corruptEndstreamEol) // 'endstream' preceded by a space instead of an EOL (§6.1.7.1-2)
            ReplaceSameLength(pdf, "\nendstream"u8, " endstream"u8);
        return pdf;
    }

    // Replaces the first occurrence of <paramref name="find"/> with the equal-length <paramref
    // name="replacement"/>, in place.
    private static void ReplaceSameLength(byte[] bytes, ReadOnlySpan<byte> find, ReadOnlySpan<byte> replacement)
    {
        var at = bytes.AsSpan().IndexOf(find);
        if (at < 0)
            throw new InvalidOperationException("pattern not found.");
        replacement.CopyTo(bytes.AsSpan(at));
    }

    /// <summary>
    /// Builds a hand-assembled minimal PDF/A-2b document (classic cross-reference) whose single page
    /// contains a content stream with the bytes in <paramref name="content"/>. Used for oracle fixtures
    /// that need to inject arbitrary content-stream bytes without going through the writer (which would
    /// only emit standard ISO 32000-1 operators).
    /// </summary>
    /// <remarks>
    /// The document satisfies all always-on structural constraints (§6.1.2 binary marker, §6.1.3 /ID,
    /// conforming XMP) so that the only failing rule is the one the content stream triggers.
    /// </remarks>
    private static byte[] HandBuiltPdfWithContent(string content)
    {
        var contentBytes = Encoding.Latin1.GetBytes(content);
        var xmp = Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

        // Object layout: 1=catalog, 2=pages, 3=page, 4=content, 5=metadata.
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        W("%PDF-1.7\n");
        ms.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // % + 4 high bytes + LF (binary marker)

        var offsets = new int[6];
        offsets[1] = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>\nendobj\n");
        offsets[2] = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");

        offsets[4] = (int)ms.Position;
        W($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        ms.Write(contentBytes);
        W("\nendstream\nendobj\n");

        offsets[5] = (int)ms.Position;
        W($"5 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n");
        ms.Write(xmp);
        W("\nendstream\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W("trailer\n<< /Size 6 /Root 1 0 R "
            + "/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] WriterPdf(VellumPdf.Document.PdfConformance conformance)
    {
        using var doc = new PdfDocument { Conformance = conformance };
        doc.Info.Title = "VellumPdf Oracle Fixture";
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Clone(byte[] source) => (byte[])source.Clone();

    private static byte[] Edit(byte[] source, Action<byte[]> mutate)
    {
        var copy = (byte[])source.Clone();
        mutate(copy);
        return copy;
    }

    /// <summary>Flips XMP <c>pdfaid:conformance</c> (<c>B</c>) to an invalid letter, in place.</summary>
    private static void CorruptXmpConformance(byte[] bytes)
        => OverwriteValueAfter(bytes, "pdfaid:conformance>", expected: (byte)'B', replacement: (byte)'X');

    /// <summary>Flips XMP <c>pdfaid:part</c> (<c>2</c>) to an invalid value, in place.</summary>
    private static void CorruptXmpPart(byte[] bytes)
        => OverwriteValueAfter(bytes, "pdfaid:part>", expected: (byte)'2', replacement: (byte)'9');

    /// <summary>Blanks the four high binary-marker bytes so the §6.1.2 marker comment is invalid.</summary>
    private static void CorruptBinaryMarker(byte[] bytes)
    {
        // The writer emits the marker comment bytes 0xE2 0xE3 0xCF 0xD3 on the line after the header.
        byte[] marker = [0xE2, 0xE3, 0xCF, 0xD3];
        var at = IndexOf(bytes, marker);
        // Guard against the (astronomically unlikely) case of this 4-byte sequence appearing first
        // inside a compressed stream: the real marker is on the header line, right after a '%'.
        if (at is < 2 or > 20 || bytes[at - 1] != (byte)'%')
            throw new InvalidOperationException(
                $"Binary marker not found on the header line where expected (offset {at}).");
        for (var i = 0; i < marker.Length; i++)
            bytes[at + i] = (byte)' ';
    }

    private static void OverwriteValueAfter(byte[] bytes, string needle, byte expected, byte replacement)
    {
        var at = IndexOf(bytes, Encoding.ASCII.GetBytes(needle));
        if (at < 0)
            throw new InvalidOperationException($"Fixture writer did not emit '{needle}'.");
        var valueIndex = at + needle.Length;
        if (bytes[valueIndex] != expected)
            throw new InvalidOperationException(
                $"Expected '{(char)expected}' after '{needle}' but found '{(char)bytes[valueIndex]}'; "
                + "the writer's output changed and the corruption would target the wrong byte.");
        bytes[valueIndex] = replacement;
    }

    // A conformant writer baseline with an extra unreferenced indirect stream object whose /Filter
    // is /LZWDecode. §6.1.7.2-1 applies to ALL stream filter names — not only decoded streams — so
    // both veraPDF (CosFilter check) and the in-process StreamRule flag it. The stream is
    // unreferenced so neither validator attempts to decode it; the body is a minimal valid LZW
    // bitstream (clear-code + EOD packed MSB-first in 9-bit codes) out of caution. The revision is
    // assembled by hand because the writer manages /Filter itself and will not emit a forbidden one.
    private static byte[] WriterPdfWithLzwStream()
    {
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);

        // Locate the base document's startxref value by scanning backwards for "startxref\n".
        var prevXrefOffset = FindStartXref(baseline, "startxref\n"u8.ToArray());

        // The next free object number follows the base document's xref /Size.
        using var reader = PdfReader.Open(baseline);
        var newObjNum = reader.Size;
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var docId = reader.Trailer.Get(PdfName.ID) as PdfArray;

        // Minimal valid LZW bitstream: clear code (0x100, 9-bit) + EOD (0x101, 9-bit) packed MSB-first.
        // Clear = 100000000b, EOD = 100000001b → 18 bits → 0x80 0x40 0x40 (padded to 3 bytes).
        byte[] lzwBody = [0x80, 0x40, 0x40];

        var ms = new MemoryStream(baseline.Length + 512);
        ms.Write(baseline);
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        var newObjStart = (long)ms.Position;
        W($"{newObjNum} 0 obj\n<< /Filter /LZWDecode /Length {lzwBody.Length} >>\nstream\n");
        ms.Write(lzwBody);
        W("\nendstream\nendobj\n");

        var xrefOffset = (long)ms.Position;
        // Incremental xref: free-list head subsection + one entry for the new object.
        W($"xref\n0 1\n0000000000 65535 f\r\n{newObjNum} 1\n{newObjStart:D10} 00000 n\r\n");

        // Incremental trailer: /Size = newObjNum + 1, /Prev = previous startxref, /Root and /ID carried.
        var idEntry = docId is not null
            ? " /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]"
            : string.Empty;
        W($"trailer\n<< /Size {newObjNum + 1} /Root {rootRef.ObjectNumber} 0 R /Prev {prevXrefOffset}{idEntry} >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // Parses the startxref value from the end of a PDF byte array by scanning for the "startxref\n"
    // keyword and reading the decimal integer on the following line.
    private static long FindStartXref(byte[] pdf, byte[] tag)
    {
        // Scan backwards from the end (the value is near %%EOF).
        for (var i = pdf.Length - tag.Length; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < tag.Length; j++)
                if (pdf[i + j] != tag[j]) { match = false; break; }
            if (!match)
                continue;
            // Parse the decimal integer that immediately follows the tag.
            var start = i + tag.Length;
            long value = 0;
            while (start < pdf.Length && pdf[start] is >= (byte)'0' and <= (byte)'9')
                value = value * 10 + (pdf[start++] - '0');
            return value;
        }
        throw new InvalidOperationException("startxref not found in the PDF baseline.");
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    // ── UA-1 font-clause probe fixtures (Batch A3) ───────────────────────────────────────────────

    /// <summary>
    /// UA-1 tagged PDF baseline with a real embedded Type0/CIDFontType2 font (DejaVu, Identity-H).
    /// Used as the positive-path anchor for §7.21 font clauses.
    /// </summary>
    internal static byte[] Ua1TaggedWithEmbeddedFont()
        => WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);

    /// <summary>
    /// §7.21.3.2-1 violation: the embedded CIDFontType2's /CIDToGIDMap entry is stripped.
    /// veraPDF should fire clause 7.21.3.2-1.
    /// </summary>
    internal static byte[] Ua1EmbeddedFontNoCidToGidMap()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontDict = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0 = (PdfDictionary)reader.ResolveValue(fontDict.Entries.First().Value)!;
        var descendants = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var descRef = (PdfIndirectReference)descendants[0];
        var desc = (PdfDictionary)reader.Resolve(descRef.ObjectNumber)!;
        return reader.AppendRevision([(descRef.ObjectNumber, CloneWithout(desc, "CIDToGIDMap"))]);
    }

    /// <summary>
    /// §7.21.6-3 violation: a symbolic TrueType font (Flags bit 3 = 4) with an /Encoding entry.
    /// veraPDF should fire clause 7.21.6-3.
    /// </summary>
    internal static byte[] Ua1SymbolicFontWithEncoding()
        => Ua1AddSimpleTrueType(flags: 4, encoding: new PdfName("WinAnsiEncoding"));

    /// <summary>
    /// §7.21.6-3 conformant: a symbolic TrueType font with no /Encoding entry.
    /// veraPDF should NOT fire clause 7.21.6-3.
    /// </summary>
    internal static byte[] Ua1SymbolicFontNoEncoding()
        => Ua1AddSimpleTrueType(flags: 4, encoding: null);

    /// <summary>
    /// §7.21.6-3 conformant: a non-symbolic TrueType font (Flags = 32) with WinAnsiEncoding.
    /// veraPDF should NOT fire clause 7.21.6-3.
    /// </summary>
    internal static byte[] Ua1NonSymbolicFontWinAnsi()
        => Ua1AddSimpleTrueType(flags: 32, encoding: new PdfName("WinAnsiEncoding"));

    // ── Batch A6 — §7.21.6-1/-2/-4 oracle fixtures ──────────────────────────────────────────────

    /// <summary>
    /// §7.21.6-1 violation: a non-symbolic TrueType font whose embedded program's cmap has been
    /// patched to a single Microsoft Symbol (3,0) subtable. veraPDF fires clause 7.21.6-1 because a
    /// non-symbolic font's program must contain at least one non-symbol-only cmap entry.
    /// </summary>
    internal static byte[] Ua1NonSymbolicTrueTypeSymbolOnlyCmap()
    {
        var asset = LoadAsset("DejaVuSans.ttf");
        var cmap = Ua1SfntTableOffset(asset, "cmap");
        asset[cmap + 2] = 0; asset[cmap + 3] = 1;   // numSubtables = 1
        asset[cmap + 4] = 0; asset[cmap + 5] = 3;   // platform = 3
        asset[cmap + 6] = 0; asset[cmap + 7] = 0;   // encoding = 0 (Symbol)
        return Ua1AddSimpleTrueType(flags: 32, encoding: new PdfName("WinAnsiEncoding"), fontProgram: asset);
    }

    /// <summary>
    /// §7.21.6-2 violation: a non-symbolic TrueType font with /Differences containing a glyph name
    /// not in the Adobe Glyph List (/BADNAME_XYZ). veraPDF fires clause 7.21.6-2 because
    /// differencesAreUnicodeCompliant is false.
    /// </summary>
    internal static byte[] Ua1NonSymbolicTrueTypeBadDifferences()
        => Ua1AddSimpleTrueType(flags: 32, encoding: Ua1MakeEncodingWithDiffs("WinAnsiEncoding", 65, "BADNAME_XYZ"));

    /// <summary>
    /// §7.21.6-1 and §7.21.6-2 violation: a non-symbolic TrueType font with AGL-compliant
    /// /Differences (/Alpha at code 65) but whose embedded program's cmap has been patched to
    /// symbol-only. veraPDF fires both 7.21.6-1 (program lacks non-symbol cmap) and 7.21.6-2
    /// (differencesAreUnicodeCompliant also requires the (3,1) cmap).
    /// </summary>
    internal static byte[] Ua1NonSymbolicTrueTypeAglDiffBadCmap()
    {
        var asset = LoadAsset("DejaVuSans.ttf");
        var cmap = Ua1SfntTableOffset(asset, "cmap");
        asset[cmap + 2] = 0; asset[cmap + 3] = 1;   // numSubtables = 1
        asset[cmap + 4] = 0; asset[cmap + 5] = 3;   // platform = 3
        asset[cmap + 6] = 0; asset[cmap + 7] = 0;   // encoding = 0 (Symbol)
        return Ua1AddSimpleTrueType(flags: 32, encoding: Ua1MakeEncodingWithDiffs("WinAnsiEncoding", 65, "Alpha"), fontProgram: asset);
    }

    /// <summary>
    /// §7.21.6-1/-2 conformant FP guard: a non-symbolic TrueType font with an AGL-compliant
    /// /Differences (/Alpha at code 65) and the standard DejaVu program (which has (3,1) cmap).
    /// veraPDF should NOT fire 7.21.6-1 or 7.21.6-2. Unit-test only (not an oracle violation).
    /// </summary>
    internal static byte[] Ua1NonSymbolicTrueTypeAglDiffCompliant()
        => Ua1AddSimpleTrueType(flags: 32, encoding: Ua1MakeEncodingWithDiffs("WinAnsiEncoding", 65, "Alpha"));

    /// <summary>
    /// §7.21.6-2 conformant FP guard: a non-symbolic TrueType font with bad /Differences
    /// (/BADNAME_XYZ at code 65) but the font is NOT selected via Tf in any content stream.
    /// veraPDF should NOT fire 7.21.6-2 because the font is unused. Unit-test only.
    /// </summary>
    internal static byte[] Ua1NonSymbolicTrueTypeBadDifferencesUnused()
        => Ua1AddSimpleTrueTypeUnused(flags: 32, encoding: Ua1MakeEncodingWithDiffs("WinAnsiEncoding", 65, "BADNAME_XYZ"));

    /// <summary>
    /// §7.21.6-4 conformant FP guard: a symbolic TrueType font with no /Encoding (satisfying
    /// §7.21.6-3) and a single cmap subtable. veraPDF should NOT fire 7.21.6-4 (nrCmaps == 1).
    /// Unit-test only (not an oracle violation).
    /// </summary>
    internal static byte[] Ua1SymbolicFontNoEncodingOneCmap()
    {
        var asset = LoadAsset("DejaVuSans.ttf");
        // Patch cmap to 1 subtable, keeping the (3,0) Symbol entry as the sole record.
        var cmap = Ua1SfntTableOffset(asset, "cmap");
        asset[cmap + 2] = 0; asset[cmap + 3] = 1;   // numSubtables = 1
        asset[cmap + 4] = 0; asset[cmap + 5] = 3;   // platform = 3
        asset[cmap + 6] = 0; asset[cmap + 7] = 0;   // encoding = 0 (Symbol)
        return Ua1AddSimpleTrueType(flags: 4, encoding: null, fontProgram: asset);
    }

    // Helper: adds a simple TrueType font (DejaVu, drawing 'A') to the UA-1 tagged baseline.
    // flags = 4 → Symbolic; flags = 32 → NonSymbolic. encoding = null → no /Encoding entry.
    // fontProgram overrides the embedded bytes (e.g. for cmap-patched programs).
    private static byte[] Ua1AddSimpleTrueType(int flags, PdfObject? encoding, byte[]? fontProgram = null)
    {
        var asset = LoadAsset("DejaVuSans.ttf");
        var programBytes = fontProgram ?? asset;
        int widthA;
        using (var measureDoc = new PdfDocument())
            widthA = (int)Math.Round(measureDoc.UseTrueTypeFont(asset).MeasureString("A", 1000));

        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontResources = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;

        var fontNum = reader.Size;
        var descNum = fontNum + 1;
        var ffNum = fontNum + 2;
        var contentNum = fontNum + 3;

        var fontFile = new PdfStream(programBytes);
        fontFile.Dictionary.Set(new PdfName("Length1"), new PdfInteger(programBytes.Length));
        var descriptor = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("FontDescriptor"))
            .Set(new PdfName("FontName"), new PdfName("DejaVuSans"))
            .Set(new PdfName("Flags"), new PdfInteger(flags))
            .Set(new PdfName("FontBBox"), new PdfArray([new PdfInteger(-1021), new PdfInteger(-463), new PdfInteger(1793), new PdfInteger(1232)]))
            .Set(new PdfName("ItalicAngle"), new PdfInteger(0))
            .Set(new PdfName("Ascent"), new PdfInteger(928))
            .Set(new PdfName("Descent"), new PdfInteger(-236))
            .Set(new PdfName("CapHeight"), new PdfInteger(928))
            .Set(new PdfName("StemV"), new PdfInteger(80))
            .Set(new PdfName("FontFile2"), new PdfIndirectReference(ffNum));
        var simple = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font"))
            .Set(PdfName.Subtype, new PdfName("TrueType"))
            .Set(PdfName.BaseFont, new PdfName("DejaVuSans"))
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(widthA)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));
        if (encoding is not null)
            simple.Set(new PdfName("Encoding"), encoding);

        var newFontResources = CloneDict(fontResources).Set(new PdfName("F1"), new PdfIndirectReference(fontNum));
        var newResources = CloneDict(resources).Set(PdfName.Font, newFontResources);
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));
        var content = new PdfStream(Encoding.ASCII.GetBytes("BT /F1 12 Tf 100 500 Td (A) Tj ET"));

        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (fontNum, simple), (descNum, descriptor), (ffNum, fontFile), (contentNum, content)]);
    }

    // Like Ua1AddSimpleTrueType but adds the font to /Resources without any content stream Tf call.
    private static byte[] Ua1AddSimpleTrueTypeUnused(int flags, PdfObject? encoding)
    {
        var asset = LoadAsset("DejaVuSans.ttf");
        int widthA;
        using (var measureDoc = new PdfDocument())
            widthA = (int)Math.Round(measureDoc.UseTrueTypeFont(asset).MeasureString("A", 1000));

        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontResources = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;

        var fontNum = reader.Size;
        var descNum = fontNum + 1;
        var ffNum = fontNum + 2;

        var fontFile = new PdfStream(asset);
        fontFile.Dictionary.Set(new PdfName("Length1"), new PdfInteger(asset.Length));
        var descriptor = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("FontDescriptor"))
            .Set(new PdfName("FontName"), new PdfName("DejaVuSans"))
            .Set(new PdfName("Flags"), new PdfInteger(flags))
            .Set(new PdfName("FontBBox"), new PdfArray([new PdfInteger(-1021), new PdfInteger(-463), new PdfInteger(1793), new PdfInteger(1232)]))
            .Set(new PdfName("ItalicAngle"), new PdfInteger(0))
            .Set(new PdfName("Ascent"), new PdfInteger(928))
            .Set(new PdfName("Descent"), new PdfInteger(-236))
            .Set(new PdfName("CapHeight"), new PdfInteger(928))
            .Set(new PdfName("StemV"), new PdfInteger(80))
            .Set(new PdfName("FontFile2"), new PdfIndirectReference(ffNum));
        var simple = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font"))
            .Set(PdfName.Subtype, new PdfName("TrueType"))
            .Set(PdfName.BaseFont, new PdfName("DejaVuSans"))
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(widthA)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));
        if (encoding is not null)
            simple.Set(new PdfName("Encoding"), encoding);

        // Font present in Resources but no content stream uses it (no Tf).
        var newFontResources = CloneDict(fontResources).Set(new PdfName("F99"), new PdfIndirectReference(fontNum));
        var newResources = CloneDict(resources).Set(PdfName.Font, newFontResources);
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);

        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (fontNum, simple), (descNum, descriptor), (ffNum, fontFile)]);
    }

    private static PdfDictionary Ua1MakeEncodingWithDiffs(string baseEnc, int atCode, string glyphName)
        => new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Encoding"))
            .Set(new PdfName("BaseEncoding"), new PdfName(baseEnc))
            .Set(new PdfName("Differences"), new PdfArray([new PdfInteger(atCode), new PdfName(glyphName)]));

    private static int Ua1SfntTableOffset(byte[] font, string tag)
    {
        var numTables = (font[4] << 8) | font[5];
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            if (font[rec] == tag[0] && font[rec + 1] == tag[1] && font[rec + 2] == tag[2] && font[rec + 3] == tag[3])
                return (font[rec + 8] << 24) | (font[rec + 9] << 16) | (font[rec + 10] << 8) | font[rec + 11];
        }
        throw new InvalidOperationException($"sfnt table '{tag}' not found.");
    }

    // ── Batch A4 — font-clause probe fixtures ────────────────────────────────────────────────────

    /// <summary>
    /// §7.21.3.3-1 violation: rewrites the embedded Type0 font's /Encoding from /Identity-H to
    /// a non-predefined, non-embedded CMap name (/FooBarCMap). veraPDF fires clause 7.21.3.3-1.
    /// </summary>
    internal static byte[] Ua1BadCMapName()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontDict = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0Ref = (PdfIndirectReference)fontDict.Entries.First().Value;
        var type0 = (PdfDictionary)reader.Resolve(type0Ref.ObjectNumber)!;
        var newType0 = CloneDict(type0).Set(new PdfName("Encoding"), new PdfName("FooBarCMap"));
        return reader.AppendRevision([(type0Ref.ObjectNumber, newType0)]);
    }

    /// <summary>
    /// §7.21.4.2-1: embeds the Noto Sans Shavian Type 1 font in a UA-1-tagged document. When
    /// <paramref name="complete"/> the /CharSet lists every glyph in the program; otherwise it
    /// omits one so veraPDF fires clause 7.21.4.2-1.
    /// Uses the invisible-draw trick (text rendering mode 3) so veraPDF counts the font as used
    /// (validating its /CharSet) but the §7.21.5 width check does not fire on a stale width.
    /// </summary>
    internal static byte[] WriterPdfWithType1CharSetUa1(bool complete)
    {
        var (fontFile, length1, length2, length3) = Type1FontAsset.ToFontFile();
        var names = Type1Glyphs.TryEnumerate(fontFile, length1)!.OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!complete)
            names.Remove("u1047F"); // drop one present glyph so the CharSet is incomplete.
        var charSet = string.Concat(names.Select(n => "/" + n));

        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);

        var fontNum = reader.Size;
        var descNum = fontNum + 1;
        var ffNum = fontNum + 2;
        var contentNum = fontNum + 3;

        var program = new PdfStream(fontFile);
        program.Dictionary
            .Set(new PdfName("Length1"), new PdfInteger(length1))
            .Set(new PdfName("Length2"), new PdfInteger(length2))
            .Set(new PdfName("Length3"), new PdfInteger(length3));
        var descriptor = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("FontDescriptor"))
            .Set(new PdfName("FontName"), new PdfName("AAAAAA+NotoSansShavian"))
            .Set(new PdfName("Flags"), new PdfInteger(4)) // symbolic
            .Set(new PdfName("FontBBox"),
                new PdfArray([new PdfInteger(0), new PdfInteger(-502), new PdfInteger(1396), new PdfInteger(1600)]))
            .Set(new PdfName("ItalicAngle"), new PdfInteger(0)).Set(new PdfName("Ascent"), new PdfInteger(1600))
            .Set(new PdfName("Descent"), new PdfInteger(-502)).Set(new PdfName("CapHeight"), new PdfInteger(1600))
            .Set(new PdfName("StemV"), new PdfInteger(80))
            .Set(new PdfName("CharSet"), new PdfLiteralString(Encoding.ASCII.GetBytes(charSet)))
            .Set(new PdfName("FontFile"), new PdfIndirectReference(ffNum));
        var font = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font")).Set(PdfName.Subtype, new PdfName("Type1"))
            .Set(PdfName.BaseFont, new PdfName("AAAAAA+NotoSansShavian"))
            .Set(new PdfName("FirstChar"), new PdfInteger(32)).Set(new PdfName("LastChar"), new PdfInteger(32))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(1000)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));

        var resObj = page.Get(new PdfName("Resources"));
        var resources = (resObj is null ? null : reader.ResolveValue(resObj)) as PdfDictionary ?? new PdfDictionary();
        var existingFonts = resources.Get(PdfName.Font) is { } ef
            ? (PdfDictionary)reader.ResolveValue(ef)!
            : new PdfDictionary();
        var newFonts = CloneDict(existingFonts).Set(new PdfName("F1"), new PdfIndirectReference(fontNum));
        var newResources = CloneDict(resources).Set(PdfName.Font, newFonts);
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);
        // Append an additional content stream using the Type1 font (Tr 3 = invisible, avoids width check).
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));
        var content = new PdfStream(Encoding.ASCII.GetBytes("BT 3 Tr /F1 12 Tf 72 700 Td (\x20) Tj ET"));

        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (fontNum, font), (descNum, descriptor), (ffNum, program),
                (contentNum, content)]);
    }

    /// <summary>
    /// §7.21.4.2-2: adds a /CIDSet to the embedded subset CIDFontType2's FontDescriptor in a
    /// UA-1-tagged document. When <paramref name="complete"/> the bitmap marks exactly
    /// CIDs 0..NumGlyphs−1; otherwise it is a single zero byte so veraPDF fires 7.21.4.2-2.
    /// </summary>
    internal static byte[] WriterPdfWithCidSetUa1(bool complete)
    {
        using var reader = PdfReader.Open(Ua1TaggedWithEmbeddedFont());
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0 = (PdfDictionary)reader.ResolveValue(fonts.Entries.First().Value)!;
        var descArr = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var cidFont = (PdfDictionary)reader.Resolve(((PdfIndirectReference)descArr[0]).ObjectNumber)!;
        var fdRef = (PdfIndirectReference)cidFont.Get(new PdfName("FontDescriptor"))!;
        var fd = (PdfDictionary)reader.Resolve(fdRef.ObjectNumber)!;
        var ff2 = reader.ResolveStream(((PdfIndirectReference)fd.Get(new PdfName("FontFile2"))!).ObjectNumber)!;
        var program = reader.GetDecodedStreamData(ff2)!;

        var numGlyphs = NumGlyphsOf(program);
        byte[] cidSet;
        if (complete)
        {
            cidSet = new byte[(numGlyphs + 7) / 8];
            for (var i = 0; i < numGlyphs; i++)
                cidSet[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        else
        {
            cidSet = [0x00];
        }

        var cidSetNum = reader.Size;
        var newFd = CloneDict(fd).Set(new PdfName("CIDSet"), new PdfIndirectReference(cidSetNum));
        return reader.AppendRevision([(fdRef.ObjectNumber, newFd), (cidSetNum, new PdfStream(cidSet))]);
    }

    // ── Batch A5a — §7.21.4.1-1 rendering-mode-scoped font embedding fixtures ──────────────────────

    /// <summary>
    /// §7.21.4.1-1 violation: a non-embedded simple TrueType font (no /FontFile2, no /FontDescriptor
    /// font program at all) drawn with a normal (visible) text rendering mode. veraPDF fires
    /// clause 7.21.4.1-1 (containsFontFile == false AND renderingMode != 3).
    /// </summary>
    internal static byte[] Ua1NonEmbeddedFontVisibleDraw()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontResources = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;

        var fontNum = reader.Size;
        var descNum = fontNum + 1;
        var contentNum = fontNum + 2;

        // Build a bare non-embedded TrueType font — /FontDescriptor present but no /FontFile2.
        var descriptor = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("FontDescriptor"))
            .Set(new PdfName("FontName"), new PdfName("VellumTestNonEmbedded"))
            .Set(new PdfName("Flags"), new PdfInteger(32)) // NonSymbolic
            .Set(new PdfName("FontBBox"), new PdfArray([new PdfInteger(0), new PdfInteger(-200), new PdfInteger(1000), new PdfInteger(800)]))
            .Set(new PdfName("ItalicAngle"), new PdfInteger(0))
            .Set(new PdfName("Ascent"), new PdfInteger(800))
            .Set(new PdfName("Descent"), new PdfInteger(-200))
            .Set(new PdfName("CapHeight"), new PdfInteger(700))
            .Set(new PdfName("StemV"), new PdfInteger(80));
        // Deliberately NO /FontFile2 — not embedded.

        var simple = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font"))
            .Set(PdfName.Subtype, new PdfName("TrueType"))
            .Set(PdfName.BaseFont, new PdfName("VellumTestNonEmbedded"))
            .Set(new PdfName("Encoding"), new PdfName("WinAnsiEncoding"))
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(722)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));

        // Draw with default Tr 0 (fill text, visible).
        var content = new PdfStream(Encoding.ASCII.GetBytes("BT /F2 12 Tf 100 500 Td (A) Tj ET"));

        var newFontResources = CloneDict(fontResources).Set(new PdfName("F2"), new PdfIndirectReference(fontNum));
        var newResources = CloneDict(resources).Set(PdfName.Font, newFontResources);
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));

        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (fontNum, simple), (descNum, descriptor), (contentNum, content)]);
    }

    /// <summary>
    /// §7.21.4.1-1 compliant (3-Tr exemption): the same non-embedded simple TrueType font as
    /// <see cref="Ua1NonEmbeddedFontVisibleDraw"/>, but drawn ONLY with text rendering mode 3
    /// (invisible text). veraPDF does NOT fire clause 7.21.4.1-1 because renderingMode == 3.
    /// This is the FP-safety fixture: if the in-process rule fired here it would be a false positive.
    /// </summary>
    internal static byte[] Ua1NonEmbeddedFontInvisibleOnly()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontResources = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;

        var fontNum = reader.Size;
        var descNum = fontNum + 1;
        var contentNum = fontNum + 2;

        var descriptor = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("FontDescriptor"))
            .Set(new PdfName("FontName"), new PdfName("VellumTestNonEmbeddedInvis"))
            .Set(new PdfName("Flags"), new PdfInteger(32))
            .Set(new PdfName("FontBBox"), new PdfArray([new PdfInteger(0), new PdfInteger(-200), new PdfInteger(1000), new PdfInteger(800)]))
            .Set(new PdfName("ItalicAngle"), new PdfInteger(0))
            .Set(new PdfName("Ascent"), new PdfInteger(800))
            .Set(new PdfName("Descent"), new PdfInteger(-200))
            .Set(new PdfName("CapHeight"), new PdfInteger(700))
            .Set(new PdfName("StemV"), new PdfInteger(80));
        // No /FontFile2 — not embedded.

        var simple = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Font"))
            .Set(PdfName.Subtype, new PdfName("TrueType"))
            .Set(PdfName.BaseFont, new PdfName("VellumTestNonEmbeddedInvis"))
            .Set(new PdfName("Encoding"), new PdfName("WinAnsiEncoding"))
            .Set(new PdfName("FirstChar"), new PdfInteger(65))
            .Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(722)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));

        // Draw ONLY with Tr 3 (invisible text). The font is used but only invisibly.
        var content = new PdfStream(Encoding.ASCII.GetBytes("BT 3 Tr /F2 12 Tf 100 500 Td (A) Tj ET"));

        var newFontResources = CloneDict(fontResources).Set(new PdfName("F2"), new PdfIndirectReference(fontNum));
        var newResources = CloneDict(resources).Set(PdfName.Font, newFontResources);
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));

        return reader.AppendRevision(
            [(pageRef.ObjectNumber, newPage), (fontNum, simple), (descNum, descriptor), (contentNum, content)]);
    }

    /// <summary>
    /// §7.21.4.1-1 compliant (embedded): a simple TrueType font WITH an embedded font program
    /// (DejaVu, /FontFile2) drawn with a normal (visible) rendering mode. veraPDF does NOT fire
    /// clause 7.21.4.1-1 because containsFontFile == true.
    /// </summary>
    internal static byte[] Ua1EmbeddedSimpleFontVisibleDraw()
        => Ua1AddSimpleTrueType(flags: 32, encoding: new PdfName("WinAnsiEncoding"));

    // ── Batch A5b — §7.21.8-1 .notdef + §7.21.7-2 forbidden-ToUnicode fixtures ─────────────────────

    /// <summary>
    /// §7.21.8-1 violation: appends a content stream to the UA-1 tagged baseline that shows glyph
    /// index 0 (0x0000 = .notdef) using the existing Identity-H / CIDFontType2 font. veraPDF fires
    /// clause 7.21.8-1 (.notdef referenced). The UA-1 baseline already has real glyphs shown by the
    /// tagged text; this adds one extra show with the .notdef code.
    /// </summary>
    internal static byte[] Ua1PdfDrawingNotdef()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var fontName = fonts.Entries.First().Key.Value;

        var contentNum = reader.Size;
        var newPage = CloneDict(page);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));
        // Show glyph index 0 (0x0000 = .notdef) using the Identity-H font.
        var content = new PdfStream(Encoding.ASCII.GetBytes($"BT /{fontName} 12 Tf 72 600 Td <0000> Tj ET"));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, content)]);
    }

    /// <summary>
    /// §7.21.7-2 violation: the Type0 font's /ToUnicode CMap maps a SHOWN code (0x0041) to U+0000,
    /// a forbidden value. The page content shows code 0x0041 using the existing Identity-H font;
    /// veraPDF fires clause 7.21.7-2 (toUnicode maps to U+0000).
    /// </summary>
    internal static byte[] Ua1ToUnicodeForbiddenShownCode()
        => Ua1WithCustomToUnicode(shownCode: 0x0041, shownCodeUnicode: " ", unusedCode: 0x0042, unusedCodeUnicode: "B");

    /// <summary>
    /// §7.21.7-2 compliant: the /ToUnicode CMap maps the SHOWN code (0x0041) to U+0041 (a valid
    /// Unicode value). The page also has an UNUSED code entry (0xFFFF → U+0000), which is a forbidden
    /// value but for an unused code — veraPDF does NOT fire 7.21.7-2 because only shown codes are checked.
    /// This is the critical regression guard for the prior false positive (git ab5dc76).
    /// </summary>
    internal static byte[] Ua1ToUnicodeUnusedBadMappingCompliant()
        => Ua1WithCustomToUnicode(shownCode: 0x0041, shownCodeUnicode: "A", unusedCode: 0xFFFF, unusedCodeUnicode: " ");

    // Builds a UA-1 tagged baseline, finds the first Identity-H Type0 font, replaces its /ToUnicode
    // with a custom CMap that:
    //   - maps shownCode to shownCodeUnicode (the code that the injected content stream shows)
    //   - maps unusedCode to unusedCodeUnicode (a code that is NOT shown)
    // Then appends a content stream that shows only shownCode with the font.
    private static byte[] Ua1WithCustomToUnicode(
        int shownCode, string shownCodeUnicode, int unusedCode, string unusedCodeUnicode)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0Ref = (PdfIndirectReference)fonts.Entries.First().Value;
        var type0 = (PdfDictionary)reader.Resolve(type0Ref.ObjectNumber)!;
        var fontName = fonts.Entries.First().Key.Value;

        // Build a minimal ToUnicode CMap with exactly two bfchar entries.
        var toUnicodeCMap = BuildToUnicodeCMap(
            [(shownCode, shownCodeUnicode), (unusedCode, unusedCodeUnicode)]);
        var toUnicodeNum = reader.Size;
        var toUnicodeStream = new PdfStream(Encoding.Latin1.GetBytes(toUnicodeCMap));

        // Replace the Type0 font's /ToUnicode with our custom stream.
        var newType0 = CloneDict(type0).Set(new PdfName("ToUnicode"), new PdfIndirectReference(toUnicodeNum));

        // Append a content stream that shows only shownCode (2-byte big-endian) with the font.
        var contentNum = reader.Size + 1;
        var newPage = CloneDict(page);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));
        var content = new PdfStream(
            Encoding.ASCII.GetBytes($"BT /{fontName} 12 Tf 72 580 Td <{shownCode:X4}> Tj ET"));

        return reader.AppendRevision(
            [(type0Ref.ObjectNumber, newType0),
             (toUnicodeNum, toUnicodeStream),
             (pageRef.ObjectNumber, newPage),
             (contentNum, content)]);
    }

    // Builds a minimal ToUnicode CMap text with a beginbfchar section.
    // Each entry is (srcCode, dstUnicode string). The destination is encoded as UTF-16BE hex.
    private static string BuildToUnicodeCMap(IEnumerable<(int Code, string Unicode)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/CIDInit /ProcSet findresource begin");
        sb.AppendLine("12 dict begin");
        sb.AppendLine("begincmap");
        sb.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def");
        sb.AppendLine("/CMapName /Adobe-Identity-UCS def");
        sb.AppendLine("/CMapType 2 def");
        var list = entries.ToList();
        sb.AppendLine($"{list.Count} beginbfchar");
        foreach (var (code, unicode) in list)
        {
            var destHex = string.Concat(Encoding.BigEndianUnicode.GetBytes(unicode).Select(b => b.ToString("X2")));
            sb.AppendLine($"<{code:X4}> <{destHex}>");
        }
        sb.AppendLine("endbfchar");
        sb.AppendLine("endcmap");
        sb.AppendLine("CMapName currentdict /CMap defineresource pop");
        sb.AppendLine("end");
        sb.AppendLine("end");
        return sb.ToString();
    }

    // ── Batch A5c — §7.21.4.1-2 glyph presence (Tr-3-exempt) fixtures ───────────────────────────

    /// <summary>
    /// §7.21.4.1-2 violation: the UA-1 tagged baseline's embedded Identity-H CIDFontType2 font is
    /// asked to show GID 0xEA60 (60000) in a VISIBLE text rendering mode (Tr 0, the default). The
    /// embedded program is a small subset — GID 60000 is far beyond numGlyphs. veraPDF fires
    /// clause 7.21.4.1-2 (isGlyphPresent == false, renderingMode != 3).
    /// </summary>
    internal static byte[] Ua1OutOfRangeGlyphVisible()
        => Ua1AppendOutOfRangeGlyph(invisible: false);

    /// <summary>
    /// §7.21.4.1-2 FP-safety guard (Tr-3 exemption): the SAME out-of-range GID (0xEA60) is shown
    /// ONLY with text rendering mode 3 (invisible text). veraPDF does NOT fire 7.21.4.1-2 because
    /// the predicate includes <c>renderingMode == 3</c> as an exemption. The in-process rule must
    /// also NOT fire. Cross-validated against veraPDF 1.30.2: clause 7.21.4.1-2 absent from failures.
    /// </summary>
    internal static byte[] Ua1OutOfRangeGlyphInvisible()
        => Ua1AppendOutOfRangeGlyph(invisible: true);

    // ── Batch A5d — §7.21.5-1 glyph width consistency (Identity-H scope) ────────────────────────

    /// <summary>
    /// §7.21.5-1 violation: the UA-1 tagged baseline's embedded CIDFontType2 has its /W array
    /// removed, so every shown glyph falls back to /DW=1000. The DejaVu subset glyphs have actual
    /// hmtx advances that differ from 1000 by more than 1 (e.g. 'T' ≈ 333), making
    /// <c>|widthFromFontProgram − widthFromDictionary| &gt; 1</c>. veraPDF fires clause 7.21.5-1.
    /// Cross-validated against veraPDF 1.30.2 (clause 7.21.5, testNumber 1, status failed).
    /// </summary>
    internal static byte[] Ua1GlyphWidthMismatch()
    {
        var baseline = Ua1TaggedWithEmbeddedFont();
        using var reader = PdfReader.Open(baseline);
        var (_, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0 = (PdfDictionary)reader.ResolveValue(fonts.Entries.First().Value)!;
        var descArr = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var descRef = (PdfIndirectReference)descArr[0];
        var descendant = (PdfDictionary)reader.Resolve(descRef.ObjectNumber)!;
        // Remove /W: shown glyphs fall to /DW=1000, but their actual hmtx widths differ > 1.
        return reader.AppendRevision([(descRef.ObjectNumber, CloneWithout(descendant, "W"))]);
    }

    /// <summary>
    /// §7.21.5-1 FP-safety (unused font): a second CIDFontType2 font with /W removed is added to
    /// the page resources but is NEVER selected via a <c>Tf</c> operator. Since no glyph from that
    /// font is actually shown, veraPDF must NOT fire clause 7.21.5-1 (usage-scoped check).
    /// Cross-validated against veraPDF 1.30.2: clause 7.21.5-1 absent when font is unused.
    /// </summary>
    internal static byte[] Ua1GlyphWidthMismatchUnused()
    {
        // Strategy: take the violation fixture (bad /W on the embedded font) and patch the page's
        // content stream to remove the Tf operator so the corrupted font is never selected.
        // Simpler: start from the baseline, add a corrupted copy of the font to resources with a
        // different resource name, but never reference it in the content stream.
        var baseline = Ua1TaggedWithEmbeddedFont();
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0Ref = (PdfIndirectReference)fonts.Entries.First().Value;
        var type0 = (PdfDictionary)reader.Resolve(type0Ref.ObjectNumber)!;
        var descArr = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var descRef = (PdfIndirectReference)descArr[0];
        var descendant = (PdfDictionary)reader.Resolve(descRef.ObjectNumber)!;

        // Inject a second font entry that points to a clone of the same Type0 font but whose
        // descendant has /W removed. The content stream never selects "/F2 Tf", so it is unused.
        var badDescNum = reader.Size;
        var badDesc = CloneWithout(descendant, "W");
        var badDescendants = new PdfArray([new PdfIndirectReference(badDescNum)]);
        var badType0 = CloneDict(type0).Set(new PdfName("DescendantFonts"), badDescendants);
        var badType0Num = badDescNum + 1;
        var newFonts = CloneDict(fonts).Set(new PdfName("F2"), new PdfIndirectReference(badType0Num));
        var newResources = CloneDict(resources).Set(PdfName.Font, newFonts);
        var newPage = CloneDict(page).Set(new PdfName("Resources"), newResources);

        return reader.AppendRevision([
            (badDescNum, badDesc),
            (badType0Num, badType0),
            (pageRef.ObjectNumber, newPage),
        ]);
    }

    /// <summary>
    /// §7.21.5-1 FP-safety (Tr-3 exemption): the /W array is removed (mismatch for all glyphs),
    /// but the font is shown ONLY with text rendering mode 3 (invisible text). The veraPDF predicate
    /// includes <c>renderingMode == 3</c> as an unconditional exemption, so it must NOT fire.
    /// Cross-validated against veraPDF 1.30.2: clause 7.21.5-1 absent when renderingMode==3.
    /// </summary>
    internal static byte[] Ua1GlyphWidthMismatchInvisible()
    {
        var baseline = Ua1TaggedWithEmbeddedFont();
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0 = (PdfDictionary)reader.ResolveValue(fonts.Entries.First().Value)!;
        var descArr = (PdfArray)reader.ResolveValue(type0.Get(new PdfName("DescendantFonts"))!)!;
        var descRef = (PdfIndirectReference)descArr[0];
        var descendant = (PdfDictionary)reader.Resolve(descRef.ObjectNumber)!;

        // Replace the existing content stream with one that prepends "3 Tr" so every draw
        // uses invisible rendering mode, AND corrupt /W on the CIDFont to introduce a mismatch.
        var contentsObj = page.Get(new PdfName("Contents"));
        PdfIndirectReference oldContentRef;
        if (contentsObj is PdfIndirectReference r)
            oldContentRef = r;
        else if (contentsObj is PdfArray arr && arr.Count > 0 && arr[0] is PdfIndirectReference ar)
            oldContentRef = ar;
        else
            throw new InvalidOperationException("Expected content stream ref");

        // Read existing content, prepend "3 Tr " to make all draws invisible.
        var existingStream = reader.ResolveStream(oldContentRef.ObjectNumber)
            ?? throw new InvalidOperationException("Expected content stream");
        var existingBytes = reader.GetDecodedStreamData(existingStream)
            ?? throw new InvalidOperationException("Expected decoded stream data");

        var trPrefix = Encoding.ASCII.GetBytes("3 Tr ");
        var newBytes = new byte[trPrefix.Length + existingBytes.Length];
        trPrefix.CopyTo(newBytes, 0);
        existingBytes.CopyTo(newBytes, trPrefix.Length);

        var newContentNum = reader.Size;

        var newPage = CloneDict(page).Set(new PdfName("Contents"), new PdfIndirectReference(newContentNum));
        var badDesc = CloneWithout(descendant, "W");

        return reader.AppendRevision([
            (pageRef.ObjectNumber, newPage),
            (newContentNum, new PdfStream(newBytes)),
            (descRef.ObjectNumber, badDesc),
        ]);
    }

    // Appends a content stream that shows GID 0xEA60 (60000) using the existing embedded Identity-H
    // font from the UA-1 tagged baseline. When invisible=true, the stream starts with `3 Tr` so the
    // draw is invisible (text rendering mode 3). The GID is 60000 — beyond any small font subset.
    private static byte[] Ua1AppendOutOfRangeGlyph(bool invisible)
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fonts = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var fontName = fonts.Entries.First().Key.Value;

        var contentNum = reader.Size;
        var newPage = CloneDict(page);
        newPage.Set(new PdfName("Contents"),
            new PdfArray([page.Get(new PdfName("Contents"))!, new PdfIndirectReference(contentNum)]));

        // GID 0xEA60 = 60000 — always beyond the small embedded subset.
        var trOp = invisible ? "3 Tr " : "";
        var content = new PdfStream(
            Encoding.ASCII.GetBytes($"BT {trOp}/{fontName} 12 Tf 72 600 Td <EA60> Tj ET"));

        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, content)]);
    }

    // ── Batch B1 — §7.1 structure-tree walker foundation ─────────────────────────────────────────

    /// <summary>
    /// §7.1-12 violation: removes the <c>/P</c> (parent) entry from the leaf P StructElem in the
    /// UA-1 tagged baseline. veraPDF fires clause 7.1, testNumber 12 (containsParent == false).
    /// </summary>
    internal static byte[] Ua1StructElemMissingParent()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);

        // Walk: StructTreeRoot -> Document StructElem -> P StructElem (the leaf)
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        // /K on StructTreeRoot is the Document StructElem ref
        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference;
        if (docRef is null) throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;

        // /K on Document StructElem is the P StructElem ref (or array of refs)
        var docK = doc.Get(new PdfName("K"));
        PdfIndirectReference pElemRef;
        if (docK is PdfIndirectReference pr)
            pElemRef = pr;
        else if (docK is PdfArray arr && arr.Count > 0 && arr[0] is PdfIndirectReference ar)
            pElemRef = ar;
        else
            throw new InvalidOperationException("Expected P StructElem ref");

        var pElem = (PdfDictionary)reader.Resolve(pElemRef.ObjectNumber)!;
        // Remove /P
        var newPElem = CloneWithout(pElem, "P");

        return reader.AppendRevision([(pElemRef.ObjectNumber, newPElem)]);
    }

    /// <summary>
    /// §7.1-6 violation: adds a circular role mapping <c>/Foo /Bar /Bar /Foo</c> to the
    /// StructTreeRoot /RoleMap, AND injects a StructElem with <c>/S /Foo</c> so that
    /// veraPDF's <c>circularMappingExist</c> property is <c>true</c> on a real PDStructElem
    /// (the rule only fires when an element actually exercises the circular chain).
    /// </summary>
    internal static byte[] Ua1CircularRoleMap()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        // Walk to the Document StructElem (root)
        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;

        // Walk to the P StructElem (leaf)
        var docK = doc.Get(new PdfName("K"));
        PdfIndirectReference pElemRef;
        if (docK is PdfIndirectReference pr) pElemRef = pr;
        else if (docK is PdfArray arr && arr.Count > 0 && arr[0] is PdfIndirectReference ar) pElemRef = ar;
        else throw new InvalidOperationException("Expected P StructElem ref");
        var pElem = (PdfDictionary)reader.Resolve(pElemRef.ObjectNumber)!;

        // Add a new /Foo StructElem (child of Document, sibling of P) that uses the circular type.
        // The existing P StructElem keeps its /K MCID; the new /Foo element has no /K.
        var fooElemNum = reader.Size;
        var fooElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("Foo"))
            .Set(new PdfName("P"), strRef); // parent = StructTreeRoot (simplified; still a /P)

        // Update Document to have /K [P, Foo]
        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference(pElemRef.ObjectNumber)),
            new PdfIndirectReference(fooElemNum),
        ]));

        // Update StructTreeRoot: add circular RoleMap
        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("Foo"), new PdfName("Bar"))
            .Set(new PdfName("Bar"), new PdfName("Foo")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
            (docRef.ObjectNumber, newDoc),
            (fooElemNum, fooElem),
        ]);
    }

    /// <summary>
    /// §7.1-7 violation: adds a /RoleMap entry whose KEY is the standard structure type
    /// <c>/Table</c> remapped to <c>/Div</c>, AND injects a StructElem with <c>/S /Table</c>
    /// so that veraPDF's <c>remappedStandardType</c> property is non-null on a real PDStructElem.
    /// </summary>
    internal static byte[] Ua1StandardTypeRemapped()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;

        var docK = doc.Get(new PdfName("K"));

        // Add a new /Table StructElem (child of Document) that uses the remapped standard type.
        var tableElemNum = reader.Size;
        var tableElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("Table"))
            .Set(new PdfName("P"), strRef); // parent reference (simplified)

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference((int)(docK is PdfInteger pi ? pi.Value : 0))),
            new PdfIndirectReference(tableElemNum),
        ]));

        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("Table"), new PdfName("Div")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
            (docRef.ObjectNumber, newDoc),
            (tableElemNum, tableElem),
        ]);
    }

    // ── Batch B9 — §7.1-5 non-standard structure type (SENonStandard) ────────────────────────────

    /// <summary>
    /// §7.1-5 violation: injects a StructElem with <c>/S /MyCustomTag</c> (a non-standard type)
    /// and NO <c>/RoleMap</c> entry for it. veraPDF fires clause 7.1, testNumber 5
    /// (<c>isNotMappedToStandardType == true</c>).
    /// </summary>
    internal static byte[] Ua1NonStandardTypeUnmapped()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var docK = doc.Get(new PdfName("K"));

        // Add a new StructElem with a custom non-standard /S that has no /RoleMap entry.
        var customElemNum = reader.Size;
        var customElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("MyCustomTag"))
            .Set(new PdfName("P"), strRef);

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference(docRef.ObjectNumber)),
            new PdfIndirectReference(customElemNum),
        ]));

        return reader.AppendRevision([
            (docRef.ObjectNumber, newDoc),
            (customElemNum, customElem),
        ]);
    }

    /// <summary>
    /// §7.1-5 FP-safety guard: injects a StructElem with <c>/S /MyCustomTag</c> and a
    /// <c>/RoleMap &lt;&lt; /MyCustomTag /Div &gt;&gt;</c> entry so the type resolves to the
    /// standard type <c>/Div</c>. veraPDF must NOT fire 7.1-5.
    /// </summary>
    internal static byte[] Ua1NonStandardTypeRoleMapped()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var docK = doc.Get(new PdfName("K"));

        // Add a StructElem with /S /MyCustomTag, role-mapped to the standard type /Div.
        var customElemNum = reader.Size;
        var customElem = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("MyCustomTag"))
            .Set(new PdfName("P"), strRef);

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray([
            (PdfObject)(docK is PdfIndirectReference ? docK : new PdfIndirectReference(docRef.ObjectNumber)),
            new PdfIndirectReference(customElemNum),
        ]));

        // StructTreeRoot with /RoleMap << /MyCustomTag /Div >>
        var newStr = CloneDict(str);
        newStr.Set(new PdfName("RoleMap"), new PdfDictionary()
            .Set(new PdfName("MyCustomTag"), new PdfName("Div")));

        return reader.AppendRevision([
            (strRef.ObjectNumber, newStr),
            (docRef.ObjectNumber, newDoc),
            (customElemNum, customElem),
        ]);
    }

    // ── Batch B10 — §7.4.2-1 heading nesting + §7.5-1/-2 connected headers ──────────────────────

    /// <summary>
    /// §7.4.2-1 VIOLATION: injects H1 then H3 (skipping H2) into the Document StructElem's /K.
    /// veraPDF fires clause 7.4.2, testNumber 1 (hasCorrectNestingLevel == false on H3).
    /// In-process: UaHeadingNestingRule fires "ISO14289-1:7.4.2-1".
    /// </summary>
    private static byte[] Ua1HeadingsSkip()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        return InjectHeadingElems(reader, ["H1", "H3"]);
    }

    /// <summary>
    /// §7.4.2-1 COMPLIANT: injects H1, H2, H3 (no skip) into the Document StructElem's /K.
    /// veraPDF does not fire 7.4.2-1. In-process: UaHeadingNestingRule must NOT fire.
    /// </summary>
    private static byte[] Ua1HeadingsNoSkip()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        return InjectHeadingElems(reader, ["H1", "H2", "H3"]);
    }

    /// <summary>
    /// Injects a sequence of Hn StructElem children into the Document StructElem's /K array,
    /// appended after the existing children.
    /// </summary>
    private static byte[] InjectHeadingElems(PdfDocumentReader reader, string[] headingTypes)
    {
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var existingK = doc.Get(new PdfName("K"));

        var revision = new List<(int, PdfObject)>();
        int nextNum = reader.Size;
        var headingRefs = new List<PdfIndirectReference>();

        foreach (var hType in headingTypes)
        {
            var hNum = nextNum++;
            var hElem = new PdfDictionary()
                .Set(PdfName.Type, new PdfName("StructElem"))
                .Set(new PdfName("S"), new PdfName(hType))
                .Set(new PdfName("P"), docRef);
            revision.Add((hNum, hElem));
            headingRefs.Add(new PdfIndirectReference(hNum));
        }

        var kItems = new List<PdfObject>();
        if (existingK is PdfIndirectReference kRef) kItems.Add(kRef);
        else if (existingK is PdfArray kArr)
            for (int i = 0; i < kArr.Count; i++) kItems.Add(kArr[i]);
        kItems.AddRange(headingRefs);

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray(kItems.ToArray()));
        revision.Add((docRef.ObjectNumber, newDoc));

        return reader.AppendRevision(revision);
    }

    /// <summary>
    /// §7.5-1 VIOLATION: injects a Table → TR → [TH (no /Scope), TD (no /Headers)] structure.
    /// veraPDF fires 7.5-1 (hasConnectedHeader == false, unknownHeaders == '').
    /// </summary>
    private static byte[] Ua1TdNoConnectedHeader()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        return InjectSimpleTable(reader, thScope: null, tdHeadersId: null, thId: null);
    }

    /// <summary>
    /// §7.5-2 VIOLATION: injects a Table → TR → [TH (no /Scope), TD (/Headers = ["nonexistent"])].
    /// veraPDF fires 7.5-2 (hasConnectedHeader == false, unknownHeaders != '').
    /// </summary>
    private static byte[] Ua1TdUnknownHeaderId()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        return InjectSimpleTable(reader, thScope: null, tdHeadersId: "nonexistent", thId: null);
    }

    /// <summary>
    /// §7.5 COMPLIANT: injects a Table → TR → [TH (/Scope /Column), TD (no /Headers)].
    /// veraPDF does not fire 7.5-1 or 7.5-2. In-process: UaTableHeaderRule must NOT fire.
    /// </summary>
    private static byte[] Ua1TdScopedHeader()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        return InjectSimpleTable(reader, thScope: "Column", tdHeadersId: null, thId: null);
    }

    /// <summary>
    /// Injects a minimal table (Table → TR → [TH, TD]) with the given TH scope and TD headers.
    /// </summary>
    private static byte[] InjectSimpleTable(
        PdfDocumentReader reader,
        string? thScope,
        string? tdHeadersId,
        string? thId)
    {
        var strRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("StructTreeRoot"))!;
        var str = (PdfDictionary)reader.Resolve(strRef.ObjectNumber)!;

        var docRef = str.Get(new PdfName("K")) as PdfIndirectReference
            ?? throw new InvalidOperationException("Expected Document StructElem ref");
        var doc = (PdfDictionary)reader.Resolve(docRef.ObjectNumber)!;
        var existingK = doc.Get(new PdfName("K"));

        int nextNum = reader.Size;
        var revision = new List<(int, PdfObject)>();

        var tableNum = nextNum++;
        var trNum = nextNum++;
        var thNum = nextNum++;
        var tdNum = nextNum++;

        var tableRef = new PdfIndirectReference(tableNum);
        var trRef = new PdfIndirectReference(trNum);

        // TH element
        var thDict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("TH"))
            .Set(new PdfName("P"), trRef);
        if (thId != null)
            thDict.Set(new PdfName("ID"), new PdfLiteralString(Encoding.ASCII.GetBytes(thId)));
        if (thScope != null)
            thDict.Set(new PdfName("A"), new PdfDictionary()
                .Set(new PdfName("O"), new PdfName("Table"))
                .Set(new PdfName("Scope"), new PdfName(thScope)));

        // TD element
        var tdDict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("TD"))
            .Set(new PdfName("P"), trRef);
        if (tdHeadersId != null)
            tdDict.Set(new PdfName("A"), new PdfDictionary()
                .Set(new PdfName("O"), new PdfName("Table"))
                .Set(new PdfName("Headers"), new PdfArray([
                    new PdfLiteralString(Encoding.ASCII.GetBytes(tdHeadersId))
                ])));

        revision.Add((thNum, thDict));
        revision.Add((tdNum, tdDict));

        revision.Add((trNum, new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("TR"))
            .Set(new PdfName("P"), tableRef)
            .Set(new PdfName("K"), new PdfArray([
                new PdfIndirectReference(thNum),
                new PdfIndirectReference(tdNum),
            ]))));

        revision.Add((tableNum, new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructElem"))
            .Set(new PdfName("S"), new PdfName("Table"))
            .Set(new PdfName("P"), docRef)
            .Set(new PdfName("K"), new PdfArray([trRef]))));

        // Update Document /K
        var kItems = new List<PdfObject>();
        if (existingK is PdfIndirectReference kRef2) kItems.Add(kRef2);
        else if (existingK is PdfArray kArr2)
            for (int i = 0; i < kArr2.Count; i++) kItems.Add(kArr2[i]);
        kItems.Add(tableRef);

        var newDoc = CloneDict(doc);
        newDoc.Set(new PdfName("K"), new PdfArray(kItems.ToArray()));
        revision.Add((docRef.ObjectNumber, newDoc));

        return reader.AppendRevision(revision);
    }

    // ── Batch C1: marked-content lang fixtures ──────────────────────────────────────────────────

    /// <summary>
    /// §7.2-34 REGRESSION (FP fix): tagged UA-1 with NO catalog /Lang, but with the owning struct
    /// element carrying /Lang (en-US). veraPDF resolves language via MCID→ParentTree→struct-elem
    /// and does NOT fire 7.2-34. The in-process UaMarkedContentLangRule must also NOT fire.
    /// This fixture captures the confirmed false positive fixed by MCID→struct-elem /Lang resolution.
    ///
    /// Construction: start from WriterPdfTagged (which writes a proper ParentTree), remove catalog
    /// /Lang, then walk the ParentTree for MCID 0 on page 0 to find the /P struct elem, and stamp
    /// /Lang (en-US) onto it via AppendRevision.
    /// </summary>
    private static byte[] Ua1McTextStructElemLang()
    {
        // Step 1: build a no-catalog-/Lang baseline (has proper struct tree + ParentTree).
        var baseline = Ua1McTextNoLang();
        using var reader = PdfReader.Open(baseline);

        // Step 2: resolve page → /StructParents integer.
        var (pageRef, page) = FirstPage(reader);
        var spRaw = page.Get(new PdfName("StructParents"));
        var structParentsObj = spRaw is null ? null : reader.ResolveValue(spRaw);
        if (structParentsObj is not PdfInteger structParentsInt)
            throw new InvalidOperationException("Page has no /StructParents.");
        var structParentsKey = (int)structParentsInt.Value;

        // Step 3: find the StructTreeRoot → /ParentTree.
        var strRaw = reader.Catalog.Get(new PdfName("StructTreeRoot"));
        var strRootObj = strRaw is null ? null : reader.ResolveValue(strRaw);
        if (strRootObj is not PdfDictionary strRoot)
            throw new InvalidOperationException("No StructTreeRoot.");
        var ptRaw = strRoot.Get(new PdfName("ParentTree"));
        var parentTreeObj = ptRaw is null ? null : reader.ResolveValue(ptRaw);
        if (parentTreeObj is not PdfDictionary parentTree)
            throw new InvalidOperationException("No ParentTree.");

        // Step 4: walk the /Nums number-tree to find the array for structParentsKey.
        var mcidArray = FindNumsArray(reader, parentTree, structParentsKey);
        if (mcidArray is null || mcidArray.Count == 0)
            throw new InvalidOperationException("ParentTree entry not found for page StructParents key.");

        // Step 5: array[0] is the indirect ref to the /P struct elem for MCID 0.
        var elemRef = mcidArray[0] as PdfIndirectReference
            ?? throw new InvalidOperationException("ParentTree MCID entry is not an indirect reference.");
        var elemDict = reader.Resolve(elemRef.ObjectNumber) as PdfDictionary
            ?? throw new InvalidOperationException("Struct elem dict could not be resolved.");

        // Step 6: clone the struct elem and add /Lang (en-US).
        var newElem = CloneDict(elemDict);
        newElem.Set(new PdfName("Lang"), new PdfLiteralString(Encoding.ASCII.GetBytes("en-US")));

        return reader.AppendRevision([(elemRef.ObjectNumber, newElem)]);
    }

    /// <summary>
    /// §7.2-34 FP fix guard — named-reference BDC: same as <see cref="Ua1McTextStructElemLang"/>
    /// but the page content uses the named-reference BDC form (<c>/P /MC0 BDC</c>) rather than an
    /// inline property dict. The page's /Resources/Properties/MC0 dict carries /MCID 0; the P struct
    /// element has /Lang (en-US). veraPDF does NOT fire 7.2-34 (resolves named-ref MCID → struct
    /// elem /Lang). Post-fix, the in-process rule must also not fire 7.2-34.
    /// </summary>
    private static byte[] Ua1McTextNamedRefBdcStructElemLang()
    {
        // Start from the struct-elem-lang baseline (inline BDC, no catalog /Lang, /Lang on P elem).
        var baseline = Ua1McTextStructElemLang();
        using var reader = PdfReader.Open(baseline);

        var (pageRef, page) = FirstPage(reader);

        // Find the existing font name to keep the content valid.
        var resourcesObj = page.Get(new PdfName("Resources"));
        var resources = resourcesObj is not null ? reader.ResolveValue(resourcesObj) as PdfDictionary : null;
        var fontDictObj = resources?.Get(PdfName.Font);
        var fontDict = fontDictObj is not null ? reader.ResolveValue(fontDictObj) as PdfDictionary : null;
        var fontName = fontDict?.Entries.FirstOrDefault().Key.Value ?? "F1";

        // Add a new /Properties dict object: /MC0 = << /MCID 0 >>
        var propsNum = reader.Size;
        var propsDict = new PdfDictionary().Set(new PdfName("MCID"), new PdfInteger(0));

        // Replace content stream: use named-ref BDC instead of inline dict BDC.
        var contentNum = reader.Size + 1;
        var contentBytes = Encoding.ASCII.GetBytes(
            $"BT\n/{fontName} 12 Tf\n1 0 0 1 72 720 Tm\n"
            + "/P /MC0 BDC\n(hello) Tj\nEMC\nET\n");
        var newContent = new PdfStream(contentBytes);

        // Patch the page: update /Resources to add /Properties, update /Contents.
        var newPage = CloneDict(page);
        var newResources = resources is not null ? CloneDict(resources) : new PdfDictionary();
        var newPropertiesDict = new PdfDictionary()
            .Set(new PdfName("MC0"), new PdfIndirectReference(propsNum));
        newResources.Set(new PdfName("Properties"), newPropertiesDict);
        newPage.Set(new PdfName("Resources"), newResources);
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));

        return reader.AppendRevision([
            (pageRef.ObjectNumber, newPage),
            (propsNum, propsDict),
            (contentNum, newContent),
        ]);
    }

    /// <summary>
    /// §7.1-1 VIOLATION: replaces the page content stream with one that nests an <c>/Artifact BMC</c>
    /// inside a <c>/P BDC</c> whose MCID (0) is linked to a struct element in the /ParentTree.
    /// veraPDF fires 7.1-1 (and 7.1-2): an Artifact is present inside tagged content.
    /// </summary>
    private static byte[] Ua1ArtifactInTagged()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);

        var (pageRef, page) = FirstPage(reader);
        var resources = reader.ResolveValue(page.Get(new PdfName("Resources"))!) as PdfDictionary;
        var fontDict = resources?.Get(PdfName.Font) is PdfObject fo
            ? reader.ResolveValue(fo) as PdfDictionary : null;
        var fontName = fontDict?.Entries.FirstOrDefault().Key.Value ?? "F1";

        // Replace the page content stream: /P BDC (MCID 0) with /Artifact BMC nested inside.
        // MCID 0 is already linked to the P struct elem in the baseline ParentTree.
        var contentNum = reader.Size;
        var contentBytes = Encoding.ASCII.GetBytes(
            $"/P << /MCID 0 >> BDC\n"
            + $"BT\n/{fontName} 12 Tf\n1 0 0 1 72 720 Tm\n(Tagged text) Tj\nET\n"
            + "/Artifact BMC\n"
            + $"BT\n/{fontName} 12 Tf\n1 0 0 1 72 700 Tm\n(Artifact inside tagged) Tj\nET\n"
            + "EMC\n"
            + "EMC\n");
        var newContentStream = new PdfStream(contentBytes);

        var newPage = CloneDict(page);
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));

        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, newContentStream)]);
    }

    /// <summary>
    /// §7.1-2 VIOLATION: replaces the page content stream with one that nests a <c>/P BDC</c>
    /// (MCID 0 linked to a struct element in the /ParentTree) inside an <c>/Artifact BMC</c>.
    /// veraPDF fires 7.1-2: tagged content is present inside content marked as Artifact.
    /// </summary>
    private static byte[] Ua1TaggedInArtifact()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);

        var (pageRef, page) = FirstPage(reader);
        var resources = reader.ResolveValue(page.Get(new PdfName("Resources"))!) as PdfDictionary;
        var fontDict = resources?.Get(PdfName.Font) is PdfObject fo
            ? reader.ResolveValue(fo) as PdfDictionary : null;
        var fontName = fontDict?.Entries.FirstOrDefault().Key.Value ?? "F1";

        // Replace the page content stream: /Artifact BMC with /P BDC (MCID 0) nested inside.
        // MCID 0 is already linked to the P struct elem in the baseline ParentTree.
        var contentNum = reader.Size;
        var contentBytes = Encoding.ASCII.GetBytes(
            "/Artifact BMC\n"
            + $"/P << /MCID 0 >> BDC\n"
            + $"BT\n/{fontName} 12 Tf\n1 0 0 1 72 720 Tm\n(Tagged inside artifact) Tj\nET\n"
            + "EMC\n"
            + "EMC\n");
        var newContentStream = new PdfStream(contentBytes);

        var newPage = CloneDict(page);
        newPage.Set(new PdfName("Contents"), new PdfIndirectReference(contentNum));

        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, newContentStream)]);
    }

    // Walks a number-tree node and returns the PdfArray at the given integer key, or null.
    private static PdfArray? FindNumsArray(PdfDocumentReader reader, PdfDictionary node, int key)
    {
        var numsRaw = node.Get(new PdfName("Nums"));
        if (numsRaw is not null && reader.ResolveValue(numsRaw) is PdfArray nums)
        {
            for (var i = 0; i + 1 < nums.Count; i += 2)
            {
                var k = reader.ResolveValue(nums[i]) as PdfInteger;
                if (k is null) continue;
                if ((int)k.Value == key)
                    return reader.ResolveValue(nums[i + 1]) as PdfArray;
            }
        }
        var kidsRaw = node.Get(new PdfName("Kids"));
        if (kidsRaw is not null && reader.ResolveValue(kidsRaw) is PdfArray kids)
        {
            for (var i = 0; i < kids.Count; i++)
            {
                if (reader.ResolveValue(kids[i]) is PdfDictionary child)
                {
                    var result = FindNumsArray(reader, child, key);
                    if (result is not null) return result;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// §7.2-34 VIOLATION: tagged UA-1 with the catalog /Lang key removed entirely (absent, not
    /// empty), so gContainsCatalogLang == false in veraPDF terms. The page content stream contains
    /// text shows inside a /P BDC with no /Lang property — no determinable language for those text
    /// items. veraPDF fires 7.2-34 (and 7.2-lang); the in-process UaMarkedContentLangRule fires
    /// 7.2-34. NOTE: /Lang must be ABSENT, not empty — an empty /Lang () still satisfies veraPDF's
    /// containsLang, which would suppress 7.2-34 (it would only fire the Lang-syntax rule 7.2-29).
    /// </summary>
    private static byte[] Ua1McTextNoLang()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);
        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var catalog = CloneWithout(reader.Catalog, "Lang");
        return reader.AppendRevision([(rootRef.ObjectNumber, catalog)]);
    }

    /// <summary>
    /// §7.2-30 VIOLATION: tagged UA-1 with the catalog /Lang key removed entirely (absent, not
    /// empty), plus an injected /Span BDC carrying /ActualText with no /Lang. veraPDF fires 7.2-30
    /// (and 7.2-lang). NOTE: /Lang must be ABSENT, not empty — an empty /Lang () still satisfies
    /// veraPDF's containsLang and would suppress 7.2-30.
    /// </summary>
    private static byte[] Ua1McSpanActualTextNoLang()
    {
        // Start from the empty-lang baseline.
        var noLangBaseline = Ua1McTextNoLang();
        using var reader = PdfReader.Open(noLangBaseline);

        var (pageRef, page) = FirstPage(reader);
        var resourcesObj = page.Get(new PdfName("Resources"));
        var resources = resourcesObj is not null ? reader.ResolveValue(resourcesObj) as PdfDictionary : null;
        var fontDictObj = resources?.Get(PdfName.Font);
        var fontDict = fontDictObj is not null ? reader.ResolveValue(fontDictObj) as PdfDictionary : null;
        var fontName = fontDict?.Entries.FirstOrDefault().Key.Value ?? "F1";

        var contentNum = reader.Size;
        // /Span BDC with /ActualText and no /Lang → violates 7.2-30 when gContainsCatalogLang==false.
        var spanContent = Encoding.ASCII.GetBytes(
            $"BT\n/{fontName} 12 Tf\n1 0 0 1 72 700 Tm\n/Span << /MCID 99 /ActualText (probe) >> BDC\n(A) Tj\nEMC\nET");
        var contentStream = new PdfStream(spanContent);

        // Append new content stream to the page's /Contents array.
        var newPage = CloneDict(page);
        var oldContents = page.Get(new PdfName("Contents"));
        PdfArray contentsArray;
        if (oldContents is PdfArray arr)
        {
            var items = new PdfObject[arr.Count + 1];
            for (var i = 0; i < arr.Count; i++) items[i] = arr[i];
            items[arr.Count] = new PdfIndirectReference(contentNum);
            contentsArray = new PdfArray(items);
        }
        else
        {
            contentsArray = new PdfArray([oldContents!, new PdfIndirectReference(contentNum)]);
        }
        newPage.Set(new PdfName("Contents"), contentsArray);

        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, contentStream)]);
    }

    /// <summary>
    /// §7.1-3 VIOLATION: appends a content stream to the UA-1 tagged baseline's page that paints
    /// a path (<c>S</c>) outside any BDC — untagged real content.
    /// veraPDF fires clause 7.1 testNumber 3 (SESimpleContentItem: isTaggedContent==false,
    /// parentsTags.contains('Artifact')==false).
    /// In-process: <c>UaSimpleContentItemRule</c> fires <c>ISO14289-1:7.1-3</c>.
    /// </summary>
    private static byte[] Ua1UntaggedRealContent()
    {
        var baseline = WriterPdfTagged(VellumPdf.Document.PdfConformance.PdfUA1);
        using var reader = PdfReader.Open(baseline);

        var (pageRef, page) = FirstPage(reader);

        var contentNum = reader.Size;
        // Path-painting outside any BDC: bare S operator (no font required, so no parse errors).
        var extraContent = Encoding.ASCII.GetBytes("0 0 m 100 0 l S\n");
        var contentStream = new PdfStream(extraContent);

        var newPage = CloneDict(page);
        var oldContents = page.Get(new PdfName("Contents"));
        PdfArray contentsArray;
        if (oldContents is PdfArray arr)
        {
            var items = new PdfObject[arr.Count + 1];
            for (var i = 0; i < arr.Count; i++) items[i] = arr[i];
            items[arr.Count] = new PdfIndirectReference(contentNum);
            contentsArray = new PdfArray(items);
        }
        else
        {
            contentsArray = new PdfArray([oldContents!, new PdfIndirectReference(contentNum)]);
        }
        newPage.Set(new PdfName("Contents"), contentsArray);

        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, contentStream)]);
    }

}
