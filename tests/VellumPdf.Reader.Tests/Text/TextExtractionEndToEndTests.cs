// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Content;

namespace VellumPdf.Reader.Tests.Text;

/// <summary>
/// Known-answer, contract, and honesty tests for <see cref="PdfDocumentReader.ExtractText()"/> and
/// <see cref="PdfReadPage.ExtractText()"/> (#98), against hand-built PDFs in the
/// <see cref="TextTestSupport"/> style.
/// </summary>
public sealed class TextExtractionEndToEndTests
{
    // ── End-to-end known-answer tests ───────────────────────────────────────────────────────────

    [Fact]
    public void TwoLines_joinedByNewline()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (Line one) Tj 0 -14 Td (Line two) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("Line one\nLine two", result.Text);
    }

    /// <summary>
    /// The discriminating fixture for the line-break rule: a mid-line <c>Ts</c> (rise) must NOT
    /// start a new run, since §9.3.7 scopes rise to superscripts and subscripts, which read as the
    /// same line as their surrounding text.
    /// </summary>
    [Fact]
    public void MidLineRise_producesNoNewline()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (AB) Tj 3 Ts (C) Tj 0 Ts (D) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("ABCD", result.Text);
    }

    /// <summary>
    /// The discriminating fixture for MEDIUM 3 (#417 round 2): under a 90° page rotation, reading
    /// the painted Trm's <c>F</c> directly as the line-grouping key mistook the ADVANCING
    /// coordinate for the perpendicular-to-baseline one, so every glyph on this one rotated line
    /// computed a distinct key and opened its own one-glyph run — measured before this fix as
    /// "H\ne\nl\nl\no" for this exact fixture. <see cref="GlyphPositioner.ComputeLineKey"/>'s KATs
    /// pin the corrected formula in isolation; this pins that <see
    /// cref="TextExtractionVisitor"/> actually uses it.
    /// </summary>
    [Fact]
    public void RotatedPage_keepsOneLineTogether_notOneRunPerGlyph()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "0 1 -1 0 0 0 cm\nBT /F1 12 Tf 100 700 Td (Hello) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public void TwoPages_joinedByPageSeparator()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            ["BT /F1 12 Tf 100 700 Td (First) Tj ET", "BT /F1 12 Tf 100 700 Td (Second) Tj ET"],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).ExtractText();

        Assert.Equal("First\fSecond", result.Text);
    }

    [Fact]
    public void CustomPageSeparator_isHonoured()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            ["BT /F1 12 Tf 100 700 Td (First) Tj ET", "BT /F1 12 Tf 100 700 Td (Second) Tj ET"],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).ExtractText(new PdfTextExtractionOptions { PageSeparator = "|" });

        Assert.Equal("First|Second", result.Text);
    }

    [Fact]
    public void PageRange_extractsOnlyTheRequestedPages()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            [
                "BT /F1 12 Tf 100 700 Td (One) Tj ET",
                "BT /F1 12 Tf 100 700 Td (Two) Tj ET",
                "BT /F1 12 Tf 100 700 Td (Three) Tj ET",
            ],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).ExtractText(new PdfTextExtractionOptions { Pages = 1..3 });

        Assert.Equal("Two\fThree", result.Text);
    }

    /// <summary>
    /// A Form XObject's own text is included, positioned through its own <c>/Matrix</c> (§8.10.2)
    /// rather than the invoker's own text-matrix trajectory, and the invoker's own text matrix is
    /// unaffected once the form's content finishes: the page's own content keeps ONE text object
    /// open across the intervening <c>Do</c> (itself informational-only per §8.2 Table 50, which
    /// lists the categories a text object may contain and does not include XObjects, but still processed —
    /// see <c>ContentInterpreter.HandleDo</c>'s own remarks), so "Y" shown after the
    /// form returns lands on the exact same baseline as "X" shown before it, producing three
    /// distinct lines in content order rather than "X" and "Y" merging with "Inside" or with each
    /// other by coincidence.
    /// </summary>
    [Fact]
    public void FormXObjectText_included_matrixReflected_invokerTextMatrixUnchangedAfter()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (X) Tj\n/Fm0 Do\n(Y) Tj ET",
            "<< /Font << /F1 5 0 R >> /XObject << /Fm0 10 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()),
            new TextTestSupport.Obj(10,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Matrix [1 0 0 1 50 50] "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
                System.Text.Encoding.ASCII.GetBytes("BT /F1 24 Tf 0 0 Td (Inside) Tj ET")));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("X\nInside\nY", result.Text);
    }

    /// <summary>
    /// A Form XObject's text that relies on a font its INVOKER set, without a 'Tf' of its own,
    /// must resolve that font against the INVOKER's resources, not the form's (ISO 32000-2 §9.3.1
    /// Table 103 binds 'Tf' to the resource dictionary in effect when IT executes; §8.10.1's
    /// implicit save then carries the resolved binding, not the bare name, into the form). Before
    /// this fix, text extraction resolved a shown glyph's font against whatever resources were
    /// current for the STREAM BEING INTERPRETED AT SHOW TIME, which inside this form is the
    /// form's /Resources — naming only /F9, not /F1 — so the lookup failed and "Hello" was
    /// dropped with no diagnostic at all.
    /// </summary>
    [Fact]
    public void FormXObjectInheritedFont_resolvesAgainstInvokersResources_notFormsOwn()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf ET\n/Fm0 Do",
            "<< /Font << /F1 5 0 R >> /XObject << /Fm0 10 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()),
            new TextTestSupport.Obj(10,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Resources << /Font << /F9 5 0 R >> >> >>",
                System.Text.Encoding.ASCII.GetBytes("BT 0 0 Td (Hello) Tj ET")));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("Hello", result.Text);
    }

    /// <summary>
    /// The sharper version of the fixture above: the form's /Resources ALSO declares a /F1, but
    /// bound to a DIFFERENT font (remapped code 65 to "B" via /Differences, the same device
    /// <see cref="FontSetInsideQQ_resolvesToOuterFont_afterQ"/> above uses). A comparer keyed on
    /// the bare name alone, or a lookup resolved against whichever resources happen to be current
    /// at show time, gets this wrong the same way: since the form never calls its own 'Tf', its
    /// inherited /F1 must still resolve against the PAGE's /Font (object 5, plain WinAnsiEncoding:
    /// code 65 is "A"), not the form's (object 6: code 65 is "B"). Before this fix, this resolved
    /// to "B" — wrong characters and wrong widths, silently.
    /// </summary>
    [Fact]
    public void FormXObjectInheritedFont_sameNameBoundDifferentlyInForm_resolvesToInvokersFont()
    {
        const string remappedF1Dict =
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            + "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /B] >> "
            + "/FirstChar 65 /LastChar 65 /Widths [600] >>";

        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf ET\n/Fm0 Do",
            "<< /Font << /F1 5 0 R >> /XObject << /Fm0 10 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict(65, 65, 600)),
            new TextTestSupport.Obj(6, remappedF1Dict),
            new TextTestSupport.Obj(10,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Resources << /Font << /F1 6 0 R >> >> >>",
                System.Text.Encoding.ASCII.GetBytes("BT 0 0 Td <41> Tj ET")));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("A", result.Text);
    }

    /// <summary>
    /// MEDIUM 4 (#417 round 4): the discriminating fixture the PR body wrongly claimed already
    /// existed for the font memo's own keying — every other form fixture here has the form inherit
    /// its font WITHOUT issuing its own 'Tf' at all, so only ever ONE Font operand instance is ever
    /// memoized and the memo never has two entries to tell apart. Here the form issues its OWN
    /// <c>/F1 12 Tf</c>, bound (through its own <c>/Resources</c>) to a DIFFERENT font than the
    /// invoker's <c>/F1</c>, and both are shown: the page's own <c>/F1</c> (object 5, plain
    /// WinAnsiEncoding: code 65 is "A") before the 'Do', the form's own <c>/F1</c> (object 6,
    /// remapped via <c>/Differences</c>: code 65 is "B") inside it, and the page's <c>/F1</c> again
    /// after the 'Do' returns — restored by the implicit save/restore §8.10.1 requires around a
    /// Form XObject invocation, with no second 'Tf' of its own. A memo that collapsed these two
    /// DIFFERENT Font operand instances onto one cache entry (the exact PdfName collision <see
    /// cref="TextExtractionVisitor"/>'s own memo field doc describes) would reuse whichever font
    /// resolved FIRST for the second lookup too, producing "AAA" or "BBB" instead of "A\nB\nA". The
    /// three lines, not two, mirror
    /// <see cref="FormXObjectText_included_matrixReflected_invokerTextMatrixUnchangedAfter"/>
    /// above: <c>TextAssembler</c> only ever compares against the immediately PRECEDING line, so the
    /// third glyph does not merge back into the first run even though it shares that run's baseline.
    /// </summary>
    [Fact]
    public void FormXObjectOwnTf_bindsDifferentFontThanInvokers_bothShown()
    {
        const string remappedF1Dict =
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            + "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /B] >> "
            + "/FirstChar 65 /LastChar 65 /Widths [600] >>";

        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td <41> Tj\n/Fm0 Do\n<41> Tj ET",
            "<< /Font << /F1 5 0 R >> /XObject << /Fm0 10 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict(65, 65, 600)),
            new TextTestSupport.Obj(6, remappedF1Dict),
            new TextTestSupport.Obj(10,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Resources << /Font << /F1 6 0 R >> >> >>",
                System.Text.Encoding.ASCII.GetBytes("BT /F1 12 Tf 0 0 Td <41> Tj ET")));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("A\nB\nA", result.Text);
    }

    /// <summary>
    /// Annotation appearance streams are excluded from text extraction (a deliberate asymmetry with
    /// <see cref="PdfDocumentReader.ExtractImages()"/>, which includes them by default): only the
    /// page's own content contributes text.
    /// </summary>
    [Fact]
    public void AnnotationAppearanceText_isExcluded()
    {
        // BuildPageDoc's own page object has no /Annots slot, so this fixture is built directly
        // with TextTestSupport.BuildPdf instead, the same hand-built-byte-string idiom every other
        // fixture here uses.
        var pdfWithAnnots = TextTestSupport.BuildPdf(
            1,
            new TextTestSupport.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new TextTestSupport.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new TextTestSupport.Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [20 0 R] >>"),
            new TextTestSupport.Obj(4, "<< >>", System.Text.Encoding.ASCII.GetBytes(
                "BT /F1 12 Tf 100 700 Td (Visible) Tj ET")),
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()),
            new TextTestSupport.Obj(20, "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AP << /N 21 0 R >> >>"),
            new TextTestSupport.Obj(21,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
                System.Text.Encoding.ASCII.GetBytes("BT /F1 12 Tf 0 0 Td (Hidden) Tj ET")));

        var result = PdfReader.Open(pdfWithAnnots).GetPage(0).ExtractText();

        Assert.Equal("Visible", result.Text);
        Assert.DoesNotContain("Hidden", result.Text);
    }

    /// <summary> Extraction reports what the file contains, not what a renderer would show:
    /// invisible-render-mode text (<c>3 Tr</c>) is included, which is why a scanned page carrying an
    /// invisible OCR text layer over its image still works with this reader.</summary>
    [Fact]
    public void InvisibleRenderModeText_isIncluded()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 3 Tr 100 700 Td (Invisible) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("Invisible", result.Text);
    }

    /// <summary>
    /// A font set inside <c>q</c>/<c>Q</c> resolves to the OUTER font again once <c>Q</c> restores
    /// it: <c>/F1</c> maps code 0x41 to "A" (plain WinAnsiEncoding), <c>/F2</c> remaps the SAME code
    /// to "B" via <c>/Differences</c>, so the three shows in this fixture are only distinguishable
    /// by which font actually resolved for each: "A" (outer), "B" (inner, before Q), "A" again
    /// (outer, after Q) — a text-only assertion this time genuinely proves font resolution, since a
    /// resolution bug (stale memoisation keyed by operand alone, say) would leak "B" into the third
    /// show instead.
    /// </summary>
    [Fact]
    public void FontSetInsideQQ_resolvesToOuterFont_afterQ()
    {
        const string f2Dict =
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            + "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /B] >> "
            + "/FirstChar 65 /LastChar 66 /Widths [600 600] >>";

        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf <41> Tj ET\nq\nBT /F2 12 Tf <41> Tj ET\nQ\nBT <41> Tj ET",
            "<< /Font << /F1 5 0 R /F2 6 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict(65, 66, 600)),
            new TextTestSupport.Obj(6, f2Dict));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("ABA", result.Text);
    }

    /// <summary>An ExtGState's own <c>/Font</c> (Table 57) sets font and size without any <c>Tf</c>
    /// at all.</summary>
    [Fact]
    public void ExtGStateFont_setsFontAndSize()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "/GS0 gs\nBT (A) Tj ET",
            "<< /Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [5 0 R 12] >> >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("A", result.Text);
    }

    /// <summary>
    /// MEDIUM 1 (#417 round 4): Table 57 requires an ExtGState's own <c>/Font</c> array's first
    /// element to be an indirect reference to a font dictionary; this producer puts a bare
    /// <c>/Resources /Font</c> name there instead. Before this fix, <c>HandleExtGState</c> stored
    /// that name into <c>GraphicsState.Font</c> unchecked and cleared <c>FontResources</c> to
    /// <see langword="null"/> in the same step, so <c>TextExtractionVisitor.ResolveFont</c> (which
    /// treats a non-<c>PdfName</c> operand as already resolved and a <c>PdfName</c> one as a
    /// <c>/Resources</c> lookup — the name here IS a <c>PdfName</c>) looked it up against a null
    /// resources dictionary, found nothing, and returned <see langword="null"/> with NO diagnostic
    /// at all: "A" vanished silently. This asserts both halves of the fix: no font was ever bound
    /// (there is no preceding 'Tf' for this fixture), so the text is empty, AND that emptiness is
    /// explained by diagnostics rather than left to look like an ordinary empty page.
    /// </summary>
    [Fact]
    public void ExtGStateFont_nonReferenceElement_reportsDiagnostic_notSilentlyLossy()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "/GS0 gs\nBT (A) Tj ET",
            "<< /Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [/F1 12] >> >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        var d = Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ExtGStateFontMalformed);
        Assert.Equal(
            "'gs' names '/GS0', whose /ExtGState /Font array's first element is neither an "
            + "indirect reference nor a direct dictionary (ISO 32000-2 §8.4.5 Table 57); the "
            + "ExtGState's /Font entry was ignored.",
            d.Message);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextShownWithoutFont);
    }

    /// <summary>
    /// The other half of MEDIUM 1: a working font bound by a preceding, conforming 'Tf' must
    /// survive a later, non-conforming 'gs' rather than being clobbered by it — "leaving a working
    /// binding in place beats replacing it with a guaranteed-unresolvable one" (#417 round 4's own
    /// brief). Both "A" (via 'Tf') and "B" (after the rejected 'gs') must come through using the
    /// SAME font, since the malformed 'gs' is dropped entirely rather than partially applied.
    /// </summary>
    [Fact]
    public void ExtGStateFont_nonReferenceElement_leavesPriorTfBindingInPlace()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (A) Tj /GS0 gs (B) Tj ET",
            "<< /Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [/F1 12] >> >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("AB", result.Text);
        var d = Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ExtGStateFontMalformed);
        Assert.Equal(
            "'gs' names '/GS0', whose /ExtGState /Font array's first element is neither an "
            + "indirect reference nor a direct dictionary (ISO 32000-2 §8.4.5 Table 57); the "
            + "ExtGState's /Font entry was ignored.",
            d.Message);
    }

    /// <summary>
    /// #417 round 5: Table 57's own text requires an ExtGState's <c>/Font</c> array's first
    /// element to be an indirect reference, but round 4's rejection lumped a DIRECT font dictionary
    /// in with a bare name or any other non-reference value. A direct font dictionary has no object
    /// number of its own (legal per <c>FontCache</c>'s own doc) and resolves through
    /// <c>PdfDocumentReader.GetFontReader</c> exactly the way an indirect reference does, so
    /// rejecting it discarded a font this reader could otherwise use. This asserts the text still
    /// comes through, and that the deviation from Table 57's own wording is still reported rather
    /// than silently accepted.
    /// </summary>
    [Fact]
    public void ExtGStateFont_directDictionaryElement_isAcceptedAndExtractsText()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "/GS0 gs\nBT (A) Tj ET",
            "<< /ExtGState << /GS0 << /Font [" + TextTestSupport.SimpleFontDict() + " 12] >> >> >>");

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("A", result.Text);
        var d = Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ExtGStateFontMalformed);
        Assert.Equal(
            "'gs' names '/GS0', whose /ExtGState /Font array's first element is a direct font "
            + "dictionary rather than an indirect reference (ISO 32000-2 §8.4.5 Table 57); the "
            + "font was resolved and used anyway.",
            d.Message);
    }

    /// <summary>
    /// #417 round 8: the fix for the self-suppression bug the round 7 review found. Both branches of
    /// <c>310</c> reported against <c>ctx.DiagObjectNumber</c> — the content STREAM's object number
    /// — and <c>DiagnosticSink</c> dedupes on <c>(code, object, page)</c>, so two DIFFERENT malformed
    /// ExtGStates named from the SAME stream collapsed into one report; when the two branches
    /// disagree, as here, the survivor's message asserts an outcome that is false for whichever one
    /// it ate. <c>/GS0</c> (object 6) is a bare name — the "ignored" branch — and <c>/GS1</c> (object
    /// 7) is a direct font dictionary — the "resolved and used anyway" branch; both are INDIRECT
    /// references, so each now keys on its own object number instead of the shared stream's, and
    /// both reports survive, each naming its own resource. The extracted text corroborates the
    /// diagnostics independently: "A" (under the ignored <c>/GS0</c>, no font ever bound) produces
    /// nothing, while "B" (under the accepted <c>/GS1</c>) comes through, so <c>result.Text</c> is
    /// "B" alone.
    /// </summary>
    [Fact]
    public void TwoDifferentlyMalformedExtGStates_onOnePage_bothDiagnosticsSurvive_eachNamingItsOwnResource()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "/GS0 gs\nBT (A) Tj ET\n/GS1 gs\nBT (B) Tj ET",
            "<< /ExtGState << /GS0 6 0 R /GS1 7 0 R >> >>",
            new TextTestSupport.Obj(6, "<< /Font [/F1 12] >>"),
            new TextTestSupport.Obj(7, "<< /Font [" + TextTestSupport.SimpleFontDict() + " 12] >>"));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("B", result.Text);
        var malformed = result.Diagnostics
            .Where(d => d.Code == PdfReaderDiagnosticCode.ExtGStateFontMalformed)
            .OrderBy(d => d.ObjectNumber)
            .ToList();
        Assert.Equal(2, malformed.Count);

        Assert.Equal(6, malformed[0].ObjectNumber);
        Assert.Equal(
            "'gs' names '/GS0', whose /ExtGState /Font array's first element is neither an "
            + "indirect reference nor a direct dictionary (ISO 32000-2 §8.4.5 Table 57); the "
            + "ExtGState's /Font entry was ignored.",
            malformed[0].Message);

        Assert.Equal(7, malformed[1].ObjectNumber);
        Assert.Equal(
            "'gs' names '/GS1', whose /ExtGState /Font array's first element is a direct font "
            + "dictionary rather than an indirect reference (ISO 32000-2 §8.4.5 Table 57); the "
            + "font was resolved and used anyway.",
            malformed[1].Message);
    }

    /// <summary>
    /// #417 round 8: <c>fontArray.Count == 2</c> (Table 57's own shape: an indirect reference or
    /// direct dictionary, THEN a size — nothing else) was unpinned. Deleting it lets a one-element
    /// array reach <c>fontArray[1]</c>, which throws <see cref="ArgumentOutOfRangeException"/>
    /// straight out of <c>ExtractText</c> — breaking the contract this interpreter's own remarks
    /// state elsewhere, that a malformed document degrades rather than throws. With the check in
    /// place, the whole <c>/Font</c> entry is skipped (silently, the same as any other unrecognised
    /// ExtGState entry): no font is ever bound, so "A" produces no characters.
    /// </summary>
    [Fact]
    public void ExtGStateFont_oneElementArray_producesNoText_notAnException()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "/GS0 gs\nBT (A) Tj ET",
            "<< /Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [5 0 R] >> >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
    }

    /// <summary>
    /// The other half of the array-length fixture: a THREE-element array is equally not Table 57's
    /// shape, and equally produces no font (the whole entry skipped) with the check in place. Unlike
    /// the one-element case, deleting <c>fontArray.Count == 2</c> does not make this fixture throw —
    /// <c>fontArray[0]</c> and <c>fontArray[1]</c> both exist, so the extra trailing element is
    /// simply never read — it instead makes the reader silently ACCEPT a shape Table 57 does not
    /// define, extracting "A" instead of reporting nothing. Recorded here as the array-length
    /// guard's own gap: it stops the crash, not every non-conforming shape.
    /// </summary>
    [Fact]
    public void ExtGStateFont_threeElementArray_producesNoText_notAnException()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "/GS0 gs\nBT (A) Tj ET",
            "<< /Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [5 0 R 12 0] >> >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
    }

    /// <summary>
    /// #417 round 5: <c>ExtGStateFontMalformed</c> must not be dedupable away by an unrelated
    /// <c>OperandStackMalformed</c> report on the same page — an unbalanced <c>Q</c> is one of the
    /// commonest producer defects and reports that code first, and <c>DiagnosticSink</c> dedupes on
    /// <c>(code, object, page)</c>. Before this fix, the malformed ExtGState font shared
    /// <c>OperandStackMalformed</c> with the unbalanced <c>Q</c>, so the font report was the SECOND
    /// occurrence of that code on this page and never reported at all. Both diagnostics must
    /// survive with the codes now distinct; a bare <see cref="Assert.Contains{T}(T, IEnumerable{T})"/>
    /// per code would pass just as readily if a third, unrelated diagnostic of either code also
    /// showed up, so this pins exactly one of each, their exact messages, and (since neither the
    /// unbalanced <c>Q</c> nor the malformed <c>gs</c> ever binds a font) the resulting empty text
    /// (#417 round 8).
    /// </summary>
    [Fact]
    public void UnbalancedQ_andMalformedExtGStateFont_onSamePage_bothDiagnosticsSurvive()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "Q\n/GS0 gs\nBT (A) Tj ET",
            "<< /Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [/F1 12] >> >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        var stackDiagnostic = Assert.Single(
            result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Equal("'Q' with no matching 'q' on the graphics-state stack; it was ignored.", stackDiagnostic.Message);
        var extGStateDiagnostic = Assert.Single(
            result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ExtGStateFontMalformed);
        Assert.Equal(
            "'gs' names '/GS0', whose /ExtGState /Font array's first element is neither an "
            + "indirect reference nor a direct dictionary (ISO 32000-2 §8.4.5 Table 57); the "
            + "ExtGState's /Font entry was ignored.",
            extGStateDiagnostic.Message);
    }

    /// <summary>
    /// An extreme but individually finite <c>cm</c> and <c>Tm</c> compound, through
    /// <c>GlyphPositioner.ComputeTextRenderingMatrix</c>'s own <c>parameters x Tm x CTM</c>, to a
    /// non-finite Trm: <see cref="TextAssembler"/> treats that as "same line as before" (see its
    /// own remarks), so extraction still terminates with one ordinary run rather than one run per
    /// glyph. ISO 32000-2 §7.3.3 defines no exponent form for a PDF real number, so a fixture
    /// using "1e200" is not valid content-stream syntax at all: both operators are REJECTED
    /// outright (reported as an unknown operator and a malformed operand, not applied), the page
    /// lays out at identity throughout, and the guard this test names is never reached — a defect
    /// that was in this test before this fix, and undetected because the fixture happened to
    /// assert the same "ABCD" text an identity layout also produces. The literal below is instead
    /// an ordinary, if enormous, all-digit real number (valid syntax; individually finite, since
    /// 10^170 is well inside IEEE 754 double's roughly 1.8x10^308 range) that genuinely overflows
    /// once 'cm' and 'Tm' compound: 10^170 x 10^170 = 10^340.
    /// </summary>
    [Fact]
    public void OverflowingCompoundTransform_terminatesInOneRun_notANewlineStorm()
    {
        var huge = "1" + new string('0', 170) + ".0";
        var (runs, sink) = RunTextExtractionVisitor(
            $"{huge} 0 0 {huge} {huge} {huge} cm\n"
            + $"BT /F1 12 Tf {huge} 0 0 {huge} {huge} {huge} Tm (AB) Tj (CD) Tj ET",
            TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("ABCD", run.Text);
        // Confirms the fixture's own operands were accepted as valid syntax and actually applied,
        // unlike the "1e200" fixture this replaced (see this test's own remarks): if either 'cm'
        // or 'Tm' had been rejected instead, the page would lay out at identity and pass for the
        // wrong reason.
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnknownOperator);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
    }

    /// <summary>
    /// The overflow excursion's own non-finite LineY must not get stuck forever. <see
    /// cref="TextAssembler.SameLine"/> treats a non-finite key as matching every LATER glyph,
    /// finite or not, which is what the previous test needs to avoid one run per glyph while the
    /// overflow lasts — but once real, finite line positions resume, two of them that are
    /// genuinely different lines must still be told apart, not folded into one giant run just
    /// because the first finite glyph after the excursion inherited the stuck key. This fixture's
    /// own CTM stays huge for the rest of the page after the excursion ('Tm' sets the text matrix
    /// absolutely, but 'cm' only ever concatenates onto the EXISTING CTM, so nothing resets it),
    /// which keeps every later Trm.F huge but FINITE — exactly the condition this guards against:
    /// "Line1" and "Line2", placed 100 text-space units apart by two independent absolute 'Tm'
    /// calls, must land in two different runs, not one.
    /// </summary>
    [Fact]
    public void NonFiniteLineY_doesNotStickPastTheExcursion_laterDistinctLinesStillSeparate()
    {
        var huge = "1" + new string('0', 170) + ".0";
        var (runs, _) = RunTextExtractionVisitor(
            $"{huge} 0 0 {huge} {huge} {huge} cm\n"
            + $"BT /F1 12 Tf {huge} 0 0 {huge} {huge} {huge} Tm (AB) Tj ET\n"
            + "BT /F1 12 Tf 1 0 0 1 100 700 Tm (Line1) Tj ET\n"
            + "BT /F1 12 Tf 1 0 0 1 100 600 Tm (Line2) Tj ET",
            TextTestSupport.SimpleFontDict());

        // "AB" and "Line1" still merge into one run: the FIRST finite glyph after the excursion
        // inherits the stuck non-finite key too (SameLine matches it against anything), which is
        // accepted — see this test's own remarks. What matters, and what the mirror-risk fix
        // guards, is that "Line1" and "Line2" do NOT also collapse into that same run.
        Assert.Equal(2, runs.Count);
        Assert.Equal("ABLine1", runs[0].Text);
        Assert.Equal("Line2", runs[1].Text);
        Assert.NotEqual(runs[0].Baseline, runs[1].Baseline);
    }

    /// <summary>
    /// #417 round 5: the end-to-end mirror of <see
    /// cref="GlyphPositionerTests.ComputeLineKey_zeroTimesInfinity_doesNotPoisonTheKey_onARotatedPage"/>,
    /// reached through real content rather than a synthesized <c>Matrix</c>. Each line resets the
    /// CTM to a 90°-family rotation whose <c>D</c> is replaced with an enormous scale rather than
    /// the 0 a pure rotation would carry there (<c>0 1 -1 {huge} 0 0 cm</c>, isolated per line by
    /// <c>q</c>/<c>Q</c>) with an absolute <c>Tm</c> whose own translation is a different
    /// all-digit-literal magnitude (10^170 vs. 10^175): the resulting Trm has <c>A</c> exactly 0,
    /// <c>B</c> exactly 12, a finite but astronomically large <c>E</c> that differs between the two
    /// lines by the same 10^170-vs-10^175 gap, and an <c>F</c> that overflows to <c>+Infinity</c> in
    /// both (their own Tm translation multiplied by the CTM's own huge <c>D</c>). With the
    /// <c>a == 0</c> guard, <c>ComputeLineKey</c> never lets that overflowing <c>F</c> into the
    /// computation, so the two lines key to two different, finite (if extreme) values and land in
    /// separate runs. Deleting
    /// <c>a == 0 ? 0.0 :</c> alone makes <c>term1</c> evaluate <c>0 * (+Infinity) == NaN</c> for
    /// BOTH lines, and <see cref="TextAssembler.SameLine"/> treats a non-finite key as matching
    /// anything — merging "Line1" and "Line2" into one run.
    /// </summary>
    [Fact]
    public void RotatedOverflowingLines_stayDistinct_viaTheAEqualsZeroGuard()
    {
        var huge170 = "1" + new string('0', 170) + ".0";
        var huge175 = "1" + new string('0', 175) + ".0";
        var (runs, _) = RunTextExtractionVisitor(
            "q\n"
            + $"0 1 -1 {huge170} 0 0 cm\n"
            + $"BT /F1 12 Tf 1 0 0 1 100 {huge170} Tm (Line1) Tj ET\n"
            + "Q\n"
            + "q\n"
            + $"0 1 -1 {huge170} 0 0 cm\n"
            + $"BT /F1 12 Tf 1 0 0 1 100 {huge175} Tm (Line2) Tj ET\n"
            + "Q",
            TextTestSupport.SimpleFontDict());

        Assert.Equal(2, runs.Count);
        Assert.Equal("Line1", runs[0].Text);
        Assert.Equal("Line2", runs[1].Text);
    }

    /// <summary>
    /// #417 round 8: the end-to-end mirror of <see
    /// cref="GlyphPositionerTests.ComputeLineKey_signCanonicalized_negativeFontSizeUnderRotation_viaBAlone"/>,
    /// reached through real content. A 90° page rotation (<c>0 1 -1 0 0 0 cm</c>) makes the sign
    /// guard's <c>a</c> exactly 0 for every glyph here, so only the guard's second disjunct
    /// (<c>a == 0 &amp;&amp; b &lt; 0</c>) can canonicalize the negative-Tfs half of this line — the
    /// identity-CTM fixture in <see
    /// cref="ComputeLineKey_signCanonicalized_negativeFontSizeMidLine_doesNotSplitTheLine"/>'s
    /// end-to-end counterpart never drives <c>a</c> to exactly 0, so it cannot exercise this half at
    /// all. "AB" paints at <c>+12 Tf</c> and "CD" at <c>-12 Tf</c> on the one baseline <c>Td 100
    /// 700</c> sets: with the guard, both key 700 and land in one run; deleting the second disjunct
    /// alone keys them 700 and -700, splitting "AB" and "CD" into two.
    /// </summary>
    [Fact]
    public void RotatedNegativeFontSizeMidLine_doesNotSplitTheLine_viaTheSecondSignDisjunct()
    {
        var (runs, _) = RunTextExtractionVisitor(
            "0 1 -1 0 0 0 cm\nBT /F1 12 Tf 100 700 Td (AB) Tj /F1 -12 Tf (CD) Tj ET",
            TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("ABCD", run.Text);
    }

    /// <summary>
    /// #417 round 5: the end-to-end mirror of <see
    /// cref="GlyphPositionerTests.ComputeLineKey_zeroMagnitude_returnsNaN_notADeceptiveZero"/>,
    /// reached through real content: a zero font size mid-line, from <c>/F1 0 Tf</c>. §9.3.1 Table
    /// 103's <c>Tf</c> row governs zero directly ("Zero sized text shall not mark or clip any
    /// pixels"), a rendering rule this reader is not bound by: it extracts "B" here the same way
    /// <see cref="InvisibleRenderModeText_isIncluded"/> extracts a <c>3 Tr</c> glyph a renderer would
    /// also paint nothing for — reporting what the file contains, not what a renderer would show.
    /// (Table 103's own note, "Negative text font size is permitted", covers only the negative case;
    /// this reader applies no lower bound there either, but that is a separate claim.) A zero
    /// <c>Tfs</c> zeroes both <paramref name="trm"/>'s <c>A</c> and <c>B</c> for "B" alone. With the
    /// explicit <c>magnitude == 0</c> check, that key is <c>NaN</c>, which <see
    /// cref="TextAssembler.SameLine"/> treats as matching the surrounding, ordinary-size lines on
    /// either side, keeping "A", "B", and "C" in one run. Deleting the check alone (leaving
    /// <c>!double.IsFinite(magnitude)</c>) makes that key a deceptively FINITE 0 instead —
    /// different from "A" and "C"'s own key of 700 — opening two extra one-glyph runs.
    /// </summary>
    [Fact]
    public void ZeroFontSizeMidLine_doesNotSplitTheLine_viaTheMagnitudeGuard()
    {
        var (runs, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td (A) Tj /F1 0 Tf (B) Tj /F1 12 Tf (C) Tj ET",
            TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("ABC", run.Text);
    }

    /// <summary>A numeric <c>Tj</c> operand (never permitted by §9.4.3, which types it a string) is
    /// skipped with exactly one <see cref="PdfReaderDiagnosticCode.OperandStackMalformed"/>
    /// (302).</summary>
    [Fact]
    public void NumericTjOperand_skippedWithOne302()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 5 Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        var d = Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    // ── Spec-stated identities (§9.4.3, Table 107) ──────────────────────────────────────────────

    [Theory]
    [InlineData("(ABC) Tj")]
    [InlineData("(A) Tj (B) Tj (C) Tj")]
    [InlineData("[(A)(B)(C)] TJ")]
    public void MultipleGlyphsInOneShow_equalsSeparateTjs_equalsTJArray(string showOps)
    {
        var pdf = TextTestSupport.BuildPageDoc(
            $"BT /F1 12 Tf 100 700 Td {showOps} ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal("ABC", result.Text);
    }

    /// <summary>
    /// MEDIUM 8 (#417 round 2): a text-only comparison cannot see a doubled move — inserting a
    /// spurious extra <c>T*</c>-equivalent move before <c>'</c>'s own show still produces
    /// "Line1\nLine2" (the run count and character content are unaffected by HOW FAR the baseline
    /// moved, only THAT it moved), so it left the previous version of this test green. Reaches
    /// <see cref="TextRun.Baseline"/> directly to pin the exact move: <c>Td 100 700</c> then
    /// <c>14 TL</c> put "Line1" at baseline 700; one <c>T*</c>-equivalent move (leading 14, moved
    /// down) puts "Line2" at exactly <c>700 - 14 = 686</c>. A doubled move would land it at 672.
    /// </summary>
    [Fact]
    public void QuoteOperator_equalsTStarThenTj()
    {
        var (viaQuoteRuns, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td 14 TL (Line1) Tj (Line2) ' ET", TextTestSupport.SimpleFontDict());
        var (viaTStarTjRuns, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td 14 TL (Line1) Tj T* (Line2) Tj ET", TextTestSupport.SimpleFontDict());

        Assert.Equal(2, viaQuoteRuns.Count);
        Assert.Equal("Line1", viaQuoteRuns[0].Text);
        Assert.Equal("Line2", viaQuoteRuns[1].Text);
        Assert.Equal(700.0, viaQuoteRuns[0].Baseline, 6);
        Assert.Equal(686.0, viaQuoteRuns[1].Baseline, 6);

        Assert.Equal(
            viaQuoteRuns.Select(r => (r.Text, r.Baseline)), viaTStarTjRuns.Select(r => (r.Text, r.Baseline)));
    }

    /// <summary>
    /// LOW 7 (#417 round 4): comparing TEXT alone cannot see a doubled move any more than
    /// <see cref="QuoteOperator_equalsTStarThenTj"/> above could for <c>'</c> (MEDIUM 8, round 2) —
    /// inserting a spurious extra <c>T*</c>-equivalent move before <c>"</c>'s own show still
    /// produces "Line1"/"Line2" as the run text, so only a pre-existing
    /// <c>ContentInterpreterTests</c> case caught the doubled-move defect this comment describes.
    /// Made symmetric with that test: <c>Td 100 700</c> then <c>14 TL</c> put "Line1" at baseline
    /// 700; one <c>T*</c>-equivalent move (leading 14, moved down) puts "Line2" at exactly
    /// <c>700 - 14 = 686</c>, whether it arrives via <c>"</c> (with its own <c>Tw</c>/<c>Tc</c>
    /// operands, 2 and 3) or via the separate <c>2 Tw 3 Tc</c> plus <c>'</c> this test already
    /// checked for text-only equivalence. A doubled move would land it at 672.
    /// </summary>
    [Fact]
    public void DoubleQuoteOperator_equalsTwAndTcThenQuote()
    {
        var (viaDoubleQuoteRuns, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td 14 TL (Line1) Tj 2 3 (Line2) \" ET", TextTestSupport.SimpleFontDict());
        var (viaTwTcQuoteRuns, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td 14 TL (Line1) Tj 2 Tw 3 Tc (Line2) ' ET", TextTestSupport.SimpleFontDict());

        Assert.Equal(2, viaDoubleQuoteRuns.Count);
        Assert.Equal("Line1", viaDoubleQuoteRuns[0].Text);
        Assert.Equal("Line2", viaDoubleQuoteRuns[1].Text);
        Assert.Equal(700.0, viaDoubleQuoteRuns[0].Baseline, 6);
        Assert.Equal(686.0, viaDoubleQuoteRuns[1].Baseline, 6);

        Assert.Equal(
            viaDoubleQuoteRuns.Select(r => (r.Text, r.Baseline)),
            viaTwTcQuoteRuns.Select(r => (r.Text, r.Baseline)));
    }

    // ── Honesty tests ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Type0Font_producesNoText_reports405()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (AB) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, "<< /Type /Font /Subtype /Type0 /BaseFont /Foo >>"));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontTypeUnsupported);
    }

    [Fact]
    public void Type3Font_producesNoText_reports405()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (AB) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, "<< /Type /Font /Subtype /Type3 /BaseFont /Foo >>"));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontTypeUnsupported);
    }

    /// <summary>A font whose <c>/Subtype</c> this reader knows nothing about (not Type0, not
    /// Type3) reports <see cref="PdfReaderDiagnosticCode.FontUnreadable"/> (400), NOT <see
    /// cref="PdfReaderDiagnosticCode.FontTypeUnsupported"/> (405): 400 means the font dictionary
    /// itself does not name a type this reader recognises at all, distinct from 405's "recognised,
    /// but not simple" (double-report avoided).</summary>
    [Fact]
    public void BogusSubtypeFont_reports400_not405()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (AB) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, "<< /Type /Font /Subtype /Bogus /BaseFont /Foo >>"));

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontUnreadable);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.FontTypeUnsupported);
    }

    [Fact]
    public void ShowOperatorWithNoTf_producesNoText_reports601()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT (AB) Tj ET", "<< >>");

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        var d = Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextShownWithoutFont);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void TfNamingAbsentResource_reportsOnlyResourceMissing_noDoubleReport()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT /F1 12 Tf (AB) Tj ET", "<< >>");

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ResourceMissing);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextShownWithoutFont);
    }

    /// <summary>
    /// #417 round 8: <c>ResolveFont</c>'s <c>is { } entry and not PdfNull</c> was unpinned. ISO
    /// 32000-2 §7.3.9: "Specifying the null object as the value of a dictionary entry shall be
    /// equivalent to omitting the entry entirely" — so <c>/Font &lt;&lt; /F1 null &gt;&gt;</c> must
    /// report the SAME single <see cref="PdfReaderDiagnosticCode.ResourceMissing"/> (306) an absent
    /// <c>/F1</c> entry already reports (<see
    /// cref="TfNamingAbsentResource_reportsOnlyResourceMissing_noDoubleReport"/> above), through
    /// <c>ContentInterpreter.ValidateFontResource</c>'s own <c>TryGetResource</c>, which already
    /// treats <c>raw is null or PdfNull</c> as absent. Dropping <c>and not PdfNull</c> alone lets
    /// <c>fontDict.Get(fontName)</c>'s <c>PdfNull.Instance</c> pass the pattern as a non-null
    /// <c>PdfObject</c>, reach <c>GetFontReader</c>, and produce a SECOND, spurious <see
    /// cref="PdfReaderDiagnosticCode.FontUnreadable"/> (400) for the same root cause.
    /// </summary>
    [Fact]
    public void NullFontEntry_treatedAsAbsent_reportsOnlyResourceMissing()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf (AB) Tj ET",
            "<< /Font << /F1 null >> >>");

        var result = PdfReader.Open(pdf).GetPage(0).ExtractText();

        Assert.Equal(string.Empty, result.Text);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.ResourceMissing, d.Code);
    }

    /// <summary>
    /// A code with no Unicode route contributes no character but its own advance is still applied:
    /// code 65 is deliberately mapped to a glyph name absent from the Adobe Glyph List (so it never
    /// resolves to Unicode), width 1000 (so its advance at 12pt is exactly 12 units); code 66 maps
    /// normally to "B". A text-only assertion on the RESULT cannot see this at all — "B" alone says
    /// nothing about whether the reader skipped the unmapped glyph's position along with its
    /// character — so this reaches <see cref="TextExtractionVisitor"/> directly to read the "B"
    /// run's own <see cref="TextRun.StartX"/>: 100 (the <c>Td</c> origin) plus 12 (the unmapped
    /// glyph's own advance) is 112, not 100.
    /// </summary>
    [Fact]
    public void UnmappedGlyph_noCharacter_butStillAdvances()
    {
        const string fontDict =
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            + "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /zzznotarealglyphname99] >> "
            + "/FirstChar 65 /LastChar 66 /Widths [1000 600] >>";

        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td <4142> Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, fontDict));

        var reader = PdfReader.Open(pdf);
        var page = reader.GetPage(0);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            TextCallBudget.DefaultMaxGlyphsPerPage, TextCallBudget.DefaultMaxCharactersPerPage,
            TextCallBudget.DefaultMaxRunsPerPage, long.MaxValue, sink);
        var interpreter = new ContentInterpreter(reader);
        var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);

        interpreter.Run(page, visitor, sink);
        visitor.Finish();

        var run = Assert.Single(visitor.Runs);
        Assert.Equal("B", run.Text);
        Assert.Equal(112.0, run.StartX, 6);
        Assert.Contains(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.UnmappedGlyphs);
    }

    // ── TJ numeric adjustment wiring (Table 107) ────────────────────────────────────────────────
    //
    // GlyphPositionerTests pins ComputeNumericAdjustment's own FORMULA in isolation; these pin
    // that TextExtractionVisitor.ShowTJArray actually calls it, in the right direction, at the
    // right position in the array (before/between/after strings), and that a non-string,
    // non-number element is skipped rather than silently misread as one or the other. A
    // text-only assertion cannot see any of this — "AB" reads the same whether or not an
    // adjustment between the two glyphs actually moved the pen — so every one of these reaches
    // TextExtractionVisitor directly to read TextRun.StartX/EndX, the same way
    // UnmappedGlyph_noCharacter_butStillAdvances above does.
    //
    // Every fixture uses SimpleFontDict's own 600-unit width and a 12pt Tf, so an unadjusted
    // glyph-to-glyph advance is always 0.6 x 12 = 7.2 text-space units, and a -500 TJ adjustment
    // is always -(-500/1000) x 12 x 1.0 = +6.0 (Table 107: a negative number moves the NEXT glyph
    // right, not left).

    private static (IReadOnlyList<TextRun> Runs, DiagnosticSink Diagnostics) RunTextExtractionVisitor(
        string content, string fontDict)
    {
        var pdf = TextTestSupport.BuildPageDoc(
            content, "<< /Font << /F1 5 0 R >> >>", new TextTestSupport.Obj(5, fontDict));
        var reader = PdfReader.Open(pdf);
        var page = reader.GetPage(0);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            TextCallBudget.DefaultMaxGlyphsPerPage, TextCallBudget.DefaultMaxCharactersPerPage,
            TextCallBudget.DefaultMaxRunsPerPage, long.MaxValue, sink);
        var interpreter = new ContentInterpreter(reader);
        var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);

        interpreter.Run(page, visitor, sink);
        visitor.Finish();
        return (visitor.Runs, sink);
    }

    /// <summary>A number BETWEEN two strings moves the second one: A shows at the <c>Td</c> origin
    /// (100), then advances by its own 7.2, then the -500 adjustment adds a further +6.0, so B
    /// shows at 113.2, not 107.2.</summary>
    [Fact]
    public void TJNumber_betweenTwoStrings_movesTheSecondString()
    {
        var (runs, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td [(A) -500 (B)] TJ ET", TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("AB", run.Text);
        Assert.Equal(100.0, run.StartX, 6);
        Assert.Equal(113.2, run.EndX, 6);
    }

    /// <summary>A LEADING number, before any string in the array, still moves the pen before the
    /// first glyph paints: A would show at 100 with no adjustment, but shows at 106.0 here.</summary>
    [Fact]
    public void TJNumber_leadingBeforeAnyString_movesTheFirstGlyph()
    {
        var (runs, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td [-500 (A)] TJ ET", TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("A", run.Text);
        Assert.Equal(106.0, run.StartX, 6);
        Assert.Equal(106.0, run.EndX, 6);
    }

    /// <summary>TWO numbers in a row both apply, cumulatively, with no string between them: -300
    /// then -200 moves the pen by +3.6 then +2.4, the same net +6.0 a single -500 would (proving
    /// each is applied on its own, per Table 107's prose, rather than only the first, or only the
    /// last, being honoured).</summary>
    [Fact]
    public void TJNumber_twoInARow_bothApplyCumulatively()
    {
        var (runs, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td [(A) -300 -200 (B)] TJ ET", TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("AB", run.Text);
        Assert.Equal(100.0, run.StartX, 6);
        Assert.Equal(113.2, run.EndX, 6);
    }

    /// <summary>
    /// A TRAILING number, with no glyph left in the array to fold into (Table 107's own "the next
    /// glyph does not exist" case — see <c>GlyphPositioner.ComputeNumericAdjustment</c>'s own
    /// remarks for why this reader implements the operator as a standalone translation rather than
    /// the clause's algebraic fold specifically so this case is still defined): the array itself
    /// shows only "A", but the adjustment still moves the pen the array leaves behind, so a
    /// SEPARATE 'Tj' right after picks up "B" at 113.2, not 107.2.
    /// </summary>
    [Fact]
    public void TJNumber_trailingWithNoFollowingGlyph_stillMovesThePenForWhatComesNext()
    {
        var (runs, _) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td [(A) -500] TJ (B) Tj ET", TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("AB", run.Text);
        Assert.Equal(100.0, run.StartX, 6);
        Assert.Equal(113.2, run.EndX, 6);
    }

    /// <summary>A non-string, non-number array element is skipped with exactly one 302
    /// (<see cref="PdfReaderDiagnosticCode.OperandStackMalformed"/>) and moves nothing: "A" and "B"
    /// land exactly one glyph-width apart, as if <c>/Bogus</c> were never there.</summary>
    [Fact]
    public void TJArray_nonStringNonNumberElement_skippedWithOne302_movesNothing()
    {
        var (runs, sink) = RunTextExtractionVisitor(
            "BT /F1 12 Tf 100 700 Td [(A) /Bogus (B)] TJ ET", TextTestSupport.SimpleFontDict());

        var run = Assert.Single(runs);
        Assert.Equal("AB", run.Text);
        Assert.Equal(100.0, run.StartX, 6);
        Assert.Equal(107.2, run.EndX, 6);
        var d = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.OperandStackMalformed);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    // ── Budget integration (MEDIUM 5, #417 round 2) ─────────────────────────────────────────────
    //
    // TextCallBudgetTests pins each of TextCallBudget's own four ceilings in isolation, against
    // the budget object alone. None of those tests proves the EXTRACTION PATH actually charges
    // them: removing the glyph consume from ShowString, the character consume from the same
    // method, the run consume from TextAssembler.Add, or BeginPage() from the per-page walk each
    // leaves TextCallBudgetTests green, since that suite never drives real content through a real
    // interpreter at all. These do, using a deliberately tiny cap on exactly one ceiling per test,
    // the same "one at a time" convention TextCallBudgetTests itself uses.

    /// <summary>
    /// A tight PER-PAGE glyph ceiling (3), reached midway through a single 'Tj' showing ten 'A's:
    /// if <c>TextExtractionVisitor.ShowString</c> stopped charging <c>TryConsumeGlyph</c>, this
    /// page would decode and show all ten instead of stopping at three.
    /// </summary>
    [Fact]
    public void GlyphBudget_stopsRealExtractionMidPage_notJustTheBudgetObject()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (AAAAAAAAAA) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));
        var reader = PdfReader.Open(pdf);
        var page = reader.GetPage(0);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 3, maxCharactersPerPage: 1_000_000, maxRunsPerPage: 1_000_000,
            maxCharactersPerCall: 1_000_000, sink);
        budget.BeginPage();
        var interpreter = new ContentInterpreter(reader);
        var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);

        interpreter.Run(page, visitor, sink);
        visitor.Finish();

        var run = Assert.Single(visitor.Runs);
        Assert.Equal("AAA", run.Text);
        Assert.Contains(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextExtractionLimitExceeded);
    }

    /// <summary>
    /// A tight PER-PAGE character ceiling (3) with the glyph ceiling left generous: since this
    /// font maps one glyph to one character, this fires at the same glyph <see
    /// cref="GlyphBudget_stopsRealExtractionMidPage_notJustTheBudgetObject"/> above does, but only
    /// because <c>ShowString</c> charges <c>TryConsumeCharacters</c> independently of
    /// <c>TryConsumeGlyph</c> — the diagnostic reads "characters on page", not "glyphs", proving
    /// THIS charge fired, not the other one. If <c>ShowString</c> stopped charging
    /// <c>TryConsumeCharacters</c>, the generous glyph ceiling would let all ten characters
    /// through.
    /// </summary>
    [Fact]
    public void CharacterBudget_stopsRealExtractionMidPage_notJustTheBudgetObject()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (AAAAAAAAAA) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));
        var reader = PdfReader.Open(pdf);
        var page = reader.GetPage(0);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 1_000_000, maxCharactersPerPage: 3, maxRunsPerPage: 1_000_000,
            maxCharactersPerCall: 1_000_000, sink);
        budget.BeginPage();
        var interpreter = new ContentInterpreter(reader);
        var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);

        interpreter.Run(page, visitor, sink);
        visitor.Finish();

        var run = Assert.Single(visitor.Runs);
        Assert.Equal("AAA", run.Text);
        var d = Assert.Single(
            sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextExtractionLimitExceeded);
        Assert.Contains("characters on page", d.Message);
    }

    /// <summary>
    /// A tight PER-PAGE run ceiling (2) against a page laying out four separate lines: if
    /// <c>TextAssembler.Add</c> stopped charging <c>TryConsumeRun</c> on every new baseline, all
    /// four lines would come through instead of stopping after two.
    /// </summary>
    [Fact]
    public void RunBudget_stopsRealExtractionMidPage_notJustTheBudgetObject()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (A) Tj ET\n"
            + "BT /F1 12 Tf 100 690 Td (B) Tj ET\n"
            + "BT /F1 12 Tf 100 680 Td (C) Tj ET\n"
            + "BT /F1 12 Tf 100 670 Td (D) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));
        var reader = PdfReader.Open(pdf);
        var page = reader.GetPage(0);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 1_000_000, maxCharactersPerPage: 1_000_000, maxRunsPerPage: 2,
            maxCharactersPerCall: 1_000_000, sink);
        budget.BeginPage();
        var interpreter = new ContentInterpreter(reader);
        var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);

        interpreter.Run(page, visitor, sink);
        visitor.Finish();

        Assert.Equal(2, visitor.Runs.Count);
        Assert.Equal("A", visitor.Runs[0].Text);
        Assert.Equal("B", visitor.Runs[1].Text);
        Assert.Contains(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextExtractionLimitExceeded);
    }

    /// <summary>
    /// MEDIUM 3 (#417 round 4): the hand-rolled loop below only ever proved that a COPY of
    /// production's per-page walk resets its budget — <c>ExtractTextFromPageCore</c>'s own
    /// <c>budget.BeginPage()</c> call, the fourth budget-charge site the PR body once claimed all
    /// four integration tests covered, was never exercised by it at all, and deleting that line
    /// left the whole suite green. This drives the REAL loop instead, through the internal
    /// <c>PdfDocumentReader.ExtractText(PdfTextExtractionOptions, DiagnosticSink, TextCallBudget)</c>
    /// seam (MEDIUM 3's own fix: <c>ExtractText(PdfTextExtractionOptions)</c> now forwards to it),
    /// with the same tight per-page glyph ceiling (3) as the test below: page 1 exhausts on its
    /// five 'A's ("AAA" then stop), and if <c>BeginPage()</c> were removed from PRODUCTION's own
    /// per-page walk, page 2 would refuse its very first glyph too and come back empty instead of
    /// its own "BBB".
    /// </summary>
    [Fact]
    public void ExtractText_realDocumentLevelLoop_resetsPerPageGlyphBudget_acrossRealPages()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            ["BT /F1 12 Tf 100 700 Td (AAAAA) Tj ET", "BT /F1 12 Tf 100 700 Td (BBBBB) Tj ET"],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));
        var reader = PdfReader.Open(pdf);
        var scope = reader.CreateContentDiagnosticScope();
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 3, maxCharactersPerPage: 1_000_000, maxRunsPerPage: 1_000_000,
            maxCharactersPerCall: 1_000_000, scope);

        var result = reader.ExtractText(new PdfTextExtractionOptions(), scope, budget);

        // Pages join with PdfTextExtractionOptions.PageSeparator (default "\f"), not "\n": that
        // joins RUNS within one page.
        Assert.Equal("AAA\fBBB", result.Text);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextExtractionLimitExceeded);
    }

    /// <summary>
    /// Mirrors <see cref="PdfDocumentReader.ExtractText(PdfTextExtractionOptions)"/>'s per-page
    /// loop exactly: one shared budget and diagnostics scope, <c>BeginPage()</c> then a fresh
    /// <c>ContentInterpreter</c>/<c>TextExtractionVisitor</c> pair for each of two REAL pages. A
    /// tight per-page glyph ceiling (3) exhausts on page 1's five 'A's ("AAA" then stop); if
    /// <c>BeginPage()</c> were removed from that per-page walk, <see
    /// cref="TextCallBudget.IsPageExhausted"/> would stay <see langword="true"/> from page 1, and
    /// page 2 would refuse its very FIRST glyph too, producing an empty page 2 instead of its own
    /// "AAA". Kept alongside <see
    /// cref="ExtractText_realDocumentLevelLoop_resetsPerPageGlyphBudget_acrossRealPages"/> above,
    /// which drives PRODUCTION's own loop instead of this one's hand-rolled copy (MEDIUM 3, #417
    /// round 4) — this one still pins the same behaviour directly against
    /// <c>ContentInterpreter</c>/<c>TextExtractionVisitor</c>, with no dependency on the seam above.
    /// </summary>
    [Fact]
    public void BeginPage_resetsPerPageBudget_acrossRealPages_inTheProductionLoopShape()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            ["BT /F1 12 Tf 100 700 Td (AAAAA) Tj ET", "BT /F1 12 Tf 100 700 Td (AAAAA) Tj ET"],
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));
        var reader = PdfReader.Open(pdf);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 3, maxCharactersPerPage: 1_000_000, maxRunsPerPage: 1_000_000,
            maxCharactersPerCall: 1_000_000, sink);

        var pageTexts = new List<string>();
        foreach (var page in reader.Pages)
        {
            budget.BeginPage();
            var interpreter = new ContentInterpreter(reader);
            var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);
            interpreter.Run(page, visitor, sink);
            visitor.Finish();
            pageTexts.Add(string.Join(' ', visitor.Runs.Select(r => r.Text)));
        }

        Assert.Equal(["AAA", "AAA"], pageTexts);
    }

    /// <summary>
    /// The call-WIDE character ceiling, driven across THREE real pages in the same
    /// per-page-loop shape <see
    /// cref="BeginPage_resetsPerPageBudget_acrossRealPages_inTheProductionLoopShape"/> above uses,
    /// with every per-page ceiling left generous so only the call-wide one can be doing the work:
    /// a cap of 6 against three pages of five characters each (15 total) stops partway through
    /// page 2, and — unlike the three per-page ceilings, which <c>BeginPage()</c> resets — this
    /// total is never reset, so page 3 is refused outright (the empty string), not merely
    /// truncated. <c>PdfDocumentReader.ExtractText()</c> shares one <c>TextCallBudget</c> instance
    /// across every page it visits the same way this test does; see
    /// <see cref="CallWideCharacterBudget_derivesFromMaxDecodedStreamBytes_throughRealPublicApi"/>
    /// below for the same mechanism driven through the real public
    /// <c>PdfReaderOptions.MaxDecodedStreamBytes</c> option instead of a literal passed directly to
    /// <see cref="TextCallBudget"/>'s own constructor.
    /// </summary>
    [Fact]
    public void CallWideCharacterBudget_survivesBeginPage_acrossRealPages()
    {
        var pdf = TextTestSupport.BuildMultiPageDoc(
            Enumerable.Repeat("BT /F1 12 Tf 100 700 Td (AAAAA) Tj ET", 3).ToList(),
            "<< /Font << /F1 100 0 R >> >>",
            new TextTestSupport.Obj(100, TextTestSupport.SimpleFontDict()));
        var reader = PdfReader.Open(pdf);
        var sink = new DiagnosticSink(cap: 50);
        var budget = new TextCallBudget(
            maxGlyphsPerPage: 1_000_000, maxCharactersPerPage: 1_000_000, maxRunsPerPage: 1_000_000,
            maxCharactersPerCall: 6, sink);

        var pageTexts = new List<string>();
        foreach (var page in reader.Pages)
        {
            budget.BeginPage();
            var interpreter = new ContentInterpreter(reader);
            var visitor = new TextExtractionVisitor(reader, interpreter, budget, sink, page.Index);
            interpreter.Run(page, visitor, sink);
            visitor.Finish();
            pageTexts.Add(string.Join(' ', visitor.Runs.Select(r => r.Text)));
        }

        Assert.Equal("AAAAA", pageTexts[0]);
        Assert.Equal("A", pageTexts[1]);
        Assert.Equal(string.Empty, pageTexts[2]);
        Assert.Contains(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextExtractionLimitExceeded);
    }

    /// <summary>
    /// LOW 5 (#417 round 2): <c>PdfReaderOptions.MaxDecodedStreamBytes</c> — a BYTE ceiling, per
    /// that option's doc — is reused unconverted as <c>ExtractText</c>'s call-wide CHARACTER
    /// ceiling, through <c>PdfDocumentReader.CreateTextCallBudget</c>. This drives that derivation
    /// through the real public API end to end, rather than constructing <see
    /// cref="TextCallBudget"/> directly the way the test above does: one page, two SEPARATE
    /// content-stream objects (so neither raw byte length approaches <see
    /// cref="ReaderLimits.MinMaxDecodedBytes"/> and gets truncated by the unrelated per-stream
    /// decode-size cap that option ALSO governs — see that doc), each showing 600,000 'A's on its
    /// own line. Set to the 1 MiB floor (1,048,576): the first line's 600,000 characters fit; the
    /// second is cut off 448,576 characters in, for a call-wide total of exactly 1,048,576 —
    /// proving the option's byte value reaches this ceiling unconverted (a real byte-to-character
    /// CONVERSION, halving or dividing by some average character width, would land somewhere else
    /// entirely).
    /// </summary>
    [Fact]
    public void CallWideCharacterBudget_derivesFromMaxDecodedStreamBytes_throughRealPublicApi()
    {
        const int chunkChars = 600_000;
        var chunk = new string('A', chunkChars);
        var pdf = TextTestSupport.BuildPdf(
            1,
            new TextTestSupport.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new TextTestSupport.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new TextTestSupport.Obj(3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents [4 0 R 6 0 R] >>"),
            new TextTestSupport.Obj(4, "<< >>",
                System.Text.Encoding.ASCII.GetBytes($"BT /F1 12 Tf 100 700 Td ({chunk}) Tj ET")),
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()),
            new TextTestSupport.Obj(6, "<< >>",
                System.Text.Encoding.ASCII.GetBytes($"BT /F1 12 Tf 100 690 Td ({chunk}) Tj ET")));
        var reader = PdfReader.Open(
            pdf, new PdfReaderOptions { MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes });

        var result = reader.ExtractText();

        var lines = result.Text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(chunkChars, lines[0].Length);
        Assert.Equal((int)ReaderLimits.MinMaxDecodedBytes - chunkChars, lines[1].Length);
        Assert.Equal((int)ReaderLimits.MinMaxDecodedBytes, result.Text.Length - 1);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.TextExtractionLimitExceeded);
    }

    // ── Contracts ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractText_nullOptions_throwsArgumentNullException()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT ET", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Throws<ArgumentNullException>(() => reader.ExtractText(null!));
        Assert.Throws<ArgumentNullException>(() => reader.GetPage(0).ExtractText(null!));
    }

    [Fact]
    public void ExtractText_pagesOutOfRange_throwsArgumentOutOfRangeException()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT ET", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => reader.ExtractText(new PdfTextExtractionOptions { Pages = 5..6 }));
    }

    [Fact]
    public void PageLevelExtractText_nonAllPages_throwsArgumentException()
    {
        var pdf = TextTestSupport.BuildPageDoc("BT ET", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Throws<ArgumentException>(
            () => reader.GetPage(0).ExtractText(new PdfTextExtractionOptions { Pages = 0..1 }));
    }

    /// <summary>
    /// LOW 6 (#417 round 2): <c>PdfReadPage.ExtractText(PdfTextExtractionOptions)</c> had no
    /// success-path known-answer test of its own — every direct test of the overload above only
    /// asserted an exception. This drives it with an explicit (default-valued) options instance,
    /// distinct from the parameterless convenience overload every other page-level test in this
    /// file uses.
    /// </summary>
    [Fact]
    public void PageLevelExtractText_withExplicitOptions_extractsThePagesOwnText()
    {
        var pdf = TextTestSupport.BuildPageDoc(
            "BT /F1 12 Tf 100 700 Td (Hello) Tj ET",
            "<< /Font << /F1 5 0 R >> >>",
            new TextTestSupport.Obj(5, TextTestSupport.SimpleFontDict()));
        var reader = PdfReader.Open(pdf);

        var result = reader.GetPage(0).ExtractText(new PdfTextExtractionOptions());

        Assert.Equal("Hello", result.Text);
    }

    /// <summary>
    /// <see cref="PdfTextExtractionOptions.PageSeparator"/> is validated in its own <c>init</c>
    /// accessor (MEDIUM 1, #417 round 2), not by either <c>ExtractText</c> overload: the options
    /// object is either fully valid or never constructed, so this throws at the object initializer
    /// above, before <see cref="PdfDocumentReader.ExtractText(PdfTextExtractionOptions)"/> is even
    /// reached. Contrast <see cref="PageLevelExtractText_nonAllPages_throwsArgumentException"/>
    /// above, whose <see cref="PdfTextExtractionOptions.Pages"/> check genuinely cannot move into
    /// the type itself: the SAME value is valid on the document-level overload and meaningless on
    /// the page-level one, so only the call site knows which applies.
    /// </summary>
    [Fact]
    public void PageSeparator_null_throwsArgumentNullException_atConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfTextExtractionOptions { PageSeparator = null! });
    }

    [Fact]
    public void ExtractText_emptyPage_producesEmptyString_neverNull()
    {
        var pdf = TextTestSupport.BuildPageDoc("", "<< >>");
        var reader = PdfReader.Open(pdf);

        Assert.Equal(string.Empty, reader.ExtractText().Text);
        Assert.Equal(string.Empty, reader.GetPage(0).ExtractText().Text);
        Assert.NotNull(reader.ExtractText().Text);
    }
}
