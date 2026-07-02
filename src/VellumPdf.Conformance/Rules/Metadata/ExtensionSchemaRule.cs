// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// ISO 19005-2 §6.6.2.3.3 (PDF/A extension schema structure) and §6.6.2.3.2 (undefined fields).
/// When the document XMP declares extension schemas (via the <c>pdfaExtension:schemas</c> bag),
/// each declared schema, property, and custom value type shall carry the required fields, and
/// no container element shall carry child fields outside its defined allowed set.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.6.2.3.2 and 6.6.2.3.3 and the PDF/A extension-schema container
/// schema. Clean-room: derived from the specification text, not from any third-party validation
/// profile. Consumes the parsed packet from <see cref="XmpPacket"/>; both the element-content and
/// attribute serialisations of each field are accepted.
/// <para>
/// §6.6.2.3.2-1 fires when any container element (pdfaSchema, pdfaProperty, pdfaType, pdfaField)
/// carries a direct child element whose expanded name is not in that container's allowed set.
/// veraPDF flags all namespaces uniformly — any foreign child (whether in the same pdfa* namespace or
/// an entirely different namespace, e.g. dc:) is undefined if its name is not in the allowed set.
/// </para>
/// <para>
/// §6.6.2.3.3 fires when a required field is absent from a container, uses the wrong RDF container
/// type (rdf:Bag vs. rdf:Seq), or carries an element with the wrong namespace prefix. Both rules can
/// fire independently on the same container.
/// </para>
/// <para>
/// §6.6.2.3.3-8/-17 additionally enforce <em>isValueTypeDefined</em>: the value type named by a
/// pdfaProperty:valueType / pdfaField:valueType must resolve to a predefined XMP value type, a
/// container form (bag/seq/alt/Lang Alt) over a defined item type, or a custom value type declared in
/// the same schema's pdfaSchema:valueType container. An unrecognised, undeclared name fires the
/// corresponding sub-clause. The predefined set is the well-established XMP simple and structured
/// value-type catalogue (ISO 16684-1 / XMP Specification Part 1 §8); a name outside it that is also
/// not schema-declared is treated as undefined.
/// </para>
/// <para>
/// Container-type and prefix checks are verified empirically against veraPDF 1.30.2 (probe suite
/// run 2026-06-23). veraPDF uses literal XML prefix token comparison, not namespace-URI equivalence,
/// so "pdfaProperty" and "prop" are distinct even if both bind the same URI. The lenient prefix
/// handling for §6.6.2.3.3-5/-6/-15 (null-OR-canonical) was probe-confirmed: the property, valueType,
/// and field <em>container</em> elements accept a null prefix (default namespace declaration); the
/// scalar field elements they contain (schema, namespaceURI, prefix, name, valueType, description)
/// require the canonical named prefix and do NOT accept a null prefix.
/// </para>
/// <para>
/// This rule validates the structure of <em>declared</em> extension schemas (a document with no
/// extension schema, or a well-formed one, is unaffected). The companion requirement that a
/// <em>used</em> property must itself be predefined or declared (§6.6.2.3.1) lives in
/// <see cref="PropertyUsageRule"/>.
/// </para>
/// </remarks>
internal sealed class ExtensionSchemaRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.6.2.3.3-extension-schema";

    public string Clause => "ISO 19005-2:2011, 6.6.2.3.3";

    private const string UndefinedFieldRuleId = "ISO19005-2:6.6.2.3.2-undefined-field";
    private const string UndefinedFieldClause = "ISO 19005-2:2011, 6.6.2.3.2";

    // §6.6.2.3.3 sub-clause rule identifiers for container-type and prefix checks.
    // Named to mirror the veraPDF testNumber so failures are cross-referenceable.
    private const string Clause_1 = "ISO19005-2:6.6.2.3.3-1";   // schemas: Bag + pdfaExtension prefix
    private const string Clause_5 = "ISO19005-2:6.6.2.3.3-5";   // property: Seq + null|pdfaSchema prefix
    private const string Clause_6 = "ISO19005-2:6.6.2.3.3-6";   // valueType: Seq + null|pdfaSchema prefix
    private const string Clause_8 = "ISO19005-2:6.6.2.3.3-8";   // pdfaProperty:valueType: pdfaProperty prefix
    private const string Clause_15 = "ISO19005-2:6.6.2.3.3-15";  // field: Seq + null|pdfaType prefix
    private const string Clause_17 = "ISO19005-2:6.6.2.3.3-17";  // pdfaField:valueType: pdfaField prefix

    // Allowed child element names for each container type (§6.6.2.3.3 Tables 1–4).
    // Any element child of a container rdf:li NOT in this set is an undefined field (§6.6.2.3.2-1).
    private static readonly HashSet<XName> _allowedSchemaChildren =
    [
        // pdfaSchema container (Table 1): schema, namespaceURI, prefix, property, valueType
        XName.Get("schema",       "http://www.aiim.org/pdfa/ns/schema#"),
        XName.Get("namespaceURI", "http://www.aiim.org/pdfa/ns/schema#"),
        XName.Get("prefix",       "http://www.aiim.org/pdfa/ns/schema#"),
        XName.Get("property",     "http://www.aiim.org/pdfa/ns/schema#"),
        XName.Get("valueType",    "http://www.aiim.org/pdfa/ns/schema#"),
    ];

    private static readonly HashSet<XName> _allowedPropertyChildren =
    [
        // pdfaProperty container (Table 2): name, valueType, category, description
        XName.Get("name",        "http://www.aiim.org/pdfa/ns/property#"),
        XName.Get("valueType",   "http://www.aiim.org/pdfa/ns/property#"),
        XName.Get("category",    "http://www.aiim.org/pdfa/ns/property#"),
        XName.Get("description", "http://www.aiim.org/pdfa/ns/property#"),
    ];

    private static readonly HashSet<XName> _allowedTypeChildren =
    [
        // pdfaType container (Table 3): type, namespaceURI, prefix, description, field
        XName.Get("type",         "http://www.aiim.org/pdfa/ns/type#"),
        XName.Get("namespaceURI", "http://www.aiim.org/pdfa/ns/type#"),
        XName.Get("prefix",       "http://www.aiim.org/pdfa/ns/type#"),
        XName.Get("description",  "http://www.aiim.org/pdfa/ns/type#"),
        XName.Get("field",        "http://www.aiim.org/pdfa/ns/type#"),
    ];

    private static readonly HashSet<XName> _allowedFieldChildren =
    [
        // pdfaField container (Table 4): name, valueType, description
        XName.Get("name",        "http://www.aiim.org/pdfa/ns/field#"),
        XName.Get("valueType",   "http://www.aiim.org/pdfa/ns/field#"),
        XName.Get("description", "http://www.aiim.org/pdfa/ns/field#"),
    ];

    private static readonly XNamespace _rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace _ext = "http://www.aiim.org/pdfa/ns/extension/";
    private static readonly XNamespace _schema = "http://www.aiim.org/pdfa/ns/schema#";
    private static readonly XNamespace _property = "http://www.aiim.org/pdfa/ns/property#";
    private static readonly XNamespace _type = "http://www.aiim.org/pdfa/ns/type#";
    private static readonly XNamespace _field = "http://www.aiim.org/pdfa/ns/field#";

    // §6.6.2.3.3-8/-17 isValueTypeDefined: the predefined XMP-Specification value types. A declared
    // pdfaProperty:valueType / pdfaField:valueType is "defined" when it names one of these, a
    // container form over one of these (bag/seq/alt/Lang Alt), or a custom value type declared in the
    // same schema's pdfaSchema:valueType container. This set is the well-established XMP simple and
    // structured value-type catalogue (ISO 16684-1 / XMP Specification Part 1 §8) — only names that
    // are unambiguously standard are listed, so a name outside the set is a positive-knowledge
    // "undefined" signal rather than a guess. FP-safe: an unusual-but-legitimate type is far more
    // likely to be a schema-declared custom type (also accepted) than an unlisted standard name.
    private static readonly HashSet<string> _predefinedValueTypes = new(StringComparer.Ordinal)
    {
        // Simple types (XMP Spec §8.2.1).
        "Boolean", "Date", "Integer", "Real", "Text",
        // Derived simple types (XMP Spec §8.2.2 / §8.3).
        "AgentName", "Choice", "GUID", "Locale", "MIMEType", "ProperName",
        "RenditionClass", "URI", "URL", "XPath", "Rational", "Part",
        // Structured types (XMP Spec §8.4 and the PDF/A-defined lang-alt).
        "Lang Alt", "Dimensions", "Font", "ColorantList", "Colorant",
        "ResourceRef", "ResourceEvent", "Thumbnail", "Job", "Version",
    };

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
        {
            // §6.6.2.3.3-1: pdfaExtension:schemas shall be an rdf:Bag with the prefix
            // "pdfaExtension". veraPDF test: isValidBag == true && prefix == "pdfaExtension".
            // Probe-confirmed 2026-06-23: Seq fires -1; wrong ext prefix (e.g. "ext:") fires -1.
            // Null prefix is NOT accepted for -1 (no leniency).
            // FP-safe: only fire when a container child is found (Bag or Seq); stay silent when
            // neither is present (unusual/malformed — reported by other rules).
            var bag = schemas.Element(_rdf + "Bag");
            var seq = schemas.Element(_rdf + "Seq");
            if (bag is not null || seq is not null)
            {
                if (bag is null || !HasRequiredPrefix(schemas, _ext, "pdfaExtension"))
                    context.Report(
                        Clause_1, Clause, PreflightSeverity.Error,
                        "The pdfaExtension:schemas container shall be an rdf:Bag and the schemas "
                        + "element shall use the namespace prefix 'pdfaExtension' (§6.6.2.3.3-1).");
            }

            foreach (var schemaItem in Items(schemas))
                CheckSchema(context, schemaItem);
        }
    }

    private void CheckSchema(PreflightContext context, XElement schema)
    {
        // §6.6.2.3.2-1: no child element outside the allowed pdfaSchema set.
        CheckUndefinedFields(context, schema, _allowedSchemaChildren, "schema");

        // §6.6.2.3.3: required fields present.
        RequireField(context, schema, _schema, "schema", "a declared extension schema is missing its 'schema' description");
        RequireField(context, schema, _schema, "namespaceURI", "a declared extension schema is missing its 'namespaceURI'");
        RequireField(context, schema, _schema, "prefix", "a declared extension schema is missing its 'prefix'");

        // §6.6.2.3.3-8/-17 isValueTypeDefined: harvest the custom value-type names this schema
        // declares in its pdfaSchema:valueType container. A property or field valueType that names
        // one of these is "defined" (as is a predefined XMP type). Done before checking properties so
        // both the property and field passes can resolve custom-type references within this schema.
        var declaredTypes = CollectDeclaredValueTypeNames(schema);

        var propertyContainer = schema.Element(_schema + "property");
        if (propertyContainer is not null)
        {
            // §6.6.2.3.3-5: pdfaSchema:property shall be an rdf:Seq with prefix null or "pdfaSchema".
            // veraPDF test: isPropertyValidSeq == true && (propertyPrefix == null || propertyPrefix == "pdfaSchema").
            // Probe-confirmed 2026-06-23: Bag fires -5; wrong prefix (e.g. "sch:") fires -5;
            // null prefix (default namespace on the property element) is accepted and must NOT fire.
            CheckContainerTypeAndPrefix(
                context, propertyContainer, _schema, "pdfaSchema",
                Clause_5,
                "pdfaSchema:property shall be an rdf:Seq and use the namespace prefix "
                + "'pdfaSchema' (or the default namespace) (§6.6.2.3.3-5).");
            foreach (var property in Items(propertyContainer))
                CheckProperty(context, property, declaredTypes);
        }

        // §6.6.2.3.3 t11–t18: custom value types declared in the schema's valueType container.
        var valueTypeContainer = schema.Element(_schema + "valueType");
        if (valueTypeContainer is not null)
        {
            // §6.6.2.3.3-6: pdfaSchema:valueType shall be an rdf:Seq with prefix null or "pdfaSchema".
            // veraPDF test: isValueTypeValidSeq == true && (valueTypePrefix == null || valueTypePrefix == "pdfaSchema").
            // Same null-prefix leniency as -5, confirmed by probe.
            CheckContainerTypeAndPrefix(
                context, valueTypeContainer, _schema, "pdfaSchema",
                Clause_6,
                "pdfaSchema:valueType shall be an rdf:Seq and use the namespace prefix "
                + "'pdfaSchema' (or the default namespace) (§6.6.2.3.3-6).");
            foreach (var valueType in Items(valueTypeContainer))
                CheckValueType(context, valueType, declaredTypes);
        }
    }

    // §6.6.2.3.3-8/-17 isValueTypeDefined: the custom value-type names declared in a schema's
    // pdfaSchema:valueType container (the pdfaType:type field of each declared value type). A
    // property or field valueType that names one of these resolves as defined. Case-sensitive to
    // match the XMP type-name convention.
    private static HashSet<string> CollectDeclaredValueTypeNames(XElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var container = schema.Element(_schema + "valueType");
        if (container is null)
            return names;
        foreach (var valueType in Items(container))
        {
            var typeName = Field(valueType, _type, "type");
            if (!string.IsNullOrEmpty(typeName))
                names.Add(typeName);
        }
        return names;
    }

    // §6.6.2.3.3-8/-17 isValueTypeDefined: true when the declared value-type string names a
    // predefined XMP value type, a container form (bag/seq/alt) over a defined item type, or a custom
    // value type declared in the same schema. Returns true (i.e. does not fire) for any string it
    // cannot confidently classify as undefined — an empty/whitespace name is handled elsewhere
    // (the field-presence check), so it is treated as defined here to avoid double-reporting.
    private static bool IsValueTypeDefined(string? valueType, HashSet<string> declaredTypes)
    {
        if (string.IsNullOrWhiteSpace(valueType))
            return true; // absence is reported by RequireField; do not double-report here.

        var name = valueType.Trim();

        if (_predefinedValueTypes.Contains(name) || declaredTypes.Contains(name))
            return true;

        // Container forms: "bag X", "seq X", "alt X" — the item type X must itself be defined.
        foreach (var prefix in _containerPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var item = name[prefix.Length..].Trim();
                return item.Length == 0 || IsValueTypeDefined(item, declaredTypes);
            }
        }

        return false;
    }

    private static readonly string[] _containerPrefixes = ["bag ", "seq ", "alt "];

    private void CheckValueType(PreflightContext context, XElement valueType, HashSet<string> declaredTypes)
    {
        // §6.6.2.3.2-1: no child element outside the allowed pdfaType set.
        CheckUndefinedFields(context, valueType, _allowedTypeChildren, "value type");

        // §6.6.2.3.3: required fields present.
        RequireField(context, valueType, _type, "type", "a declared value type is missing its 'type' name");
        RequireField(context, valueType, _type, "namespaceURI", "a declared value type is missing its 'namespaceURI'");
        RequireField(context, valueType, _type, "prefix", "a declared value type is missing its 'prefix'");
        RequireField(context, valueType, _type, "description", "a declared value type is missing its 'description'");

        var fieldContainer = valueType.Element(_type + "field");
        if (fieldContainer is null)
            return;

        // §6.6.2.3.3-15: pdfaType:field shall be an rdf:Seq with prefix null or "pdfaType".
        // veraPDF test: isFieldValidSeq == true && (fieldPrefix == null || fieldPrefix == "pdfaType").
        // Same null-prefix leniency as -5/-6: the field container element may use the default namespace.
        // Probe-confirmed 2026-06-23: Bag fires -15; wrong prefix (e.g. "t:") fires -15; null prefix accepted.
        CheckContainerTypeAndPrefix(
            context, fieldContainer, _type, "pdfaType",
            Clause_15,
            "pdfaType:field shall be an rdf:Seq and use the namespace prefix "
            + "'pdfaType' (or the default namespace) (§6.6.2.3.3-15).");

        foreach (var field in Items(fieldContainer))
        {
            // §6.6.2.3.2-1: no child element outside the allowed pdfaField set.
            CheckUndefinedFields(context, field, _allowedFieldChildren, "field");

            // §6.6.2.3.3: required fields present.
            RequireField(context, field, _field, "name", "a value-type field is missing its 'name'");
            RequireField(context, field, _field, "valueType", "a value-type field is missing its 'valueType'");
            RequireField(context, field, _field, "description", "a value-type field is missing its 'description'");

            // §6.6.2.3.3-17: pdfaField:valueType shall have prefix "pdfaField" (no null leniency) AND
            // name a defined value type (isValueTypeDefined). veraPDF test: isValueTypeValidText &&
            // isValueTypeDefined && valueTypePrefix == "pdfaField". Probe-confirmed 2026-06-23 for the
            // prefix condition; the isValueTypeDefined condition is from ISO 19005-2 §6.6.2.3.3.
            var fieldValueType = field.Element(_field + "valueType");
            if (fieldValueType is not null && !HasRequiredPrefix(fieldValueType, _field, "pdfaField"))
                context.Report(
                    Clause_17, Clause, PreflightSeverity.Error,
                    "Field 'valueType' of a pdfaField container shall use the namespace prefix "
                    + "'pdfaField'; a different prefix was found (§6.6.2.3.3-17).");
            else if (!IsValueTypeDefined(Field(field, _field, "valueType"), declaredTypes))
                context.Report(
                    Clause_17, Clause, PreflightSeverity.Error,
                    $"The value type '{Field(field, _field, "valueType")}' declared for a pdfaField "
                    + "is neither a predefined XMP value type nor a value type declared by the same "
                    + "extension schema (§6.6.2.3.3-17).");
        }
    }

    private void CheckProperty(PreflightContext context, XElement property, HashSet<string> declaredTypes)
    {
        // §6.6.2.3.2-1: no child element outside the allowed pdfaProperty set.
        CheckUndefinedFields(context, property, _allowedPropertyChildren, "property");

        // §6.6.2.3.3: required fields present.
        RequireField(context, property, _property, "name", "an extension-schema property is missing its 'name'");
        RequireField(context, property, _property, "valueType", "an extension-schema property is missing its 'valueType'");
        RequireField(context, property, _property, "description", "an extension-schema property is missing its 'description'");

        // §6.6.2.3.3-8: pdfaProperty:valueType shall have prefix "pdfaProperty" (no null leniency) AND
        // name a defined value type (isValueTypeDefined). veraPDF test: isValueTypeValidText &&
        // isValueTypeDefined && valueTypePrefix == "pdfaProperty". Probe-confirmed 2026-06-23 for the
        // prefix condition; the isValueTypeDefined condition is from ISO 19005-2 §6.6.2.3.3. The
        // scalar-field prefix check is separate from the container-type check above (-5/-6).
        var valueTypeField = property.Element(_property + "valueType");
        if (valueTypeField is not null && !HasRequiredPrefix(valueTypeField, _property, "pdfaProperty"))
            context.Report(
                Clause_8, Clause, PreflightSeverity.Error,
                "Field 'valueType' of a pdfaProperty container shall use the namespace prefix "
                + "'pdfaProperty'; a different prefix was found (§6.6.2.3.3-8).");
        else if (!IsValueTypeDefined(Field(property, _property, "valueType"), declaredTypes))
            context.Report(
                Clause_8, Clause, PreflightSeverity.Error,
                $"The value type '{Field(property, _property, "valueType")}' declared for an "
                + "extension-schema property is neither a predefined XMP value type nor a value type "
                + "declared by the same extension schema (§6.6.2.3.3-8).");

        var category = Field(property, _property, "category");
        if (category is null || (category != "internal" && category != "external"))
            context.Report(
                "ISO19005-2:6.6.2.3.3-property-category",
                Clause,
                PreflightSeverity.Error,
                "An extension-schema property's 'category' shall be present and equal to 'internal' or "
                + $"'external' (found {(category is null ? "absent" : $"'{category}'")}).");
    }

    // §6.6.2.3.2-1: check that every direct child element of <paramref name="container"/> has an
    // expanded name that is in <paramref name="allowed"/>. Any element not in the allowed set is an
    // "undefined field" per the PDF/A extension-schema container schema. veraPDF flags all namespaces
    // uniformly — a foreign child (same pdfa* namespace or a completely different namespace, e.g. dc:)
    // is equally non-conformant if its expanded name is not in the allowed set.
    private static void CheckUndefinedFields(
        PreflightContext context, XElement container, HashSet<XName> allowed, string containerKind)
    {
        foreach (var child in container.Elements())
        {
            if (!allowed.Contains(child.Name))
                context.Report(
                    UndefinedFieldRuleId,
                    UndefinedFieldClause,
                    PreflightSeverity.Error,
                    $"An extension-schema {containerKind} container carries an undefined field "
                    + $"'{child.Name.LocalName}' (namespace '{child.Name.NamespaceName}') "
                    + "that is not defined by the PDF/A extension-schema container schema (§6.6.2.3.2).");
        }
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
    // container). An rdf:li with rdf:parseType="Resource" carries the fields as its own children; the
    // equivalent blank-node serialisation wraps them in a single rdf:Description — Unwrap descends
    // into that so both forms expose the same field children.
    private static IEnumerable<XElement> Items(XElement container)
    {
        var collection = container.Element(_rdf + "Bag") ?? container.Element(_rdf + "Seq");
        var source = collection ?? container;
        foreach (var li in source.Elements(_rdf + "li"))
            yield return Unwrap(li);
    }

    // Descends into a lone <rdf:Description> child (the blank-node serialisation of a container);
    // when the item carries its fields directly (rdf:parseType="Resource") it is returned unchanged.
    private static XElement Unwrap(XElement item)
    {
        XElement? only = null;
        foreach (var child in item.Elements())
        {
            if (only is not null)
                return item; // more than one element child — fields are carried directly
            only = child;
        }
        return only is not null && only.Name == _rdf + "Description" ? only : item;
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

    // §6.6.2.3.3-5/-6/-15: reports when a container element is not an rdf:Seq, or when its namespace
    // prefix is neither null (default namespace) nor the canonical prefix. The null-prefix leniency is
    // confirmed by veraPDF probe (2026-06-23): a container element whose namespace is the XMP default
    // namespace (xmlns="...") is accepted, but a non-canonical, non-null prefix (e.g. "sch:") is not.
    //
    // FP-safe: only fires when either rdf:Bag or rdf:Seq is found; an absent or malformed inner
    // collection stays silent. The prefix check is scoped to the element visible after XDocument parsing
    // (uses GetPrefixOfNamespace / GetNamespaceOfPrefix, matching the XmpConformanceRule pattern).
    private void CheckContainerTypeAndPrefix(
        PreflightContext context, XElement containerElem,
        XNamespace ns, string canonicalPrefix,
        string ruleId, string message)
    {
        var innerBag = containerElem.Element(_rdf + "Bag");
        var innerSeq = containerElem.Element(_rdf + "Seq");
        if (innerBag is null && innerSeq is null)
            return; // absent/malformed — stay silent (FP-safe)

        // Fail if the container is a Bag (must be Seq) or if the prefix is wrong.
        var isSeq = innerSeq is not null && innerBag is null;
        var prefixOk = HasNullOrCanonicalPrefix(containerElem, ns, canonicalPrefix);

        if (!isSeq || !prefixOk)
            context.Report(ruleId, Clause, PreflightSeverity.Error, message);
    }

    // Returns true when <paramref name="element"/>'s namespace is bound via the canonical
    // prefix (strict — null/default-namespace is NOT accepted). Used for scalar field elements
    // (§6.6.2.3.3-8/-17) where veraPDF requires the exact literal prefix.
    // Pattern mirrors XmpConformanceRule.CheckPrefix (same XDocument prefix-inspection semantics).
    private static bool HasRequiredPrefix(XElement element, XNamespace ns, string required)
    {
        // null from GetPrefixOfNamespace means ns is the default namespace — NOT accepted here.
        if (element.GetPrefixOfNamespace(ns) is null)
            return false;
        return element.GetNamespaceOfPrefix(required) == ns;
    }

    // Returns true when <paramref name="element"/>'s namespace prefix is null (default namespace)
    // OR equals the canonical prefix. Used for container elements where veraPDF accepts both
    // (§6.6.2.3.3-5/-6/-15 null-prefix leniency, confirmed by probe 2026-06-23).
    private static bool HasNullOrCanonicalPrefix(XElement element, XNamespace ns, string canonical)
    {
        if (element.GetPrefixOfNamespace(ns) is null)
            return true; // default namespace — null prefix accepted for container elements
        return element.GetNamespaceOfPrefix(canonical) == ns;
    }
}
