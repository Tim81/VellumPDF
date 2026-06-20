// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// ISO 19005-2 §6.6.2.3.1 (XMP property provenance). Every property in the document's XMP metadata
/// shall belong either to a predefined XMP schema or to a schema declared by a PDF/A extension
/// schema (see <see cref="ExtensionSchemaRule"/>). A property in a namespace that is neither
/// predefined nor declared is not permitted.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.6.2.3.1, the XMP Specification (2005) predefined schema set,
/// and the PDF/A identification and extension-schema schemas. Clean-room: the predefined-namespace
/// set is derived from the specifications, not copied from any third-party validation profile, and
/// was cross-checked against veraPDF (every namespace below is accepted without an extension schema;
/// <c>pdfuaid</c> is deliberately excluded — veraPDF requires it to be declared in PDF/A-2).
/// <para>
/// The check is at the <em>namespace</em> level: a property whose namespace is predefined or declared
/// is accepted regardless of its local name. This is intentionally conservative — it cannot reject a
/// conforming property (no false positive against the predefined set), at the cost of not catching a
/// non-predefined <em>property name</em> within a predefined namespace, nor a value-type mismatch
/// (§6.6.2.3.1 t2). Both are deferred and need the per-property predefined catalogue.
/// </para>
/// </remarks>
internal sealed class PropertyUsageRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.6.2.3.1-undeclared-property";

    public string Clause => "ISO 19005-2:2011, 6.6.2.3.1";

    private static readonly PdfName _metadata = new("Metadata");

    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmpMeta = "adobe:ns:meta/";
    private const string Xml = "http://www.w3.org/XML/1998/namespace";

    private static readonly XNamespace _ext = "http://www.aiim.org/pdfa/ns/extension/";
    private static readonly XNamespace _schema = "http://www.aiim.org/pdfa/ns/schema#";

    /// <summary>The XMP-2005 predefined schemas plus the PDF/A identification and extension-schema
    /// namespaces — the namespaces a conforming PDF/A-2 file may use without an extension schema.</summary>
    private static readonly HashSet<string> _predefined = new(StringComparer.Ordinal)
    {
        "http://purl.org/dc/elements/1.1/",                 // dc
        "http://ns.adobe.com/xap/1.0/",                     // xmp
        "http://ns.adobe.com/xap/1.0/rights/",              // xmpRights
        "http://ns.adobe.com/xap/1.0/mm/",                  // xmpMM
        "http://ns.adobe.com/xap/1.0/bj/",                  // xmpBJ
        "http://ns.adobe.com/xap/1.0/t/pg/",                // xmpTPg
        "http://ns.adobe.com/xmp/1.0/DynamicMedia/",        // xmpDM
        "http://ns.adobe.com/xap/1.0/g/",                   // xmpG
        "http://ns.adobe.com/xap/1.0/g/img/",               // xmpGImg
        "http://ns.adobe.com/xmp/Identifier/qual/1.0/",     // xmpidq
        "http://ns.adobe.com/pdf/1.3/",                     // pdf
        "http://ns.adobe.com/photoshop/1.0/",               // photoshop
        "http://ns.adobe.com/camera-raw-settings/1.0/",     // crs
        "http://ns.adobe.com/tiff/1.0/",                    // tiff
        "http://ns.adobe.com/exif/1.0/",                    // exif
        "http://ns.adobe.com/exif/1.0/aux/",                // aux
        "http://ns.adobe.com/xap/1.0/sType/Dimensions#",    // stDim
        "http://ns.adobe.com/xap/1.0/sType/ResourceEvent#", // stEvt
        "http://ns.adobe.com/xap/1.0/sType/ResourceRef#",   // stRef
        "http://ns.adobe.com/xap/1.0/sType/Version#",       // stVer
        "http://ns.adobe.com/xap/1.0/sType/Job#",           // stJob
        "http://ns.adobe.com/xap/1.0/sType/Font#",          // stFnt
        "http://www.aiim.org/pdfa/ns/id/",                  // pdfaid
        "http://www.aiim.org/pdfa/ns/extension/",           // pdfaExtension
        "http://www.aiim.org/pdfa/ns/schema#",              // pdfaSchema
        "http://www.aiim.org/pdfa/ns/property#",            // pdfaProperty
        "http://www.aiim.org/pdfa/ns/type#",                // pdfaType
        "http://www.aiim.org/pdfa/ns/field#",               // pdfaField
    };

    public void Evaluate(PreflightContext context)
    {
        var stream = context.ResolveStream(context.Catalog.Get(_metadata));
        if (stream is null)
            return;
        var bytes = context.DecodeStream(stream);
        if (bytes is null)
            return;

        var packet = XmpPacket.Parse(bytes);
        if (packet.Document is not { } doc)
            return; // Malformed XMP is reported by XmpConformanceRule.

        var declared = CollectDeclaredNamespaces(doc);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in doc.Descendants())
        {
            Check(context, element.Name, declared, reported);
            foreach (var attribute in element.Attributes())
                if (!attribute.IsNamespaceDeclaration)
                    Check(context, attribute.Name, declared, reported);
        }
    }

    private void Check(PreflightContext context, XName name, HashSet<string> declared, HashSet<string> reported)
    {
        var ns = name.NamespaceName;

        // Skip the RDF/XMP structural namespaces and unqualified names — these are not XMP properties.
        if (ns is "" or Rdf or XmpMeta or Xml)
            return;
        if (_predefined.Contains(ns) || declared.Contains(ns))
            return;

        if (reported.Add(ns))
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"The XMP metadata uses a property in namespace <{ns}>, which is neither a predefined "
                + "schema nor declared by a PDF/A extension schema.");
    }

    // The namespace URIs declared by the document's PDF/A extension schemas.
    private static HashSet<string> CollectDeclaredNamespaces(XDocument doc)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in doc.Descendants(_schema + "namespaceURI"))
            declared.Add(uri.Value.Trim());
        // Also accept the attribute serialisation of pdfaSchema:namespaceURI.
        foreach (var element in doc.Descendants())
            if (element.Attribute(_schema + "namespaceURI") is { } attribute)
                declared.Add(attribute.Value.Trim());
        return declared;
    }
}
