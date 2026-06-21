// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Linq;
using System.Text;
using VellumPdf.Conformance;
using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Conformance.Tests.Oracle;
using VellumPdf.Document;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Tests;

public sealed class PdfPreflightTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal PDF/A-2b document. The writer emits the %PDF-1.7 header, the binary
    /// marker comment, a trailer /ID, and a /Type /Catalog — i.e. everything the §6.1 file-structure
    /// rules require — so the result is compliant against the rules implemented so far.
    /// </summary>
    private static byte[] BuildOnePagePdf()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A plain (non-PDF/A) document. The writer emits a %PDF-2.0 header, which is not
    /// valid PDF/A-2 (§6.1.2).</summary>
    private static byte[] BuildPlainPdf20()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A catalog (object 1) that is a dictionary but is missing the required <c>/Type /Catalog</c>
    /// entry; everything else (header, marker, /ID, XMP) is valid, so the catalog-type rule is the
    /// only violation.
    /// </summary>
    private static byte[] BuildCatalogMissingTypePdf()
        => AssemblePdf(
        [
            new("<< /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids [] /Count 0 >>"),
        ]);

    /// <summary>
    /// A structurally valid catalog, with the §6.1.2 binary marker and the §6.1.3 trailer
    /// <c>/ID</c> toggleable so each file-structure rule can be exercised in isolation.
    /// </summary>
    private static byte[] BuildMinimalPdf(bool binaryMarker, bool trailerId)
        => AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R >>"),
                new("<< /Type /Pages /Kids [] /Count 0 >>"),
            ],
            binaryMarker: binaryMarker,
            trailerId: trailerId);

    /// <summary>
    /// A single indirect object for <see cref="AssemblePdf"/>. For a non-stream object,
    /// <see cref="Dict"/> is the complete object text (e.g. <c>"&lt;&lt; /Type /Catalog &gt;&gt;"</c>).
    /// For a stream object, <see cref="Dict"/> is the dictionary's inner entries only (e.g.
    /// <c>"/N 3"</c>); the assembler wraps it and appends the correct <c>/Length</c>.
    /// </summary>
    private sealed record PdfObj(string Dict, byte[]? Stream = null);

    /// <summary>
    /// Assembles a classic-xref PDF/A-shaped file from a 1-indexed object list (object 1 is the
    /// document catalog). By default it satisfies every always-on structural rule — the %PDF-1.7
    /// header, binary marker, trailer <c>/ID</c>, and a conforming XMP <c>/Metadata</c> stream —
    /// so a fixture trips only the rule it is built to violate. Each flag drops the corresponding
    /// element so a single structural rule can be exercised in isolation.
    /// </summary>
    private static byte[] AssemblePdf(
        IReadOnlyList<PdfObj> objects,
        bool binaryMarker = true,
        bool trailerId = true,
        bool metadata = true,
        string xmpConformance = "B",
        byte[]? metadataOverride = null)
    {
        var all = new List<PdfObj>(objects);

        // Append an XMP metadata stream and reference it from the catalog (object 1).
        if (metadata)
        {
            var metaObjNum = all.Count + 1;
            var xmp = metadataOverride ?? XmpBytes("2", xmpConformance);
            all.Add(new PdfObj("/Type /Metadata /Subtype /XML", xmp));
            all[0] = all[0] with { Dict = InjectIntoDict(all[0].Dict, $"/Metadata {metaObjNum} 0 R") };
        }

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        if (binaryMarker)
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
        var id = trailerId
            ? " /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]"
            : string.Empty;
        W($"trailer\n<< /Size {size} /Root 1 0 R{id} >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>Inserts <paramref name="entry"/> into a <c>&lt;&lt; … &gt;&gt;</c> dictionary literal, before the closing delimiter.</summary>
    private static string InjectIntoDict(string dict, string entry)
    {
        var i = dict.LastIndexOf(">>", StringComparison.Ordinal);
        return i < 0 ? dict : string.Concat(dict[..i], entry, " ", dict[i..]);
    }

    /// <summary>A minimal conforming XMP packet carrying the given pdfaid part and conformance.</summary>
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

    /// <summary>A conforming PDF/A-2b XMP packet with <paramref name="headerExtra"/> injected into the
    /// <c>&lt;?xpacket?&gt;</c> header, serialised as UTF-8 (optionally with a BOM) or UTF-16.</summary>
    private static byte[] XmpWithHeader(string headerExtra = "", Encoding? encoding = null, bool bom = false)
    {
        var xmp =
            $"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"{headerExtra}?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        encoding ??= Encoding.UTF8;
        var body = encoding.GetBytes(xmp);
        return bom ? encoding.GetPreamble().Concat(body).ToArray() : body;
    }

    private static byte[] BuildXmpPdf(byte[] metadata) => AssemblePdf(
        [new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj], metadataOverride: metadata);

    /// <summary>A PDF/A-2b XMP packet declaring one extension schema (element serialisation) with the
    /// given <paramref name="schemaFields"/> and one property with <paramref name="propertyFields"/>.</summary>
    private static byte[] ExtensionSchemaXmp(string schemaFields, string propertyFields)
    {
        const string ns =
            "xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
            + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" "
            + "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"";
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>"
            + $"<rdf:Description rdf:about=\"\" {ns}><pdfaExtension:schemas><rdf:Bag>"
            + "<rdf:li rdf:parseType=\"Resource\">" + schemaFields
            + "<pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" + propertyFields
            + "</rdf:li></rdf:Seq></pdfaSchema:property></rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>"
            + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    private const string _validSchemaFields =
        "<pdfaSchema:schema>S</pdfaSchema:schema>"
        + "<pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
        + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>";

    private const string _validPropertyFields =
        "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>"
        + "<pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>";

    /// <summary>A PDF/A-2b XMP packet whose RDF carries the given extra <c>rdf:Description</c> markup
    /// after the mandatory pdfaid description.</summary>
    private static byte[] XmpWithDescriptions(string extra)
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>"
            + extra + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    // A custom property in a non-predefined namespace.
    private const string _customProperty =
        "<rdf:Description rdf:about=\"\" xmlns:ex=\"http://example.com/ns/\"><ex:foo>bar</ex:foo></rdf:Description>";

    private const string _validValueTypeFields =
        "<pdfaType:type>MyType</pdfaType:type><pdfaType:namespaceURI>http://example.com/t/</pdfaType:namespaceURI>"
        + "<pdfaType:prefix>mt</pdfaType:prefix><pdfaType:description>d</pdfaType:description>";

    private const string _validValueTypeField =
        "<pdfaField:name>f</pdfaField:name><pdfaField:valueType>Text</pdfaField:valueType><pdfaField:description>d</pdfaField:description>";

    /// <summary>An extension schema declaring a custom value type (pdfaType) with one field (pdfaField).</summary>
    private static string ValueTypeSchema(string typeFields, string fieldFields) =>
        "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
        + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\" "
        + "xmlns:pdfaType=\"http://www.aiim.org/pdfa/ns/type#\" xmlns:pdfaField=\"http://www.aiim.org/pdfa/ns/field#\">"
        + "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">"
        + "<pdfaSchema:schema>S</pdfaSchema:schema><pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
        + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
        + "<pdfaSchema:valueType><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" + typeFields
        + "<pdfaType:field><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" + fieldFields
        + "</rdf:li></rdf:Seq></pdfaType:field></rdf:li></rdf:Seq></pdfaSchema:valueType>"
        + "</rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>";

    // A PDF/A extension schema declaring the http://example.com/ns/ namespace.
    private const string _exampleExtensionSchema =
        "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
        + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" "
        + "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">"
        + "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">"
        + "<pdfaSchema:schema>S</pdfaSchema:schema>"
        + "<pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
        + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
        + "<pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">"
        + "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>"
        + "<pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>"
        + "</rdf:li></rdf:Seq></pdfaSchema:property></rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>";

    /// <summary>A minimal ICC profile body: zero-filled, with the 'acsp' signature at offset 36.</summary>
    private static byte[] FakeIccProfile()
    {
        var icc = new byte[128];
        icc[36] = (byte)'a';
        icc[37] = (byte)'c';
        icc[38] = (byte)'s';
        icc[39] = (byte)'p';
        return icc;
    }

    private static readonly PdfObj _pagesObj = new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    private static readonly PdfObj _pageObj = new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

    /// <summary>Builds a doc whose catalog /OutputIntents references the given extra object bodies.
    /// The page paints device-dependent colour, so the output-intent requirements actually apply
    /// (issue #128). The colour content stream is appended after the extras so their object numbers
    /// — referenced by <paramref name="catalogIntents"/> — are unchanged.</summary>
    private static byte[] BuildOutputIntentPdf(string catalogIntents, params PdfObj[] extra)
    {
        var contentObjNum = 3 + extra.Length + 1; // catalog(1) pages(2) page(3) extra… then content
        var objects = new List<PdfObj>
        {
            new($"<< /Type /Catalog /Pages 2 0 R /OutputIntents {catalogIntents} >>"),
            _pagesObj,
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentObjNum} 0 R >>"),
        };
        objects.AddRange(extra);
        objects.Add(new(string.Empty, Encoding.ASCII.GetBytes("1 0 0 rg 100 100 50 50 re f")));
        return AssemblePdf(objects);
    }

    /// <summary>Builds a one-page doc whose /Resources /ExtGState /GS0 has the given /BM body,
    /// with page content that applies the graphics state (`/GS0 gs`) so the blend mode is current.</summary>
    private static byte[] BuildBlendModePdf(string bmEntry)
    {
        return AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 7 0 R >>"),
            new("<< /ExtGState 5 0 R >>"),
            new("<< /GS0 6 0 R >>"),
            new($"<< /Type /ExtGState /BM {bmEntry} >>"),
            new(string.Empty, Encoding.ASCII.GetBytes("q /GS0 gs Q")),
        ]);
    }

    /// <summary>Builds a one-page doc whose /Resources /ExtGState /GS0 carries the given inner
    /// entries; the page content applies it (<c>/GS0 gs</c>) when <paramref name="apply"/> is true.</summary>
    private static byte[] BuildExtGStatePdf(string gsEntries, bool apply = true)
    {
        return AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 7 0 R >>"),
            new("<< /ExtGState 5 0 R >>"),
            new("<< /GS0 6 0 R >>"),
            new($"<< /Type /ExtGState {gsEntries} >>"),
            new(string.Empty, Encoding.ASCII.GetBytes(apply ? "q /GS0 gs Q" : "q Q")),
        ]);
    }

    /// <summary>Builds a one-page doc whose single content stream is <paramref name="content"/>.</summary>
    private static byte[] BuildContentStreamPdf(string content)
    {
        return AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>"),
            new(string.Empty, Encoding.ASCII.GetBytes(content)),
        ]);
    }

    /// <summary>
    /// Builds a one-page doc whose /Resources /XObject /X0 references the XObject described by
    /// <paramref name="xobjectDict"/> (a stream object with body <paramref name="body"/>). When
    /// <paramref name="draw"/> is true the page content paints it (<c>/X0 Do</c>); otherwise the
    /// page has no /Contents, so the XObject is present in resources but never drawn.
    /// </summary>
    private static byte[] BuildXObjectPdf(string xobjectDict, byte[] body, bool draw)
    {
        var page = draw
            ? "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 7 0 R >>"
            : "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R >>";
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new(page),
            new("<< /XObject 5 0 R >>"),
            new("<< /X0 6 0 R >>"),
            new(xobjectDict, body),
        };
        if (draw)
            objects.Add(new PdfObj(string.Empty, Encoding.ASCII.GetBytes("q /X0 Do Q")));
        return AssemblePdf(objects);
    }

    private const string _imageDict =
        "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray";

    private const string _formDict = "/Type /XObject /Subtype /Form /BBox [0 0 1 1]";

    /// <summary>
    /// Builds a one-page doc whose /Resources /Font /F0 references object 6, with
    /// <paramref name="fontObjects"/> supplying objects 6..N (the font dict and any descriptor /
    /// font-program streams it points to). The page content selects the font via <c>/F0 12 Tf</c>
    /// so that font rules — which now scope to fonts actually used by page content (issue #118) —
    /// still exercise the font.
    /// </summary>
    private static byte[] BuildFontPdf(params PdfObj[] fontObjects)
    {
        // Objects: 1=catalog 2=pages 3=page 4=resources-dict 5=font-dict-map 6..=fontObjects
        // The content stream comes after the font objects; its number is 6 + fontObjects.Length.
        var contentObjNum = 6 + fontObjects.Length;
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents {contentObjNum} 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
        };
        objects.AddRange(fontObjects);
        objects.Add(new PdfObj(string.Empty, Encoding.ASCII.GetBytes("BT /F0 12 Tf ET")));
        return AssemblePdf(objects);
    }

    /// <summary>
    /// Builds a one-page doc where the page dictionary carries <paramref name="pageExtra"/> (e.g.
    /// <c>"/Annots [4 0 R]"</c>) and <paramref name="extra"/> supplies objects 4..N.
    /// </summary>
    private static byte[] BuildPagePdf(string pageExtra, params PdfObj[] extra)
    {
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageExtra} >>"),
        };
        objects.AddRange(extra);
        return AssemblePdf(objects);
    }

    /// <summary>Builds a one-page doc with a single embedded Identity-H Type0 font, optionally with /ToUnicode.
    /// The page content selects the font via <c>/F0 12 Tf</c> so that font rules — which now scope to
    /// fonts actually used by page content (issue #118) — still exercise the font.</summary>
    private static byte[] BuildType0FontPdf(bool withToUnicode, string xmpConformance)
    {
        // Objects: 1=catalog 2=pages 3=page 4=resources-dict 5=font-dict-map 6=Type0 7=CIDFont
        // 8=FontDescriptor 9=FontFile2 [10=ToUnicode if withToUnicode] then content stream.
        var toUnicodeObjNum = 10;
        var contentObjNum = withToUnicode ? 11 : 10;

        var fontDict = withToUnicode
            ? $"<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode {toUnicodeObjNum} 0 R >>"
            : "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] >>";

        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents {contentObjNum} 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
            new(fontDict),
            new("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /FontDescriptor 8 0 R /CIDToGIDMap /Identity >>"),
            new("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new("/Length1 4", [1, 2, 3, 4]),
        };
        if (withToUnicode)
            objects.Add(new PdfObj("/Type /CMap", Encoding.ASCII.GetBytes("/CIDInit")));
        objects.Add(new PdfObj(string.Empty, Encoding.ASCII.GetBytes("BT /F0 12 Tf ET")));

        return AssemblePdf(objects, xmpConformance: xmpConformance);
    }

    /// <summary>A minimal PDF/UA-1 XMP packet (pdfuaid:part + optional dc:title).</summary>
    private static byte[] UaXmpBytes(string part = "1", bool withTitle = true)
    {
        var title = withTitle
            ? "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Title</rdf:li></rdf:Alt></dc:title>"
            : string.Empty;
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" "
            + "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + $"<pdfuaid:part>{part}</pdfuaid:part>"
            + title
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    /// <summary>
    /// Builds a PDF/UA-1 fixture that is compliant by default; each flag drops one requirement so a
    /// single UA rule can be exercised in isolation. Object 4 is always a /StructTreeRoot.
    /// </summary>
    private static byte[] BuildUaPdf(
        bool lang = true,
        bool marked = true,
        bool structTreeRoot = true,
        bool displayDocTitle = true,
        byte[]? xmpOverride = null,
        string pageExtra = "")
    {
        var catalog = "<< /Type /Catalog /Pages 2 0 R"
            + (lang ? " /Lang (en-US)" : string.Empty)
            + (marked ? " /MarkInfo << /Marked true >>" : " /MarkInfo << /Marked false >>")
            + (structTreeRoot ? " /StructTreeRoot 4 0 R" : string.Empty)
            + (displayDocTitle
                ? " /ViewerPreferences << /DisplayDocTitle true >>"
                : " /ViewerPreferences << /DisplayDocTitle false >>")
            + " >>";

        return AssemblePdf(
            [
                new(catalog),
                _pagesObj,
                new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageExtra} >>"),
                new("<< /Type /StructTreeRoot >>"),
            ],
            metadataOverride: xmpOverride ?? UaXmpBytes());
    }

    /// <summary>Builds a tagged-document fixture (XMP conformance A) with the given catalog/extra objects.</summary>
    private static byte[] Build2aPdf(string catalogExtra, params PdfObj[] extra)
    {
        var objects = new List<PdfObj>
        {
            new($"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>"),
            _pagesObj,
            _pageObj,
        };
        objects.AddRange(extra);
        return AssemblePdf(objects, xmpConformance: "A");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_WellFormedDocument_IsCompliant()
    {
        var result = PdfPreflight.Validate(BuildOnePagePdf(), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
        Assert.Equal(PdfConformance.PdfA2B, result.Conformance);
    }

    [Fact]
    public void Validate_CatalogMissingType_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildCatalogMissingTypePdf(), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO32000-1:7.7.2-catalog-type", assertion.RuleId);
        Assert.Equal(PreflightSeverity.Error, assertion.Severity);
        Assert.Contains("/Catalog", assertion.Message);
    }

    [Fact]
    public void Validate_UsesOpenedReader_WithoutDisposingIt()
    {
        using var reader = PdfReader.Open(BuildOnePagePdf());

        var result = PdfPreflight.Validate(reader, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        // Reader is still usable: validation did not dispose it.
        Assert.NotNull(reader.Catalog);
    }

    [Fact]
    public void Validate_UnregisteredLevel_Throws()
    {
        // All defined levels are registered; an out-of-range value exercises the unsupported path.
        var bytes = BuildOnePagePdf();
        Assert.Throws<NotSupportedException>(() => PdfPreflight.Validate(bytes, (PdfConformance)999));
    }

    [Fact]
    public void Validate_NullBytes_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfPreflight.Validate((byte[])null!, PdfConformance.PdfA2B));
    }

    [Fact]
    public void Validate_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfPreflight.Validate((Stream)null!, PdfConformance.PdfA2B));
    }

    [Fact]
    public void Validate_NullReader_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfPreflight.Validate((PdfDocumentReader)null!, PdfConformance.PdfA2B));
    }

    [Fact]
    public void Validate_StreamOverload_Validates()
    {
        using var stream = new MemoryStream(BuildOnePagePdf());

        var result = PdfPreflight.Validate(stream, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
    }

    // ── §6.1 file-structure rules ──────────────────────────────────────────────

    [Theory]
    [InlineData("/F (ext.dat)")]
    [InlineData("/FFilter /FlateDecode")]
    [InlineData("/FDecodeParms << >>")]
    public void Validate_ExternalStream_ReportsError(string externalKey)
    {
        // §6.1.7.1: a stream dictionary shall not carry /F, /FFilter, or /FDecodeParms (external
        // streams). The stream object here is not referenced, but §6.1.7.1 constrains every stream.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new(externalKey, []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.1.7.1-external-stream", assertion.RuleId);
    }

    [Fact]
    public void Validate_OversizedInteger_ReportsError()
    {
        // §6.1.13: no integer greater than 2147483647.
        var bytes = AssemblePdf(
            [new("<< /Type /Catalog /Pages 2 0 R /VellumBig 9999999999 >>"), _pagesObj, _pageObj]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-integer");
    }

    [Fact]
    public void Validate_OversizedName_ReportsError()
    {
        // §6.1.13: no name longer than 127 bytes.
        var bytes = AssemblePdf(
            [new($"<< /Type /Catalog /Pages 2 0 R /{new string('A', 200)} 0 >>"), _pagesObj, _pageObj]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-name");
    }

    [Fact]
    public void Validate_OversizedString_ReportsError()
    {
        // §6.1.13: no string longer than 32767 bytes.
        var bytes = AssemblePdf(
            [new($"<< /Type /Catalog /Pages 2 0 R /VellumStr ({new string('x', 32768)}) >>"), _pagesObj, _pageObj]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-string");
    }

    [Fact]
    public void Validate_TinyMediaBox_ReportsError()
    {
        // §6.1.13: a page-boundary side shall be between 3 and 14400 units.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-page-bounds");
    }

    [Fact]
    public void Validate_TrailingDataAfterEof_ReportsError()
    {
        // §6.1.3: no data after the final %%EOF except an optional single EOL.
        var clean = AssemblePdf([new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj]);
        var withJunk = clean.Concat(Encoding.ASCII.GetBytes(" trailing-junk")).ToArray();

        var result = PdfPreflight.Validate(withJunk, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.3-trailing-data");
    }

    [Fact]
    public void Validate_CatalogRequirements_ReportsError()
    {
        // §6.11-1: the document catalog shall not contain the Requirements key.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Requirements [<< /Type /Requirement /S /EnableJavaScripts >>] >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.11-1-requirements");
    }

    [Fact]
    public void Validate_AlternatePresentations_ReportsError()
    {
        // §6.10-1: no AlternatePresentations entry in the document's name dictionary.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Names << /AlternatePresentations << >> >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.10-1-alternate-presentations");
    }

    [Fact]
    public void Validate_PagePresSteps_ReportsError()
    {
        // §6.10-2: no PresSteps entry in any page dictionary.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /PresSteps << /Type /NavNode >> >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.10-2-pres-steps");
    }

    [Fact]
    public void Validate_OptionalContentNoName_ReportsError()
    {
        // §6.9-1: each optional-content configuration dictionary needs a non-empty /Name.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] /D << >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /OCG /Name (Layer 1) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.9-1-config-name");
    }

    [Fact]
    public void Validate_OptionalContentDuplicateName_ReportsError()
    {
        // §6.9-2: configuration dictionaries' /Name values shall be unique.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] "
                + "/D << /Name (Default) >> /Configs [<< /Name (Default) >>] >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /OCG /Name (Layer 1) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.9-2-config-name-unique");
    }

    [Fact]
    public void Validate_OptionalContentOrderIncomplete_ReportsError()
    {
        // §6.9-3: a config /Order array shall reference every OCG (here OCG 5 0 R is omitted).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R 5 0 R] "
                + "/D << /Name (Default) /Order [4 0 R] >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /OCG /Name (Layer 1) >>"),
            new("<< /Type /OCG /Name (Layer 2) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.9-3-order-complete");
    }

    [Fact]
    public void Validate_OptionalContentOrderComplete_IsAllowed()
    {
        // §6.9-3: an /Order referencing every OCG is accepted — no false positive.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R 5 0 R] "
                + "/D << /Name (Default) /Order [4 0 R 5 0 R] >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /OCG /Name (Layer 1) >>"),
            new("<< /Type /OCG /Name (Layer 2) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId.StartsWith("ISO19005-2:6.9-3", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OptionalContentAutomaticState_ReportsError()
    {
        // §6.9-4: the AS key shall not appear in any configuration dictionary.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] "
                + "/D << /Name (Default) /AS [<< /Event /View /OCGs [] /Category [/View] >>] >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /OCG /Name (Layer 1) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.9-4-config-as");
    }

    [Fact]
    public void Validate_ValidOptionalContent_IsCompliant()
    {
        // §6.9: a named /D config with one OCG is valid — the no-false-positive guard.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] /D << /Name (Default) >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /OCG /Name (Layer 1) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId.StartsWith("ISO19005-2:6.9", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PermissionsBadKey_ReportsError()
    {
        // §6.1.12-1: a /Perms permissions dictionary may contain only /UR3 and /DocMDP.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Perms << /Foo << /Type /Sig >> >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.12-1-permissions");
    }

    [Fact]
    public void Validate_PermissionsDocMdp_IsAllowed()
    {
        // §6.1.12-1: /DocMDP is a permitted permissions-dictionary key — no false positive.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Perms << /DocMDP << /Type /Sig >> >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.12-1-permissions");
    }

    [Fact]
    public void Validate_EmbeddedFileMissingUf_ReportsError()
    {
        // §6.8-2: a file specification with /EF must contain both /F and /UF (here /UF is absent).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.bin) 4 0 R] >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /Filespec /F (a.bin) /EF << /F 5 0 R >> >>"),
            new("/Type /EmbeddedFile", Stream: Encoding.ASCII.GetBytes("data")),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.8-2-embedded-file-names");
    }

    [Fact]
    public void Validate_EfKeyOnNonFilespec_IsAllowed()
    {
        // §6.8-2 applies only to genuine file specifications. An /EF key on a page dictionary (not a
        // filespec) must not trip the rule — regression guard for the bare-/EF false positive.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /EF << /F 4 0 R >> >>"),
            new("/Type /EmbeddedFile", Stream: Encoding.ASCII.GetBytes("data")),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.8-2-embedded-file-names");
    }

    [Fact]
    public void Validate_UntypedFilespecInNameTreeMissingUf_ReportsError()
    {
        // §6.8-2: a filespec reached through the EmbeddedFiles name tree is identified even when it
        // omits /Type /Filespec; missing /UF must still be flagged.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.bin) 4 0 R] >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /F (a.bin) /EF << /F 5 0 R >> >>"), // no /Type, no /UF
            new("/Type /EmbeddedFile", Stream: Encoding.ASCII.GetBytes("data")),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.8-2-embedded-file-names");
    }

    [Fact]
    public void Validate_EmbeddedFileWithFAndUf_IsAllowed()
    {
        // §6.8-2: a file specification carrying both /F and /UF is accepted — no false positive.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.bin) 4 0 R] >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /Filespec /F (a.bin) /UF (a.bin) /EF << /F 5 0 R >> >>"),
            new("/Type /EmbeddedFile", Stream: Encoding.ASCII.GetBytes("data")),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.8-2-embedded-file-names");
    }

    [Fact]
    public void Validate_PdfXOutputIntentDestOutputProfileRef_ReportsError()
    {
        // §6.2.3-3: a GTS_PDFX output intent shall not carry /DestOutputProfileRef.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OutputIntents "
                + "[<< /Type /OutputIntent /S /GTS_PDFX /DestOutputProfileRef << >> >>] >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.3-3-dest-output-profile-ref");
    }

    [Fact]
    public void Validate_PdfXOutputIntentWithoutRef_IsAllowed()
    {
        // §6.2.3-3 applies only to /DestOutputProfileRef — a PDF/X intent without it is fine here.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX >>] >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.3-3-dest-output-profile-ref");
    }

    [Fact]
    public void Validate_HideAction_ReportsError()
    {
        // §6.5.1-1: /Hide is not among the permitted action types.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /Hide >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.5.1-action");
    }

    [Fact]
    public void Validate_UnknownActionType_ReportsError()
    {
        // §6.5.1-1: an unknown/vendor action type is rejected by the allow-list.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /VellumFoo >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.5.1-action");
    }

    [Fact]
    public void Validate_ActionWithoutS_ReportsError()
    {
        // §6.5.1-1: an action dictionary with no /S action-type key is not a permitted action.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction << /Type /Action >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.5.1-action");
    }

    [Fact]
    public void Validate_PermittedGoToAction_IsAllowed()
    {
        // §6.5.1-1: /GoTo is a permitted action type — no false positive from the allow-list.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /GoTo /D [3 0 R /Fit] >> >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.5.1-action");
    }

    [Fact]
    public void Validate_AnnotToggleNoView_ReportsError()
    {
        // §6.3.2-2: the ToggleNoView flag bit (256) shall be clear. F = Print(4) | ToggleNoView(256).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Link /Rect [10 10 50 50] /F 260 >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.Message.Contains("ToggleNoView", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AnnotAppearanceExtraKey_ReportsError()
    {
        // §6.3.3-2: an annotation's /AP appearance dictionary shall contain only /N (here it has /D too).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Text /Rect [10 10 50 50] /F 4 /Contents (n) /AP << /N 5 0 R /D 5 0 R >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Stream: []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.Message.Contains("/AP", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SymbolicTrueTypeNoSymbolCmap_ReportsError()
    {
        // §6.2.11.6-4: a symbolic TrueType program's cmap must be exactly one subtable or include the
        // Microsoft Symbol (3,0) encoding. DejaVu's cmap has 5 subtables and no (3,0).
        var bytes = Oracle.OracleCorpus.SimpleTrueTypeFont(_ => { }, flags: 4, encoding: null);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.6-symbolic-cmap");
    }

    [Fact]
    public void Validate_OddLengthHexString_ReportsError()
    {
        // §6.1.6-1: a hexadecimal string with an odd number of hex digits.
        var bytes = Oracle.OracleCorpus.AssembleClassicXref(injectOddHex: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.6-hex-string");
    }

    [Fact]
    public void Validate_WellFormedHexStrings_IsAllowed()
    {
        // §6.1.6: even-length, hex-only strings (the /ID) are accepted — no false positive, and the
        // XMP stream's markup is masked out.
        var bytes = Oracle.OracleCorpus.AssembleClassicXref();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.6-hex-string");
    }

    [Fact]
    public void Validate_ObjectBadSpacing_ReportsError()
    {
        // §6.1.9-1: the object and generation numbers separated by two spaces, not one.
        var bytes = Oracle.OracleCorpus.AssembleClassicXref(corruptObjSpacing: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.9-object-spacing");
    }

    [Fact]
    public void Validate_ObjectWellFormedSpacing_IsAllowed()
    {
        // §6.1.9-1: a well-laid-out classic-xref document is accepted — no false positive.
        var bytes = Oracle.OracleCorpus.AssembleClassicXref();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.9-object-spacing");
    }

    [Fact]
    public void Validate_StreamKeywordBadEol_ReportsError()
    {
        // §6.1.7.1-2: the stream keyword followed by a lone CR (not CRLF/LF).
        var bytes = Oracle.OracleCorpus.AssembleClassicXref(corruptStreamEol: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.1-stream-eol");
    }

    [Fact]
    public void Validate_EndstreamBadEol_ReportsError()
    {
        // §6.1.7.1-2: the endstream keyword preceded by a space (not an EOL marker).
        var bytes = Oracle.OracleCorpus.AssembleClassicXref(corruptEndstreamEol: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.1-stream-eol");
    }

    [Fact]
    public void Validate_StreamWellFormedEol_IsAllowed()
    {
        // §6.1.7.1-2: a stream with proper LF EOLs around the keywords is accepted — no false positive.
        var bytes = Oracle.OracleCorpus.AssembleClassicXref();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.1-stream-eol");
    }

    [Fact]
    public void Validate_XrefKeywordBadEol_ReportsError()
    {
        // §6.1.4-2: a space (not a single EOL) between the xref keyword and the subsection header.
        var bytes = Oracle.OracleCorpus.AssembleClassicXref(corruptXrefEol: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.4-xref-eol");
    }

    [Fact]
    public void Validate_ClassicXref_IsAllowed()
    {
        // §6.1.4-2: a well-formed classic xref (single EOL after the keyword) is accepted — no FP.
        var bytes = Oracle.OracleCorpus.AssembleClassicXref();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.4-xref-eol");
    }

    // ── §6.1.7.2-1 stream filter tests ────────────────────────────────────────

    [Fact]
    public void Validate_LzwDecodeFilter_ReportsError()
    {
        // §6.1.7.2-1: LZWDecode is not in the permitted filter list for PDF/A-2.
        // The stream is unreferenced; §6.1.7.2 applies to ALL streams regardless of reachability.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new("/Filter /LZWDecode", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
    }

    [Fact]
    public void Validate_UnknownFilter_ReportsError()
    {
        // §6.1.7.2-1: any filter name not in the allowlist (e.g. a vendor extension) is forbidden.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new("/Filter /VellumFooFilter", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
    }

    [Fact]
    public void Validate_AllowedFilter_IsNotFlagged()
    {
        // §6.1.7.2-1: FlateDecode is in the allowlist — must NOT be flagged (no false positive).
        // Uses a dummy body so /Length matches (the stream body is raw, zero bytes is fine for FlateDecode
        // when we pass an empty raw body; the rule reads the dictionary only).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new("/Filter /FlateDecode", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
    }

    [Fact]
    public void Validate_AllowedFilterArray_IsNotFlagged()
    {
        // §6.1.7.2-1: an array of two allowed filters must NOT be flagged.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new("/Filter [/ASCII85Decode /FlateDecode]", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
    }

    [Fact]
    public void Validate_CryptFilterWithIdentity_IsNotFlagged()
    {
        // §6.1.7.2-1: /Crypt is permitted when the matching /DecodeParms dict has /Name /Identity.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new("/Filter /Crypt /DecodeParms << /Name /Identity >>", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
    }

    [Fact]
    public void Validate_CryptFilterWithoutIdentity_ReportsError()
    {
        // §6.1.7.2-1: /Crypt without /Name /Identity in its /DecodeParms is not permitted.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new("/Filter /Crypt /DecodeParms << /Name /StdCF >>", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
    }

    [Fact]
    public void Validate_CryptFilterNoParms_ReportsError()
    {
        // §6.1.7.2-1: /Crypt with no /DecodeParms at all is not permitted.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            _pageObj,
            new("/Filter /Crypt", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.7.2-1-filter");
    }

    [Fact]
    public void Validate_IncompleteCidSet_ReportsError()
    {
        // §6.2.11.4.2-2: a subset CIDFontType2's /CIDSet must identify exactly the present CIDs.
        var bytes = Oracle.OracleCorpus.ByName("pdfa2b-cidset-incomplete").Bytes;

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.4.2-cidset");
    }

    [Fact]
    public void Validate_CompleteCidSet_IsAllowed()
    {
        // §6.2.11.4.2-2: a /CIDSet marking exactly CIDs 0..NumGlyphs-1 is accepted — no false positive.
        var bytes = Oracle.OracleCorpus.ByName("pdfa2b-cidset-complete").Bytes;

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.4.2-cidset");
    }

    [Fact]
    public void Validate_NonSymbolicTrueTypeSymbolOnlyCmap_ReportsError()
    {
        // §6.2.11.6-1: a non-symbolic TrueType whose embedded cmap is a single (3,0) symbol subtable
        // cannot serve glyph lookups.
        var bytes = Oracle.OracleCorpus.SimpleTrueTypeFontSymbolOnlyCmap();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.6-nonsymbolic-cmap");
    }

    [Fact]
    public void Validate_NonSymbolicTrueType_NoCmapFalsePositive()
    {
        // §6.2.11.6-4 must not fire on a non-symbolic font — the regression guard.
        var bytes = Oracle.OracleCorpus.SimpleTrueTypeFont(_ => { }, encoding: new VellumPdf.Core.PdfName("WinAnsiEncoding"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.6-symbolic-cmap");
    }

    [Fact]
    public void Validate_PopupAppearanceSubDictionary_ReportsError()
    {
        // §6.3.3-4: a /Popup with an /AP /N sub-dictionary is non-compliant (Popup has no kind exemption).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Popup /Rect [10 10 50 50] /AP << /N << /S 5 0 R >> >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Stream: []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.Message.Contains("appearance stream", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PopupWithoutPrintFlag_IsAllowed()
    {
        // §6.3.2: a /Popup is exempt from the flag requirements — a Popup with no /F (and a stream /AP)
        // must not be flagged for a missing Print flag. Guards the §6.3.3-4 fix against a regression.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Popup /Rect [10 10 50 50] /AP << /N 5 0 R >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Stream: []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.3-annotation");
    }

    [Fact]
    public void Validate_ButtonWidgetAppearanceStream_ReportsError()
    {
        // §6.3.3-3: a Widget /Btn field's /AP /N shall be an appearance sub-dictionary, not a stream.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Widget /FT /Btn /Rect [10 10 50 50] /F 4 /AP << /N 5 0 R >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Stream: []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.Message.Contains("sub-dictionary", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TextFieldAppearanceSubDictionary_ReportsError()
    {
        // §6.3.3-4: a non-button annotation's /AP /N shall be an appearance stream, not a sub-dictionary.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Widget /FT /Tx /Rect [10 10 50 50] /F 4 /AP << /N << /S 5 0 R >> >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Stream: []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.Message.Contains("appearance stream", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ButtonWidgetAppearanceSubDictionary_IsAllowed()
    {
        // §6.3.3-3: a Widget /Btn field with an /AP /N sub-dictionary is correct — no false positive.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new("<< /Type /Annot /Subtype /Widget /FT /Btn /Rect [10 10 50 50] /F 4 /AP << /N << /On 5 0 R /Off 5 0 R >> >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Stream: []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions,
            a => a.Message.Contains("sub-dictionary", StringComparison.Ordinal) || a.Message.Contains("appearance stream", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Pdf20Header_ReportsHeaderError()
    {
        // A plain document declares %PDF-2.0, which is not valid PDF/A-2 (§6.1.2).
        var result = PdfPreflight.Validate(BuildPlainPdf20(), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.2-file-header");
    }

    [Fact]
    public void Validate_MissingBinaryMarker_ReportsError()
    {
        var bytes = BuildMinimalPdf(binaryMarker: false, trailerId: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.1.2-binary-marker", assertion.RuleId);
        Assert.Equal(PreflightSeverity.Error, assertion.Severity);
    }

    [Fact]
    public void Validate_MissingTrailerId_ReportsError()
    {
        var bytes = BuildMinimalPdf(binaryMarker: true, trailerId: false);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.1.3-id-present", assertion.RuleId);
        Assert.Equal("ISO 19005-2:2011, 6.1.3", assertion.Clause);
    }

    [Fact]
    public void Validate_ValidHeaderMarkerAndId_NoFileStructureFindings()
    {
        var bytes = BuildMinimalPdf(binaryMarker: true, trailerId: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.2.3 / §6.2.4.3 output-intent rules ───────────────────────────────────

    [Fact]
    public void Validate_PdfAOutputIntent_MissingDestProfile_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.4.3-output-intent", assertion.RuleId);
        Assert.Contains("DestOutputProfile", assertion.Message);
    }

    [Fact]
    public void Validate_MissingDestProfile_NoDeviceColour_NoFinding()
    {
        // §6.2.4.3 scopes the output-intent requirement to documents that use device-dependent
        // colour. With no colour painted, a profile-less GTS_PDFA1 output intent is tolerated —
        // matching veraPDF (issue #128). The page here has no content stream.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OutputIntents [4 0 R] >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /OutputIntent /S /GTS_PDFA1 >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_DeviceColourWithoutOutputIntent_ReportsError()
    {
        // §6.2.4.3: a document that paints device-dependent colour shall have a PDF/A output intent.
        // The page fills a device-RGB rectangle but the catalog has no /OutputIntents. (#122)
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>"),
            new(string.Empty, Encoding.ASCII.GetBytes("1 0 0 rg 100 100 50 50 re f")),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.2.4.3-output-intent" && a.Message.Contains("device-dependent colour"));
    }

    [Fact]
    public void Validate_XfaInAcroForm_ReportsError()
    {
        // §6.4.2: the AcroForm shall not contain an /XFA entry — XFA forms are forbidden in PDF/A. (#122)
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /Fields [] /XFA (xfadata) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.2-xfa");
    }

    [Fact]
    public void Validate_CatalogNeedsRendering_ReportsError()
    {
        // §6.4.2: the catalog shall not contain the /NeedsRendering key.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /NeedsRendering true >>"),
            _pagesObj,
            _pageObj,
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4.2-needs-rendering", assertion.RuleId);
    }

    [Fact]
    public void Validate_AcroFormNeedAppearances_ReportsError()
    {
        // §6.4.1: the interactive form's /NeedAppearances shall be absent or false.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /Fields [] /NeedAppearances true >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4.1-need-appearances", assertion.RuleId);
    }

    [Theory]
    [InlineData("/A << /S /Named /N /NextPage >>")]
    [InlineData("/AA << /U << /S /Named /N /NextPage >> >>")]
    public void Validate_WidgetWithAction_ReportsError(string actionEntry)
    {
        // §6.4.1: a widget annotation shall not contain /A or /AA. The widget is otherwise conformant
        // (Print flag set, valid appearance), and the action type (Named) is itself permitted, so the
        // form-action rule is the only violation.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            new($"<< /Type /Annot /Subtype /Widget /Rect [0 0 1 1] /F 4 /AP << /N 5 0 R >> {actionEntry} >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4.1-widget-action", assertion.RuleId);
    }

    [Theory]
    [InlineData("/A << /S /Named /N /NextPage >>")]
    [InlineData("/AA << /U << /S /Named /N /NextPage >> >>")]
    public void Validate_FormFieldWithAction_ReportsError(string actionEntry)
    {
        // §6.4.1: a form field (in the AcroForm /Fields tree, here not a widget annotation) shall not
        // contain /A or /AA.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /Fields [5 0 R] >>"),
            new($"<< /FT /Btn /T (b) {actionEntry} >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4.1-field-action", assertion.RuleId);
    }

    [Fact]
    public void Validate_CompliantAcroForm_NoFinding()
    {
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /Fields [5 0 R] /NeedAppearances false >>"),
            new("<< /FT /Btn /T (b) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_MergedWidgetField_ReportsActionOnce()
    {
        // A combined field/widget (object 5, referenced from both the page /Annots and the AcroForm
        // /Fields) carrying an /A is a single object; it must yield exactly one finding, attributed to
        // the widget rule — matching veraPDF, which reports the merged object once.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>"),
            new("<< /Fields [5 0 R] >>"),
            // /AP /N is a sub-dictionary (correct for a /Btn widget per §6.3.3-3) so this fixture
            // isolates the action-dedup concern; the /A is the single intended violation.
            new("<< /Type /Annot /Subtype /Widget /FT /Btn /T (b) /Rect [0 0 1 1] /F 4 "
                + "/AP << /N << /On 6 0 R /Off 6 0 R >> >> /A << /S /Named /N /NextPage >> >>"),
            new("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", []),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4.1-widget-action", assertion.RuleId);
    }

    [Fact]
    public void Validate_PdfAOutputIntent_IndirectSubtype_StillEnforced()
    {
        // /S is an indirect reference to the name GTS_PDFA1 (legal per ISO 32000-1 §7.3.10). The rule
        // must resolve it before deciding the intent is a PDF/A output intent — otherwise the
        // mandatory DestOutputProfile check is silently skipped (a false negative). Round-5 guard.
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S 5 0 R >>"),
            new PdfObj("/GTS_PDFA1"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.4.3-output-intent", assertion.RuleId);
        Assert.Contains("DestOutputProfile", assertion.Message);
    }

    [Fact]
    public void Validate_OutputIntent_InvalidIccProfile_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3", Encoding.ASCII.GetBytes("this is not an ICC profile")));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.3-output-intent", assertion.RuleId);
        Assert.Contains("acsp", assertion.Message);
    }

    [Fact]
    public void Validate_OutputIntent_BadComponentCount_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 2", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.3-output-intent", assertion.RuleId);
        Assert.Contains("/N", assertion.Message);
    }

    [Fact]
    public void Validate_OutputIntents_DifferentProfiles_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R 6 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3", FakeIccProfile()),
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFX /DestOutputProfile 7 0 R >>"),
            new PdfObj("/N 3", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.2.3-output-intent" && a.Message.Contains("same ICC profile"));
    }

    [Fact]
    public void Validate_ValidOutputIntent_NoFindings()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.4 transparency (blend mode) rules ────────────────────────────────────

    [Theory]
    [InlineData("/Normal")]
    [InlineData("/Multiply")]
    [InlineData("/Luminosity")]
    [InlineData("[/Multiply /Screen]")]
    public void Validate_StandardBlendMode_NoFindings(string bm)
    {
        var result = PdfPreflight.Validate(BuildBlendModePdf(bm), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_NonStandardBlendMode_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildBlendModePdf("/FooBar"), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.10-blend-mode", assertion.RuleId);
        Assert.Contains("/FooBar", assertion.Message);
    }

    [Fact]
    public void Validate_BlendModeArrayWithInvalidEntry_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildBlendModePdf("[/Multiply /Bogus]"), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.10-blend-mode", assertion.RuleId);
        Assert.Contains("/Bogus", assertion.Message);
    }

    [Fact]
    public void Validate_UnusedNonStandardBlendMode_NoFinding()
    {
        // A non-standard /BM in an /ExtGState the content never applies (no `gs`) is not the current
        // blend mode, so it is not a §6.4 violation — matching veraPDF (issue #127). The page here has
        // no /Contents, so the graphics state is never applied.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R >>"),
            new("<< /ExtGState 5 0 R >>"),
            new("<< /GS0 6 0 R >>"),
            new("<< /Type /ExtGState /BM /BogusMode >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.2.5 graphics-state / §6.2.6 rendering-intent rules ───────────────────

    [Theory]
    [InlineData("/TR /Identity", "ISO19005-2:6.2.5-transfer-function")]
    [InlineData("/TR2 /Identity", "ISO19005-2:6.2.5-transfer-function-2")]
    [InlineData("/HTP []", "ISO19005-2:6.2.5-halftone-phase")]
    [InlineData("/RI /FooIntent", "ISO19005-2:6.2.6-rendering-intent")]
    public void Validate_AppliedExtGStateWithForbiddenEntry_ReportsError(string gsEntry, string ruleId)
    {
        var result = PdfPreflight.Validate(BuildExtGStatePdf(gsEntry), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal(ruleId, assertion.RuleId);
    }

    [Fact]
    public void Validate_AppliedHalftoneBadType_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildExtGStatePdf("/HT << /Type /Halftone /HalftoneType 6 >>"), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.5-halftone-type");
    }

    [Fact]
    public void Validate_AppliedHalftoneWithName_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildExtGStatePdf("/HT << /Type /Halftone /HalftoneType 1 /HalftoneName (X) >>"), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.5-halftone-name");
    }

    [Fact]
    public void Validate_Type1HalftoneWithTransferFunction_ReportsError()
    {
        // §6.2.5-6: a type-1 halftone (colorantName == null) must NOT carry a /TransferFunction.
        var result = PdfPreflight.Validate(
            BuildExtGStatePdf("/HT << /Type /Halftone /HalftoneType 1 /TransferFunction /Identity >>"),
            PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.5-halftone-transfer");
    }

    [Fact]
    public void Validate_Type5HalftoneWithProcessColorantTransferFunction_ReportsError()
    {
        // §6.2.5-6: in a type-5 halftone, a process colorant entry (e.g. /Cyan) must NOT have a /TransferFunction.
        var result = PdfPreflight.Validate(
            BuildExtGStatePdf(
                "/HT << /HalftoneType 5"
                + " /Default << /HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /SimpleDot >>"
                + " /Cyan << /HalftoneType 1 /Frequency 60 /Angle 15 /SpotFunction /SimpleDot /TransferFunction /Identity >> >>"),
            PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.5-halftone-transfer");
    }

    [Fact]
    public void Validate_Type5HalftoneCompliant_NoFinding()
    {
        // §6.2.5-6: type-5 with /Default (exempt) and /Cyan without TransferFunction → compliant.
        var result = PdfPreflight.Validate(
            BuildExtGStatePdf(
                "/HT << /HalftoneType 5"
                + " /Default << /HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /SimpleDot >>"
                + " /Cyan << /HalftoneType 1 /Frequency 60 /Angle 15 /SpotFunction /SimpleDot >> >>"),
            PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_Type1HalftoneWithoutTransferFunction_NoFinding()
    {
        // §6.2.5-6: a type-1 halftone without a /TransferFunction is compliant.
        var result = PdfPreflight.Validate(
            BuildExtGStatePdf("/HT << /Type /Halftone /HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /SimpleDot >>"),
            PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Theory]
    [InlineData("/TR2 /Default")]
    [InlineData("/RI /RelativeColorimetric")]
    public void Validate_AppliedExtGStatePermittedEntry_NoFinding(string gsEntry)
    {
        var result = PdfPreflight.Validate(BuildExtGStatePdf(gsEntry), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_UnappliedExtGStateTransferFunction_NoFinding()
    {
        // §6.2.5 governs the current graphics state. A /TR in an /ExtGState the page never applies
        // (no `gs`) is not a violation — matching veraPDF (issue #127).
        var result = PdfPreflight.Validate(BuildExtGStatePdf("/TR /Identity", apply: false), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_NonStandardRenderingIntentOperator_ReportsError()
    {
        // §6.2.6: a rendering intent set by the `ri` operator shall be one of the four standard ones.
        var result = PdfPreflight.Validate(BuildContentStreamPdf("/FooIntent ri"), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.6-rendering-intent", assertion.RuleId);
    }

    [Fact]
    public void Validate_StandardRenderingIntentOperator_NoFinding()
    {
        var result = PdfPreflight.Validate(BuildContentStreamPdf("/Perceptual ri"), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.1.13-8 q/Q nesting rules ──────────────────────────────────────────────

    [Fact]
    public void Validate_QNestingExceeds28_ReportsError()
    {
        // 29 nested `q` operators pushes depth to 29, which exceeds the allowed 28 (§6.1.13-8).
        var content = string.Concat(Enumerable.Repeat("q ", 29)) + string.Concat(Enumerable.Repeat("Q ", 29));
        var result = PdfPreflight.Validate(BuildContentStreamPdf(content), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-8-q-nesting");
    }

    [Fact]
    public void Validate_QNestingExactly28_NoFinding()
    {
        // Exactly 28 nested `q` operators reaches depth 28, which is the allowed maximum.
        var content = string.Concat(Enumerable.Repeat("q ", 28)) + string.Concat(Enumerable.Repeat("Q ", 28));
        var result = PdfPreflight.Validate(BuildContentStreamPdf(content), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-8-q-nesting");
    }

    [Fact]
    public void Validate_QNestingRepeatedShallow_NoFinding()
    {
        // Repeated q/Q pairs that open and close — depth 2 at most — must not be flagged.
        // Proves we track running depth, not a cumulative count of q tokens.
        var pair = "q q Q Q ";
        var content = string.Concat(Enumerable.Repeat(pair, 20));
        var result = PdfPreflight.Validate(BuildContentStreamPdf(content), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-8-q-nesting");
    }

    // ── §6.2.8 / §6.2.9 image and XObject rules ─────────────────────────────────

    [Fact]
    public void Validate_DrawnPostScriptXObject_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildXObjectPdf("/Type /XObject /Subtype /PS", [], draw: true),
            PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.9-postscript-xobject", assertion.RuleId);
        Assert.Equal("ISO 19005-2:2011, 6.2.9", assertion.Clause);
        Assert.Equal(PreflightSeverity.Error, assertion.Severity);
    }

    [Fact]
    public void Validate_DrawnImageWithInterpolate_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildXObjectPdf($"{_imageDict} /Interpolate true", [0], draw: true),
            PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.8-image-interpolate", assertion.RuleId);
        Assert.Equal("ISO 19005-2:2011, 6.2.8", assertion.Clause);
        Assert.Contains("Interpolate", assertion.Message);
    }

    [Theory]
    [InlineData("/Type /XObject /Subtype /PS", new byte[0])]
    [InlineData(
        "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Interpolate true",
        new byte[] { 0 })]
    public void Validate_ForbiddenXObjectPresentButNotDrawn_NoFinding(string xobjectDict, byte[] body)
    {
        // §6.2.8/§6.2.9 constrain XObjects that are painted. An XObject present in /Resources but
        // never invoked by a `Do` operator is not rendered, so it is not a violation — matching
        // veraPDF (the page here has no /Contents). See issues #127, #128.
        var result = PdfPreflight.Validate(
            BuildXObjectPdf(xobjectDict, body, draw: false),
            PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Theory]
    [InlineData("/Interpolate false")]
    [InlineData("")]
    public void Validate_DrawnImageWithoutInterpolation_NoFinding(string interpolate)
    {
        var result = PdfPreflight.Validate(
            BuildXObjectPdf($"{_imageDict} {interpolate}".Trim(), [0], draw: true),
            PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // Forbidden keys on a drawn Image (§6.2.8) and form (§6.2.9) XObject, each cross-validated
    // against veraPDF: image /OPI → 6.2.8-2, /Alternates → 6.2.8-1, bad BPC → 6.2.8-4; form /OPI,
    // /PS, /Subtype2 /PS → 6.2.9-1; reference XObject (/Ref) → 6.2.9-2.
    [Theory]
    [InlineData(_imageDict + " /OPI << >>", new byte[] { 0 }, "ISO19005-2:6.2.8-image-opi")]
    [InlineData(_imageDict + " /Alternates []", new byte[] { 0 }, "ISO19005-2:6.2.8-image-alternates")]
    [InlineData(
        "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 3 /ColorSpace /DeviceGray",
        new byte[] { 0 }, "ISO19005-2:6.2.8-image-bitspercomponent")]
    [InlineData(_formDict + " /OPI << >>", new byte[0], "ISO19005-2:6.2.9-form-opi")]
    [InlineData(_formDict + " /PS (x)", new byte[0], "ISO19005-2:6.2.9-form-ps")]
    [InlineData(_formDict + " /Subtype2 /PS", new byte[0], "ISO19005-2:6.2.9-form-subtype2-ps")]
    [InlineData(_formDict + " /Ref << >>", new byte[0], "ISO19005-2:6.2.9-reference-xobject")]
    public void Validate_DrawnXObjectWithForbiddenKey_ReportsError(string xobjectDict, byte[] body, string ruleId)
    {
        var result = PdfPreflight.Validate(BuildXObjectPdf(xobjectDict, body, draw: true), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal(ruleId, assertion.RuleId);
        Assert.Equal(PreflightSeverity.Error, assertion.Severity);
    }

    [Theory]
    [InlineData(_imageDict + " /OPI << >>", new byte[] { 0 })]
    [InlineData(_formDict + " /Ref << >>", new byte[0])]
    public void Validate_ForbiddenKeyXObjectPresentButNotDrawn_NoFinding(string xobjectDict, byte[] body)
    {
        // Like the PS/Interpolate cases, the forbidden-key XObjects are only a violation when drawn;
        // veraPDF passes a reference XObject (or OPI image) that is present but never invoked. (#127/#128)
        var result = PdfPreflight.Validate(BuildXObjectPdf(xobjectDict, body, draw: false), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_DrawnValidFormXObject_NoFinding()
    {
        var result = PdfPreflight.Validate(BuildXObjectPdf(_formDict, [], draw: true), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.3 font embedding rules ───────────────────────────────────────────────

    [Fact]
    public void Validate_UnembeddedStandard14Font_ReportsError()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
        Assert.Contains("Helvetica", assertion.Message);
    }

    [Fact]
    public void Validate_InvalidFontSubtype_ReportsError()
    {
        // §6.2.11.2: a font dictionary's /Subtype must be one of the recognised font subtypes.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /BogusType /BaseFont /Foo /FirstChar 0 /LastChar 0 "
                + "/Widths [500] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.2-font-subtype");
    }

    [Fact]
    public void Validate_FontMissingType_ReportsError()
    {
        // §6.2.11.2-1: a font dictionary must carry /Type /Font.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Subtype /Type1 /BaseFont /Foo /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile3 8 0 R >>"),
            new PdfObj("/Subtype /Type1C", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.2-type");
    }

    [Fact]
    public void Validate_NonType3FontMissingBaseFont_ReportsError()
    {
        // §6.2.11.2-3: every font other than a Type 3 font must carry a /BaseFont name.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /TrueType /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /Encoding /WinAnsiEncoding /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /Flags 32 /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.2-basefont");
    }

    [Fact]
    public void Validate_FontFile3WithDisallowedSubtype_ReportsError()
    {
        // §6.2.11.2-7: an embedded FontFile3 program's /Subtype must be Type1C, CIDFontType0C, or
        // OpenType (here /Type2, which is not permitted).
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /Foo /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile3 8 0 R >>"),
            new PdfObj("/Subtype /Type2", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.2-fontfile3-subtype");
    }

    [Fact]
    public void Validate_FontFile3WithOpenTypeSubtype_IsNotFlagged()
    {
        // §6.2.11.2-7 no-false-positive: /Subtype /OpenType is one of the permitted values.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /Foo /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile3 8 0 R >>"),
            new PdfObj("/Subtype /OpenType", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(
            result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.2-fontfile3-subtype");
    }

    [Fact]
    public void Validate_Type0NonPredefinedCMapName_ReportsError()
    {
        // §6.2.11.3.3-1: a composite font's /Encoding must name a predefined CMap or be embedded.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /FooBarCMap "
                + "/DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /FontDescriptor 8 0 R "
                + "/CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-cmap-name");
    }

    [Fact]
    public void Validate_Type0IdentityHEncoding_IsNotFlagged()
    {
        // §6.2.11.3.3-1 no-false-positive: Identity-H is a predefined CMap.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H "
                + "/DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /FontDescriptor 8 0 R "
                + "/CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-cmap-name");
    }

    [Fact]
    public void Validate_Type1SubsetIncompleteCharSet_ReportsError()
    {
        // §6.2.11.4.2-1: a subset Type 1 font's /CharSet must list every glyph in the program.
        var (fontFile, len1, len2, len3) = Type1FontAsset.ToFontFile();
        var names = Type1Glyphs.TryEnumerate(fontFile, len1)!.Where(n => n != "u1047F"); // omit one glyph
        var charSet = string.Concat(names.Select(n => "/" + n));
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /AAAAAA+NotoSansShavian "
                + "/FirstChar 0 /LastChar 0 /Widths [0] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /AAAAAA+NotoSansShavian /Flags 4 "
                + "/FontBBox [0 -502 1396 1600] /ItalicAngle 0 /Ascent 1600 /Descent -502 /CapHeight 1600 "
                + $"/StemV 80 /CharSet ({charSet}) /FontFile 8 0 R >>"),
            new PdfObj($"/Length1 {len1} /Length2 {len2} /Length3 {len3}", fontFile));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.4.2-charset");
    }

    [Fact]
    public void Validate_Type1NonSubsetIncompleteCharSet_IsNotFlagged()
    {
        // §6.2.11.4.2-1 applies only to subset fonts; a non-subset /BaseFont is exempt even with a
        // wildly incomplete /CharSet.
        var (fontFile, len1, len2, len3) = Type1FontAsset.ToFontFile();
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /NotoSansShavian "
                + "/FirstChar 0 /LastChar 0 /Widths [0] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /NotoSansShavian /Flags 4 "
                + "/FontBBox [0 -502 1396 1600] /ItalicAngle 0 /Ascent 1600 /Descent -502 /CapHeight 1600 "
                + "/StemV 80 /CharSet (/.notdef) /FontFile 8 0 R >>"),
            new PdfObj($"/Length1 {len1} /Length2 {len2} /Length3 {len3}", fontFile));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.4.2-charset");
    }

    [Fact]
    public void Validate_SimpleFontWidthsMismatch_ReportsError()
    {
        // §6.2.11.2: a non-standard simple font's /Widths length must equal LastChar−FirstChar+1.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /Foo /FirstChar 65 /LastChar 67 "
                + "/Widths [600] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile3 8 0 R >>"),
            new PdfObj("/Subtype /Type1C", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.2-widths");
    }

    [Fact]
    public void Validate_NonSymbolicTrueTypeWithoutWinAnsi_ReportsError()
    {
        // §6.2.11.6: a non-symbolic TrueType font's /Encoding must be MacRoman or WinAnsi (here absent).
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /Flags 32 /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.6-nonsymbolic-encoding");
    }

    [Fact]
    public void Validate_SymbolicTrueTypeWithEncoding_ReportsError()
    {
        // §6.2.11.6: a symbolic TrueType font shall not carry an /Encoding entry.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /Encoding /WinAnsiEncoding /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /Flags 4 /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.6-symbolic-encoding");
    }

    [Fact]
    public void Validate_EmbeddedCidFontType2WithoutCidToGidMap_ReportsError()
    {
        // §6.2.11.3.2: an embedded CIDFontType2 shall have a /CIDToGIDMap (here omitted).
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /FontDescriptor 8 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.2-cidtogidmap");
    }

    [Fact]
    public void Validate_EmbeddedTrueTypeFont_NoFindings()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Arial /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /Encoding /WinAnsiEncoding /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 32 /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_UnembeddedType0Font_ReportsError()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /XYZ+Sub /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /XYZ+Sub /FontDescriptor 8 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /XYZ+Sub >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
    }

    [Fact]
    public void Validate_EmbeddedType0Font_NoFindings()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /XYZ+Sub /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /XYZ+Sub /FontDescriptor 8 0 R /CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /XYZ+Sub /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_Type3Font_IsExemptFromEmbedding()
    {
        // Type3 glyphs are content streams — no font program is expected.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /CharProcs 7 0 R >>"),
            new PdfObj("<< >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.6.4 XMP metadata rules ───────────────────────────────────────────────

    [Fact]
    public void Validate_MissingXmpMetadata_ReportsError()
    {
        var bytes = AssemblePdf([new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj], metadata: false);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.6.4-pdfaid", assertion.RuleId);
    }

    [Fact]
    public void Validate_XmpConformanceMismatch_ReportsError()
    {
        // The XMP claims conformance U but it is being validated as PDF/A-2b.
        var bytes = AssemblePdf(
            [new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj],
            xmpConformance: "U");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.6.4-pdfaid", assertion.RuleId);
        Assert.Contains("conformance", assertion.Message);
    }

    // ── §6.6.2.1 XMP packet serialisation rules ─────────────────────────────────

    [Theory]
    [InlineData(" bytes=\"100\"", "ISO19005-2:6.6.2.1-xmp-bytes")]
    [InlineData(" encoding=\"UTF-8\"", "ISO19005-2:6.6.2.1-xmp-encoding")]
    public void Validate_XmpHeaderForbiddenAttribute_ReportsError(string headerExtra, string ruleId)
    {
        var result = PdfPreflight.Validate(BuildXmpPdf(XmpWithHeader(headerExtra)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal(ruleId, assertion.RuleId);
    }

    [Fact]
    public void Validate_Utf8XmpPacketWithBom_NoFinding()
    {
        // A UTF-8 BOM is still UTF-8 — the encoding rule must not flag it, and the header has no
        // bytes/encoding pseudo-attribute.
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithHeader(encoding: Encoding.UTF8, bom: true)), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.6.2.3.3 extension-schema structure rules ─────────────────────────────

    [Fact]
    public void Validate_ValidExtensionSchema_NoFinding()
    {
        // A well-formed PDF/A extension schema must not be flagged (no false positive).
        var result = PdfPreflight.Validate(
            BuildXmpPdf(ExtensionSchemaXmp(_validSchemaFields, _validPropertyFields)), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Theory]
    [InlineData(
        "<pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI><pdfaSchema:prefix>ex</pdfaSchema:prefix>",
        "ISO19005-2:6.6.2.3.3-schema")]
    [InlineData(
        "<pdfaSchema:schema>S</pdfaSchema:schema><pdfaSchema:prefix>ex</pdfaSchema:prefix>",
        "ISO19005-2:6.6.2.3.3-namespaceuri")]
    public void Validate_ExtensionSchemaMissingSchemaField_ReportsError(string schemaFields, string ruleId)
    {
        var result = PdfPreflight.Validate(
            BuildXmpPdf(ExtensionSchemaXmp(schemaFields, _validPropertyFields)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == ruleId);
    }

    [Theory]
    [InlineData(
        "<pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>",
        "ISO19005-2:6.6.2.3.3-name")]
    [InlineData(
        "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>d</pdfaProperty:description>",
        "ISO19005-2:6.6.2.3.3-valuetype")]
    [InlineData(
        "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category>",
        "ISO19005-2:6.6.2.3.3-description")]
    public void Validate_ExtensionSchemaPropertyMissingField_ReportsError(string propertyFields, string ruleId)
    {
        var result = PdfPreflight.Validate(
            BuildXmpPdf(ExtensionSchemaXmp(_validSchemaFields, propertyFields)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == ruleId);
    }

    [Theory]
    [InlineData("<pdfaProperty:category>bogus</pdfaProperty:category>")]
    [InlineData("")]
    public void Validate_ExtensionSchemaPropertyBadCategory_ReportsError(string categoryField)
    {
        var propertyFields =
            "<pdfaProperty:name>foo</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>"
            + categoryField + "<pdfaProperty:description>d</pdfaProperty:description>";

        var result = PdfPreflight.Validate(
            BuildXmpPdf(ExtensionSchemaXmp(_validSchemaFields, propertyFields)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.3-property-category");
    }

    // ── §6.6.4 pdfaid prefix + §6.6.2.3.3 value-type rules ──────────────────────

    [Fact]
    public void Validate_CanonicalPdfaidPrefix_NoFinding()
    {
        // The control for the alternate-prefix test: the canonical 'pdfaid' prefix is accepted.
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithDescriptions(string.Empty)), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_ValidExtensionValueType_NoFinding()
    {
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithDescriptions(ValueTypeSchema(_validValueTypeFields, _validValueTypeField))),
            PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Theory]
    [InlineData(
        "<pdfaType:namespaceURI>http://example.com/t/</pdfaType:namespaceURI><pdfaType:prefix>mt</pdfaType:prefix><pdfaType:description>d</pdfaType:description>",
        _validValueTypeField, "ISO19005-2:6.6.2.3.3-type")]
    [InlineData(
        _validValueTypeFields,
        "<pdfaField:valueType>Text</pdfaField:valueType><pdfaField:description>d</pdfaField:description>",
        "ISO19005-2:6.6.2.3.3-name")]
    public void Validate_ExtensionValueTypeMissingField_ReportsError(string typeFields, string fieldFields, string ruleId)
    {
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithDescriptions(ValueTypeSchema(typeFields, fieldFields))), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == ruleId);
    }

    // ── §6.6.2.3.1 XMP property-provenance rules ────────────────────────────────

    [Fact]
    public void Validate_UndeclaredXmpProperty_ReportsError()
    {
        // §6.6.2.3.1: a property in a non-predefined, undeclared namespace is not permitted.
        var result = PdfPreflight.Validate(BuildXmpPdf(XmpWithDescriptions(_customProperty)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.6.2.3.1-undeclared-property", assertion.RuleId);
    }

    [Fact]
    public void Validate_DeclaredCustomXmpProperty_NoFinding()
    {
        // The same custom property is permitted once its namespace is declared by an extension schema.
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithDescriptions(_customProperty + _exampleExtensionSchema)), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_PredefinedXmpSchemas_NoFinding()
    {
        // A property drawn from any predefined XMP schema (here Dublin Core, XMP Basic, Adobe PDF,
        // TIFF, EXIF, and an xmpMM struct value) must not be flagged — the false-positive guard.
        const string rich =
            "<rdf:Description rdf:about=\"\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
            + "xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" "
            + "xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" "
            + "xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\" "
            + "xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" "
            + "xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\" "
            + "xmlns:stRef=\"http://ns.adobe.com/xap/1.0/sType/ResourceRef#\" "
            + "pdf:Producer=\"P\" xmp:CreatorTool=\"T\" tiff:Make=\"M\">"
            + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Title</rdf:li></rdf:Alt></dc:title>"
            + "<exif:ExifVersion>0230</exif:ExifVersion>"
            + "<xmpMM:DerivedFrom rdf:parseType=\"Resource\"><stRef:documentID>d</stRef:documentID></xmpMM:DerivedFrom>"
            + "</rdf:Description>";

        var result = PdfPreflight.Validate(BuildXmpPdf(XmpWithDescriptions(rich)), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_UndeclaredPdfUaIdInPdfA_ReportsError()
    {
        // pdfuaid is NOT a predefined schema in PDF/A-2: a PDF/A document using it must declare it via
        // an extension schema (matching veraPDF).
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithDescriptions(
                "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">"
                + "<pdfuaid:part>1</pdfuaid:part></rdf:Description>")),
            PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-undeclared-property");
    }

    // ── §6.3 annotation rules ─────────────────────────────────────────────────

    [Fact]
    public void Validate_AnnotationMissingPrintFlagAndAppearance_ReportsErrors()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /F 2 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        // /F 2 sets Hidden and leaves Print clear; the /Stamp has no /AP — exactly three findings.
        Assert.False(result.IsCompliant);
        Assert.Equal(3, result.Assertions.Count);
        Assert.All(result.Assertions, a => Assert.Equal("ISO19005-2:6.3-annotation", a.RuleId));
        Assert.Contains(result.Assertions, a => a.Message.Contains("Print"));
        Assert.Contains(result.Assertions, a => a.Message.Contains("Hidden"));
        Assert.Contains(result.Assertions, a => a.Message.Contains("appearance"));
    }

    [Fact]
    public void Validate_WellFormedAnnotation_NoFindings()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /F 4 /AP << /N 5 0 R >> >>"),
            new PdfObj("/Subtype /Form /BBox [0 0 1 1]", []));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_AnnotationWithInvisibleFlag_ReportsError()
    {
        // §6.3.2 requires the Invisible flag (ISO 32000-1 Table 165, bit 1) clear, alongside Hidden
        // and NoView. /F 5 = Print(4) | Invisible(1): Print is set and an appearance is present, so
        // the only violation is the Invisible bit. Round-5 follow-up (issue #124).
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /F 5 /AP << /N 5 0 R >> >>"),
            new PdfObj("/Subtype /Form /BBox [0 0 1 1]", []));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3-annotation", assertion.RuleId);
        Assert.Contains("Invisible", assertion.Message);
    }

    [Fact]
    public void Validate_LinkAnnotation_ExemptFromAppearance()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_LinkAnnotationWithoutPrintFlag_ReportsError()
    {
        // A /Link with no /F (flags 0, Print clear) must be flagged: §6.3.2's Print-flag requirement
        // is NOT relaxed for Link annotations. veraPDF agrees — see the pdfa2b-link-no-print oracle
        // fixture, which confirmed a Link without Print is non-compliant. (Link is exempt only from
        // the appearance-stream requirement, covered by Validate_LinkAnnotation_ExemptFromAppearance.)
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.3-annotation" && a.Message.Contains("Print"));
    }

    // ── §6.5.1 action rules ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_JavaScriptOpenAction_ReportsError()
    {
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /S /JavaScript /JS (noop) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.5.1-action", assertion.RuleId);
        Assert.Contains("/JavaScript", assertion.Message);
    }

    [Theory]
    [InlineData("SetState")]
    [InlineData("NoOp")]
    public void Validate_ForbiddenActionType_ReportsError(string actionType)
    {
        // ISO 19005-2 §6.5.1 also forbids the (deprecated) SetState and NoOp action types.
        // Regression guard for review round 4.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new($"<< /S /{actionType} >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.5.1-action", assertion.RuleId);
        Assert.Contains($"/{actionType}", assertion.Message);
    }

    [Fact]
    public void Validate_LaunchActionOnAnnotation_ReportsError()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 /A 5 0 R >>"),
            new PdfObj("<< /S /Launch >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.5.1-action", assertion.RuleId);
        Assert.Contains("/Launch", assertion.Message);
    }

    [Fact]
    public void Validate_PermittedGoToAction_NoFindings()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 /A 5 0 R >>"),
            new PdfObj("<< /S /GoTo /D [3 0 R /Fit] >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_DisallowedNamedAction_ReportsError()
    {
        // §6.5.1: only NextPage/PrevPage/FirstPage/LastPage named actions are permitted.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /S /Named /N /GoForward >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.5.1-named-action", assertion.RuleId);
    }

    [Fact]
    public void Validate_PermittedNamedAction_NoFinding()
    {
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /S /Named /N /NextPage >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_CatalogAdditionalActions_ReportsError()
    {
        // §6.5.2: the catalog shall not contain an /AA additional-actions dictionary.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /AA 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /WC << /S /Named /N /NextPage >> >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.5.2-catalog-aa", assertion.RuleId);
    }

    [Fact]
    public void Validate_PageAdditionalActions_ReportsError()
    {
        // §6.5.2: a page dictionary shall not contain an /AA additional-actions dictionary.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /AA 4 0 R >>"),
            new("<< /O << /S /Named /N /NextPage >> >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.5.2-page-aa", assertion.RuleId);
    }

    // ── PDF/A-2u §6.2.11.7 ToUnicode ────────────────────────────────────────────

    [Fact]
    public void Validate2u_IdentityType0FontWithoutToUnicode_ReportsError()
    {
        var bytes = BuildType0FontPdf(withToUnicode: false, xmpConformance: "U");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2U);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.11.7.2-tounicode", assertion.RuleId);
    }

    [Fact]
    public void Validate2u_IdentityType0FontWithToUnicode_NoFindings()
    {
        var bytes = BuildType0FontPdf(withToUnicode: true, xmpConformance: "U");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2U);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate2b_IdentityType0FontWithoutToUnicode_IsAllowed()
    {
        // ToUnicode is a 2u requirement; the same font is acceptable at level B.
        var bytes = BuildType0FontPdf(withToUnicode: false, xmpConformance: "B");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── PDF/A-2a §6.8 logical structure ─────────────────────────────────────────

    [Fact]
    public void Validate2a_TaggedDocument_NoFindings()
    {
        var bytes = Build2aPdf(
            "/MarkInfo << /Marked true >> /StructTreeRoot 4 0 R",
            new PdfObj("<< /Type /StructTreeRoot >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate2a_MissingStructTreeRoot_ReportsError()
    {
        var bytes = Build2aPdf("/MarkInfo << /Marked true >>");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.8-logical-structure", assertion.RuleId);
        Assert.Contains("StructTreeRoot", assertion.Message);
    }

    [Fact]
    public void Validate2a_NotMarked_ReportsError()
    {
        var bytes = Build2aPdf(
            "/MarkInfo << /Marked false >> /StructTreeRoot 4 0 R",
            new PdfObj("<< /Type /StructTreeRoot >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.8-logical-structure", assertion.RuleId);
        Assert.Contains("Marked", assertion.Message);
    }

    [Fact]
    public void Validate2a_CircularRoleMap_ReportsError()
    {
        var bytes = Build2aPdf(
            "/MarkInfo << /Marked true >> /StructTreeRoot 4 0 R",
            new PdfObj("<< /Type /StructTreeRoot /RoleMap << /Foo /Bar /Bar /Foo >> >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.8-logical-structure", assertion.RuleId);
        Assert.Contains("circular", assertion.Message);
    }

    // ── PDF/UA-1 (ISO 14289-1) ──────────────────────────────────────────────────

    [Fact]
    public void ValidateUa_CompliantDocument_NoFindings()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(), PdfConformance.PdfUA1);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void ValidateUa_MissingLang_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(lang: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.2-lang", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_NotMarked_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(marked: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-tagged", assertion.RuleId);
        Assert.Contains("Marked", assertion.Message);
    }

    [Fact]
    public void ValidateUa_MissingStructTreeRoot_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(structTreeRoot: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-tagged", assertion.RuleId);
        Assert.Contains("StructTreeRoot", assertion.Message);
    }

    [Fact]
    public void ValidateUa_DisplayDocTitleFalse_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(displayDocTitle: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-title", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_MissingDcTitle_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildUaPdf(xmpOverride: UaXmpBytes(withTitle: false)), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-title", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_WrongPdfuaidPart_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildUaPdf(xmpOverride: UaXmpBytes(part: "2")), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:5-pdfuaid", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_AnnotatedPageWithoutTabsS_ReportsError()
    {
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 4 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                _pagesObj,
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>"),
                new("<< /Type /StructTreeRoot >>"),
                new("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 >>"),
            ],
            metadataOverride: UaXmpBytes());

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.18.3-tabs", assertion.RuleId);
    }

    [Fact]
    public void Validate_ForbiddenAnnotationSubtype_ReportsError()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Movie /Rect [0 0 1 1] /F 4 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3-annotation", assertion.RuleId);
        Assert.Contains("/Movie", assertion.Message);
    }

    [Fact]
    public void Validate_JavaScriptInAdditionalActions_ReportsError()
    {
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /AA << /O 4 0 R >> >>"),
            new("<< /S /JavaScript /JS (noop) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.5.1-action" && a.Message.Contains("/JavaScript"));
    }

    [Fact]
    public void Validate_FontWithWrongFontFileVariant_ReportsError()
    {
        // A Type1 font embedded with /FontFile2 (a TrueType program) is not correctly embedded.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /Foo /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
    }

    [Fact]
    public void Validate_FontFileDanglingReference_ReportsUnembedded()
    {
        // /FontFile2 points at a nonexistent object — resolving to no stream means "not embedded".
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FirstChar 65 /LastChar 65 "
                + "/Widths [600] /Encoding /WinAnsiEncoding /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile2 99 0 R >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
    }

    [Fact]
    public void Validate_MalformedXmpMetadata_ReportsError()
    {
        var bytes = AssemblePdf(
            [new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj],
            metadataOverride: Encoding.UTF8.GetBytes("this is not <<< well-formed xml"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.6.4-pdfaid", assertion.RuleId);
    }

    [Fact]
    public void Validate_Utf16XmpPacket_ReportsEncodingOnly()
    {
        // A UTF-16 XMP packet is non-conformant for its encoding (§6.6.2.1-5), but its pdfaid must
        // still be READ correctly (the old UTF-8-only decode broke that): the encoding finding shall
        // be the ONLY one — no spurious pdfaid part/conformance mismatch from a mis-decoded packet.
        var xmpText =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta>";
        var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var bom = enc.GetPreamble();
        var body = enc.GetBytes(xmpText);
        var xmp = new byte[bom.Length + body.Length];
        bom.CopyTo(xmp, 0);
        body.CopyTo(xmp, bom.Length);

        var result = PdfPreflight.Validate(BuildXmpPdf(xmp), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.6.2.1-xmp-encoding-utf8", assertion.RuleId);
    }

    [Fact]
    public void Validate_OutputIntent_NonIntegralN_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3.9", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.3-output-intent", assertion.RuleId);
    }

    [Fact]
    public void Validate_XmpWithAlternatePdfaidPrefix_ReportsPrefixError()
    {
        // §6.6.4: the pdfaid properties shall use the prefix 'pdfaid'. Here the namespace URI is bound
        // to 'aid', which veraPDF rejects. The values are still READ correctly (resolution is by URI),
        // so the only findings are the prefix violations — no spurious part/conformance mismatch.
        var xmp = Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:aid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<aid:part>2</aid:part><aid:conformance>B</aid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

        var result = PdfPreflight.Validate(BuildXmpPdf(xmp), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.NotEmpty(result.Assertions);
        Assert.All(result.Assertions, a => Assert.Equal("ISO19005-2:6.6.4-pdfaid-prefix", a.RuleId));
    }

    [Fact]
    public void Validate_PdfaidWithCanonicalAndAliasPrefix_NoFinding()
    {
        // The pdfaid properties are written with the canonical 'pdfaid' prefix, but an alias 'aid' is
        // ALSO bound to the same URI in scope. The properties are conformant; the prefix check must
        // not misfire on the alias (veraPDF accepts this). Regression guard for the review.
        var xmp = Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" "
            + "xmlns:aid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

        var result = PdfPreflight.Validate(BuildXmpPdf(xmp), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void ValidateUa_EmptyDcTitle_ReportsError()
    {
        // dc:title is present but its rdf:li value is empty — not a real title.
        var xmp = Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" "
            + "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<pdfuaid:part>1</pdfuaid:part>"
            + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\"></rdf:li></rdf:Alt></dc:title>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

        var result = PdfPreflight.Validate(BuildUaPdf(xmpOverride: xmp), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-title", assertion.RuleId);
    }

    [Fact]
    public void PreflightResult_Assertions_AreImmutable()
    {
        var result = PdfPreflight.Validate(BuildCatalogMissingTypePdf(), PdfConformance.PdfA2B);

        Assert.NotEmpty(result.Assertions);
        Assert.IsNotType<List<PreflightAssertion>>(result.Assertions);
        Assert.Throws<NotSupportedException>(
            () => ((IList<PreflightAssertion>)result.Assertions).Add(result.Assertions[0]));
    }

    [Fact]
    public void Validate_EncryptedPdf_PropagatesUnsupported()
    {
        // An unsupported reader feature (encryption) must surface as UnsupportedPdfFeatureException,
        // not be swallowed into a conformance finding.
        var bytes = BuildEncryptedTrailerPdf();

        Assert.Throws<UnsupportedPdfFeatureException>(
            () => PdfPreflight.Validate(bytes, PdfConformance.PdfA2B));
    }

    private static byte[] BuildEncryptedTrailerPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        var xref = (int)ms.Position;
        Write("xref\n0 3\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{o1:D10} 00000 n \n");
        Write($"{o2:D10} 00000 n \n");
        Write("trailer\n<< /Size 3 /Root 1 0 R /Encrypt << /Filter /Standard /V 1 /R 2 >> >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void Validate_FlateCompressedMetadataStream_IsAccepted()
    {
        // PDF/A-2 (ISO 19005-2) relaxed PDF/A-1's plain-text metadata requirement: a FlateDecode
        // /Metadata stream is permitted (Acrobat and Ghostscript routinely emit one), and veraPDF
        // does not flag it. The packet must still be decoded and its pdfaid read — and a filtered
        // stream must NOT be reported as a violation. (Regression guard for review round 4.)
        var compressed = ZlibCompress(XmpBytes("2", "B"));
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>"),
                _pagesObj,
                _pageObj,
                new("/Type /Metadata /Subtype /XML /Filter /FlateDecode", compressed),
            ],
            metadata: false);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_IndirectFilterOnMetadataStream_IsResolvedAndDecoded()
    {
        // /Filter may itself be an indirect reference (e.g. Ghostscript emits `/Filter 12 0 R`).
        // The filter chain must dereference it; otherwise the still-compressed body is handed to the
        // XMP parser as if decoded and the packet reads as garbage. Here object 5 is the /FlateDecode
        // name. Correct resolution decodes the packet, reads pdfaid:part 2 / conformance B → compliant.
        var compressed = ZlibCompress(XmpBytes("2", "B"));
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>"),
                _pagesObj,
                _pageObj,
                new("/Type /Metadata /Subtype /XML /Filter 5 0 R", compressed),
                new("/FlateDecode"),
            ],
            metadata: false);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_MetadataWithDtd_IsRejectedNotResolved()
    {
        // The XMP packet is untrusted: a DOCTYPE / external entity must be refused outright
        // (DtdProcessing.Prohibit), never resolved (XXE). The reader rejects the DTD, so the packet
        // fails to parse and is reported as not-well-formed rather than expanding the entity.
        var xmpWithDtd = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?>"
            + "<!DOCTYPE x [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>&xxe;</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta>");
        var bytes = AssemblePdf(
            [new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj],
            metadataOverride: xmpWithDtd);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.Message.Contains("well-formed XMP"));
    }

    [Fact]
    public void Validate_DocumentLevelJavaScript_ReportsError()
    {
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /Names << /JavaScript << /Names [(JS1) 4 0 R] >> >> >>"),
            _pagesObj,
            _pageObj,
            new("<< /S /JavaScript /JS (noop) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.5.1-action" && a.Message.Contains("/JavaScript"));
    }

    [Fact]
    public void ValidateUa_TabsOnlyOnPagesNode_ReportsError()
    {
        // /Tabs /S is set ONLY on the intermediate /Pages node. /Tabs is not an inheritable page
        // attribute (ISO 32000-1 Table 31), so the annotated leaf page does not satisfy §7.18.3 and
        // must be flagged — a conformant reader ignores the ancestor's /Tabs. (Round-7 fix.)
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 5 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                new("<< /Type /Pages /Kids [3 0 R] /Count 1 /Tabs /S >>"),
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
                new("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 >>"),
                new("<< /Type /StructTreeRoot >>"),
            ],
            metadataOverride: UaXmpBytes());

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.18.3-tabs");
    }

    [Fact]
    public void ValidateUa_TabsSetOnPage_NoFinding()
    {
        // /Tabs /S set directly on the annotated page satisfies §7.18.3.
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 5 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Tabs /S /Annots [4 0 R] >>"),
                new("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 >>"),
                new("<< /Type /StructTreeRoot >>"),
            ],
            metadataOverride: UaXmpBytes());

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.1.13-9 DeviceN colourant limit ─────────────────────────────────────

    /// <summary>Builds a one-page doc whose /Resources /ColorSpace /CS0 is the given colour-space
    /// array body (object 4), with object 5 as a minimal tint-function stub.</summary>
    private static byte[] BuildColourSpacePdf(string csArray)
        => BuildPagePdf(
            "/Resources << /ColorSpace << /CS0 4 0 R >> >> /Contents 6 0 R",
            new PdfObj(csArray),             // 4 0 obj — the colour space array
            new PdfObj("<< /FunctionType 2 /Domain [0 1] /N 1 >>"), // 5 0 obj — tint function stub
            new PdfObj(string.Empty, Encoding.ASCII.GetBytes("/CS0 cs"))); // 6 0 obj — selects the space

    [Fact]
    public void Validate_DeviceN33Colourants_ReportsError()
    {
        // §6.1.13-9: a DeviceN with 33 colourant names (one over the limit) must be flagged.
        var names = string.Join(" ", Enumerable.Range(0, 33).Select(i => "/c" + i));
        var bytes = BuildColourSpacePdf($"[/DeviceN [{names}] /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-9-devicen");
    }

    [Fact]
    public void Validate_DeviceN32Colourants_IsAllowed()
    {
        // §6.1.13-9: exactly 32 colourant names is the permitted maximum — no false positive.
        var names = string.Join(" ", Enumerable.Range(0, 32).Select(i => "/c" + i));
        var bytes = BuildColourSpacePdf($"[/DeviceN [{names}] /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-9-devicen");
    }

    [Fact]
    public void Validate_SeparationColourSpace_IsNotFlagged()
    {
        // §6.1.13-9: a Separation colour space [/Separation /Spot /DeviceCMYK tint] is a
        // 1-colourant special case and must never trip the DeviceN rule.
        var bytes = BuildColourSpacePdf("[/Separation /Spot /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-9-devicen");
    }

    // ── §6.2.4.4-1 DeviceN Colorants dictionary ───────────────────────────────

    /// <summary>
    /// Builds a PDF with a USED DeviceN colour space that has a 2-input Type 4 tint function
    /// at object 5, and optional Separation colour spaces for its colourants at objects 7 and 8
    /// (each backed by the single-input tint stub at object 5). The attributes dict and its
    /// /Colorants sub-dict are embedded inline in the colour-space array literal at object 4.
    /// Object 6 is the content stream that selects CS0 via <c>/CS0 cs</c>.
    ///
    /// Object map:
    ///   1 = catalog, 2 = pages, 3 = page
    ///   4 = DeviceN array  (csBody)
    ///   5 = tint func (2-input Type 4 pop pop → 0 0 0 0)
    ///   6 = content stream (/CS0 cs 0 0 scn 10 10 50 50 re f)
    ///   7 = single-input tint func for Spot1 Separation (FunctionType 2)
    ///   8 = single-input tint func for Spot2 Separation (FunctionType 2)
    /// </summary>
    private static byte[] BuildDeviceNColorantsPdf(string csBody)
        => BuildPagePdf(
            "/Resources << /ColorSpace << /CS0 4 0 R >> >> /Contents 6 0 R",
            new PdfObj(csBody),  // 4: colour-space array
            // 5: 2-input tint function for DeviceN → DeviceCMYK
            new PdfObj("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1 0 1]",
                Encoding.ASCII.GetBytes("{ pop pop 0 0 0 0 }")),
            // 6: content stream — selects CS0 as fill space and sets both components to 0
            new PdfObj(string.Empty, Encoding.ASCII.GetBytes("/CS0 cs 0 0 scn 10 10 50 50 re f")),
            // 7: single-input tint function for Spot1's Separation entry
            new PdfObj("<< /FunctionType 2 /Domain [0 1] /N 1 >>"),
            // 8: single-input tint function for Spot2's Separation entry
            new PdfObj("<< /FunctionType 2 /Domain [0 1] /N 1 >>"));

    [Fact]
    public void Validate_DeviceNMissingColorant_ReportsError()
    {
        // §6.2.4.4-1: Spot2 is listed in the DeviceN names array but absent from /Colorants.
        // veraPDF flags this; the in-process rule must agree.
        var bytes = BuildDeviceNColorantsPdf(
            "[/DeviceN [/Spot1 /Spot2] /DeviceCMYK 5 0 R"
            + " << /Subtype /DeviceN /Colorants"
            + " << /Spot1 [/Separation /Spot1 /DeviceCMYK 7 0 R] >> >>]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-1-colorants");
    }

    [Fact]
    public void Validate_DeviceNAllColorantsPresent_IsAllowed()
    {
        // §6.2.4.4-1: both Spot1 and Spot2 have /Colorants entries — no violation.
        var bytes = BuildDeviceNColorantsPdf(
            "[/DeviceN [/Spot1 /Spot2] /DeviceCMYK 5 0 R"
            + " << /Subtype /DeviceN /Colorants"
            + " << /Spot1 [/Separation /Spot1 /DeviceCMYK 7 0 R]"
            + "    /Spot2 [/Separation /Spot2 /DeviceCMYK 8 0 R] >> >>]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-1-colorants");
    }

    [Fact]
    public void Validate_DeviceNNoAttributesDict_ReportsError()
    {
        // §6.2.4.4-1: a DeviceN with a spot colourant but no attributes dict (4 elements only)
        // is flagged by veraPDF — the missing attributes means /Colorants is absent.
        // Uses a 1-input tint function (object 5 stub is /FunctionType 2 so reuse BuildColourSpacePdf).
        var bytes = BuildColourSpacePdf("[/DeviceN [/Spot1] /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-1-colorants");
    }

    [Fact]
    public void Validate_DeviceNProcessColorantExempt_IsAllowed()
    {
        // §6.2.4.4-1: process colourants (Cyan, Magenta, Yellow, Black) and None do not require
        // a /Colorants entry. A DeviceN [/Cyan /Magenta /Yellow /Black] with no attributes dict
        // must NOT be flagged. Verified empirically (probe C, H).
        // Uses a 4-input tint function; reuse the inline structure from BuildColourSpacePdf
        // (obj 5 = FunctionType 2 stub) — veraPDF does not execute it during this check.
        var bytes = BuildPagePdf(
            "/Resources << /ColorSpace << /CS0 4 0 R >> >> /Contents 6 0 R",
            // 4: DeviceN array — no attributes dict
            new PdfObj("[/DeviceN [/Cyan /Magenta /Yellow /Black] /DeviceCMYK 5 0 R]"),
            // 5: 4-input tint function stub
            new PdfObj("/FunctionType 4 /Domain [0 1 0 1 0 1 0 1] /Range [0 1 0 1 0 1 0 1]",
                Encoding.ASCII.GetBytes("{ pop pop pop pop 0 0 0 0 }")),
            // 6: content stream
            new PdfObj(string.Empty, Encoding.ASCII.GetBytes("/CS0 cs 0 0 0 0 scn 10 10 50 50 re f")));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-1-colorants");
    }

    [Fact]
    public void Validate_DeviceNNoneColorantExempt_IsAllowed()
    {
        // §6.2.4.4-1: the special colourant name /None is exempt from the /Colorants requirement.
        // Verified empirically (probe D).
        var bytes = BuildColourSpacePdf("[/DeviceN [/None] /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-1-colorants");
    }

    [Fact]
    public void Validate_DeviceNUnusedMissingColorant_IsAllowed()
    {
        // §6.2.4.4-1: a DeviceN with a missing /Colorants entry but never selected by page
        // content (no cs/CS operator) must NOT be flagged — veraPDF validates only used spaces.
        // The page has no /Contents, so the colour space is purely in /Resources.
        var bytes = BuildPagePdf(
            "/Resources << /ColorSpace << /CS0 4 0 R >> >>",
            // 4: DeviceN with incomplete /Colorants (Spot2 absent)
            new PdfObj("[/DeviceN [/Spot1 /Spot2] /DeviceCMYK 5 0 R"
                + " << /Subtype /DeviceN /Colorants << /Spot1 [/Separation /Spot1 /DeviceCMYK 5 0 R] >> >>]"),
            // 5: tint function stub (any shape — rule never reaches it)
            new PdfObj("<< /FunctionType 2 /Domain [0 1] /N 1 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-1-colorants");
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void Assertion_ToString_IncludesRuleAndSeverity()
    {
        var result = PdfPreflight.Validate(BuildCatalogMissingTypePdf(), PdfConformance.PdfA2B);
        var text = result.Assertions[0].ToString();

        Assert.Contains("Error", text);
        Assert.Contains("ISO32000-1:7.7.2-catalog-type", text);
    }
}
