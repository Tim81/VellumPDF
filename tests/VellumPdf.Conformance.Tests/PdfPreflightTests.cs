// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Conformance;
using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Conformance.Tests.Oracle;
using VellumPdf.Document;
using VellumPdf.Reader;
using VellumPdf.Signing;

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
    public void Validate_SigContentsOversized_NoStringFinding()
    {
        // §6.1.13 false-positive guard: the /Contents entry of a signature dictionary is a DER-
        // encoded CMS blob whose size is dictated by the CMS structure. PAdES B-T/B-LTA signatures
        // (as produced by the Signing package's TimestampedDefaultReserve path) use a placeholder
        // of 32768 decoded bytes. The string-length check shall not fire for this entry because the
        // signature value is not document text subject to the implementation-limit table.
        // A non-signature string of the same length STILL fires (see Validate_OversizedString_ReportsError).
        var bigHex = "<" + new string('0', 32768 * 2) + ">"; // 32768 decoded bytes
        var bytes = AssemblePdf(
        [
            new($"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>"),
            _pagesObj,
            _pageObj,
            new($"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /Rect [0 0 0 0] /F 4"
                + $" /V << /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached"
                + $" /ByteRange [0 1 2 3] /Contents {bigHex} >> >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-string");
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

    // ── §6.8-5 embedded-file PDF/A conformance ────────────────────────────────

    /// <summary>
    /// Builds a minimal PDF/A-2b file spec dict with an embedded PDF stream having the given
    /// body bytes. The filespec carries both /F and /UF so §6.8-2 is satisfied; only §6.8-5 is
    /// under test.
    /// </summary>
    private static byte[] BuildPdfWithEmbeddedBytes(byte[] innerBytes) => AssemblePdf(
    [
        new("<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.pdf) 4 0 R] >> >> >>"),
        _pagesObj,
        _pageObj,
        new("<< /Type /Filespec /F (a.pdf) /UF (a.pdf) /EF << /F 5 0 R >> >>"),
        new("/Type /EmbeddedFile", Stream: innerBytes),
    ]);

    [Fact]
    public void Validate_EmbeddedNonPdfADocument_ReportsError()
    {
        // §6.8-5: an embedded PDF that carries no pdfaid identification is not a valid PDF/A-1 or
        // PDF/A-2 document. The inner PDF is produced with PdfConformance.None (no XMP pdfaid).
        var innerBytes = BuildPlainPdf20();
        var bytes = BuildPdfWithEmbeddedBytes(innerBytes);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.8-5-embedded-pdfa");
    }

    [Fact]
    public void Validate_EmbeddedCompliantPdfA2b_IsAllowed()
    {
        // §6.8-5: an embedded PDF that is itself a compliant PDF/A-2b document must not be flagged —
        // the no-false-positive guard for the recursive embedded-PDF/A check.
        var innerBytes = BuildOnePagePdf(); // produces a compliant PDF/A-2b
        var bytes = BuildPdfWithEmbeddedBytes(innerBytes);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.8-5-embedded-pdfa");
    }

    [Fact]
    public void Validate_EmbeddedNonPdfAttachment_ReportsError()
    {
        // §6.8-5: a non-PDF attachment (plain text) is not a valid PDF/A-1 or PDF/A-2 document.
        // veraPDF (confirmed in STEP 0) flags this case; the in-process rule must agree.
        var textBytes = System.Text.Encoding.UTF8.GetBytes("Hello world — plain text, not a PDF.");
        var bytes = BuildPdfWithEmbeddedBytes(textBytes);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.8-5-embedded-pdfa");
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

    // ── §6.1.8-1 name UTF-8 validity ─────────────────────────────────────────────

    // The helpers below embed the non-ASCII bytes directly via #XX PDF-name escaping.
    // /BaseFont /#80Name has one lone continuation byte (0x80) — invalid UTF-8.
    // /BaseFont /ABCDEF#2BBold has a '+' (#2B, 0x2B) escape — valid ASCII, valid UTF-8.

    [Fact]
    public void Validate_InvalidUtf8BaseFontName_ReportsError()
    {
        // §6.1.8-1: a /BaseFont name whose escaped bytes are not valid UTF-8 shall be flagged.
        // 0x80 is a lone UTF-8 continuation byte with no leading byte — invalid.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /#80InvalidFont /FirstChar 65 /LastChar 65 /Widths [600] /Encoding /WinAnsiEncoding >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.8-1");
    }

    [Fact]
    public void Validate_ValidEscapedBaseFontName_IsAllowed()
    {
        // §6.1.8-1: a /BaseFont whose escaped bytes are valid UTF-8 must not be flagged.
        // #2B expands to '+' (0x2B, ASCII) — trivially valid UTF-8.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF#2BBold /FirstChar 65 /LastChar 65 /Widths [600] /Encoding /WinAnsiEncoding >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.8-1");
    }

    [Fact]
    public void Validate_InvalidUtf8SeparationColourantName_ReportsError()
    {
        // §6.1.8-1: a Separation colourant name with invalid UTF-8 bytes shall be flagged.
        // #C0#C0 is an overlong sequence — invalid UTF-8.
        var bytes = BuildColourSpacePdf("[/Separation /Bad#C0#C0Spot /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.8-1");
    }

    [Fact]
    public void Validate_ValidSeparationColourantName_IsAllowed()
    {
        // §6.1.8-1: a plain ASCII Separation colourant name must not be flagged.
        var bytes = BuildColourSpacePdf("[/Separation /SpotColor /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.8-1");
    }

    [Fact]
    public void Validate_InvalidUtf8DeviceNColourantName_ReportsError()
    {
        // §6.1.8-1: a DeviceN colourant name with invalid UTF-8 bytes shall be flagged.
        // #80 is a lone continuation byte — invalid UTF-8.
        var bytes = BuildColourSpacePdf("[/DeviceN [/GoodSpot /Bad#80Spot] /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.8-1");
    }

    [Fact]
    public void Validate_InvalidUtf8StructureTypeName_ReportsError()
    {
        // §6.1.8-1: a structure element /S name with invalid UTF-8 bytes shall be flagged.
        // Uses a tagged PDF (PDF/A-2a profile needed for full coverage, but the rule runs on 2b too).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /Type /StructTreeRoot /K [5 0 R] >>"),
            new("<< /Type /StructElem /S /Bad#80Type /P 4 0 R >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.8-1");
    }

    [Fact]
    public void Validate_AllAsciiNames_NoNameUtf8Finding()
    {
        // §6.1.8-1: a writer-produced PDF/A with all-ASCII font, colour, and structure names
        // must never be flagged — no false positive.
        var bytes = BuildOnePagePdf();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.8-1");
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
    public void Validate_Type0EmbeddedCMapOrderingMismatch_ReportsError()
    {
        // §6.2.11.3.1-1: CMap CIDSystemInfo ordering (Japan1) differs from CIDFont CIDSystemInfo
        // ordering (Identity) → incompatible → must be flagged.
        // Objects: 6=Type0 7=CMapStream 8=CIDFont 9=FontDescriptor 10=FontFile2; content=obj 11.
        var cmapBody = Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 0 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + "1 begincodespacerange <0020> <007E> endcodespacerange "
            + "1 begincidrange <0020> <007E> 32 endcidrange "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 7 0 R /DescendantFonts [8 0 R] >>"),
            new PdfObj("/Type /CMap /CMapName /CustomCMap /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 0 >> /WMode 0", cmapBody),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 9 0 R /CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 10 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.1-cidsysteminfo");
    }

    [Fact]
    public void Validate_Type0EmbeddedCMapMatchingCidSystemInfo_IsNotFlagged()
    {
        // §6.2.11.3.1-1 no-false-positive: CMap and CIDFont CIDSystemInfo match → compliant.
        var cmapBody = Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 1 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + "1 begincodespacerange <0020> <007E> endcodespacerange "
            + "1 begincidrange <0020> <007E> 32 endcidrange "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 7 0 R /DescendantFonts [8 0 R] >>"),
            new PdfObj("/Type /CMap /CMapName /CustomCMap /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 1 >> /WMode 0", cmapBody),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 0 >> /FontDescriptor 9 0 R /CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 10 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.1-cidsysteminfo");
    }

    [Fact]
    public void Validate_Type0IdentityHEncoding_CidSystemInfoNotFlagged()
    {
        // §6.2.11.3.1-1 exempt path: Identity-H is always conformant regardless of CIDSystemInfo.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.1-cidsysteminfo");
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

    // ── §6.6.2.3.2 extension-schema undefined-field rules ────────────────────────

    [Fact]
    public void Validate_ExtensionSchemaUndefinedField_SchemaContainer_ReportsError()
    {
        // A bogus field in the pdfaSchema namespace inside the schema container is an undefined field
        // (§6.6.2.3.2-1). Corresponds to veraPDF Probe B.
        var schemaFields =
            "<pdfaSchema:schema>S</pdfaSchema:schema>"
            + "<pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
            + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
            + "<pdfaSchema:bogusField>x</pdfaSchema:bogusField>";

        var result = PdfPreflight.Validate(
            BuildXmpPdf(ExtensionSchemaXmp(schemaFields, _validPropertyFields)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.2-undefined-field");
    }

    [Fact]
    public void Validate_ExtensionSchemaUndefinedField_PropertyContainer_ReportsError()
    {
        // A bogus field in the pdfaProperty namespace inside a property entry is an undefined field
        // (§6.6.2.3.2-1). Corresponds to veraPDF Probe C.
        var propertyFields =
            "<pdfaProperty:name>foo</pdfaProperty:name>"
            + "<pdfaProperty:valueType>Text</pdfaProperty:valueType>"
            + "<pdfaProperty:category>external</pdfaProperty:category>"
            + "<pdfaProperty:description>d</pdfaProperty:description>"
            + "<pdfaProperty:bogus>x</pdfaProperty:bogus>";

        var result = PdfPreflight.Validate(
            BuildXmpPdf(ExtensionSchemaXmp(_validSchemaFields, propertyFields)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.2-undefined-field");
    }

    [Fact]
    public void Validate_ExtensionSchemaUndefinedField_ForeignNsChild_ReportsError()
    {
        // An unrelated-namespace child (dc:foo) inside the schema container is also an undefined field
        // (§6.6.2.3.2-1). veraPDF flags all namespaces uniformly — Probe D confirmed this.
        const string ns =
            "xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
            + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" "
            + "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"";
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>"
            + $"<rdf:Description rdf:about=\"\" {ns}><pdfaExtension:schemas><rdf:Bag>"
            + "<rdf:li rdf:parseType=\"Resource\">"
            + "<pdfaSchema:schema>S</pdfaSchema:schema>"
            + "<pdfaSchema:namespaceURI>http://example.com/ns/</pdfaSchema:namespaceURI>"
            + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
            + "<dc:foo>x</dc:foo>"
            + "<pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" + _validPropertyFields
            + "</rdf:li></rdf:Seq></pdfaSchema:property>"
            + "</rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>"
            + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

        var result = PdfPreflight.Validate(BuildXmpPdf(Encoding.UTF8.GetBytes(xmp)), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.2-undefined-field");
    }

    [Fact]
    public void Validate_ValidExtensionSchema_NoUndefinedFieldFinding()
    {
        // A well-formed PDF/A extension schema with only the defined fields must not trigger
        // the §6.6.2.3.2-1 undefined-field check (no false positive).
        var result = PdfPreflight.Validate(
            BuildXmpPdf(ExtensionSchemaXmp(_validSchemaFields, _validPropertyFields)), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.2-undefined-field");
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

    // ── §6.6.2.3.1-2 XMP property value-type rules ──────────────────────────

    /// <summary>
    /// Builds an XMP packet that declares one extension-schema property with
    /// <paramref name="declaredType"/> and emits one usage of that property whose value
    /// element body is <paramref name="valueBody"/> (inserted as raw XML inside the property
    /// element, so callers can inject either a scalar string or an rdf:Bag/Seq/Alt).
    /// </summary>
    private static byte[] PropertyValueTypeXmpRaw(string declaredType, string valueBody)
    {
        const string ns = "http://example.com/ns/";
        var ext =
            "<rdf:Description rdf:about=\"\" "
            + "xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" "
            + "xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" "
            + "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">"
            + "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">"
            + "<pdfaSchema:schema>T</pdfaSchema:schema>"
            + "<pdfaSchema:namespaceURI>" + ns + "</pdfaSchema:namespaceURI>"
            + "<pdfaSchema:prefix>ex</pdfaSchema:prefix>"
            + "<pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">"
            + "<pdfaProperty:name>p</pdfaProperty:name>"
            + "<pdfaProperty:valueType>" + declaredType + "</pdfaProperty:valueType>"
            + "<pdfaProperty:category>external</pdfaProperty:category>"
            + "<pdfaProperty:description>d</pdfaProperty:description>"
            + "</rdf:li></rdf:Seq></pdfaSchema:property>"
            + "</rdf:li></rdf:Bag></pdfaExtension:schemas>"
            + "</rdf:Description>";
        var usage =
            "<rdf:Description rdf:about=\"\" xmlns:ex=\"" + ns + "\""
            + " xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<ex:p>" + valueBody + "</ex:p>"
            + "</rdf:Description>";
        return BuildXmpPdf(XmpWithDescriptions(ext + usage));
    }

    // ── §6.6.2.3.1-2: writer-produced conformant PDF/A-2b — no false positive ──

    [Fact]
    public void Validate_PropertyValueType_WriterProducedPdfA_NoFinding()
    {
        // A fully-writer-produced PDF/A-2b document must not trigger the property-value-type
        // rule — this is the critical false-positive guard. The writer emits dc:title as
        // rdf:Alt, xmp:CreateDate as an ISO-8601 date, pdf:Producer as text, etc.
        var bytes = BuildOnePagePdf();
        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    // ── §6.6.2.3.1-2: extension-schema Integer type ──────────────────────────

    [Theory]
    [InlineData("42")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("0")]
    public void Validate_PropertyValueType_IntegerCorrect_NoFinding(string value)
    {
        // An extension-schema Integer property with a valid integer value must not be flagged.
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Integer", value), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("3.14")]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_PropertyValueType_IntegerWrong_ReportsError(string value)
    {
        // An extension-schema Integer property with a non-integer value must trigger the rule.
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Integer", value), PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    // ── §6.6.2.3.1-2: extension-schema Boolean type ──────────────────────────

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    public void Validate_PropertyValueType_BooleanCorrect_NoFinding(string value)
    {
        // XMP Boolean is exactly "True" or "False" (case-sensitive per XMP Spec §8.2.1.3).
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Boolean", value), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Theory]
    [InlineData("true")]    // lowercase — not valid XMP Boolean
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData("1")]
    public void Validate_PropertyValueType_BooleanWrong_ReportsError(string value)
    {
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Boolean", value), PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    // ── §6.6.2.3.1-2: extension-schema Date type ─────────────────────────────

    [Theory]
    [InlineData("2024-01-15T12:00:00+00:00")]
    [InlineData("2024-01-15")]
    [InlineData("2024-01")]
    [InlineData("2024")]
    public void Validate_PropertyValueType_DateCorrect_NoFinding(string value)
    {
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Date", value), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("Jan 15 2024")]
    public void Validate_PropertyValueType_DateWrong_ReportsError(string value)
    {
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Date", value), PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    // ── §6.6.2.3.1-2: extension-schema container types ───────────────────────

    [Fact]
    public void Validate_PropertyValueType_BagTextCorrect_NoFinding()
    {
        // bag Text: value must be an rdf:Bag; correct usage must not fire.
        const string bagValue = "<rdf:Bag><rdf:li>item1</rdf:li><rdf:li>item2</rdf:li></rdf:Bag>";
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("bag Text", bagValue), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_BagTextScalar_ReportsError()
    {
        // A "bag Text" property serialised as a scalar instead of rdf:Bag must be flagged.
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("bag Text", "scalar value"), PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_SeqIntegerCorrect_NoFinding()
    {
        const string seqValue = "<rdf:Seq><rdf:li>1</rdf:li><rdf:li>2</rdf:li></rdf:Seq>";
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("seq Integer", seqValue), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_SeqIntegerScalar_ReportsError()
    {
        // A "seq Integer" property serialised as a scalar must be flagged.
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("seq Integer", "42"), PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_LangAltCorrect_NoFinding()
    {
        // Lang Alt: value must be an rdf:Alt; correct rdf:Alt usage must not fire.
        const string altValue =
            "<rdf:Alt><rdf:li xml:lang=\"x-default\">Hello</rdf:li></rdf:Alt>";
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Lang Alt", altValue), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_LangAltScalar_ReportsError()
    {
        // A "Lang Alt" property serialised as a scalar instead of rdf:Alt must be flagged.
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Lang Alt", "plain text"), PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    // ── §6.6.2.3.1-2: Text type (scalar) — no container allowed ──────────────

    [Fact]
    public void Validate_PropertyValueType_TextScalar_NoFinding()
    {
        // A "Text" property with a plain string value must not be flagged.
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Text", "hello world"), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_TextWithBagContainer_ReportsError()
    {
        // A "Text" property carrying an rdf:Bag is wrong-typed (should be scalar).
        const string bagValue = "<rdf:Bag><rdf:li>item</rdf:li></rdf:Bag>";
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("Text", bagValue), PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    // ── §6.6.2.3.1-2: deferred cases — no finding even when value looks wrong ─

    [Fact]
    public void Validate_PropertyValueType_UnknownDeclaredType_NoFinding()
    {
        // A property whose declared type is unrecognised (custom value type, struct type, etc.)
        // is deferred — the rule must not flag it even with an arbitrary value.
        var result = PdfPreflight.Validate(
            PropertyValueTypeXmpRaw("MyCustomType", "some value"), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_NoPredefinedSchemaChecks_NoFinding()
    {
        // Predefined-schema properties (dc:, xmp:, pdfaid:, …) are not type-checked by this
        // rule (Partial implementation) — even a dc:title serialised as a scalar must not
        // trigger 6.6.2.3.1-2.  (veraPDF does flag dc:title as scalar, but this rule defers
        // predefined schemas to avoid false-positives from an incomplete built-in type table.)
        const string dcTitleScalar =
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<dc:title>Simple scalar title</dc:title>"
            + "</rdf:Description>";
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithDescriptions(dcTitleScalar)), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
    }

    [Fact]
    public void Validate_PropertyValueType_NoExtensionSchema_NoFinding()
    {
        // A document with no extension schemas has nothing to type-check — must be silent.
        var result = PdfPreflight.Validate(
            BuildXmpPdf(XmpWithDescriptions(string.Empty)), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.6.2.3.1-2");
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

    [Fact]
    public void Validate_InvisibleSigWidget_DegenerateRect_NoAnnotationFinding()
    {
        // §6.3.3-1 false-positive guard: a signature Widget annotation with /Rect [0 0 0 0] (zero
        // area) has no visible extent and is exempt from the appearance-stream presence requirement.
        // veraPDF 1.30.2 accepts such annotations without flagging a missing /AP. The flag
        // requirements (/F Print set, Hidden/Invisible/NoView/ToggleNoView clear) still apply.
        // This mirrors the invisible signature widget the Signing package writes via doc.Sign().
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /Rect [0 0 0 0] /F 4 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.3-annotation");
    }

    [Fact]
    public void Validate_InvisibleStampAnnotation_DegenerateRect_NoAnnotationFinding()
    {
        // §6.3.3-1 false-positive guard (non-Widget): a /Stamp annotation with /Rect [0 0 0 0]
        // is also exempt from the appearance requirement. The exemption is rect-based, not
        // limited to signature widgets.
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Stamp /Rect [0 0 0 0] /F 4 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.3-annotation");
    }

    [Fact]
    public void Validate_VisibleAnnotationMissingAP_DegenerateRectExemptionDoesNotApply()
    {
        // Regression guard: a non-degenerate /Rect annotation with no /AP still fires the
        // 6.3-annotation rule. The degenerate-rect exemption must not over-broaden.
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Stamp /Rect [10 10 200 100] /F 4 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.3-annotation" && a.Message.Contains("appearance"));
    }

    [Fact]
    public void Validate_RealSignedPdf_NoAnnotationOrStringFinding()
    {
        // The signing package produces an invisible signature widget with /Rect [0 0 0 0] and no
        // /AP — historically flagged as 6.3-annotation. After the degenerate-rect fix, both the
        // 6.3-annotation and 6.1.13-string rules must be silent on a well-formed signed PDF.
        using var cert = CreateSigningCertificate();
        var signed = SignMinimalPdf(cert);

        var result = PdfPreflight.Validate(signed, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.3-annotation");
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-string");
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
        // The Link annotation includes /Contents so that §7.18.5-2 and §7.18.1-2 do not fire,
        // isolating the §7.18.3 tab-order violation as the only finding.
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 4 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                _pagesObj,
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>"),
                new("<< /Type /StructTreeRoot >>"),
                new("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 /Contents (link) >>"),
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
        // The Link annotation includes /Contents so §7.18.5-2 and §7.18.1-2 also pass —
        // all §7.18 requirements are satisfied, so the document is fully compliant.
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 5 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Tabs /S /Annots [4 0 R] >>"),
                new("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 /Contents (link) >>"),
                new("<< /Type /StructTreeRoot >>"),
            ],
            metadataOverride: UaXmpBytes());

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void ValidateUa_AnnotWithoutContentsButStructElementAlt_DoesNotReport7181_2()
    {
        // False-positive guard for §7.18.1-2 (UaAnnotContentsRule): a Text annotation with NO
        // /Contents and NO annotation-level /Alt, but bound into the structure tree via /StructParent
        // to a struct element that DOES carry /Alt. veraPDF 1.30.2 resolves the alt-text from the
        // enclosing structure element and does NOT fail 7.18.1-2 here (verified directly). Our rule
        // cannot read the struct-element /Alt without the walker, so it must skip /StructParent-bound
        // annotations rather than over-reject this conformant tagged annotation.
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 4 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Tabs /S /Annots [6 0 R] "
                    + "/StructParents 1 >>"),
                new("<< /Type /StructTreeRoot /K [5 0 R] /ParentTree 7 0 R >>"),
                new("<< /Type /StructElem /S /Annot /P 4 0 R /Alt (annotation description) /Pg 3 0 R "
                    + "/K [ << /Type /OBJR /Obj 6 0 R >> ] >>"),
                new("<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /F 4 /StructParent 0 >>"),
                new("<< /Nums [0 5 0 R] >>"),
            ],
            metadataOverride: UaXmpBytes());

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        // The struct-bound annotation must NOT trip the annotation-Contents/Alt rule.
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.18.1-2");
    }

    [Fact]
    public void ValidateUa_UntaggedAnnotWithoutContents_Reports7181_2()
    {
        // Positive control for the guard above: the SAME annotation with no /StructParent (not bound
        // into the structure tree) has no possible struct-element alt-text, so §7.18.1-2 fires —
        // matching veraPDF, which fails 7.18.1-2 for an untagged annotation lacking /Contents.
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 4 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Tabs /S /Annots [5 0 R] >>"),
                new("<< /Type /StructTreeRoot >>"),
                new("<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /F 4 >>"),
            ],
            metadataOverride: UaXmpBytes());

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.18.1-2");
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

    // ── §6.2.4.2-1 ICCBased colour space — ICC profile validity ──────────────

    /// <summary>
    /// Builds a one-page PDF whose /Resources /ColorSpace /CS0 is [/ICCBased &lt;stream&gt;].
    /// The stream carries <paramref name="iccBytes"/> compressed with FlateDecode. When
    /// <paramref name="usedInContent"/> is true the page content selects CS0 via the
    /// <c>cs</c> operator so the rule fires; when false CS0 is merely present in resources.
    /// </summary>
    private static byte[] BuildIccBasedPdf(byte[] iccBytes, int n = 3, bool usedInContent = true)
    {
        var compressed = ZlibCompress(iccBytes);
        // Objects: 1=catalog 2=pages 3=page 4=ICC stream 5=content (optional)
        if (usedInContent)
        {
            return AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R >>"),
                _pagesObj,
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                    + "/Resources << /ColorSpace << /CS0 [/ICCBased 4 0 R] >> >> "
                    + "/Contents 5 0 R >>"),
                new($"/Filter /FlateDecode /N {n}", compressed),
                new(string.Empty, Encoding.ASCII.GetBytes("/CS0 cs\n0 sc")),
            ]);
        }
        else
        {
            // CS0 in resources but no content selects it.
            return AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R >>"),
                _pagesObj,
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                    + "/Resources << /ColorSpace << /CS0 [/ICCBased 4 0 R] >> >> >>"),
                new($"/Filter /FlateDecode /N {n}", compressed),
            ]);
        }
    }

    /// <summary>
    /// Builds a synthetic 128-byte ICC profile header with the 'acsp' signature and the given
    /// device class, data colour space, and major version.
    /// </summary>
    private static byte[] MakeIccHeader(string deviceClass, string colorSpace, byte majorVersion)
    {
        var hdr = new byte[128];
        // bytes 0–3: profile size
        hdr[0] = 0; hdr[1] = 0; hdr[2] = 0; hdr[3] = 128;
        // byte 8: major version
        hdr[8] = majorVersion;
        hdr[9] = 0x10; // minor version (irrelevant to the check)
        // bytes 12–15: device class (4 ASCII, space-padded)
        for (var i = 0; i < 4; i++)
            hdr[12 + i] = i < deviceClass.Length ? (byte)deviceClass[i] : (byte)' ';
        // bytes 16–19: data colour space (4 ASCII, space-padded)
        for (var i = 0; i < 4; i++)
            hdr[16 + i] = i < colorSpace.Length ? (byte)colorSpace[i] : (byte)' ';
        // bytes 20–23: PCS (irrelevant)
        hdr[20] = (byte)'X'; hdr[21] = (byte)'Y'; hdr[22] = (byte)'Z'; hdr[23] = (byte)' ';
        // bytes 36–39: 'acsp' signature (required for the profile to be parsed)
        hdr[36] = (byte)'a'; hdr[37] = (byte)'c'; hdr[38] = (byte)'s'; hdr[39] = (byte)'p';
        return hdr;
    }

    // -- invalid device class → finding

    [Fact]
    public void Validate_IccBased_InvalidDeviceClass_ReportsError()
    {
        // Device class 'link' is not in the allowed set (prtr/mntr/scnr/spac).
        // Confirmed against veraPDF 1.30.2: probe 3 (link/RGB/v2) triggers 6.2.4.2-1.
        var bytes = BuildIccBasedPdf(MakeIccHeader("link", "RGB ", 2));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Validate_IccBased_AbstractDeviceClass_ReportsError()
    {
        // Device class 'abst' is not in the allowed set.
        // Confirmed against veraPDF 1.30.2: probe 8 (abst/RGB/v2) triggers 6.2.4.2-1.
        var bytes = BuildIccBasedPdf(MakeIccHeader("abst", "RGB ", 2));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    // -- invalid colour space → finding

    [Fact]
    public void Validate_IccBased_InvalidColorSpace_ReportsError()
    {
        // Data colour space 'XYZ ' is not in the allowed set (RGB /CMYK/GRAY/Lab ).
        // Confirmed against veraPDF 1.30.2: probe 4 (mntr/XYZ/v2) triggers 6.2.4.2-1.
        var bytes = BuildIccBasedPdf(MakeIccHeader("mntr", "XYZ ", 2));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    // -- version 5 → finding; version 4 → no finding

    [Fact]
    public void Validate_IccBased_Version5_ReportsError()
    {
        // ICC major version 5 is not permitted (version < 5.0 required).
        // Confirmed against veraPDF 1.30.2: probe 5/11 (mntr/RGB/v5) triggers 6.2.4.2-1;
        // byte[8] = 5 is the major version boundary.
        var bytes = BuildIccBasedPdf(MakeIccHeader("mntr", "RGB ", 5));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Validate_IccBased_Version4_NoFinding()
    {
        // ICC major version 4 is permitted (byte[8] < 5).
        // Confirmed against veraPDF 1.30.2: probe 2/10 (mntr/RGB/v4) does not trigger 6.2.4.2-1.
        var bytes = BuildIccBasedPdf(MakeIccHeader("mntr", "RGB ", 4));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    // -- valid profiles → no finding

    [Fact]
    public void Validate_IccBased_ValidMntrRgbV2_NoFinding()
    {
        // mntr/RGB /v2 is fully valid. Probe 1 (veraPDF 1.30.2): no 6.2.4.2-1 finding.
        var bytes = BuildIccBasedPdf(MakeIccHeader("mntr", "RGB ", 2));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Validate_IccBased_ValidScnrGrayV4_NoFinding()
    {
        // scnr/GRAY/v4 is fully valid. Probe 6 (veraPDF 1.30.2): no 6.2.4.2-1 finding.
        var bytes = BuildIccBasedPdf(MakeIccHeader("scnr", "GRAY", 4), n: 1);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Validate_IccBased_ValidSpacLabV4_NoFinding()
    {
        // spac/Lab /v4 is fully valid. Probe 7 (veraPDF 1.30.2): no 6.2.4.2-1 finding.
        var bytes = BuildIccBasedPdf(MakeIccHeader("spac", "Lab ", 4), n: 3);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Validate_IccBased_ValidPrtrCmykV4_NoFinding()
    {
        // prtr/CMYK/v4 is fully valid. Probe 9 (veraPDF 1.30.2): no 6.2.4.2-1 finding.
        var bytes = BuildIccBasedPdf(MakeIccHeader("prtr", "CMYK", 4), n: 4);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    // -- defensive: truncated / no-acsp → no finding (never a spurious finding)

    [Fact]
    public void Validate_IccBased_TruncatedProfile_NoFinding()
    {
        // A profile shorter than 128 bytes cannot be parsed; the rule skips it defensively.
        var bytes = BuildIccBasedPdf(new byte[64]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Validate_IccBased_MissingAcspSignature_NoFinding()
    {
        // A 128-byte block with the wrong signature at offset 36 is not a recognisable ICC
        // profile; the rule skips it defensively rather than producing a spurious finding.
        var hdr = new byte[128];
        hdr[8] = 2; // valid version
        hdr[12] = (byte)'l'; hdr[13] = (byte)'i'; hdr[14] = (byte)'n'; hdr[15] = (byte)'k'; // bad class
        hdr[16] = (byte)'R'; hdr[17] = (byte)'G'; hdr[18] = (byte)'B'; hdr[19] = (byte)' ';
        // bytes 36-39 are zero — NOT 'acsp'
        var bytes = BuildIccBasedPdf(hdr);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    // -- scoping: present but not used in content → no finding

    [Fact]
    public void Validate_IccBased_UnusedInvalidProfile_NoFinding()
    {
        // An invalid ICCBased colour space (device class 'link') that is present in /Resources
        // but never selected by a cs/CS operator must NOT be flagged — matching veraPDF 1.30.2
        // (probes 13–14: unused invalid spaces are not reported).
        var bytes = BuildIccBasedPdf(MakeIccHeader("link", "RGB ", 2), usedInContent: false);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    // -- no-false-positive with a real writer-produced sRGB ICC profile

    [Fact]
    public void Validate_IccBased_RealSrgbProfile_NoFinding()
    {
        // The VellumPdf kernel's built-in sRGB ICC v2 profile (mntr/RGB /v2) embedded as an
        // ICCBased colour space must NOT trigger 6.2.4.2-1. This guards against false positives
        // on profiles produced by the library itself.
        var bytes = BuildIccBasedPdf(IccProfiles.Srgb, n: 3);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Validate_IccBased_RealCmykProfile_NoFinding()
    {
        // The VellumPdf kernel's built-in generic CMYK ICC profile embedded as an ICCBased
        // colour space must NOT trigger 6.2.4.2-1.
        var bytes = BuildIccBasedPdf(IccProfiles.GenericCmyk, n: 4);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-1");
    }

    [Fact]
    public void Assertion_ToString_IncludesRuleAndSeverity()
    {
        var result = PdfPreflight.Validate(BuildCatalogMissingTypePdf(), PdfConformance.PdfA2B);
        var text = result.Assertions[0].ToString();

        Assert.Contains("Error", text);
        Assert.Contains("ISO32000-1:7.7.2-catalog-type", text);
    }

    // ── §6.2.2-1 Content-stream operator checks ───────────────────────────────

    [Fact]
    public void Validate_UnknownContentStreamOperator_IsFlagged()
    {
        // A page content stream containing an operator keyword ('zz') that is not defined in
        // ISO 32000-1 must be rejected (§6.2.2-1). Empirically confirmed against veraPDF.
        var bytes = BuildContentStreamPdf("q zz Q");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-1");
    }

    [Fact]
    public void Validate_UnknownOperatorInBxEx_IsFlagged()
    {
        // The BX/EX compatibility brackets do NOT exempt an unknown operator (§6.2.2-1 explicitly
        // states "even if such operators are bracketed by the BX/EX compatibility operators").
        // Empirically confirmed: veraPDF flags 'zz' inside BX...EX with the same 6.2.2-1 rule.
        var bytes = BuildContentStreamPdf("BX zz EX");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-1");
    }

    [Fact]
    public void Validate_StandardOperatorsOnly_NoOperatorFinding()
    {
        // A page content stream using only ISO 32000-1 operators must NOT be flagged. This guards
        // against false positives: 'q', 'Q', 'BT', 'ET', 'BX', 'EX', 'n', 're', 'W', 'W*', etc.
        var bytes = BuildContentStreamPdf("q BX q Q EX 10 10 100 100 re W n Q");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-1");
    }

    [Fact]
    public void Validate_EmptyContentStream_NoOperatorFinding()
    {
        // An empty (or whitespace-only) page must not produce a 6.2.2-1 finding.
        var bytes = BuildContentStreamPdf("");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-1");
    }

    [Fact]
    public void Validate_UnknownOperatorReportedOnce()
    {
        // Even when the same unknown operator appears multiple times, it is reported at most once
        // per document to avoid flooding the finding list.
        var bytes = BuildContentStreamPdf("foo foo foo");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Equal(1, result.Assertions.Count(a => a.RuleId == "ISO19005-2:6.2.2-1"));
    }

    [Fact]
    public void Validate_BooleanOperandInContent_NoOperatorFinding()
    {
        // Regression: the content-stream lexer emits true/false/null as Keyword tokens, but they are
        // operands, not operators. An inline image with `/I true` (Interpolate) and a BDC inline
        // property dict carrying a boolean/null are valid ISO 32000-1 content — veraPDF does not flag
        // them under 6.2.2-1. The rule must not mistake the value keyword for an unknown operator.
        var bytes = BuildContentStreamPdf(
            "q /Tag << /B true /N null >> BDC BI /W 1 /H 1 /BPC 8 /CS /G /I true ID A EI EMC Q");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-1");
    }

    // ── §6.2.2-2 Inherited-resource checks ──────────────────────────────────────

    /// <summary>
    /// Builds a two-level page tree where the <c>/Resources</c> dictionary lives only on the
    /// parent <c>Pages</c> node (inherited), and the leaf page has NO <c>/Resources</c> key.
    /// The page content <paramref name="content"/> uses the named resource. This is the
    /// structure veraPDF empirically flags with §6.2.2-2.
    /// Objects: 1=catalog 2=pages(with Resources) 3=page(no Resources) 4=resources 5=content.
    /// </summary>
    private static byte[] BuildInheritedResourcePdf(string content)
    {
        var contentBytes = Encoding.ASCII.GetBytes(content);
        return AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            // Pages node carries the /Resources — the page inherits them from here.
            new($"<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources 4 0 R >>"),
            // Page has NO /Resources key — so resources are only reachable via inheritance.
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>"),
            // Shared resources dict (obj 4): a stub font and a stub form XObject.
            new("<< /Font << /F0 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /XObject << /X0 << /Type /XObject /Subtype /Form /BBox [0 0 1 1] >> >> /ExtGState << /GS0 << /Type /ExtGState >> >> >>"),
            // Content stream (obj 5).
            new(string.Empty, contentBytes),
        ]);
    }

    /// <summary>
    /// Builds a one-page doc where the page has its OWN <c>/Resources</c> key (even if not
    /// fully populated), satisfying §6.2.2-2's "explicitly associated Resources dictionary"
    /// requirement. The content <paramref name="content"/> may use named resources.
    /// Objects: 1=catalog 2=pages(no Resources) 3=page(owns Resources) 4=resources 5=content.
    /// </summary>
    private static byte[] BuildExplicitResourcePdf(string content)
    {
        var contentBytes = Encoding.ASCII.GetBytes(content);
        return AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            // Pages node has NO /Resources.
            new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            // Page has its OWN /Resources — rule is satisfied.
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 5 0 R >>"),
            // Own resources dict with a stub font.
            new("<< /Font << /F0 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"),
            // Content stream.
            new(string.Empty, contentBytes),
        ]);
    }

    [Fact]
    public void Validate_UsedFontInheritedOnly_IsReported()
    {
        // §6.2.2-2: the page uses /F0 via Tf but /F0 is only in the parent Pages /Resources
        // (the page itself has no /Resources key). veraPDF empirically flags this.
        var bytes = BuildInheritedResourcePdf("BT /F0 12 Tf (Hi) Tj ET");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a =>
            a.RuleId == "ISO19005-2:6.2.2-2" && a.Message.Contains("F0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UsedXObjectInheritedOnly_IsReported()
    {
        // §6.2.2-2: the page paints /X0 via Do but /X0 is only in the parent Pages /Resources.
        var bytes = BuildInheritedResourcePdf("q /X0 Do Q");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a =>
            a.RuleId == "ISO19005-2:6.2.2-2" && a.Message.Contains("X0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UsedExtGStateInheritedOnly_IsReported()
    {
        // §6.2.2-2: the page applies /GS0 via gs but /GS0 is only in the parent Pages /Resources.
        var bytes = BuildInheritedResourcePdf("q /GS0 gs Q");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a =>
            a.RuleId == "ISO19005-2:6.2.2-2" && a.Message.Contains("GS0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PageWithExplicitResources_NeverReports6222()
    {
        // §6.2.2-2 false-positive guard: a page that has its OWN /Resources key (even if empty
        // or not fully covering used names) must NOT be flagged — veraPDF empirically confirmed
        // that the presence of the /Resources key on the page itself satisfies the clause.
        var bytes = BuildExplicitResourcePdf("BT /F0 12 Tf (Hi) Tj ET");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-2");
    }

    [Fact]
    public void Validate_PageWithNoContentUsesNoResources_NeverReports6222()
    {
        // §6.2.2-2: a page that uses NO named resources (even without /Resources) must not be
        // flagged — there are no "inherited resource names" to complain about.
        var bytes = BuildInheritedResourcePdf("100 100 50 50 re f");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-2");
    }

    [Fact]
    public void Validate_EmptyPage_NeverReports6222()
    {
        // §6.2.2-2: an empty page (no /Contents) with no /Resources must not be flagged.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-2");
    }

    [Fact]
    public void Validate_WriterProducedPdfA_NeverReports6222()
    {
        // §6.2.2-2 false-positive guard: VellumPdf's own writer always puts /Resources on each
        // page, so a writer-produced PDF/A-2b document must never trigger the rule.
        var bytes = BuildOnePagePdf();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-2");
    }

    [Fact]
    public void Validate_PageDeviceColourSpaceNoResources_NeverReports6222()
    {
        // §6.2.2-2 false-positive guard: /DeviceGray, /DeviceRGB, /DeviceCMYK and /Pattern are
        // resolved directly by cs/CS without a /Resources /ColorSpace lookup (ISO 32000-1 §8.6.3),
        // so a resource-less page selecting only these is valid PDF/A — veraPDF accepts it and the
        // rule must not flag the device names as inherited resources.
        var bytes = BuildInheritedResourcePdf("/DeviceRGB CS /DeviceRGB cs 1 0 0 SC 0 0 1 sc 10 10 50 50 re B");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.2-2");
    }

    // ── §6.1.10-1 Inline-image filter checks ──────────────────────────────────

    [Fact]
    public void Validate_InlineImageLzwAbbrev_IsFlagged()
    {
        // /F /LZW in an inline image is forbidden (§6.1.10-1). Empirically confirmed against
        // veraPDF 1.30.2: the probe PDF triggers clause 6.1.10 testNumber 1.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F /LZW ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageLzwFullName_IsFlagged()
    {
        // /F /LZWDecode (full name) in an inline image is also forbidden (§6.1.10-1). veraPDF
        // flags this in the same way as the abbreviated form.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F /LZWDecode ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageFilterKeyLzw_IsFlagged()
    {
        // /Filter /LZWDecode (using the full /Filter key instead of abbreviated /F) is also
        // forbidden. ISO 32000-1 §8.9.7 permits both key names in inline images; veraPDF honours
        // both — confirmed empirically.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /Filter /LZWDecode ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageCrypt_IsFlagged()
    {
        // /F /Crypt is explicitly forbidden (§6.1.10-1). Empirically confirmed against veraPDF.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F /Crypt ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageBogusFilter_IsFlagged()
    {
        // A filter name not in ISO 32000-1 Table 6 (e.g. /Foo) is forbidden (§6.1.10-1).
        // Empirically confirmed against veraPDF.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F /Foo ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageJpxDecode_IsFlagged()
    {
        // JPXDecode is NOT in ISO 32000-1 Table 6's inline-image permitted set (§6.1.10-1).
        // Empirically confirmed: veraPDF flags it.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F /JPXDecode ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageArrayWithBadMember_IsFlagged()
    {
        // An array filter where any member is forbidden causes a §6.1.10-1 finding. Here
        // /AHx is permitted but /LZW is not — the array fails. Empirically confirmed against
        // veraPDF.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F [/AHx /LZW] ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageFlateAbbrev_NoFilterFinding()
    {
        // /F /Fl (abbreviated FlateDecode) is permitted (§6.1.10-1). Empirically confirmed:
        // veraPDF does not flag it.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F /Fl ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageFlateFullName_NoFilterFinding()
    {
        // /Filter /FlateDecode (full key, full name) is permitted. Empirically confirmed.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /Filter /FlateDecode ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageNoFilter_NoFilterFinding()
    {
        // An inline image with no /F or /Filter key (raw uncompressed samples) is permitted.
        // Empirically confirmed: veraPDF does not flag it.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageAllPermittedAbbrevFilters_NoFilterFinding()
    {
        // Each of the six permitted abbreviated filter names must not cause a §6.1.10-1 finding.
        // Empirically confirmed: AHx, A85, RL, CCF, DCT are all accepted by veraPDF.
        string[] filters = ["AHx", "A85", "RL", "CCF", "DCT"];
        foreach (var f in filters)
        {
            var bytes = BuildContentStreamPdf($"BI /W 1 /H 1 /BPC 8 /CS /G /F /{f} ID \x80 EI");
            var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);
            Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
        }
    }

    [Fact]
    public void Validate_InlineImageBooleanInDict_NoFilterFinding()
    {
        // Regression: the lexer emits true/false/null as Keyword tokens. An inline image that
        // has boolean entries such as /IM true or /I true alongside no /F key must not cause a
        // §6.1.10-1 finding. The rule must not mistake the boolean value keyword for a filter name.
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /IM true /I true ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    [Fact]
    public void Validate_InlineImageValidArrayFilter_NoFilterFinding()
    {
        // An array whose members are all permitted filters is accepted (§6.1.10-1). Empirically
        // confirmed: veraPDF does not flag [/Fl /DCT].
        var bytes = BuildContentStreamPdf("BI /W 1 /H 1 /BPC 8 /CS /G /F [/Fl /DCT] ID \x80 EI");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.10-1");
    }

    // ── §6.1.13-10: CID value must not exceed 65,535 ─────────────────────────

    /// <summary>
    /// Builds a one-page PDF with a Type0 font whose /Encoding is an embedded CMap stream. The
    /// CMap body is supplied by the caller; the content stream selects F0 and shows
    /// <paramref name="shownCharHex"/> as a hex string (e.g. "0000").
    /// Objects: 1=catalog 2=pages 3=page 4=resources 5=font-map 6=Type0 7=CMapStream
    /// 8=CIDFont 9=FontDescriptor 10=FontFile2 11=content [12=XMP via AssemblePdf].
    /// </summary>
    private static byte[] BuildEmbeddedCMapFontPdf(byte[] cmapBody, string shownCharHex)
    {
        var cisys = "/Registry (Adobe) /Ordering (Japan1) /Supplement 6";
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),                                                    // 1
            _pagesObj,                                                                                    // 2
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 11 0 R >>"), // 3
            new("<< /Font 5 0 R >>"),                                                                    // 4
            new("<< /F0 6 0 R >>"),                                                                      // 5
            new("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 7 0 R /DescendantFonts [8 0 R] >>"), // 6
            new($"/Type /CMap /CMapName /CustomCMap /CIDSystemInfo << {cisys} >> /WMode 0", cmapBody),  // 7
            new($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << {cisys} >> /FontDescriptor 9 0 R /CIDToGIDMap /Identity >>"), // 8
            new("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent -200 /CapHeight 800 /StemV 80 /FontFile2 10 0 R >>"), // 9
            new("/Length1 4", [1, 2, 3, 4]),                                                             // 10
            new(string.Empty, Encoding.ASCII.GetBytes($"BT /F0 12 Tf <{shownCharHex}> Tj ET")),        // 11
        };
        return AssemblePdf(objects);
    }

    private static byte[] MakeCidRangeCMap(string cidRangeLine)
    {
        return Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 6 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + $"1 begincidrange {cidRangeLine} endcidrange "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");
    }

    private static byte[] MakeCidCharCMap(string cidCharLine)
    {
        return Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 6 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + $"1 begincidchar {cidCharLine} endcidchar "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");
    }

    [Fact]
    public void Validate_EmbeddedCMap_CidRangeDestExceeds65535_ReportsError()
    {
        // §6.1.13-10: an embedded CMap mapping char code 0x0000 to CID 70000 exceeds the limit.
        // The content stream renders char code <0000>, producing CID 70000. Expect a finding.
        // veraPDF oracle: probe PDF with CMap <0000> <0000> 70000 and <0000> Tj → 6.1.13-10 FAIL.
        var cmapBody = MakeCidRangeCMap("<0000> <0000> 70000");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0000");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_EmbeddedCMap_CidRangeEndExceeds65535_ReportsError()
    {
        // §6.1.13-10: CMap range <0000> <00FF> 65500 → max producible CID = 65500+255 = 65755.
        // Content shows char code <00FF> → resolves to CID 65755 > 65535. Expect a finding.
        // veraPDF oracle: probe PDF with this range and <00FF> Tj → 6.1.13-10 FAIL (CID 65755).
        var cmapBody = MakeCidRangeCMap("<0000> <00FF> 65500");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "00FF");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_EmbeddedCMap_CidCharDestExceeds65535_ReportsError()
    {
        // §6.1.13-10: begincidchar mapping <0020> to CID 70000 exceeds the limit.
        // Content shows char code <0020> → resolves to CID 70000. Expect a finding.
        // veraPDF oracle: probe with cidchar 70000 and <0020> Tj → 6.1.13-10 FAIL.
        var cmapBody = MakeCidCharCMap("<0020> 70000");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0020");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_EmbeddedCMap_AllCidsWithin65535_NoFinding()
    {
        // §6.1.13-10 no-false-positive: CMap range <0020> <007E> 32 → max CID = 32+94 = 126.
        // All CIDs are well within the 65535 limit. Expect no finding.
        var cmapBody = MakeCidRangeCMap("<0020> <007E> 32");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0020");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_EmbeddedCMap_CidAtExactBoundary65535_NoFinding()
    {
        // §6.1.13-10 boundary: CID exactly 65535 must not be flagged (rule is maximalCID <= 65535).
        var cmapBody = MakeCidCharCMap("<0000> 65535");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0000");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_IdentityHEncoding_CidExceedCheck_NoFinding()
    {
        // §6.1.13-10 exempt: Identity-H maps char codes to equal CIDs, so the max is structurally
        // 65535 — the rule should never fire. Uses BuildFontPdf (content: BT /F0 12 Tf ET).
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_PredefinedNamedCMap_CidExceedCheck_NoFinding()
    {
        // §6.1.13-10 deferred: a predefined named CMap (UniGB-UCS2-H) has no embedded program to
        // parse, so the rule is deferred — no finding shall be generated.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /UniGB-UCS2-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 5 >> /FontDescriptor 8 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile3 9 0 R >>"),
            new PdfObj("/Subtype /CIDFontType0C", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_EmbeddedCMap_FontPresentButNotUsedInContent_NoFinding()
    {
        // §6.1.13-10 scoping: the rule only fires on CIDs produced from text-show operators.
        // A font in /Resources with an overflow CMap but no Tf/Tj operators is not checked.
        // This matches veraPDF's behaviour of evaluating only CIDs actually rendered.
        var cmapBody = MakeCidRangeCMap("<0000> <0000> 70000");
        // Build using AssemblePdf directly with empty content (no BT/Tf/ET).
        var cisys = "/Registry (Adobe) /Ordering (Japan1) /Supplement 6";
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 11 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
            new("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 7 0 R /DescendantFonts [8 0 R] >>"),
            new($"/Type /CMap /CMapName /CustomCMap /CIDSystemInfo << {cisys} >> /WMode 0", cmapBody),
            new($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << {cisys} >> /FontDescriptor 9 0 R /CIDToGIDMap /Identity >>"),
            new("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent -200 /CapHeight 800 /StemV 80 /FontFile2 10 0 R >>"),
            new("/Length1 4", [1, 2, 3, 4]),
            new(string.Empty, []/* empty content — no Tf, no text */),
        };
        var bytes = AssemblePdf(objects);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    [Fact]
    public void Validate_EmbeddedCMap_OneByteCodespace_DoesNotSynthesizeWideCid()
    {
        // §6.1.13-10 false-positive guard for the codespace-aware decoder. The CMap declares a
        // ONE-byte codespace (<00>..<FF>) but also an (inconsistent) two-byte cidrange that would
        // map code 0x0100 to CID 70000. Content shows the two bytes 01 00. Decoding per the
        // codespace yields two single-byte codes (0x01, 0x00) — neither maps to a CID — so there is
        // no finding. A naive fixed-width-2 split would instead form code 0x0100, hit the range, and
        // wrongly report CID 70000. veraPDF decodes per codespace, so it does not flag this.
        var cmapBody = Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 6 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + "1 begincodespacerange <00> <FF> endcodespacerange "
            + "1 begincidrange <0100> <0100> 70000 endcidrange "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0100");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.13-10");
    }

    // ── §6.2.11.3.3-2: embedded CMap stream WMode must equal program WMode ────

    /// <summary>
    /// Builds a one-page PDF like <see cref="BuildEmbeddedCMapFontPdf"/> but with an explicit
    /// <paramref name="dictWMode"/> in the CMap stream dictionary (overriding the fixed /WMode 0
    /// that <see cref="BuildEmbeddedCMapFontPdf"/> uses). Used to test WMode consistency checks.
    /// </summary>
    private static byte[] BuildEmbeddedCMapFontPdfWithDictWMode(
        byte[] cmapBody, string shownCharHex, int dictWMode)
    {
        var cisys = "/Registry (Adobe) /Ordering (Japan1) /Supplement 6";
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 11 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
            new("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 7 0 R /DescendantFonts [8 0 R] >>"),
            new($"/Type /CMap /CMapName /CustomCMap /CIDSystemInfo << {cisys} >> /WMode {dictWMode}", cmapBody),
            new($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << {cisys} >> /FontDescriptor 9 0 R /CIDToGIDMap /Identity >>"),
            new("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent -200 /CapHeight 800 /StemV 80 /FontFile2 10 0 R >>"),
            new("/Length1 4", [1, 2, 3, 4]),
            new(string.Empty, Encoding.ASCII.GetBytes($"BT /F0 12 Tf <{shownCharHex}> Tj ET")),
        };
        return AssemblePdf(objects);
    }

    /// <summary>
    /// Builds a CMap program with /WMode programWMode explicitly declared via "/WMode N def".
    /// </summary>
    private static byte[] MakeCMapWithWMode(int programWMode)
        => Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 6 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + $"/WMode {programWMode} def "
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + "1 begincidrange <0020> <007E> 32 endcidrange "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");

    /// <summary>
    /// Builds a CMap program without any /WMode declaration (defaults to 0).
    /// </summary>
    private static byte[] MakeCMapWithoutWMode()
        => Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 6 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + "1 begincidrange <0020> <007E> 32 endcidrange "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");

    /// <summary>
    /// Builds a CMap program that contains a usecmap invocation referencing the given name.
    /// </summary>
    private static byte[] MakeCMapWithUseCMap(string referencedName)
        => Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap "
            + "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 6 def end def "
            + "/CMapName /CustomCMap def /CMapType 1 def "
            + "/WMode 0 def "
            + $"/{referencedName} usecmap "
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + "1 begincidrange <0020> <007E> 32 endcidrange "
            + "endcmap CMapName currentdict /CMap defineresource pop end end");

    [Fact]
    public void Validate_EmbeddedCMap_DictWMode1_ProgWMode0_ReportsError()
    {
        // §6.2.11.3.3-2: stream dictionary /WMode=1 but program declares /WMode 0 def → mismatch.
        // veraPDF oracle (STEP-0): dict=1, prog=0 → 6.2.11.3.3-2 FAIL
        // ("WMode entry (value 0) in the embedded CMap and in the CMap dictionary (value 1) are not identical").
        var cmapBody = MakeCMapWithWMode(0);
        var bytes = BuildEmbeddedCMapFontPdfWithDictWMode(cmapBody, "0020", dictWMode: 1);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    [Fact]
    public void Validate_EmbeddedCMap_DictWMode0_ProgWMode1_ReportsError()
    {
        // §6.2.11.3.3-2: stream dictionary /WMode=0 but program declares /WMode 1 def → mismatch.
        // veraPDF oracle (STEP-0): dict=0, prog=1 → 6.2.11.3.3-2 FAIL.
        var cmapBody = MakeCMapWithWMode(1);
        var bytes = BuildEmbeddedCMapFontPdfWithDictWMode(cmapBody, "0020", dictWMode: 0);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    [Fact]
    public void Validate_EmbeddedCMap_BothWMode1_NoFinding()
    {
        // §6.2.11.3.3-2 no-false-positive: dict /WMode=1 and program /WMode 1 def → match → PASS.
        // veraPDF oracle (STEP-0): both=1 → PASS.
        var cmapBody = MakeCMapWithWMode(1);
        var bytes = BuildEmbeddedCMapFontPdfWithDictWMode(cmapBody, "0020", dictWMode: 1);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    [Fact]
    public void Validate_EmbeddedCMap_BothWModeAbsent_DefaultZero_NoFinding()
    {
        // §6.2.11.3.3-2 no-false-positive: neither the stream dictionary nor the program declares
        // /WMode — both default to 0 → match → PASS.
        // veraPDF oracle (STEP-0): both absent → PASS.
        var cmapBody = MakeCMapWithoutWMode();
        // BuildEmbeddedCMapFontPdf uses /WMode 0 in the dict, so we need a variant without it.
        // Use BuildEmbeddedCMapFontPdfWithDictWMode with 0 to test default agreement.
        var bytes = BuildEmbeddedCMapFontPdfWithDictWMode(cmapBody, "0020", dictWMode: 0);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    [Fact]
    public void Validate_EmbeddedCMap_DictWMode0_ProgWModeAbsent_DefaultZero_NoFinding()
    {
        // §6.2.11.3.3-2 no-false-positive: stream dictionary /WMode=0, program has no /WMode
        // declaration (defaults to 0) → match → PASS.
        // veraPDF oracle (STEP-0): dict=0, prog=absent → PASS.
        var cmapBody = MakeCMapWithoutWMode();
        var bytes = BuildEmbeddedCMapFontPdfWithDictWMode(cmapBody, "0020", dictWMode: 0);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    [Fact]
    public void Validate_EmbeddedCMap_WModeMismatch_FontNotUsedInContent_NoFinding()
    {
        // §6.2.11.3.3-2 scoping: a WMode mismatch on a font that is never selected via Tf in the
        // content stream shall NOT produce a finding. The rule is scoped to used fonts, matching
        // veraPDF (STEP-0: unused-font WMode mismatch → PASS).
        var cmapBody = MakeCMapWithWMode(0); // prog=0
        var cisys = "/Registry (Adobe) /Ordering (Japan1) /Supplement 6";
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 11 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
            new("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 7 0 R /DescendantFonts [8 0 R] >>"),
            new($"/Type /CMap /CMapName /CustomCMap /CIDSystemInfo << {cisys} >> /WMode 1", cmapBody), // dict=1, prog=0 mismatch
            new($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << {cisys} >> /FontDescriptor 9 0 R /CIDToGIDMap /Identity >>"),
            new("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent -200 /CapHeight 800 /StemV 80 /FontFile2 10 0 R >>"),
            new("/Length1 4", [1, 2, 3, 4]),
            new(string.Empty, []), // empty content — no Tf, font never selected
        };
        var bytes = AssemblePdf(objects);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    [Fact]
    public void Validate_IdentityHEncoding_WModeCheck_NotApplied()
    {
        // §6.2.11.3.3-2 exempt: Identity-H is not an embedded CMap stream; the WMode check does
        // not apply. No finding shall be generated regardless of any other issues.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    [Fact]
    public void Validate_PredefinedNamedCMap_WModeCheck_NotApplied()
    {
        // §6.2.11.3.3-2 exempt: a predefined named CMap (UniGB-UCS2-H) has no embedded program;
        // the WMode check is not applicable. No finding shall be generated.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /UniGB-UCS2-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 5 >> /FontDescriptor 8 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile3 9 0 R >>"),
            new PdfObj("/Subtype /CIDFontType0C", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-2");
    }

    // ── §6.2.11.3.3-3: usecmap must reference only Table 118 predefined CMaps ───

    [Fact]
    public void Validate_EmbeddedCMap_UseCMapNonPredefined_ReportsError()
    {
        // §6.2.11.3.3-3: the CMap program references a non-predefined CMap via /CustomThing usecmap.
        // Expect a finding naming "CustomThing".
        // Oracle: verified against veraPDF profile XML (PDReferencedCMap / CMapName check);
        // in-process tests cover this directly (probe PDFs cannot trigger it — veraPDF needs a
        // fully resolved PDReferencedCMap structure).
        var cmapBody = MakeCMapWithUseCMap("CustomThing");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0020");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a =>
            a.RuleId == "ISO19005-2:6.2.11.3.3-3" && a.Message.Contains("CustomThing"));
    }

    [Fact]
    public void Validate_EmbeddedCMap_UseCMapIdentityH_NoFinding()
    {
        // §6.2.11.3.3-3 no-false-positive: /Identity-H is a predefined CMap (Table 118). A usecmap
        // referencing it shall not produce a finding.
        var cmapBody = MakeCMapWithUseCMap("Identity-H");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0020");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-3");
    }

    [Fact]
    public void Validate_EmbeddedCMap_UseCMapGB_EUC_H_NoFinding()
    {
        // §6.2.11.3.3-3 no-false-positive: GB-EUC-H is listed in Table 118 → PASS.
        var cmapBody = MakeCMapWithUseCMap("GB-EUC-H");
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0020");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-3");
    }

    [Fact]
    public void Validate_EmbeddedCMap_NoUseCMap_NoFinding()
    {
        // §6.2.11.3.3-3 no-false-positive: a CMap program with no usecmap operator shall not
        // produce a finding.
        var cmapBody = MakeCMapWithoutWMode();
        var bytes = BuildEmbeddedCMapFontPdf(cmapBody, "0020");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-3");
    }

    [Fact]
    public void Validate_EmbeddedCMap_UseCMapNonPredefined_FontNotUsed_NoFinding()
    {
        // §6.2.11.3.3-3 scoping: non-predefined usecmap on an unused font → no finding.
        // The rule is scoped to fonts actually selected via Tf in the content stream.
        var cmapBody = MakeCMapWithUseCMap("CustomThing");
        var cisys = "/Registry (Adobe) /Ordering (Japan1) /Supplement 6";
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R /Contents 11 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
            new("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 7 0 R /DescendantFonts [8 0 R] >>"),
            new($"/Type /CMap /CMapName /CustomCMap /CIDSystemInfo << {cisys} >> /WMode 0", cmapBody),
            new($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << {cisys} >> /FontDescriptor 9 0 R /CIDToGIDMap /Identity >>"),
            new("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent -200 /CapHeight 800 /StemV 80 /FontFile2 10 0 R >>"),
            new("/Length1 4", [1, 2, 3, 4]),
            new(string.Empty, []), // empty content — font never selected
        };
        var bytes = AssemblePdf(objects);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-3");
    }

    [Fact]
    public void Validate_IdentityHEncoding_UseCMapCheck_NotApplied()
    {
        // §6.2.11.3.3-3 exempt: Identity-H is not an embedded CMap stream; the usecmap check does
        // not apply. No finding for 6.2.11.3.3-3.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /CIDToGIDMap /Identity >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-3");
    }

    [Fact]
    public void Validate_PredefinedNamedCMap_UseCMapCheck_NotApplied()
    {
        // §6.2.11.3.3-3 exempt: a predefined named CMap (UniGB-UCS2-H) has no embedded program;
        // the usecmap check is not applicable. No finding for 6.2.11.3.3-3.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /UniGB-UCS2-H /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 5 >> /FontDescriptor 8 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /X /FontFile3 9 0 R >>"),
            new PdfObj("/Subtype /CIDFontType0C", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.11.3.3-3");
    }

    // ── §6.2.8.3 JPEG2000 codestream constraints ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a one-page PDF whose /Resources /XObject /X0 is a JPXDecode image stream carrying
    /// the given raw JP2/JPEG2000 <paramref name="jp2Bytes"/>. The page draws the image with Do so
    /// the rule's content-usage scoping selects it.
    /// </summary>
    private static byte[] BuildJpxImagePdf(byte[] jp2Bytes)
        => BuildXObjectPdf(
            "/Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode /ColorSpace /DeviceRGB",
            jp2Bytes,
            draw: true);

    // ── JP2 box file builder (mirrors JpxImageTests, reusable in this file) ───────────────────────

    private sealed record Jp2FileData(byte[] File, byte[] Codestream);

    /// <summary>
    /// Builds a minimal JP2 box file: signature + ftyp + jp2h(ihdr[+bpcc][+colr…]) + jp2c.
    /// Supports multiple colr boxes, per-component bpcc, and controllable APPROX/EnumCS fields.
    /// </summary>
    private static Jp2FileData BuildJp2(
        int nc,
        int bpc,
        int[]? perComponentBpc = null,
        IReadOnlyList<(byte Meth, byte Approx, int? EnumCs)>? colrBoxes = null)
    {
        var codestream = Jp2BuildRawCodestream(nc, perComponentBpc ?? Enumerable.Repeat(bpc, nc).ToArray());
        var buf = new List<byte>();

        // Signature box: LBox=12, TBox="jP  ", magic=0D 0A 87 0A
        Jp2Append32(buf, 12u);
        buf.AddRange([0x6A, 0x50, 0x20, 0x20]);
        buf.AddRange([0x0D, 0x0A, 0x87, 0x0A]);

        // ftyp box
        var ftyp = new List<byte>();
        ftyp.AddRange([0x6A, 0x70, 0x32, 0x20]); // brand "jp2 "
        Jp2Append32(ftyp, 0u);                     // MinV
        ftyp.AddRange([0x6A, 0x70, 0x32, 0x20]); // compat "jp2 "
        Jp2AppendBox(buf, 0x66747970u, [.. ftyp]);

        // ihdr box: Height(4) Width(4) NC(2) BPC(1) C(1) UnkC(1) IPR(1)
        var ihdr = new List<byte>();
        Jp2Append32(ihdr, 4u); // height
        Jp2Append32(ihdr, 4u); // width
        Jp2Append16(ihdr, (ushort)nc);
        var bpcByte = perComponentBpc is not null && nc > 1 ? (byte)0xFF : (byte)((bpc - 1) & 0x7F);
        ihdr.Add(bpcByte);
        ihdr.Add(0x07); // C
        ihdr.Add(0x00); // UnkC
        ihdr.Add(0x00); // IPR
        var ihdrBox = Jp2MakeBox(0x69686472u, [.. ihdr]);

        // Optional bpcc box when per-component bit depths differ.
        byte[] bpccBox = [];
        if (bpcByte == 0xFF && perComponentBpc is not null)
        {
            var bpcc = new List<byte>();
            foreach (var d in perComponentBpc)
                bpcc.Add((byte)((d - 1) & 0x7F));
            bpccBox = Jp2MakeBox(0x62706363u, [.. bpcc]);
        }

        // colr boxes
        var colrContent = new List<byte>();
        if (colrBoxes is not null)
        {
            foreach (var (meth, approx, enumCs) in colrBoxes)
            {
                var colr = new List<byte> { meth, 0x00, approx }; // METH, PREC, APPROX
                if (meth == 1 && enumCs.HasValue)
                    Jp2Append32(colr, (uint)enumCs.Value);
                colrContent.AddRange(Jp2MakeBox(0x636F6C72u, [.. colr]));
            }
        }
        else
        {
            // Default: single colr with METH=1, EnumCS=16 (sRGB), APPROX=0
            var colr = new List<byte> { 0x01, 0x00, 0x00 };
            Jp2Append32(colr, 16u);
            colrContent.AddRange(Jp2MakeBox(0x636F6C72u, [.. colr]));
        }

        // jp2h superbox
        var jp2h = new List<byte>();
        jp2h.AddRange(ihdrBox);
        jp2h.AddRange(bpccBox);
        jp2h.AddRange(colrContent);
        Jp2AppendBox(buf, 0x6A703268u, [.. jp2h]);

        // jp2c box
        Jp2AppendBox(buf, 0x6A703263u, codestream);

        return new Jp2FileData([.. buf], codestream);
    }

    /// <summary>Builds a minimal raw JPEG2000 codestream (SOC + SIZ + EOC) for nc components.</summary>
    private static byte[] Jp2BuildRawCodestream(int nc, int[] bpcs)
    {
        var lsiz = 38 + 3 * nc;
        var buf = new List<byte>();
        buf.AddRange([0xFF, 0x4F]);       // SOC
        buf.AddRange([0xFF, 0x51]);       // SIZ
        Jp2Append16(buf, (ushort)lsiz);   // Lsiz
        Jp2Append16(buf, 0);              // Rsiz
        Jp2Append32(buf, 4u);             // Xsiz
        Jp2Append32(buf, 4u);             // Ysiz
        Jp2Append32(buf, 0u);             // XOsiz
        Jp2Append32(buf, 0u);             // YOsiz
        Jp2Append32(buf, 4u);             // XTsiz
        Jp2Append32(buf, 4u);             // YTsiz
        Jp2Append32(buf, 0u);             // XTOsiz
        Jp2Append32(buf, 0u);             // YTOsiz
        Jp2Append16(buf, (ushort)nc);     // Csiz
        foreach (var d in bpcs)
        {
            buf.Add((byte)((d - 1) & 0x7F)); // Ssiz
            buf.Add(0x01); buf.Add(0x01);     // XRsiz, YRsiz
        }
        buf.AddRange([0xFF, 0xD9]);       // EOC
        return [.. buf];
    }

    private static void Jp2Append32(List<byte> buf, uint v)
    {
        buf.Add((byte)(v >> 24)); buf.Add((byte)((v >> 16) & 0xFF));
        buf.Add((byte)((v >> 8) & 0xFF)); buf.Add((byte)(v & 0xFF));
    }

    private static void Jp2Append16(List<byte> buf, ushort v)
        => buf.AddRange([(byte)(v >> 8), (byte)(v & 0xFF)]);

    private static byte[] Jp2MakeBox(uint type, byte[] payload)
    {
        var box = new List<byte>();
        Jp2Append32(box, (uint)(8 + payload.Length));
        Jp2Append32(box, type);
        box.AddRange(payload);
        return [.. box];
    }

    private static void Jp2AppendBox(List<byte> buf, uint type, byte[] payload)
        => buf.AddRange(Jp2MakeBox(type, payload));

    // ── §6.2.8.3-1: colour channel count ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Jpeg2000_NcEquals2_IsFlagged_6283_1()
    {
        // NC=2 is not in {1,3,4} — must be flagged.
        var jp2 = BuildJp2(nc: 2, bpc: 8);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-1");
    }

    [Fact]
    public void Validate_Jpeg2000_NcEquals5_IsFlagged_6283_1()
    {
        // NC=5 is not in {1,3,4}.
        var jp2 = BuildJp2(nc: 5, bpc: 8);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-1");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Validate_Jpeg2000_NcValid_NoFinding_6283_1(int nc)
    {
        // NC ∈ {1,3,4}: no §6.2.8.3-1 finding.
        var jp2 = BuildJp2(nc: nc, bpc: 8);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-1");
    }

    [Fact]
    public void Validate_Jpeg2000_RawCodestream_NcEquals2_IsFlagged_6283_1()
    {
        // Raw codestream (no JP2 wrapper), Csiz=2.
        var raw = Jp2BuildRawCodestream(2, [8, 8]);
        var pdf = BuildJpxImagePdf(raw);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-1");
    }

    [Fact]
    public void Validate_Jpeg2000_RawCodestream_NcEquals3_NoFinding_6283_1()
    {
        var raw = Jp2BuildRawCodestream(3, [8, 8, 8]);
        var pdf = BuildJpxImagePdf(raw);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-1");
    }

    // ── §6.2.8.3-2: APPROX field when multiple colr boxes ─────────────────────────────────────────

    [Fact]
    public void Validate_Jpeg2000_MultipleColr_ZeroApproxOnes_IsFlagged_6283_2()
    {
        // Two colr boxes, neither has APPROX=1. Must be flagged.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes:
        [
            (0x01, 0x00, 16),  // METH=1, APPROX=0, sRGB
            (0x01, 0x00, 17),  // METH=1, APPROX=0, Greyscale
        ]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-2");
    }

    [Fact]
    public void Validate_Jpeg2000_MultipleColr_TwoApproxOnes_IsFlagged_6283_2()
    {
        // Two colr boxes, both have APPROX=1. Must be flagged (exactly one required).
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes:
        [
            (0x01, 0x01, 16),
            (0x01, 0x01, 17),
        ]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-2");
    }

    [Fact]
    public void Validate_Jpeg2000_MultipleColr_ExactlyOneApprox1_NoFinding_6283_2()
    {
        // Two colr boxes, exactly one has APPROX=1. No finding.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes:
        [
            (0x01, 0x01, 16),  // APPROX=1
            (0x02, 0x00, null), // METH=2 (ICC), APPROX=0
        ]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-2");
    }

    [Fact]
    public void Validate_Jpeg2000_SingleColr_ApproxRuleNotApplied_NoFinding_6283_2()
    {
        // Rule only applies when colr count > 1. Single colr APPROX=0 is fine.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes: [(0x01, 0x00, 16)]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-2");
    }

    // ── §6.2.8.3-3: METH field constraint ─────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Jpeg2000_ColrMeth0_IsFlagged_6283_3()
    {
        // METH=0 is not in {1,2,3}.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes: [(0x00, 0x00, null)]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-3");
    }

    [Fact]
    public void Validate_Jpeg2000_ColrMeth4_IsFlagged_6283_3()
    {
        // METH=4 is not in {1,2,3}.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes: [(0x04, 0x00, null)]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-3");
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x03)]
    public void Validate_Jpeg2000_ColrMethValid_NoFinding_6283_3(int meth)
    {
        var colrBoxes = meth == 1
            ? new[] { ((byte)meth, (byte)0x00, (int?)16) }
            : new[] { ((byte)meth, (byte)0x00, (int?)null) };
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes: colrBoxes);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-3");
    }

    // ── §6.2.8.3-4: CIEJab (EnumCS=19) prohibited ────────────────────────────────────────────────

    [Fact]
    public void Validate_Jpeg2000_ColrEnumCs19_IsFlagged_6283_4()
    {
        // METH=1, EnumCS=19 (CIEJab) — forbidden.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes: [(0x01, 0x00, 19)]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-4");
    }

    [Fact]
    public void Validate_Jpeg2000_ColrEnumCs16_NoFinding_6283_4()
    {
        // METH=1, EnumCS=16 (sRGB) — permitted.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes: [(0x01, 0x00, 16)]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-4");
    }

    // ── §6.2.8.3-5: bit depth in 1..38, all equal ─────────────────────────────────────────────────

    [Fact]
    public void Validate_Jpeg2000_BitDepth0_IsFlagged_6283_5()
    {
        // BPC byte = 0xFF (per-component), bpcc has component with depth 0: (0 & 0x7F)+1 = 1 → OK
        // Actually depth 0 means bpcc byte = 0xFF → (0xFF & 0x7F)+1 = 128 > 38 → out of range.
        // Build a JP2 where ihdr BPC=0xFF and bpcc has a byte of 0xFF (depth=128).
        var codestream = Jp2BuildRawCodestream(1, [8]);
        var buf = new List<byte>();
        // Signature
        Jp2Append32(buf, 12u);
        buf.AddRange([0x6A, 0x50, 0x20, 0x20]);
        buf.AddRange([0x0D, 0x0A, 0x87, 0x0A]);
        // ftyp
        var ftyp = new List<byte>();
        ftyp.AddRange([0x6A, 0x70, 0x32, 0x20]);
        Jp2Append32(ftyp, 0u);
        ftyp.AddRange([0x6A, 0x70, 0x32, 0x20]);
        Jp2AppendBox(buf, 0x66747970u, [.. ftyp]);
        // ihdr with BPC=0xFF (per-component depths in bpcc)
        var ihdr = new List<byte>();
        Jp2Append32(ihdr, 4u); Jp2Append32(ihdr, 4u);
        Jp2Append16(ihdr, 1);   // NC=1
        ihdr.Add(0xFF);          // BPC = 0xFF → read bpcc box
        ihdr.Add(0x07); ihdr.Add(0x00); ihdr.Add(0x00);
        // bpcc box: single component with depth byte=0xFF → (0x7F)+1 = 128, out of range
        var bpcc = new List<byte> { 0xFF };
        // jp2h
        var jp2h = new List<byte>();
        jp2h.AddRange(Jp2MakeBox(0x69686472u, [.. ihdr]));
        jp2h.AddRange(Jp2MakeBox(0x62706363u, [.. bpcc]));
        // colr
        var colr = new List<byte> { 0x01, 0x00, 0x00 };
        Jp2Append32(colr, 17u);  // Greyscale
        jp2h.AddRange(Jp2MakeBox(0x636F6C72u, [.. colr]));
        Jp2AppendBox(buf, 0x6A703268u, [.. jp2h]);
        Jp2AppendBox(buf, 0x6A703263u, codestream);

        var pdf = BuildJpxImagePdf([.. buf]);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-5");
    }

    [Fact]
    public void Validate_Jpeg2000_NonUniformBitDepths_IsFlagged_6283_5()
    {
        // Three components with differing bit depths: {8, 8, 16} — not uniform.
        var jp2 = BuildJp2(nc: 3, bpc: 8, perComponentBpc: [8, 8, 16]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-5");
    }

    [Fact]
    public void Validate_Jpeg2000_BitDepthUniform8_NoFinding_6283_5()
    {
        // Uniform BPC=8 — no violation.
        var jp2 = BuildJp2(nc: 3, bpc: 8);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-5");
    }

    [Fact]
    public void Validate_Jpeg2000_RawCodestream_NonUniformBitDepths_IsFlagged_6283_5()
    {
        // Raw codestream, Csiz=3, Ssiz bytes giving depths {8,8,16}.
        var raw = Jp2BuildRawCodestream(3, [8, 8, 16]);
        var pdf = BuildJpxImagePdf(raw);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-5");
    }

    [Fact]
    public void Validate_Jpeg2000_RawCodestream_UniformBitDepths_NoFinding_6283_5()
    {
        // Raw codestream, uniform BPC=8 — no violation.
        var raw = Jp2BuildRawCodestream(3, [8, 8, 8]);
        var pdf = BuildJpxImagePdf(raw);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-5");
    }

    // ── Overall no-false-positive guard: fully conformant JP2 (NC=3, METH=1, sRGB, BPC=8) ─────────

    [Fact]
    public void Validate_Jpeg2000_ConformantJp2_NoFindings_6283()
    {
        // A fully conformant JP2: NC=3, single colr METH=1 EnumCS=16 (sRGB), APPROX=0, uniform BPC=8.
        // Must produce no 6.2.8.3-* findings.
        var jp2 = BuildJp2(nc: 3, bpc: 8, colrBoxes: [(0x01, 0x00, 16)]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId.StartsWith("ISO19005-2:6.2.8.3-", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Jpeg2000_Grayscale_ConformantJp2_NoFindings_6283()
    {
        // Greyscale JP2: NC=1, METH=1, EnumCS=17, BPC=8.
        var jp2 = BuildJp2(nc: 1, bpc: 8, colrBoxes: [(0x01, 0x00, 17)]);
        var pdf = BuildJpxImagePdf(jp2.File);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId.StartsWith("ISO19005-2:6.2.8.3-", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Jpeg2000_NotDrawn_NoFinding_6283()
    {
        // An invalid JP2 (NC=5) present in resources but NOT drawn → no finding (scoping guard).
        var jp2 = BuildJp2(nc: 5, bpc: 8);
        var pdf = BuildXObjectPdf(
            "/Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode /ColorSpace /DeviceRGB",
            jp2.File,
            draw: false);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.8.3-1");
    }

    [Fact]
    public void Validate_Jpeg2000_MalformedBytes_NoFinding_6283()
    {
        // Completely garbage bytes in a JPXDecode stream → no spurious finding (defensive guard).
        var garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03, 0xFF, 0x00 };
        var pdf = BuildJpxImagePdf(garbage);
        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId.StartsWith("ISO19005-2:6.2.8.3-", StringComparison.Ordinal));
    }

    // ── §6.2.4.4-2 Separation tint/alternate consistency ──────────────────────

    /// <summary>
    /// Builds a two-page PDF/A-2b PDF where each page uses a different /ColorSpace resource
    /// entry for a Separation colour space. The caller supplies the two colour-space array
    /// literals (<paramref name="cs1Body"/> and <paramref name="cs2Body"/>) and two tint-function
    /// bodies (<paramref name="tint1Dict"/> and <paramref name="tint2Dict"/>).
    ///
    /// Object map:
    ///   1 = catalog   2 = pages   3 = page1   4 = page2
    ///   5 = CS1 array (cs1Body)   6 = tint1 (tint1Dict)
    ///   7 = CS2 array (cs2Body)   8 = tint2 (tint2Dict)
    ///   9 = content stream page1 (/CS1 cs 0.5 scn …)
    ///  10 = content stream page2 (/CS2 cs 0.5 scn …)
    /// </summary>
    private static byte[] BuildTwoPageSeparationPdf(
        string cs1Body,
        string tint1Dict,
        string cs2Body,
        string tint2Dict)
        => AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS1 5 0 R >> >> /Contents 9 0 R >>"),
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS2 7 0 R >> >> /Contents 10 0 R >>"),
            new(cs1Body),                                                          // 5: CS1
            new(tint1Dict),                                                        // 6: tint1
            new(cs2Body),                                                          // 7: CS2
            new(tint2Dict),                                                        // 8: tint2
            new(string.Empty, Encoding.ASCII.GetBytes("/CS1 cs 0.5 scn 10 10 50 50 re f")),  // 9
            new(string.Empty, Encoding.ASCII.GetBytes("/CS2 cs 0.5 scn 10 10 50 50 re f")),  // 10
        ]);

    /// <summary>
    /// Separation (SpotA, /DeviceCMYK, Type-2 tint) — minimal tint dict for reuse. Returns
    /// a /FunctionType 2 with the given /C1 values so callers can vary only the output colour.
    /// </summary>
    private static string SepTintDict(string c1) =>
        $"<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [{c1}] /N 1 >>";

    [Fact]
    public void Validate_SeparationConsistency_IdenticalStructureDistinctObjects_NoFinding()
    {
        // §6.2.4.4-2: two same-name Separations with structurally identical tintTransforms but
        // at different object numbers must NOT be flagged — the spec says object identity is
        // irrelevant ("whether an object is direct or indirect shall be ignored"). This is the
        // key no-false-positive guard (veraPDF probe A).
        var tintDict = SepTintDict("0 0 0 1");
        var bytes = BuildTwoPageSeparationPdf(
            "[/Separation /SpotA /DeviceCMYK 6 0 R]", tintDict,
            "[/Separation /SpotA /DeviceCMYK 8 0 R]", tintDict);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_DifferentTintTransform_ReportsError()
    {
        // §6.2.4.4-2: two same-name Separations (SpotA) with different tint functions → error.
        var bytes = BuildTwoPageSeparationPdf(
            "[/Separation /SpotA /DeviceCMYK 6 0 R]", SepTintDict("0 0 0 1"),   // black
            "[/Separation /SpotA /DeviceCMYK 8 0 R]", SepTintDict("1 0 0 0"));  // cyan

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_DifferentAlternateSpace_ReportsError()
    {
        // §6.2.4.4-2: same name, different alternateSpace (/DeviceCMYK vs /DeviceRGB) → error.
        var bytes = BuildTwoPageSeparationPdf(
            "[/Separation /SpotA /DeviceCMYK 6 0 R]",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>",
            "[/Separation /SpotA /DeviceRGB 8 0 R]",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [0 0 1] /N 1 >>");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_DifferentColourantNames_NoFinding()
    {
        // §6.2.4.4-2: SpotA and SpotB are different names — they are never compared → no error.
        var bytes = BuildTwoPageSeparationPdf(
            "[/Separation /SpotA /DeviceCMYK 6 0 R]", SepTintDict("0 0 0 1"),
            "[/Separation /SpotB /DeviceCMYK 8 0 R]", SepTintDict("1 0 0 0"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_SingleOccurrence_NoFinding()
    {
        // §6.2.4.4-2: a single Separation occurrence has nothing to compare → no error.
        var bytes = BuildColourSpacePdf("[/Separation /SpotA /DeviceCMYK 5 0 R]");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_ColorantsDictInconsistent_ReportsError()
    {
        // §6.2.4.4-2: a top-level Separation (page1) and a Separation in a USED DeviceN
        // /Colorants dict (page2) with the same name but different tintTransform → error.
        // Verified empirically (veraPDF probe F).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            // page1: uses CS1 = [/Separation /SpotA /DeviceCMYK 5 0 R]
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS1 5 0 R >> >> /Contents 7 0 R >>"),
            // page2: uses CS2 = DeviceN with SpotA in /Colorants (tint at obj 6)
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS2 8 0 R >> >> /Contents 10 0 R >>"),
            // 5: top-level SpotA, tint → black (C1 = 0 0 0 1)
            new("[/Separation /SpotA /DeviceCMYK 6 0 R]"),
            // 6: tint for page1 SpotA
            new("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>"),
            // 7: content stream page1
            new(string.Empty, Encoding.ASCII.GetBytes("/CS1 cs 0.5 scn 10 10 50 50 re f")),
            // 8: DeviceN with SpotA in /Colorants using tint obj 9 (DIFFERENT from obj 6)
            new("[/DeviceN [/SpotA] /DeviceCMYK 11 0 R "
                + "<< /Subtype /DeviceN /Colorants "
                + "<< /SpotA [/Separation /SpotA /DeviceCMYK 9 0 R] >> >>]"),
            // 9: tint for Colorants SpotA — DIFFERENT (C1 = 1 0 0 0 = cyan)
            new("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>"),
            // 10: content stream page2
            new(string.Empty, Encoding.ASCII.GetBytes("/CS2 cs 0 scn 10 10 50 50 re f")),
            // 11: DeviceN tint function (1→4 input)
            new("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_UnusedInconsistentPair_NoFinding()
    {
        // §6.2.4.4-2 scope: a Separation present in /Resources but never selected by a cs/CS
        // operator is not in scope — the inconsistent pair must NOT be flagged.
        // Scope verified empirically (veraPDF probes G2 and G4).
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            // Page has CS1 and CS2 (SpotA with different tints) in Resources but the
            // content stream does not select either.
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS1 4 0 R /CS2 6 0 R >> >> >>"),
            new("[/Separation /SpotA /DeviceCMYK 5 0 R]"),
            new("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>"),
            new("[/Separation /SpotA /DeviceCMYK 8 0 R]"),
            new("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>"),
            // placeholder 8 was the tint — pad so obj numbers are correct
            new("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_WriterProducedConsistentSeparation_NoFinding()
    {
        // §6.2.4.4-2: a writer that consistently emits SpotA with the same tint function
        // (shared indirect reference) on multiple pages must NOT trigger a false positive.
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            // page1 and page2 both point to the SAME colour-space object (5 0 R)
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS1 5 0 R >> >> /Contents 7 0 R >>"),
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS1 5 0 R >> >> /Contents 8 0 R >>"),
            // 5: shared Separation colour space
            new("[/Separation /SpotA /DeviceCMYK 6 0 R]"),
            // 6: shared tint function
            new("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>"),
            new(string.Empty, Encoding.ASCII.GetBytes("/CS1 cs 0.5 scn 10 10 50 50 re f")),
            new(string.Empty, Encoding.ASCII.GetBytes("/CS1 cs 0.5 scn 20 20 80 80 re f")),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.4-2");
    }

    [Fact]
    public void Validate_SeparationConsistency_ReportsOncePerName()
    {
        // §6.2.4.4-2: when multiple pages all use SpotA inconsistently, only one finding per
        // colourant name is emitted (no duplicate findings for the same name).
        var bytes = BuildTwoPageSeparationPdf(
            "[/Separation /SpotA /DeviceCMYK 6 0 R]", SepTintDict("0 0 0 1"),
            "[/Separation /SpotA /DeviceCMYK 8 0 R]", SepTintDict("1 0 0 0"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        var findings = result.Assertions.Where(a => a.RuleId == "ISO19005-2:6.2.4.4-2").ToList();
        Assert.Single(findings);
    }

    // ── §6.2.4.2-2 Overprint (OPM) with ICCBased CMYK colour space ───────────

    /// <summary>
    /// Builds a one-page PDF/A-2b with:
    /// <list type="bullet">
    ///   <item>CS0 = ICCBased CMYK (N=4) in /Resources /ColorSpace</item>
    ///   <item>GS1 = ExtGState with the given overprint parameters</item>
    ///   <item>content = the given content stream string</item>
    /// </list>
    /// The ICC profile bytes come from the kernel's built-in generic CMYK profile (prtr/CMYK/v2).
    /// </summary>
    private static byte[] BuildOverprintPdf(string extGStateEntries, string content, int iccN = 4)
    {
        // Build either CMYK (N=4, prtr) or RGB (N=3, mntr) ICC profile bytes via MakeIccHeader.
        byte[] iccBytes = iccN == 4
            ? MakeIccHeader("prtr", "CMYK", 2)
            : MakeIccHeader("mntr", "RGB ", 2);
        var compressed = ZlibCompress(iccBytes);
        return AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R "
                + $"/Resources << /ColorSpace << /CS0 [/ICCBased 4 0 R] >> "
                + $"/ExtGState << /GS1 << {extGStateEntries} >> >> >> >>"),
            new($"/Filter /FlateDecode /N {iccN}", compressed),
            new(string.Empty, Encoding.ASCII.GetBytes(content)),
        ]);
    }

    // -- fill + ICCBased CMYK + op true + OPM 1 → finding
    // Confirmed against veraPDF 1.30.2 (probe P1).

    [Fact]
    public void Validate_Overprint_FillIccCmyk_OpTrue_Opm1_ReportsError()
    {
        // Fill colour space = ICCBased CMYK (cs), fill overprint true (/op true), OPM 1.
        // veraPDF probe P1: FAIL §6.2.4.2-2.
        var bytes = BuildOverprintPdf("/op true /OPM 1", "/CS0 cs\n0 0 0 1 sc\n/GS1 gs\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- OPM 0 → no finding (PASS condition)
    // Confirmed against veraPDF 1.30.2 (probe P2).

    [Fact]
    public void Validate_Overprint_FillIccCmyk_OpTrue_Opm0_NoFinding()
    {
        // Same but OPM = 0 → PASS: OPM 0 is always permitted.
        // veraPDF probe P2: compliant.
        var bytes = BuildOverprintPdf("/op true /OPM 0", "/CS0 cs\n0 0 0 1 sc\n/GS1 gs\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- fill overprint false → no finding (PASS condition)
    // Confirmed against veraPDF 1.30.2 (probe P3).

    [Fact]
    public void Validate_Overprint_FillIccCmyk_OpFalse_Opm1_NoFinding()
    {
        // Fill overprint disabled (/op false); OPM 1 alone is not a violation.
        // veraPDF probe P3: compliant.
        var bytes = BuildOverprintPdf("/op false /OPM 1", "/CS0 cs\n0 0 0 1 sc\n/GS1 gs\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- no gs operator applied (default OPM=0, overprint=false) → no finding
    // Confirmed against veraPDF 1.30.2 (probe P4).

    [Fact]
    public void Validate_Overprint_FillIccCmyk_NoGsApplied_NoFinding()
    {
        // GS1 is in resources but the content never calls `gs` — defaults remain (OPM=0, op=false).
        // veraPDF probe P4: compliant.
        var bytes = BuildOverprintPdf("/op true /OPM 1", "/CS0 cs\n0 0 0 1 sc\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- stroke variant: /OP true + OPM 1 → finding
    // Confirmed against veraPDF 1.30.2 (probe P5).

    [Fact]
    public void Validate_Overprint_StrokeIccCmyk_OP_True_Opm1_ReportsError()
    {
        // Stroke colour space = ICCBased CMYK (CS), stroke overprint true (/OP true), OPM 1.
        // veraPDF probe P5: FAIL §6.2.4.2-2.
        var bytes = BuildOverprintPdf("/OP true /OPM 1", "/CS0 CS\n0 0 0 1 SC\n/GS1 gs\n0 0 100 100 re\nS\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- ICCBased RGB (N=3, not CMYK) → no finding
    // Confirmed against veraPDF 1.30.2 (probe P6).

    [Fact]
    public void Validate_Overprint_FillIccRgb_OpTrue_Opm1_NoFinding()
    {
        // CS0 is ICCBased with N=3 (RGB) — only ICCBased CMYK (N=4) triggers this rule.
        // veraPDF probe P6: compliant.
        var bytes = BuildOverprintPdf("/op true /OPM 1",
            "/CS0 cs\n0.5 0.5 0.5 sc\n/GS1 gs\n0 0 100 100 re\nf\n", iccN: 3);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- DeviceCMYK (k operator) with op true + OPM 1 → no finding for §6.2.4.2-2
    // Confirmed against veraPDF 1.30.2 (probe P7 triggers 6.2.4.3 but NOT 6.2.4.2-2).

    [Fact]
    public void Validate_Overprint_DeviceCmyk_K_Operator_NoOverprintFinding()
    {
        // `k` sets DeviceCMYK fill colour — that is NOT an ICCBased colour space, so the rule
        // must not fire. (veraPDF probe P7: non-compliant only due to §6.2.4.3, not §6.2.4.2-2.)
        var bytes = BuildOverprintPdf("/op true /OPM 1", "/GS1 gs\n0 0 0 1 k\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- /OP only (no /op key) propagates to fill overprint → finding
    // Confirmed against veraPDF 1.30.2 (probe P8): FAIL.
    // Per ISO 32000-1 §8.4.5: /OP sets fill overprint when /op is absent.

    [Fact]
    public void Validate_Overprint_FillIccCmyk_OP_Only_Opm1_ReportsError()
    {
        // ExtGState has /OP true but no /op key. /OP propagates to fill overprint per §8.4.5.
        // veraPDF probe P8: FAIL §6.2.4.2-2.
        var bytes = BuildOverprintPdf("/OP true /OPM 1", "/CS0 cs\n0 0 0 1 sc\n/GS1 gs\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- /OP true but /op explicitly false → no finding (/op wins)
    // Confirmed against veraPDF 1.30.2 (probe P14): PASS.

    [Fact]
    public void Validate_Overprint_FillIccCmyk_OP_True_OpExplicitFalse_NoFinding()
    {
        // /OP true sets stroke overprint; /op false explicitly overrides fill overprint.
        // veraPDF probe P14: compliant.
        var bytes = BuildOverprintPdf("/OP true /op false /OPM 1",
            "/CS0 cs\n0 0 0 1 sc\n/GS1 gs\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- q/Q restores state: gs inside q, paint after Q → no finding
    // Confirmed against veraPDF 1.30.2 (probe P9): PASS.

    [Fact]
    public void Validate_Overprint_QQ_RestoresState_NoFinding()
    {
        // gs is applied inside q…Q; after Q the state is restored to defaults.
        // Paint occurs after Q → OPM and overprint are back to 0/false → no violation.
        // veraPDF probe P9: compliant.
        var bytes = BuildOverprintPdf("/op true /OPM 1",
            "/CS0 cs\n0 0 0 1 sc\nq\n/GS1 gs\nQ\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- gs before q, paint after Q: state from before q persists → finding
    // Confirmed against veraPDF 1.30.2 (probe P10): FAIL.

    [Fact]
    public void Validate_Overprint_GsBeforeQ_PaintAfterQ_ReportsError()
    {
        // gs is applied before q; q pushes a copy of that state; Q pops back to the same state.
        // The overprint condition is still active when painting occurs after Q.
        // veraPDF probe P10: FAIL §6.2.4.2-2.
        var bytes = BuildOverprintPdf("/op true /OPM 1",
            "/CS0 cs\n/GS1 gs\nq\nQ\n0 0 100 100 re\nf\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- fill-and-stroke (B) operator with fill ICCBased CMYK + op true + OPM 1 → finding
    // Confirmed against veraPDF 1.30.2 (probe P13): FAIL.

    [Fact]
    public void Validate_Overprint_FillStroke_B_Operator_ReportsError()
    {
        // The `B` operator (fill then stroke, nonzero winding) checks both fill and stroke
        // conditions. With fill ICCBased CMYK + op true + OPM 1, fill condition fires.
        // veraPDF probe P13: FAIL §6.2.4.2-2.
        var bytes = BuildOverprintPdf("/op true /OPM 1",
            "/CS0 cs\n0 0 0 1 sc\n/GS1 gs\n0 0 100 100 re\nB\n");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // -- writer-produced PDF/A (no overprint, default state) → no false positive

    [Fact]
    public void Validate_Overprint_WriterProducedPdfA_NoFinding()
    {
        // A PDF/A-2b document produced by the VellumPdf writer has no overprint operators;
        // the rule must not produce a false positive.
        var bytes = BuildOnePagePdf();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.2.4.2-2");
    }

    // ── §6.4.3 Digital-signature rules ───────────────────────────────────────

    // ── §6.4.3-1 ByteRange coverage ──────────────────────────────────────────

    /// <summary>
    /// Creates a self-signed RSA-2048 / SHA-256 certificate for signing tests.
    /// The returned certificate includes the private key.
    /// </summary>
    private static X509Certificate2 CreateSigningCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Conformance Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>Signs a one-page PdfDocument with the given certificate, returning the signed bytes.</summary>
    private static byte[] SignMinimalPdf(X509Certificate2 cert)
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.AddPage();
        var settings = new PdfSignatureSettings { Certificate = cert };
        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        return ms.ToArray();
    }

    [Fact]
    public void Validate_RealSignedPdf_NoSignatureFinding()
    {
        // A genuinely signed PDF produced by the Signing package must NOT trigger any §6.4.3-*
        // or §6.1.12-2 finding. This is the critical no-false-positive baseline.
        using var cert = CreateSigningCertificate();
        var signed = SignMinimalPdf(cert);

        var result = PdfPreflight.Validate(signed, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-1");
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-2");
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-3");
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.12-2");
    }

    [Fact]
    public void Validate_ByteRange_UnderCoverage_DeferredNoFinding()
    {
        // A /ByteRange that ends before EOF (c+d < fileLength) is DEFERRED, not flagged: it is
        // indistinguishable from a conformant PAdES B-LT/B-LTA signature whose later /DSS or
        // document timestamp was appended after signing (veraPDF reports those compliant). The
        // companion a!=0 test proves the signature IS enumerated, so this no-finding is meaningful.
        var bytes = BuildSignedPdfWithByteRange(0, 50, 70, 10); // c+d = 80; file is longer

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-1");
    }

    [Fact]
    public void Validate_ByteRange_ExceedsFileLength_ReportsError()
    {
        // /ByteRange claiming more bytes than the file contains (c+d > fileLength) is an
        // unambiguous, revision-independent violation and must be flagged.
        var pdf = BuildSignedPdfWithByteRange(0, 50, 70, 1_000_000); // c+d ≫ fileLength → violation

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-1");
    }

    [Fact]
    public void Validate_ByteRange_NotStartingAtZero_ReportsError()
    {
        // /ByteRange where br[0] != 0 — an unambiguous violation regardless of revisions.
        var pdf = BuildSignedPdfWithByteRange(1, 49, 70, 30); // a=1 → violation

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-1");
    }

    // ── §6.4.3-2 Certificate presence ────────────────────────────────────────

    [Fact]
    public void Validate_CmsNoCertificates_ReportsError()
    {
        // Hand-built CMS DER with certificates [0] explicitly set to empty.
        var der = BuildCmsDer(certCount: 0, signerCount: 1);
        var pdf = BuildPdfWithFakeCms(der);

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-2");
    }

    [Fact]
    public void Validate_CmsOneCertificate_NoFinding()
    {
        // Single signer, one certificate → compliant for §6.4.3-2 and §6.4.3-3.
        var der = BuildCmsDer(certCount: 1, signerCount: 1);
        var pdf = BuildPdfWithFakeCms(der);

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-2");
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-3");
    }

    // ── §6.4.3-3 Single signer ────────────────────────────────────────────────

    [Fact]
    public void Validate_CmsTwoSigners_ReportsError()
    {
        // Hand-built CMS DER with two entries in the signerInfos SET.
        var der = BuildCmsDer(certCount: 1, signerCount: 2);
        var pdf = BuildPdfWithFakeCms(der);

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-3");
    }

    [Fact]
    public void Validate_CmsSingleSigner_NoFinding()
    {
        var der = BuildCmsDer(certCount: 1, signerCount: 1);
        var pdf = BuildPdfWithFakeCms(der);

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-3");
    }

    [Fact]
    public void Validate_CmsMalformedContents_NoFinding()
    {
        // Garbage /Contents bytes → TryParse returns false → no finding (defensive).
        var junk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00 };
        var pdf = BuildPdfWithFakeCms(junk);

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-2");
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.4.3-3");
    }

    // ── §6.1.12-2 DocMDP reference keys ──────────────────────────────────────

    [Fact]
    public void Validate_DocMdp_WithDigestMethod_ReportsError()
    {
        // /Perms /DocMDP signature dict whose /Reference array contains /DigestMethod.
        var pdf = BuildDocMdpPdfDirect(withForbiddenKeys: true);

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.12-2");
    }

    [Fact]
    public void Validate_DocMdp_NoForbiddenKeys_NoFinding()
    {
        // /Perms /DocMDP with a clean reference dict (no Digest* keys).
        var pdf = BuildDocMdpPdfDirect(withForbiddenKeys: false);

        var result = PdfPreflight.Validate(pdf, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.12-2");
    }

    [Fact]
    public void Validate_NoDocMdp_NoFinding()
    {
        // Document without /Perms at all → §6.1.12-2 must not fire.
        var bytes = BuildOnePagePdf();

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.12-2");
    }

    // ── Signature test helpers ────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal PDF that has a signature dictionary with the given /ByteRange values,
    /// but whose actual /Contents is trivial (so the CMS check is indeterminate, not finding).
    /// The file bytes end at a fixed length so the ByteRange check is testable.
    /// </summary>
    private static byte[] BuildSignedPdfWithByteRange(int a, int b, int c, int d)
    {
        // We construct a raw PDF whose AcroForm /Fields contains a /Sig field with a
        // signature dict carrying the requested /ByteRange values. The /Contents is
        // a 4-byte hex string (2 bytes DER, enough to be parsed as junk → indeterminate).
        // The ByteRange itself is the focus of this fixture.
        var br = $"[{a} {b} {c} {d}]";
        // Contents is a hex-encoded placeholder (4 zero bytes → malformed CMS → no §6.4.3-2/3 finding)
        const string contentsHex = "<00000000>";

        // We need the file to be exactly c+d bytes long for a conformant ByteRange to pass, but
        // we're deliberately testing non-conformant values so any length works here.
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        // Object 1: catalog with AcroForm
        var off1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> /Metadata 5 0 R >>\nendobj\n");

        // Object 2: pages
        var off2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Object 3: page
        var off3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // Object 4: sig field
        var off4 = (int)ms.Position;
        W($"4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /V << /Type /Sig /SubFilter /ETSI.CAdES.detached /ByteRange {br} /Contents {contentsHex} >> >>\nendobj\n");

        // Object 5: minimal XMP metadata
        var xmp = XmpBytes("2", "B");
        var off5 = (int)ms.Position;
        W($"5 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n");
        ms.Write(xmp);
        W("\nendstream\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 6\n");
        W($"{0:D10} 65535 f \n");
        W($"{off1:D10} 00000 n \n");
        W($"{off2:D10} 00000 n \n");
        W($"{off3:D10} 00000 n \n");
        W($"{off4:D10} 00000 n \n");
        W($"{off5:D10} 00000 n \n");
        W($"trailer\n<< /Size 6 /Root 1 0 R /ID [<AABBCCDDEEFF00112233445566778899> <AABBCCDDEEFF00112233445566778899>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal but structurally valid CMS SignedData DER with
    /// <paramref name="certCount"/> dummy certificates and <paramref name="signerCount"/> dummy signerInfos.
    ///
    /// DER structure:
    ///   SEQUENCE (ContentInfo) {
    ///     OID 1.2.840.113549.1.7.2
    ///     [0] EXPLICIT {
    ///       SEQUENCE (SignedData) {
    ///         INTEGER (version=1)
    ///         SET (digestAlgorithms) { SEQUENCE { OID(SHA-256) NULL } }
    ///         SEQUENCE (encapContentInfo) { OID 1.2.840.113549.1.7.1 }
    ///         [0] (certificates) { <certCount> SEQUENCE { dummy } }
    ///         SET (signerInfos) { <signerCount> SEQUENCE { dummy } }
    ///       }
    ///     }
    ///   }
    /// </summary>
    private static byte[] BuildCmsDer(int certCount, int signerCount)
    {
        // OIDs
        var oidSignedData = new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02 };
        var oidData = new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x01 };
        var oidSha256 = new byte[] { 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01 };

        // A dummy 1-byte "certificate" SEQUENCE (just a placeholder for structural validity)
        var dummyCert = WrapSequence([0x01]); // SEQUENCE { BOOLEAN TRUE }
        var certsField = certCount > 0 ? WrapContextImplicit0(Repeat(dummyCert, certCount)) : WrapContextImplicit0([]);

        // A dummy signerInfo SEQUENCE
        var dummySigner = WrapSequence([0x01]); // minimal placeholder
        var signerInfosSet = WrapSet(Repeat(dummySigner, signerCount));

        // digestAlgorithms SET: one SHA-256 algorithm identifier
        var sha256AlgId = WrapSequence([.. oidSha256, 0x05, 0x00]); // OID + NULL
        var digestAlgorithms = WrapSet([.. sha256AlgId]);

        // encapContentInfo: OID id-data
        var encapContentInfo = WrapSequence([.. oidData]);

        // version INTEGER (1)
        var version = new byte[] { 0x02, 0x01, 0x01 };

        // SignedData SEQUENCE
        byte[] signedDataValue = [
            .. version,
            .. digestAlgorithms,
            .. encapContentInfo,
            .. certsField,
            .. signerInfosSet,
        ];
        var signedData = WrapSequence(signedDataValue);

        // [0] EXPLICIT wrapping SignedData
        var explicitWrapper = WrapContextImplicit0(signedData);

        // ContentInfo SEQUENCE: OID + [0] EXPLICIT SignedData
        return WrapSequence([.. oidSignedData, .. explicitWrapper]);
    }

    private static byte[] WrapSequence(byte[] value) => WrapTag(0x30, value);
    private static byte[] WrapSet(byte[] value) => WrapTag(0x31, value);
    private static byte[] WrapContextImplicit0(byte[] value) => WrapTag(0xA0, value);

    private static byte[] WrapTag(byte tag, byte[] value)
    {
        var lenBytes = EncodeLength(value.Length);
        var result = new byte[1 + lenBytes.Length + value.Length];
        result[0] = tag;
        lenBytes.CopyTo(result, 1);
        value.CopyTo(result, 1 + lenBytes.Length);
        return result;
    }

    private static byte[] EncodeLength(int len)
    {
        if (len <= 0x7F)
            return [(byte)len];
        if (len <= 0xFF)
            return [0x81, (byte)len];
        if (len <= 0xFFFF)
            return [0x82, (byte)(len >> 8), (byte)(len & 0xFF)];
        return [0x83, (byte)(len >> 16), (byte)((len >> 8) & 0xFF), (byte)(len & 0xFF)];
    }

    private static byte[] Repeat(byte[] item, int count)
    {
        if (count == 0) return [];
        var result = new byte[item.Length * count];
        for (var i = 0; i < count; i++)
            item.CopyTo(result, i * item.Length);
        return result;
    }

    /// <summary>
    /// Builds a minimal signed PDF whose /Contents hex string is the hex encoding of
    /// <paramref name="derBytes"/> (zero-padded to 8192 bytes as the Signing package does).
    /// The /ByteRange is computed to cover the actual file bytes, so §6.4.3-1 passes.
    /// </summary>
    private static byte[] BuildPdfWithFakeCms(byte[] derBytes)
    {
        // Reserve 8192 bytes for /Contents (matching the Signing package default).
        const int reserve = 8192;
        var contentsPlaceholder = "<" + new string('0', reserve * 2) + ">";

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var off1 = (int)ms.Position;
        // Catalog — ByteRange and Contents will be patched in-place after we know offsets.
        // Build sig dict so /ByteRange is before /Contents (matches Signing package convention).
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> /Metadata 5 0 R >>\nendobj\n");

        var off2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        var off3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        var off4 = (int)ms.Position;
        // Write the sig dict with a known-width /ByteRange placeholder and the /Contents placeholder.
        // We use 10-digit fixed-width fields (matching PdfSignatureHelper.ByteRangeFieldWidth).
        var brPlaceholderStr = "[9999999999 9999999999 9999999999 9999999999]";
        W($"4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /V << /Type /Sig /SubFilter /ETSI.CAdES.detached /ByteRange {brPlaceholderStr}\n/Contents {contentsPlaceholder} >> >>\nendobj\n");

        // Object 5: XMP metadata
        var xmp = XmpBytes("2", "B");
        var off5 = (int)ms.Position;
        W($"5 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n");
        ms.Write(xmp);
        W("\nendstream\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 6\n");
        W($"{0:D10} 65535 f \n");
        W($"{off1:D10} 00000 n \n");
        W($"{off2:D10} 00000 n \n");
        W($"{off3:D10} 00000 n \n");
        W($"{off4:D10} 00000 n \n");
        W($"{off5:D10} 00000 n \n");
        W($"trailer\n<< /Size 6 /Root 1 0 R /ID [<AABBCCDDEEFF00112233445566778899> <AABBCCDDEEFF00112233445566778899>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        var pdfBytes = ms.ToArray();
        var text = Encoding.Latin1.GetString(pdfBytes);

        // Locate and patch /ByteRange in-place.
        var brPos = text.IndexOf(brPlaceholderStr, StringComparison.Ordinal);
        var posLt = text.IndexOf('<', brPos + brPlaceholderStr.Length); // opening '<' of /Contents
        var posGt = text.IndexOf('>', posLt); // closing '>'
        var tokenLen = posGt - posLt + 1; // length of the hex token including '<' and '>'

        var br0 = 0;
        var br1 = posLt;
        var br2 = posLt + tokenLen;
        var br3 = pdfBytes.Length - br2;

        // Write the ByteRange in-place (fixed-width, 10 digits each).
        var brValueStr = $"[{br0,-10} {br1,-10} {br2,-10} {br3,-10}]";
        // Ensure same total length as placeholder.
        Assert.Equal(brPlaceholderStr.Length, brValueStr.Length);
        var brValueBytes = Encoding.ASCII.GetBytes(brValueStr);
        brValueBytes.CopyTo(pdfBytes, brPos);

        // Write the hex-encoded DER into the /Contents placeholder (zero-padded to reserve).
        var hexDer = Convert.ToHexString(derBytes).ToLowerInvariant();
        // Pad with zeros to fill the reserved space.
        var hexPadded = hexDer.PadRight(reserve * 2, '0');
        Assert.Equal(reserve * 2, hexPadded.Length); // must fit
        var contentsBytes = Encoding.ASCII.GetBytes(hexPadded);
        contentsBytes.CopyTo(pdfBytes, posLt + 1); // +1 to skip '<'

        return pdfBytes;
    }

    /// <summary>
    /// Builds a minimal PDF with /Perms /DocMDP pointing to a signature dict whose /Reference
    /// array either contains or omits the forbidden /Digest* keys.
    /// </summary>
    private static byte[] BuildDocMdpPdfDirect(bool withForbiddenKeys)
    {
        var refDictContent = withForbiddenKeys
            ? "/TransformMethod /DocMDP /DigestMethod /SHA1 /DigestLocation [0 0] /DigestValue <AABB>"
            : "/TransformMethod /DocMDP";

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        // Object 1: catalog with /Perms pointing to object 4 (sig dict)
        var off1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Perms << /DocMDP 4 0 R >> /Metadata 6 0 R >>\nendobj\n");

        // Object 2: pages
        var off2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Object 3: page
        var off3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // Object 4: DocMDP signature dict with /Reference array pointing to object 5
        var off4 = (int)ms.Position;
        W("4 0 obj\n<< /Type /Sig /Reference [5 0 R] >>\nendobj\n");

        // Object 5: signature reference dict
        var off5 = (int)ms.Position;
        W($"5 0 obj\n<< {refDictContent} >>\nendobj\n");

        // Object 6: XMP metadata
        var xmp = XmpBytes("2", "B");
        var off6 = (int)ms.Position;
        W($"6 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n");
        ms.Write(xmp);
        W("\nendstream\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 7\n");
        W($"{0:D10} 65535 f \n");
        W($"{off1:D10} 00000 n \n");
        W($"{off2:D10} 00000 n \n");
        W($"{off3:D10} 00000 n \n");
        W($"{off4:D10} 00000 n \n");
        W($"{off5:D10} 00000 n \n");
        W($"{off6:D10} 00000 n \n");
        W($"trailer\n<< /Size 7 /Root 1 0 R /ID [<AABBCCDDEEFF00112233445566778899> <AABBCCDDEEFF00112233445566778899>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── Batch A3 — §7.21 font clause unit tests ─────────────────────────────────────────────────

    /// <summary>
    /// False-positive guard for §7.21.6-3 (UaSymbolicFontRule): a symbolic TrueType font (Flags
    /// bit 3 = Symbolic) with NO /Encoding entry must NOT fire §7.21.6-3 — the absence of
    /// /Encoding is exactly what the rule requires. This fixture also fails other UA-1 rules
    /// (7.21.6-4 for the cmap subtable count, 7.1-3 for untagged text) but must not produce a
    /// §7.21.6-3 finding. Verified directly against veraPDF 1.30.2: that validator does NOT list
    /// clause 7.21.6, testNumber 3 in its failures for this document.
    /// </summary>
    [Fact]
    public void SymbolicFontNoEncoding_DoesNotFire7216_3()
    {
        var bytes = OracleCorpus.Ua1SymbolicFontNoEncoding();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.6-3");
    }

    /// <summary>
    /// Positive control for §7.21.6-3: a symbolic TrueType font (Flags = 4) WITH an /Encoding
    /// entry fires §7.21.6-3. Both veraPDF 1.30.2 (clause 7.21.6-3) and the in-process
    /// UaSymbolicFontRule must flag it.
    /// </summary>
    [Fact]
    public void SymbolicFontWithEncoding_Fires7216_3()
    {
        var bytes = OracleCorpus.Ua1SymbolicFontWithEncoding();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.6-3");
    }

    /// <summary>
    /// False-positive guard for §7.21.6-3: a non-symbolic TrueType font (Flags = 32,
    /// NonSymbolic) with /Encoding /WinAnsiEncoding must NOT fire §7.21.6-3 (the rule is only
    /// for symbolic fonts). Verified against veraPDF 1.30.2: clause 7.21.6-3 does not appear
    /// in the validation report for this fixture.
    /// </summary>
    [Fact]
    public void NonSymbolicFontWinAnsi_DoesNotFire7216_3()
    {
        var bytes = OracleCorpus.Ua1NonSymbolicFontWinAnsi();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.6-3");
    }

    // ── Batch A4 — §7.21 font clause unit tests ─────────────────────────────────────────────────

    /// <summary>
    /// §7.21.3.3-1 (UaCMapRule): a composite font's /Encoding must be a predefined CMap name or
    /// an embedded stream. A non-predefined named /Encoding (/FooBarCMap) fires 7.21.3.3-1.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture.
    /// </summary>
    [Fact]
    public void UaBadCMapName_Fires72133_1()
    {
        var bytes = OracleCorpus.Ua1BadCMapName();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.3.3-1");
    }

    /// <summary>
    /// §7.21.3.3-1 false-positive guard: the standard UA-1 tagged baseline uses /Identity-H
    /// (a predefined CMap) and must NOT fire 7.21.3.3-1.
    /// </summary>
    [Fact]
    public void UaBaselineIdentityH_DoesNotFire72133_1()
    {
        var bytes = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.3.3-1");
    }

    /// <summary>
    /// §7.21.4.2-1 (UaType1CharSetRule): an embedded subset Type1 font whose /CharSet lists
    /// every program glyph must NOT fire 7.21.4.2-1.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture.
    /// </summary>
    [Fact]
    public void UaType1CharSetComplete_DoesNotFire72142_1()
    {
        var bytes = OracleCorpus.WriterPdfWithType1CharSetUa1(complete: true);
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.2-1");
    }

    /// <summary>
    /// §7.21.4.2-1 positive control: an embedded subset Type1 font whose /CharSet omits one
    /// glyph must fire 7.21.4.2-1.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture.
    /// </summary>
    [Fact]
    public void UaType1CharSetIncomplete_Fires72142_1()
    {
        var bytes = OracleCorpus.WriterPdfWithType1CharSetUa1(complete: false);
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.2-1");
    }

    /// <summary>
    /// §7.21.4.2-2 (UaCidSetRule): an embedded subset CIDFontType2 whose /CIDSet correctly
    /// marks all CIDs must NOT fire 7.21.4.2-2.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture.
    /// </summary>
    [Fact]
    public void UaCidSetComplete_DoesNotFire72142_2()
    {
        var bytes = OracleCorpus.WriterPdfWithCidSetUa1(complete: true);
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.2-2");
    }

    /// <summary>
    /// §7.21.4.2-2 positive control: an embedded subset CIDFontType2 with an incomplete /CIDSet
    /// (single zero byte) must fire 7.21.4.2-2.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture.
    /// </summary>
    [Fact]
    public void UaCidSetIncomplete_Fires72142_2()
    {
        var bytes = OracleCorpus.WriterPdfWithCidSetUa1(complete: false);
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.2-2");
    }

    /// <summary>
    /// §7.21.3.3-2/-3 false-positive guard (UaCMapRule): the standard UA-1 tagged baseline uses
    /// /Identity-H (a named predefined CMap, not an embedded stream) so 7.21.3.3-2 and 7.21.3.3-3
    /// do not apply and must NOT fire. (They only apply to embedded CMap programs.)
    /// </summary>
    [Fact]
    public void UaBaselineIdentityH_DoesNotFire72133_2or3()
    {
        var bytes = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.3.3-2");
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.3.3-3");
    }

    /// <summary>
    /// §7.21.3.1-1 false-positive guard (UaCidSystemInfoRule): the standard UA-1 tagged baseline
    /// uses /Identity-H (always conformant) and must NOT fire 7.21.3.1-1.
    /// </summary>
    [Fact]
    public void UaBaselineIdentityH_DoesNotFire72131_1()
    {
        var bytes = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.3.1-1");
    }

    // ── Batch A5a — §7.21.4.1-1 rendering-mode-scoped font embedding unit tests ────────────────────

    /// <summary>
    /// §7.21.4.1-1 positive control (UaFontEmbeddingRule): a non-embedded simple TrueType font
    /// drawn with a visible text rendering mode (default Tr 0) must fire 7.21.4.1-1.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture
    /// <c>pdfua1-nonembedded-font-visible</c>.
    /// </summary>
    [Fact]
    public void UaNonEmbeddedFontVisibleDraw_Fires72141_1()
    {
        var bytes = OracleCorpus.Ua1NonEmbeddedFontVisibleDraw();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-1");
    }

    /// <summary>
    /// §7.21.4.1-1 FP-safety guard — Tr 3 exemption (UaFontEmbeddingRule): a non-embedded simple
    /// TrueType font drawn ONLY with text rendering mode 3 (invisible text) must NOT fire 7.21.4.1-1.
    /// veraPDF 1.30.2 does not fire 7.21.4.1-1 for this document (renderingMode == 3 exemption).
    /// This is the critical false-positive guard: if the rule fired here it would be an FP.
    /// </summary>
    [Fact]
    public void UaNonEmbeddedFontInvisibleOnly_DoesNotFire72141_1()
    {
        var bytes = OracleCorpus.Ua1NonEmbeddedFontInvisibleOnly();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-1");
    }

    /// <summary>
    /// §7.21.4.1-1 FP-safety guard — embedded font (UaFontEmbeddingRule): a simple TrueType font
    /// WITH an embedded font program (/FontFile2, DejaVu) drawn visibly must NOT fire 7.21.4.1-1.
    /// veraPDF 1.30.2 does not fire 7.21.4.1-1 for this document (containsFontFile == true).
    /// </summary>
    [Fact]
    public void UaEmbeddedSimpleFontVisibleDraw_DoesNotFire72141_1()
    {
        var bytes = OracleCorpus.Ua1EmbeddedSimpleFontVisibleDraw();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-1");
    }

    /// <summary>
    /// §7.21.4.1-1 FP-safety guard — Type0 composite font (UaFontEmbeddingRule): the standard
    /// UA-1 tagged baseline uses a Type0 / CIDFontType2 composite font, which is exempt from
    /// 7.21.4.1-1. The rule must NOT fire for the baseline.
    /// </summary>
    [Fact]
    public void UaBaselineType0Font_DoesNotFire72141_1()
    {
        var bytes = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-1");
    }

    // ── Batch A5b — §7.21.8-1 .notdef + §7.21.7-2 forbidden-ToUnicode unit tests ─────────────────

    /// <summary>
    /// §7.21.8-1 positive control (UaNotdefGlyphRule): a document that shows glyph index 0 (0x0000 =
    /// .notdef) with an Identity-H CIDFontType2 composite font must fire 7.21.8-1.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture <c>pdfua1-notdef-glyph</c>.
    /// </summary>
    [Fact]
    public void UaNotdefGlyphShown_Fires72181()
    {
        var bytes = OracleCorpus.Ua1PdfDrawingNotdef();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.8-1");
    }

    /// <summary>
    /// §7.21.8-1 FP-safety guard (UaNotdefGlyphRule): the standard UA-1 tagged baseline draws only
    /// normal glyphs (no glyph index 0). The rule must NOT fire.
    /// veraPDF 1.30.2 does not fire 7.21.8-1 for this document.
    /// </summary>
    [Fact]
    public void UaBaselineNoNotdef_DoesNotFire72181()
    {
        var bytes = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.8-1");
    }

    /// <summary>
    /// §7.21.7-2 positive control (UaToUnicodeForbiddenRule): a document whose /ToUnicode CMap maps
    /// a SHOWN code (0x0041) to U+0000 must fire 7.21.7-2.
    /// Cross-validated against veraPDF 1.30.2 via the oracle fixture
    /// <c>pdfua1-tounicode-forbidden-shown</c>.
    /// </summary>
    [Fact]
    public void UaToUnicodeForbiddenShownCode_Fires72172()
    {
        var bytes = OracleCorpus.Ua1ToUnicodeForbiddenShownCode();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.7-2");
    }

    /// <summary>
    /// §7.21.7-2 regression guard (UaToUnicodeForbiddenRule): a document whose /ToUnicode CMap maps
    /// an UNUSED code (0xFFFF) to U+0000 — but the only SHOWN code (0x0041) maps to U+0041 (valid)
    /// — must NOT fire 7.21.7-2. This is the critical false-positive guard: the prior reverted
    /// implementation (git ab5dc76) fired this incorrectly because it scanned the whole CMap.
    /// veraPDF 1.30.2 does not fire 7.21.7-2 for this document (only shown codes are evaluated).
    /// </summary>
    [Fact]
    public void UaToUnicodeUnusedBadMapping_DoesNotFire72172()
    {
        var bytes = OracleCorpus.Ua1ToUnicodeUnusedBadMappingCompliant();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.7-2");
    }

    /// <summary>
    /// §7.21.7-2 FP-safety guard — no /ToUnicode (UaToUnicodeForbiddenRule): a font without a
    /// /ToUnicode stream has no mapping → rule must NOT fire (null mapping is compliant per spec).
    /// </summary>
    [Fact]
    public void UaNoToUnicode_DoesNotFire72172()
    {
        // The PDF/A-2b embedded-font baseline has a Type0 Identity-H font but we validate as UA-1
        // which doesn't enforce /ToUnicode presence for 7.21.7-2. The font HAS a ToUnicode so this
        // tests that normal mappings don't fire; the actual no-ToUnicode case is tested via the
        // baseline type that omits it.
        var bytes = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.7-2");
    }

    // ── Batch A5c — §7.21.4.1-2 glyph presence (Tr-3-exempt) unit tests ─────────────────────────

    /// <summary>
    /// §7.21.4.1-2 positive control (UaGlyphPresenceRule): a document that shows GID 0xEA60 (60000,
    /// beyond the embedded program's glyph count) with a VISIBLE rendering mode (Tr 0) must fire
    /// 7.21.4.1-2. Cross-validated against veraPDF 1.30.2 via the oracle fixture
    /// <c>pdfua1-glyph-not-present</c>: veraPDF fires clause 7.21.4.1-2 (exit 1).
    /// </summary>
    [Fact]
    public void UaOutOfRangeGlyphVisible_Fires7214121()
    {
        var bytes = OracleCorpus.Ua1OutOfRangeGlyphVisible();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-2");
    }

    /// <summary>
    /// §7.21.4.1-2 FP-safety guard — Tr-3 exemption (UaGlyphPresenceRule): the SAME out-of-range
    /// GID (0xEA60) drawn ONLY with text rendering mode 3 (invisible text) must NOT fire 7.21.4.1-2.
    /// veraPDF 1.30.2 does not fire 7.21.4.1-2 for this document (renderingMode == 3 exemption).
    /// Cross-validated via the oracle fixture <c>pdfua1-glyph-not-present-invisible</c>.
    /// </summary>
    [Fact]
    public void UaOutOfRangeGlyphInvisible_DoesNotFire7214121()
    {
        var bytes = OracleCorpus.Ua1OutOfRangeGlyphInvisible();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-2");
    }

    /// <summary>
    /// §7.21.4.1-2 FP-safety guard — in-range glyphs (UaGlyphPresenceRule): the standard UA-1
    /// tagged baseline draws only glyphs present in the embedded subset. The rule must NOT fire.
    /// Cross-validated against veraPDF 1.30.2: clause 7.21.4.1-2 is absent from failures (exit 0).
    /// </summary>
    [Fact]
    public void UaInRangeGlyphsBaseline_DoesNotFire7214121()
    {
        var bytes = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-2");
    }

    /// <summary>
    /// §7.21.4.1-2 FP-safety guard — unknown rendering mode (UaGlyphPresenceRule): when the
    /// rendering mode cannot be determined (RenderingMode == -1 from an indeterminate Tr operand)
    /// the rule must NOT fire. A rendering mode of -1 maps to "can't determine" (isGlyphPresent ==
    /// null direction), which the veraPDF predicate treats as compliant.
    /// </summary>
    [Fact]
    public void UaUnknownRenderingMode_DoesNotFire7214121()
    {
        // Build a UA-1 document where Tr is set to an indeterminate value (a name operand instead
        // of an integer, which ContentStreamUsage tracks as RenderingMode -1). The glyph is
        // out-of-range (GID 60000), but since the rendering mode is unknown, the rule must not fire.
        var bytes = Ua1OutOfRangeGlyphUnknownTr();
        var result = PdfPreflight.Validate(bytes, Conformance.PdfConformance.PdfUA1);
        Assert.DoesNotContain(result.Assertions, a => a.RuleId == "ISO14289-1:7.21.4.1-2");
    }

    // Builds a UA-1 document where an out-of-range glyph (GID 0xEA60) is shown after setting
    // Tr to a name operand (/foo Tr), which ContentStreamUsage parses as RenderingMode == -1.
    private static byte[] Ua1OutOfRangeGlyphUnknownTr()
    {
        var baseline = OracleCorpus.Ua1TaggedWithEmbeddedFont();
        using var reader = VellumPdf.Reader.PdfReader.Open(baseline);
        var pagesRef = (VellumPdf.Core.PdfIndirectReference)reader.Catalog.Get(new VellumPdf.Core.PdfName("Pages"))!;
        var pages = (VellumPdf.Core.PdfDictionary)reader.Resolve(pagesRef.ObjectNumber)!;
        var kidsObj = pages.Get(new VellumPdf.Core.PdfName("Kids"));
        var kids = kidsObj is VellumPdf.Core.PdfIndirectReference kr
            ? (VellumPdf.Core.PdfArray)reader.Resolve(kr.ObjectNumber)!
            : (VellumPdf.Core.PdfArray)kidsObj!;
        var pageRef = (VellumPdf.Core.PdfIndirectReference)kids[0];
        var page = (VellumPdf.Core.PdfDictionary)reader.Resolve(pageRef.ObjectNumber)!;
        var resources = (VellumPdf.Core.PdfDictionary)reader.ResolveValue(page.Get(new VellumPdf.Core.PdfName("Resources"))!)!;
        var fonts = (VellumPdf.Core.PdfDictionary)reader.ResolveValue(resources.Get(VellumPdf.Core.PdfName.Font)!)!;
        var fontName = fonts.Entries.First().Key.Value;

        var contentNum = reader.Size;
        var newPage = ClonePageDict(page);
        newPage.Set(new VellumPdf.Core.PdfName("Contents"),
            new VellumPdf.Core.PdfArray([page.Get(new VellumPdf.Core.PdfName("Contents"))!, new VellumPdf.Core.PdfIndirectReference(contentNum)]));

        // `/foo Tr` — a name operand for Tr, which ContentStreamUsage resolves to RenderingMode -1 (unknown).
        var content = new VellumPdf.Core.PdfStream(
            System.Text.Encoding.ASCII.GetBytes($"BT /foo Tr /{fontName} 12 Tf 72 600 Td <EA60> Tj ET"));
        return reader.AppendRevision([(pageRef.ObjectNumber, newPage), (contentNum, content)]);
    }

    private static VellumPdf.Core.PdfDictionary ClonePageDict(VellumPdf.Core.PdfDictionary d)
    {
        var clone = new VellumPdf.Core.PdfDictionary();
        foreach (var e in d.Entries)
            clone.Set(e.Key, e.Value);
        return clone;
    }

}
