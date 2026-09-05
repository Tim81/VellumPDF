// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Conformance.Rules;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// Value-level tests for the #403 message bound: <see cref="PreflightContext.Report"/> cuts a
/// message at <see cref="PreflightContext.MaxMessageChars"/>, and the ten sites that quoted a
/// producer <see cref="PdfName"/> whole (nine <c>.Value</c> interpolations and the annotation
/// label <c>AnnotationRule</c> builds from one) excerpt it through
/// <see cref="DiagnosticExcerpt.Quote(string)"/> first, so the sentence shape survives instead of
/// being cut mid-word by the sink. Every other producer-controlled interpolation relies on the
/// sink cut alone; #405 lists them.
/// </summary>
public sealed class PreflightMessageBoundTests
{
    // ── Fixture helpers (copied from PdfPreflightTests.AssemblePdf and kept independent; that ────
    // file is over 12,000 lines already and does not need a #403-only dependency added to it) ────

    /// <summary>
    /// A single indirect object for <see cref="AssemblePdf"/>. For a non-stream object,
    /// <see cref="Dict"/> is the complete object text. For a stream object, <see cref="Dict"/> is
    /// the dictionary's inner entries only; the assembler wraps it and appends the /Length.
    /// </summary>
    private sealed record PdfObj(string Dict, byte[]? Stream = null);

    private static readonly PdfObj _pagesObj = new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    private static readonly PdfObj _pageObj = new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

