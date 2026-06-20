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
