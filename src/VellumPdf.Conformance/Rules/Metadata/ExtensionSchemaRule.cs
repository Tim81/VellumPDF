// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// ISO 19005-2 §6.6.2.3.3 (PDF/A extension schema structure). When the document XMP declares
/// extension schemas (via the <c>pdfaExtension:schemas</c> bag), each declared schema, property, and
/// custom value type shall carry the required fields: a schema needs a <c>schema</c> description, a
/// <c>namespaceURI</c>, and a <c>prefix</c>; a property needs a <c>name</c>, a <c>valueType</c>, a
/// <c>category</c> of <c>internal</c> or <c>external</c>, and a <c>description</c>; a value type
/// (<c>pdfaType</c>) needs a <c>type</c>, <c>namespaceURI</c>, <c>prefix</c>, and <c>description</c>,
/// and each of its fields (<c>pdfaField</c>) needs a <c>name</c>, <c>valueType</c>, and
/// <c>description</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.6.2.3.3 and the PDF/A extension-schema container schema.
/// Clean-room: derived from the specification text, not from any third-party validation profile.
/// Consumes the parsed packet from <see cref="XmpPacket"/>; both the element-content and
/// attribute serialisations of each field are accepted.
/// <para>
/// This rule validates the structure of <em>declared</em> extension schemas (a document with no
/// extension schema, or a well-formed one, is unaffected — so there is no risk to conforming
/// metadata). The companion requirement that a <em>used</em> property must itself be predefined or
/// declared (§6.6.2.3.1) lives in <see cref="PropertyUsageRule"/>.
/// </para>
/// </remarks>
internal sealed class ExtensionSchemaRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.6.2.3.3-extension-schema";

    public string Clause => "ISO 19005-2:2011, 6.6.2.3.3";

    private static readonly XNamespace _rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace _ext = "http://www.aiim.org/pdfa/ns/extension/";
    private static readonly XNamespace _schema = "http://www.aiim.org/pdfa/ns/schema#";
    private static readonly XNamespace _property = "http://www.aiim.org/pdfa/ns/property#";
    private static readonly XNamespace _type = "http://www.aiim.org/pdfa/ns/type#";
    private static readonly XNamespace _field = "http://www.aiim.org/pdfa/ns/field#";

    private static readonly PdfName _metadata = new("Metadata");

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

        foreach (var schemas in doc.Descendants(_ext + "schemas"))
            foreach (var schemaItem in Items(schemas))
                CheckSchema(context, schemaItem);
    }

    private void CheckSchema(PreflightContext context, XElement schema)
    {
        RequireField(context, schema, _schema, "schema", "a declared extension schema is missing its 'schema' description");
        RequireField(context, schema, _schema, "namespaceURI", "a declared extension schema is missing its 'namespaceURI'");
        RequireField(context, schema, _schema, "prefix", "a declared extension schema is missing its 'prefix'");

        var propertyContainer = schema.Element(_schema + "property");
        if (propertyContainer is not null)
            foreach (var property in Items(propertyContainer))
                CheckProperty(context, property);

        // §6.6.2.3.3 t11–t18: custom value types declared in the schema's valueType container.
        var valueTypeContainer = schema.Element(_schema + "valueType");
        if (valueTypeContainer is not null)
            foreach (var valueType in Items(valueTypeContainer))
                CheckValueType(context, valueType);
    }

    private void CheckValueType(PreflightContext context, XElement valueType)
    {
        RequireField(context, valueType, _type, "type", "a declared value type is missing its 'type' name");
        RequireField(context, valueType, _type, "namespaceURI", "a declared value type is missing its 'namespaceURI'");
        RequireField(context, valueType, _type, "prefix", "a declared value type is missing its 'prefix'");
        RequireField(context, valueType, _type, "description", "a declared value type is missing its 'description'");

        var fieldContainer = valueType.Element(_type + "field");
        if (fieldContainer is null)
            return;
        foreach (var field in Items(fieldContainer))
        {
            RequireField(context, field, _field, "name", "a value-type field is missing its 'name'");
            RequireField(context, field, _field, "valueType", "a value-type field is missing its 'valueType'");
            RequireField(context, field, _field, "description", "a value-type field is missing its 'description'");
        }
    }

    private void CheckProperty(PreflightContext context, XElement property)
    {
        RequireField(context, property, _property, "name", "an extension-schema property is missing its 'name'");
        RequireField(context, property, _property, "valueType", "an extension-schema property is missing its 'valueType'");
        RequireField(context, property, _property, "description", "an extension-schema property is missing its 'description'");

        var category = Field(property, _property, "category");
        if (category is null || (category != "internal" && category != "external"))
            context.Report(
                "ISO19005-2:6.6.2.3.3-property-category",
                Clause,
                PreflightSeverity.Error,
                "An extension-schema property's 'category' shall be present and equal to 'internal' or "
                + $"'external' (found {(category is null ? "absent" : $"'{category}'")}).");
    }

    private void RequireField(PreflightContext context, XElement parent, XNamespace ns, string local, string message)
    {
        if (Field(parent, ns, local) is null)
            context.Report(
                $"ISO19005-2:6.6.2.3.3-{local.ToLowerInvariant()}",
                Clause,
                PreflightSeverity.Error,
                $"In the PDF/A extension schema declaration, {message} (§6.6.2.3.3).");
    }

    // The members of an rdf:Bag / rdf:Seq: its rdf:li children (or the children of a single nested
    // container). An rdf:li with rdf:parseType="Resource" carries the fields as its own children.
    private static IEnumerable<XElement> Items(XElement container)
    {
        var collection = container.Element(_rdf + "Bag") ?? container.Element(_rdf + "Seq");
        var source = collection ?? container;
        return source.Elements(_rdf + "li");
    }

    // A field serialised either as a child element or as an attribute on <paramref name="parent"/>.
    private static string? Field(XElement parent, XNamespace ns, string local)
    {
        var element = parent.Element(ns + local);
        if (element is not null)
            return element.Value.Trim();
        var attribute = parent.Attribute(ns + local);
        return attribute?.Value.Trim();
    }
}