    private static byte[] AssemblePdf(
        IReadOnlyList<PdfObj> objects,
        string xmpConformance = "B")
    {
        var all = new List<PdfObj>(objects);

        var metaObjNum = all.Count + 1;
        var xmp = XmpBytes("2", xmpConformance);
        all.Add(new PdfObj("/Type /Metadata /Subtype /XML", xmp));
        all[0] = all[0] with { Dict = InjectIntoDict(all[0].Dict, $"/Metadata {metaObjNum} 0 R") };

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var offsets = new int[all.Count + 1];
        for (var i = 0; i < all.Count; i++)
        {
            offsets[i + 1] = (int)ms.Position;
            var n = i + 1;
            if (all[i].Stream is { } body)
            {
                W($"{n} 0 obj\n<< {all[i].Dict} /Length {body.Length} >>\nstream\n");
                ms.Write(body);
                W("\nendstream\nendobj\n");
            }
            else
            {
                W($"{n} 0 obj\n{all[i].Dict}\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        var size = all.Count + 1;
        W($"xref\n0 {size}\n");
        W($"{0:D10} 65535 f \n");
        for (var i = 1; i <= all.Count; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R "
            + "/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static string InjectIntoDict(string dict, string entry)
    {
        var i = dict.LastIndexOf(">>", StringComparison.Ordinal);
        return i < 0 ? dict : string.Concat(dict[..i], entry, " ", dict[i..]);
    }

    private static byte[] XmpBytes(string part, string conformance)
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + $"<pdfaid:part>{part}</pdfaid:part>"
            + $"<pdfaid:conformance>{conformance}</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    /// <summary>
    /// Builds a doc whose page's /Contents stream carries the given /Filter name.
    /// </summary>
    private static byte[] BuildOversizedFilterPdf(string filterName)
        => AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>"),
            new($"/Filter /{filterName}", []),
        ]);

    /// <summary>
    /// Same shape as <c>PdfPreflightTests.BuildFontPdf</c>: a Type0 font selected via Tf.
    /// </summary>
    private static byte[] BuildFontPdf(params PdfObj[] fontObjects)
    {
        var contentObjNum = 6 + fontObjects.Length;
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R "
                + $"/Contents {contentObjNum} 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
        };
        objects.AddRange(fontObjects);
        objects.Add(new PdfObj(string.Empty, Encoding.ASCII.GetBytes("BT /F0 12 Tf ET")));
        return AssemblePdf(objects);
    }

    /// <summary>
    /// Builds a UA-1 tagged document with one embedded (Type0) font selected via Tf, mirroring
    /// <c>OracleCorpus.WriterPdfTagged</c>. Kept independent because that method is private to
    /// <c>OracleCorpus</c>.
    /// </summary>
    private static byte[] BuildUa1TaggedPdf()
    {
        using var doc = new PdfDocument
        {
            Conformance = VellumPdf.Document.PdfConformance.PdfUA1,
            Language = "en-US",
        };
        doc.Info.Title = "PreflightMessageBoundTests fixture";
        var page = doc.AddPage(PageSize.A4);
        var handle = doc.EmbedStandard14Font(Standard14.Helvetica);
        doc.RegisterEmbeddedFontUsage(page, handle);

        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[7];
        var count = handle.GetGlyphIds("Tagged.", gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.EndMarkedContent();
        canvas.Finish();

        var p = new PdfStructElem("P") { Page = page, Mcid = mcid };
        var root = new PdfStructElem("Document");
        root.AddChild(p);
        doc.RegisterStructElem(root);

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Rewrites the fixture's Type0 font's /Encoding to an oversized name via an incremental
    /// update, mirroring <c>OracleCorpus.Ua1BadCMapName</c>'s clone-and-AppendRevision step (that
    /// method itself is a registered oracle fixture and is not reused directly).
    /// </summary>
    private static byte[] BuildUa1OversizedCMapNamePdf(int nameBytes)
    {
        var baseline = BuildUa1TaggedPdf();
        using var reader = PdfReader.Open(baseline);
        var pagesRef = (PdfIndirectReference)reader.Catalog.Get(PdfName.Pages)!;
        var pages = (PdfDictionary)reader.Resolve(pagesRef.ObjectNumber)!;
        var kidsObj = pages.Get(new PdfName("Kids"));
        var kids = kidsObj is PdfIndirectReference kr
            ? (PdfArray)reader.Resolve(kr.ObjectNumber)!
            : (PdfArray)kidsObj!;
        var pageRef = (PdfIndirectReference)kids[0];
        var page = (PdfDictionary)reader.Resolve(pageRef.ObjectNumber)!;
        var resources = (PdfDictionary)reader.ResolveValue(page.Get(new PdfName("Resources"))!)!;
        var fontDict = (PdfDictionary)reader.ResolveValue(resources.Get(PdfName.Font)!)!;
        var type0Ref = (PdfIndirectReference)fontDict.Entries.First().Value;
        var type0 = (PdfDictionary)reader.Resolve(type0Ref.ObjectNumber)!;

        var clone = new PdfDictionary();
        foreach (var kv in type0.Entries)
            clone.Set(kv.Key, kv.Value);
        clone.Set(new PdfName("Encoding"), new PdfName(new string('A', nameBytes)));

        return reader.AppendRevision([(type0Ref.ObjectNumber, 0, clone)]);
    }

    // ── The shared oversized-/Filter fixture: the StreamRule site, and the sink ─────────────────

    [Fact]
    public void StreamFilter_withAnOversizedName_reportsAFixedExcerpt()
    {
        var bytes = BuildOversizedFilterPdf(new string('A', 1_048_576));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
        Assert.Equal(
            "A stream uses the /" + new string('A', 32) + "... (1048576 bytes) filter, "
            + "which is not permitted in PDF/A-2.",
            assertion.Message);
    }

    [Fact]
    public void RuleEvaluationFailure_withAnOversizedToken_isBoundedByTheSink()
    {
        var bytes = BuildOversizedFilterPdf(new string('A', 1_048_576));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var full = "Rule evaluation failed: Unknown PDF filter: /" + new string('A', 1_048_576);
        var expected = full[..1024] + $"... ({full.Length} chars)";

        // Several rules try to decode the same oversized-filter /Contents stream and each wraps
        // the same InvalidDataException as its own "Rule evaluation failed" finding (#403); assert
        // every one of them, not just the first, since they all quote the identical thrown message.
        var matching = result.Assertions
            .Where(a => a.Message.StartsWith(
                "Rule evaluation failed: Unknown PDF filter: /", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(matching);
        Assert.All(matching, a => Assert.Equal(expected, a.Message));
    }

    // ── One test per remaining site that quotes a producer name ──────────────────────────────────

    [Fact]
    public void ActionType_withAnOversizedName_reportsAFixedExcerpt()
    {
        var name = new string('A', 1_048_576);
        var bytes = AssemblePdf(
        [
            new($"<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /{name} >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.5.1-action");
        Assert.Equal(
            "The action type /" + new string('A', 32) + "... (1048576 bytes) is not permitted in PDF/A.",
            assertion.Message);
    }

    [Fact]
    public void NamedAction_withAnOversizedName_reportsAFixedExcerpt()
    {
        var name = new string('A', 1_048_576);
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new($"<< /S /Named /N /{name} >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.5.1-named-action");
        Assert.Equal(
            "The named action /" + new string('A', 32) + "... (1048576 bytes) is not permitted "
            + "in PDF/A (only NextPage, PrevPage, FirstPage, and LastPage are allowed).",
            assertion.Message);
    }

    [Fact]
    public void AnnotationAppearanceExtraKey_withAnOversizedName_reportsAFixedExcerpt()
    {
        var key = new string('A', 1_048_576);
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Text /Rect [10 10 50 50] /F 4 /Contents (n) "
                + $"/AP << /N 5 0 R /{key} 5 0 R >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Stream: []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var assertion = Assert.Single(
            result.Assertions,
            a => a.RuleId == "ISO19005-2:6.3-annotation" && a.Message.Contains("(/AP)", StringComparison.Ordinal));
        Assert.Equal(
            "A /Text annotation's appearance dictionary (/AP) shall contain only the /N entry (found /"
            + new string('A', 32) + "... (1048576 bytes)).",
            assertion.Message);
    }

    [Fact]
    public void AnnotationLabel_withAnOversizedSubtype_reportsAFixedExcerptEverywhereItIsReused()
    {
        var subtype = new string('A', 1_048_576);
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new($"<< /Type /Annot /Subtype /{subtype} /Rect [10 10 50 50] >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var label = "A /" + new string('A', 32) + "... (1048576 bytes) annotation";
        var annotationFindings = result.Assertions
            .Where(a => a.RuleId == "ISO19005-2:6.3-annotation")
            .ToList();
        // No /F and no /AP: the Print-flag message and the missing-appearance message both fire,
        // and both must carry the excerpt rather than the whole /Subtype.
        Assert.Equal(2, annotationFindings.Count);
        Assert.All(
            annotationFindings,
            a => Assert.StartsWith(label, a.Message, StringComparison.Ordinal));
        Assert.Contains(
            annotationFindings,
            a => a.Message == label + " shall have the Print flag set.");
        Assert.Contains(
            annotationFindings,
            a => a.Message == label + " shall have a normal appearance (/AP /N).");
    }

    [Fact]
    public void Type0CMapName_withAnOversizedName_reportsAFixedExcerpt()
    {
        var cmapName = new string('A', 1_048_576);
        var bytes = BuildFontPdf(
            new PdfObj($"<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /{cmapName} "
                + "/DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /FontDescriptor 8 0 R "
                + "/CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-cmap-name");
        Assert.Equal(
            "A composite font's /Encoding names the CMap /" + new string('A', 32)
            + "... (1048576 bytes), which is neither one of the predefined CMaps nor an embedded "
            + "CMap stream.",
            assertion.Message);
    }

    [Fact]
    public void RoleMapEntry_withAnOversizedName_reportsAFixedExcerpt()
    {
        // LogicalStructureRule's "shall map to a name" branch needs a /RoleMap value that is not a
        // name (a name key alone cannot reach it); 42 is an arbitrary non-name.
        var roleKey = new string('A', 1_048_576);
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new($"<< /Type /StructTreeRoot /RoleMap << /{roleKey} 42 >> >>"),
        ],
        xmpConformance: "A");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.8-logical-structure");
        Assert.Equal(
            "The structure tree /RoleMap entry /" + new string('A', 32)
            + "... (1048576 bytes) shall map to a name.",
            assertion.Message);
    }

    [Fact]
    public void PermissionsKey_withAnOversizedName_reportsAFixedExcerpt()
    {
        var key = new string('A', 1_048_576);
        var bytes = AssemblePdf(
        [
            new($"<< /Type /Catalog /Pages 2 0 R /Perms << /{key} << /Type /Sig >> >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.1.12-1-permissions");
        Assert.Equal(
            "The permissions dictionary contains the key /" + new string('A', 32)
            + "... (1048576 bytes); only /UR3 and /DocMDP are permitted in PDF/A-2.",
            assertion.Message);
    }

    [Fact]
    public void BlendMode_withAnOversizedName_reportsAFixedExcerpt()
    {
        var bm = new string('A', 1_048_576);
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 7 0 R >>"),
            new("<< /ExtGState 5 0 R >>"),
            new("<< /GS0 6 0 R >>"),
            new($"<< /Type /ExtGState /BM /{bm} >>"),
            new(string.Empty, Encoding.ASCII.GetBytes("q /GS0 gs Q")),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.2.10-blend-mode");
        Assert.Equal(
            "The blend mode /" + new string('A', 32) + "... (1048576 bytes) is not one of the "
            + "standard blend modes permitted in PDF/A-2.",
            assertion.Message);
    }

    [Fact]
    public void UaType0CMapName_withAnOversizedName_reportsAFixedExcerpt()
    {
        var bytes = BuildUa1OversizedCMapNamePdf(1_048_576);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        var assertion = Assert.Single(
            result.Assertions, a => a.RuleId == "ISO14289-1:7.21.3.3-1");
        Assert.Equal(
            "A composite font's /Encoding names the CMap /" + new string('A', 32)
            + "... (1048576 bytes), which is neither one of the predefined CMaps nor an embedded "
            + "CMap stream (§7.21.3.3).",
            assertion.Message);
    }

    // ── Layer A: the sink's own cut, at the boundary ─────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Report_atTheBoundary_keepsExactlyMaxMessageChars(int scenario)
    {
        var bytes = AssemblePdf([new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj]);
        using var reader = PdfReader.Open(bytes);
        var assertions = new List<PreflightAssertion>();
        var context = new PreflightContext(reader, PdfConformance.PdfA2B, assertions);

        var message = scenario switch
        {
            0 => new string('x', PreflightContext.MaxMessageChars),
            1 => new string('x', PreflightContext.MaxMessageChars + 1),
            _ => new string('x', 1023) + "\U0001F600" + "trailing text past the boundary",
        };

        context.Report("VellumTest:boundary", "n/a", PreflightSeverity.Error, message);

        var retained = Assert.Single(assertions).Message;
        switch (scenario)
        {
            case 0:
                Assert.Equal(message, retained);
                break;
            case 1:
                Assert.Equal(message[..1024] + $"... ({message.Length} chars)", retained);
                break;
            default:
                Assert.Equal(message[..1023] + $"... ({message.Length} chars)", retained);
                break;
        }

        // Scenario 2 fails here when the surrogate step-back in Report is removed: the cut would
        // then land between the two halves of U+1F600 and leave a lone high surrogate.
        Assert.DoesNotContain(retained, char.IsSurrogate);
    }

    // ── The issue's own shape: 400 pages sharing one oversized /Filter name ─────────────────────

    [Fact]
    public void FourHundredPagesSharingOneOversizedFilterName_retainOnlyBoundedMessages()
    {
        const int pageCount = 400;
        const int nameBytes = 900_000;
        var firstContentObj = 3 + pageCount;
        var sharedFilterObj = 3 + pageCount * 2;

        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids ["
                + string.Join(" ", Enumerable.Range(3, pageCount).Select(n => $"{n} 0 R"))
                + $"] /Count {pageCount} >>"),
        };
        for (var i = 0; i < pageCount; i++)
            objects.Add(new PdfObj(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {firstContentObj + i} 0 R >>"));
        for (var i = 0; i < pageCount; i++)
            objects.Add(new PdfObj($"/Filter {sharedFilterObj} 0 R", []));
        objects.Add(new PdfObj("/" + new string('A', nameBytes)));

        var bytes = AssemblePdf(objects, xmpConformance: "A");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        // "... (" + up to ten digits of length + " chars)" is the longest suffix Report appends.
        const int maxSuffixChars = 5 + 10 + 7;
        Assert.All(
            result.Assertions,
            a => Assert.True(
                a.Message.Length <= PreflightContext.MaxMessageChars + maxSuffixChars,
                $"A retained message of rule {a.RuleId} was {a.Message.Length} chars."));

        // 128 Ki characters for 407 findings; the pre-fix total was 705.7 MiB of message text.
        var totalLength = result.Assertions.Sum(a => a.Message.Length);
        Assert.True(totalLength < 131_072, $"Total retained message length was {totalLength} chars.");

        Assert.Contains(
            result.Assertions,
            a => a.Message == "A stream uses the /" + new string('A', 32) + "... (900000 bytes) filter, "
                + "which is not permitted in PDF/A-2.");
    }
}
