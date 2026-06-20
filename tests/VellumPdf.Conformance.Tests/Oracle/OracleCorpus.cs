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

            // A PDF/A-2b document that draws text with a properly embedded (subset) TrueType font via
            // the Type0/CIDFontType2/Identity-H path. veraPDF accepts it (all §6.2.11.x font checks
            // pass) and so does the in-process validator — the positive end-to-end font fixture.
            new OracleFixture("pdfa2b-embedded-font", WriterPdfWithEmbeddedFont(),
                Conformance.PdfConformance.PdfA2B, "2b", ExpectedCompliant: true),

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

            // An extension schema declaring a custom value type (pdfaType) whose first field omits the
            // mandatory 'type' name (§6.6.2.3.3-11). veraPDF and the in-process rule both reject it.
            new OracleFixture("pdfa2b-extension-valuetype-bad", WriterPdfWithMetadata(Encoding.UTF8.GetBytes(BadValueTypeXmp2b())),
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
    {
        // Merge an /ExtGState carrying a /TR transfer function into the page and apply it (`/GS0 gs`).
        var baseline = WriterPdf(VellumPdf.Document.PdfConformance.PdfA2b);
        using var reader = PdfReader.Open(baseline);
        var (pageRef, page) = FirstPage(reader);
        var newPage = CloneDict(page);

        var gsNum = reader.Size;
        var contentNum = gsNum + 1;
        var gs = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("ExtGState"))
            .Set(new PdfName("TR"), new PdfName("Identity"));
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
    internal static byte[] SimpleTrueTypeFont(Action<PdfDictionary> mutate, int flags = 32, PdfName? encoding = null)
    {
        var dejavu = LoadAsset("DejaVuSans.ttf");
        int widthA;
        using (var measureDoc = new PdfDocument())
            widthA = (int)Math.Round(measureDoc.UseTrueTypeFont(dejavu).MeasureString("A", 1000));

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
            .Set(PdfName.BaseFont, new PdfName("DejaVuSans"))
            .Set(new PdfName("FirstChar"), new PdfInteger(65)).Set(new PdfName("LastChar"), new PdfInteger(65))
            .Set(new PdfName("Widths"), new PdfArray([new PdfInteger(widthA)]))
            .Set(new PdfName("FontDescriptor"), new PdfIndirectReference(descNum));
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
