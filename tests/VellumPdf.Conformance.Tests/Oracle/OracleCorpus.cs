// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
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

            // The same for PDF/UA-1: lang + title present but no structure tree, isolating the
            // tagging requirement (§7.1). Cross-validates the UA tagging rule's negative path.
            new OracleFixture("pdfua1-no-structure", WriterPdfMissingStructure(VellumPdf.Document.PdfConformance.PdfUA1),
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

            // An extension-schema property whose 'category' is neither internal nor external
            // (§6.6.2.3.3-9). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-extension-schema-bad-category",
                WriterPdfWithMetadata(Encoding.UTF8.GetBytes(ExtensionSchemaXmp2b(
                    "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>"
                    + "<pdfaProperty:category>bogus</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>"))),
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
        ];
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

    private static byte[] WriterPdfWithXmpHeader(string headerExtra)
        => WriterPdfWithMetadata(Encoding.UTF8.GetBytes(Xmp2b(headerExtra)));

    private static byte[] WriterPdfWithUtf16Xmp()
        => WriterPdfWithMetadata(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Xmp2b(""))).ToArray());

    internal const string ValidPropertyFields =
        "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>"
        + "<pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>";

    // A PDF/A-2b XMP packet declaring one valid extension schema with one property carrying the
    // given <paramref name="propertyFields"/> (element serialisation).
    private static string ExtensionSchemaXmp2b(string propertyFields)
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
            + "<pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" + propertyFields
            + "</rdf:li></rdf:Seq></pdfaSchema:property></rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>"
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
}
