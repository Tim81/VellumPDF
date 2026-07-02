// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using System.Xml.Linq;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// ISO 19005-2 §6.6.2.3.1 (XMP property value-type match). Every XMP property — whether in an
/// extension schema or one of the predefined XMP-Specification namespaces — shall carry a value
/// whose RDF/XMP serialisation matches the property's declared value type.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Extension-schema properties.</strong>  The document itself states the expected type
/// for each such property via the <c>pdfaProperty:valueType</c> field, so the validation is
/// entirely self-contained. Properties whose declared type resolves to an unrecognised name are
/// skipped (deferred, FP-safe).
/// </para>
/// <para>
/// <strong>Predefined-namespace properties.</strong>  A clean-room catalogue of well-established
/// Dublin Core (dc:), XMP Basic (xmp:), Adobe PDF (pdf:), and PDF/A identification (pdfaid:)
/// properties is checked against the XMP Specification 2005 §8 type tables. Only properties
/// whose expected type is documented unambiguously in the spec are included; any property not in
/// the table is skipped (unknown → no finding), making the check FP-safe by construction.
/// </para>
/// <para>
/// <strong>Type families checked.</strong>
/// <list type="bullet">
/// <item><description>
///   <em>Simple scalar types</em> — <c>Text</c>, <c>URI</c>, <c>URL</c>, <c>Rational</c>,
///   <c>MIMEType</c>, <c>XPath</c>, <c>AgentName</c>, <c>ProperName</c>,
///   <c>RenditionClass</c>, <c>Locale</c>: the property element may not have an
///   <c>rdf:Bag</c>, <c>rdf:Seq</c>, or <c>rdf:Alt</c> child (that would make it a
///   collection type, not a scalar).
/// </description></item>
/// <item><description>
///   <em>Constrained scalar types</em>: additionally the text value is validated against
///   the type's grammar:
///   <c>Integer</c> — <c>[+-]?\d+</c>;
///   <c>Real</c> — IEEE decimal notation;
///   <c>Boolean</c> — exactly <c>True</c> or <c>False</c> (XMP Spec §8.2.1.3);
///   <c>Date</c> — XMP/ISO-8601 subset (YYYY[-MM[-DD[Thh:mm[:ss[.s][±hh:mm|Z]]]]]).
/// </description></item>
/// <item><description>
///   <em>Container types</em> — <c>bag <em>Item</em></c>, <c>seq <em>Item</em></c>,
///   <c>alt <em>Item</em></c>, <c>Lang Alt</c>: the property element must have a
///   corresponding <c>rdf:Bag</c>, <c>rdf:Seq</c>, or <c>rdf:Alt</c> child.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Authored from ISO 19005-2:2011 §6.6.2.3.1, XMP Specification Part 1 (2005) §8.2, and
/// the PDF/A-1 Extension Schema specification. Clean-room: no veraPDF profile text reproduced.
/// </para>
/// <para>
/// ALL per-document state (declared property map, reported set) is local to
/// <see cref="Evaluate"/>: rule instances are shared singletons and must carry no mutable
/// instance state (thread safety, re-entrancy).
/// </para>
/// </remarks>
internal sealed class PropertyValueTypeRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.6.2.3.1-2";

    public string Clause => "ISO 19005-2:2011, 6.6.2.3.1";

    private static readonly XNamespace _rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace _ext = "http://www.aiim.org/pdfa/ns/extension/";
    private static readonly XNamespace _schema = "http://www.aiim.org/pdfa/ns/schema#";
    private static readonly XNamespace _property = "http://www.aiim.org/pdfa/ns/property#";

    // Clean-room predefined property type table. Authored from:
    //   XMP Specification Part 1 (Adobe, Sept 2005) §8 "Standard XMP Namespaces"
    //   PDF/A-1 Extension Schema specification (ISO 19005-1:2005)
    // FP-safety: only include entries whose expected type is unambiguously documented.
    // Unknown properties (not in this table) are skipped — no finding.
    private static readonly IReadOnlyDictionary<(string Ns, string Local), ValueTypeKind> _predefined =
        new Dictionary<(string, string), ValueTypeKind>(ValueTypeEqualityComparer.Instance)
        {
            // ── Dublin Core (XMP Spec §8.4, sourced from Dublin Core Metadata Element Set 1.1) ──
            // dc:title, dc:description, dc:rights → Lang Alt (rdf:Alt of language alternatives)
            { ("http://purl.org/dc/elements/1.1/", "title"),       ValueTypeKind.LangAlt },
            { ("http://purl.org/dc/elements/1.1/", "description"), ValueTypeKind.LangAlt },
            { ("http://purl.org/dc/elements/1.1/", "rights"),      ValueTypeKind.LangAlt },
            // dc:creator, dc:date → ordered array (rdf:Seq)
            { ("http://purl.org/dc/elements/1.1/", "creator"),     ValueTypeKind.ContainerSeq },
            { ("http://purl.org/dc/elements/1.1/", "date"),        ValueTypeKind.ContainerSeq },
            // dc:subject, dc:publisher, dc:contributor, dc:language, dc:type,
            // dc:relation → unordered bag (rdf:Bag)
            { ("http://purl.org/dc/elements/1.1/", "subject"),     ValueTypeKind.ContainerBag },
            { ("http://purl.org/dc/elements/1.1/", "publisher"),   ValueTypeKind.ContainerBag },
            { ("http://purl.org/dc/elements/1.1/", "contributor"), ValueTypeKind.ContainerBag },
            { ("http://purl.org/dc/elements/1.1/", "language"),    ValueTypeKind.ContainerBag },
            { ("http://purl.org/dc/elements/1.1/", "type"),        ValueTypeKind.ContainerBag },
            { ("http://purl.org/dc/elements/1.1/", "relation"),    ValueTypeKind.ContainerBag },
            // dc:format, dc:identifier, dc:source, dc:coverage → simple Text
            { ("http://purl.org/dc/elements/1.1/", "format"),      ValueTypeKind.ScalarText },
            { ("http://purl.org/dc/elements/1.1/", "identifier"),  ValueTypeKind.ScalarText },
            { ("http://purl.org/dc/elements/1.1/", "source"),      ValueTypeKind.ScalarText },
            { ("http://purl.org/dc/elements/1.1/", "coverage"),    ValueTypeKind.ScalarText },

            // ── XMP Basic (XMP Spec §8.1) ────────────────────────────────────────────────────
            { ("http://ns.adobe.com/xap/1.0/", "CreateDate"),   ValueTypeKind.Date },
            { ("http://ns.adobe.com/xap/1.0/", "ModifyDate"),   ValueTypeKind.Date },
            { ("http://ns.adobe.com/xap/1.0/", "MetadataDate"), ValueTypeKind.Date },
            { ("http://ns.adobe.com/xap/1.0/", "CreatorTool"),  ValueTypeKind.ScalarText },
            { ("http://ns.adobe.com/xap/1.0/", "BaseURL"),      ValueTypeKind.ScalarText },
            { ("http://ns.adobe.com/xap/1.0/", "Nickname"),     ValueTypeKind.ScalarText },
            { ("http://ns.adobe.com/xap/1.0/", "Label"),        ValueTypeKind.ScalarText },
            { ("http://ns.adobe.com/xap/1.0/", "Rating"),       ValueTypeKind.Integer },
            // xmp:Identifier → unordered bag of Text
            { ("http://ns.adobe.com/xap/1.0/", "Identifier"),   ValueTypeKind.ContainerBag },

            // ── Adobe PDF (XMP Spec §8.7) ────────────────────────────────────────────────────
            { ("http://ns.adobe.com/pdf/1.3/", "Producer"),   ValueTypeKind.ScalarText },
            { ("http://ns.adobe.com/pdf/1.3/", "Keywords"),   ValueTypeKind.ScalarText },
            { ("http://ns.adobe.com/pdf/1.3/", "PDFVersion"), ValueTypeKind.ScalarText },

            // ── PDF/A identification (ISO 19005-1:2005 Annex B) ─────────────────────────────
            // pdfaid:part → Integer; pdfaid:conformance, pdfaid:amd → Text
            { ("http://www.aiim.org/pdfa/ns/id/", "part"),        ValueTypeKind.Integer },
            { ("http://www.aiim.org/pdfa/ns/id/", "conformance"), ValueTypeKind.ScalarText },
            { ("http://www.aiim.org/pdfa/ns/id/", "amd"),         ValueTypeKind.ScalarText },
        };

    private static readonly PdfName _metadata = new("Metadata");

    // XMP date pattern per XMP Spec §1.2.7 / ISO 8601.
    // YYYY[-MM[-DD[Thh:mm[:ss[.s…][Z|±hh:mm]]]]]
    // We accept any non-empty string matching this prefix grammar without semantic validation
    // (e.g. month 13 is accepted) — veraPDF does the same (probe-verified).
    private static readonly Regex _dateRegex = new(
        @"^\d{4}(-\d{2}(-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?)?)?)?)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // XMP integer per XMP Spec §8.2.1.1: optional sign followed by one or more digits.
    private static readonly Regex _integerRegex = new(
        @"^[+-]?\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // XMP real number per XMP Spec §8.2.1.2 (also accepts integers).
    private static readonly Regex _realRegex = new(
        @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Scalar XMP primitive type names (from the XMP Specification and ISO 19005)
    /// whose value type check is: the property element must NOT contain an rdf:Bag/Seq/Alt
    /// container, and must match the type's value grammar (for constrained types).
    /// </summary>
    private enum ValueTypeKind
    {
        Unknown,          // type name not recognised — deferred
        ScalarText,       // any string: Text, URI, URL, MIMEType, XPath, AgentName, ProperName, RenditionClass, Locale, Rational
        Integer,          // [+-]?\d+
        Real,             // decimal number
        Boolean,          // exactly "True" or "False"
        Date,             // XMP date (ISO 8601 subset)
        ContainerBag,     // bag <Item>  → rdf:Bag child
        ContainerSeq,     // seq <Item>  → rdf:Seq child
        ContainerAlt,     // alt <Item>  → rdf:Alt child
        LangAlt,          // Lang Alt    → rdf:Alt child
    }

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

        // Build the combined type map: extension-schema declared properties (from this document)
        // merged with the predefined XMP-Specification table. Extension-schema declarations take
        // precedence; predefined entries fill in the rest.
        var combined = CollectDeclaredPropertyTypes(doc);
        foreach (var (key, kind) in _predefined)
            combined.TryAdd(key, kind);

        // Walk every element in the document and check any whose (ns, name) is
        // in the combined map. Use a local per-call reported set for dedup
        // (singleton rule — NO instance state).
        var reported = new HashSet<(string, string)>(ValueTypeEqualityComparer.Instance);

        foreach (var element in doc.Descendants())
        {
            var ns = element.Name.NamespaceName;
            var local = element.Name.LocalName;

            if (string.IsNullOrEmpty(ns))
                continue;

            var key = (ns, local);
            if (!combined.TryGetValue(key, out var kind))
                continue;

            // Deduplicate: only report the first occurrence of each property.
            if (!reported.Add(key))
                continue;

            CheckProperty(context, element, kind);
        }
    }

    // ── Validation helpers ───────────────────────────────────────────────────

    private void CheckProperty(PreflightContext context, XElement element, ValueTypeKind kind)
    {
        var ns = element.Name.NamespaceName;
        var local = element.Name.LocalName;

        switch (kind)
        {
            case ValueTypeKind.ScalarText:
                // A scalar property must not carry an rdf:Bag/Seq/Alt child.
                if (HasContainerChild(element))
                    Report(context, ns, local, "Text or scalar URI/URL type", "the property value is serialised as an RDF container (Bag/Seq/Alt) but the declared type is a scalar");
                break;

            case ValueTypeKind.Integer:
                if (HasContainerChild(element))
                {
                    Report(context, ns, local, "Integer", "the property value is serialised as an RDF container but the declared type is Integer");
                    break;
                }
                var intVal = element.Value.Trim();
                if (!_integerRegex.IsMatch(intVal))
                    Report(context, ns, local, "Integer", $"the value {Quote(intVal)} does not conform to the XMP Integer type ([+-]?[0-9]+)");
                break;

            case ValueTypeKind.Real:
                if (HasContainerChild(element))
                {
                    Report(context, ns, local, "Real", "the property value is serialised as an RDF container but the declared type is Real");
                    break;
                }
                var realVal = element.Value.Trim();
                if (!_realRegex.IsMatch(realVal))
                    Report(context, ns, local, "Real", $"the value {Quote(realVal)} does not conform to the XMP Real type");
                break;

            case ValueTypeKind.Boolean:
                if (HasContainerChild(element))
                {
                    Report(context, ns, local, "Boolean", "the property value is serialised as an RDF container but the declared type is Boolean");
                    break;
                }
                var boolVal = element.Value.Trim();
                // XMP Spec §8.2.1.3: the only valid values are "True" and "False" (case-sensitive).
                if (boolVal != "True" && boolVal != "False")
                    Report(context, ns, local, "Boolean", $"the value {Quote(boolVal)} is not a valid XMP Boolean (must be exactly 'True' or 'False')");
                break;

            case ValueTypeKind.Date:
                if (HasContainerChild(element))
                {
                    Report(context, ns, local, "Date", "the property value is serialised as an RDF container but the declared type is Date");
                    break;
                }
                var dateVal = element.Value.Trim();
                if (!_dateRegex.IsMatch(dateVal))
                    Report(context, ns, local, "Date", $"the value {Quote(dateVal)} does not conform to the XMP Date type (ISO 8601 subset)");
                break;

            case ValueTypeKind.ContainerBag:
                if (!HasSpecificContainerChild(element, "Bag"))
                    Report(context, ns, local, "bag", "the property value must be an rdf:Bag container but is not");
                break;

            case ValueTypeKind.ContainerSeq:
                if (!HasSpecificContainerChild(element, "Seq"))
                    Report(context, ns, local, "seq", "the property value must be an rdf:Seq container but is not");
                break;

            case ValueTypeKind.ContainerAlt:
                if (!HasSpecificContainerChild(element, "Alt"))
                    Report(context, ns, local, "alt", "the property value must be an rdf:Alt container but is not");
                break;

            case ValueTypeKind.LangAlt:
                if (!HasSpecificContainerChild(element, "Alt"))
                    Report(context, ns, local, "Lang Alt", "the property value must be an rdf:Alt language-alternative container but is not");
                break;

            case ValueTypeKind.Unknown:
                // Deferred — do not flag.
                break;
        }
    }

    private void Report(PreflightContext context, string ns, string local, string typeName, string detail)
        => context.Report(
            RuleId,
            Clause,
            PreflightSeverity.Error,
            $"XMP property {{{ns}}}{local} does not correspond to type {typeName}: {detail} (§6.6.2.3.1).");

    // True when the element has at least one rdf:Bag, rdf:Seq, or rdf:Alt child.
    private static bool HasContainerChild(XElement element)
        => element.Element(_rdf + "Bag") is not null
        || element.Element(_rdf + "Seq") is not null
        || element.Element(_rdf + "Alt") is not null;

    // True when the element has an rdf:<containerKind> child (e.g. containerKind="Bag").
    private static bool HasSpecificContainerChild(XElement element, string containerKind)
        => element.Element(_rdf + containerKind) is not null;

    private static string Quote(string s)
        => s.Length > 40 ? $"'{s[..40]}…'" : $"'{s}'";

    // ── Extension-schema harvest ─────────────────────────────────────────────

    /// <summary>
    /// Harvests the declared extension-schema properties and their valueType kinds from the XMP
    /// document. Returns a map from (namespaceURI, localName) to <see cref="ValueTypeKind"/>.
    /// Only recognised type names contribute an entry; unknown types are omitted.
    /// </summary>
    private static Dictionary<(string Ns, string Local), ValueTypeKind> CollectDeclaredPropertyTypes(
        System.Xml.Linq.XDocument doc)
    {
        var result = new Dictionary<(string, string), ValueTypeKind>(ValueTypeEqualityComparer.Instance);

        // Walk every pdfaExtension:schemas bag, descend into each schema,
        // harvest namespaceURI and the property declarations.
        foreach (var schemas in doc.Descendants(_ext + "schemas"))
        {
            foreach (var schemaItem in Items(schemas))
            {
                var nsUri = FieldText(schemaItem, _schema, "namespaceURI");
                if (string.IsNullOrEmpty(nsUri))
                    continue;

                var propertyContainer = schemaItem.Element(_schema + "property");
                if (propertyContainer is null)
                    continue;

                foreach (var prop in Items(propertyContainer))
                {
                    var propName = FieldText(prop, _property, "name");
                    var valueTypeName = FieldText(prop, _property, "valueType");
                    if (string.IsNullOrEmpty(propName) || string.IsNullOrEmpty(valueTypeName))
                        continue;

                    var kind = ClassifyType(valueTypeName);
                    if (kind == ValueTypeKind.Unknown)
                        continue; // deferred — unknown types not checked

                    var key = (nsUri, propName);
                    // First declaration wins when the same property is declared twice.
                    result.TryAdd(key, kind);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Maps the string value of a <c>pdfaProperty:valueType</c> to its
    /// <see cref="ValueTypeKind"/>. Returns <see cref="ValueTypeKind.Unknown"/> for any
    /// name not recognised — those properties are deferred and never trigger a finding.
    /// </summary>
    private static ValueTypeKind ClassifyType(string typeName)
    {
        // Exact XMP Spec type names (case-sensitive, as the extension-schema specification
        // prescribes). Verified against veraPDF 1.30.2 (empirical probing):
        //   - "Text", "Integer", "Real", "Boolean", "Date" trigger their respective checks
        //   - "bag X", "seq X", "alt X" trigger container checks
        //   - "Lang Alt" triggers alt-container check
        //   - "text" (lowercase) is accepted by veraPDF as equivalent to "Text"
        //     (probe-verified: no finding when declared as "text" with text value) — we
        //     therefore also accept lowercase for these aliases.

        return typeName switch
        {
            // Simple scalar types — accept any string value, no container allowed.
            "Text" or "text" => ValueTypeKind.ScalarText,
            "URI" or "URL" => ValueTypeKind.ScalarText,
            "MIMEType" or "XPath" or "Rational" or "Locale" => ValueTypeKind.ScalarText,
            "AgentName" or "ProperName" or "RenditionClass" => ValueTypeKind.ScalarText,

            // Constrained scalar types — value must match a grammar.
            "Integer" => ValueTypeKind.Integer,
            "Real" => ValueTypeKind.Real,
            "Boolean" => ValueTypeKind.Boolean,
            "Date" => ValueTypeKind.Date,

            // Container types — "bag X", "seq X", "alt X".
            _ when typeName.StartsWith("bag ", StringComparison.Ordinal) => ValueTypeKind.ContainerBag,
            _ when typeName.StartsWith("seq ", StringComparison.Ordinal) => ValueTypeKind.ContainerSeq,
            _ when typeName.StartsWith("alt ", StringComparison.Ordinal) => ValueTypeKind.ContainerAlt,
            "Lang Alt" => ValueTypeKind.LangAlt,

            // All other names (custom value types, XMP structured types such as ResourceRef,
            // Font, Colorant, Dimensions, etc.) are deferred — not checked.
            _ => ValueTypeKind.Unknown,
        };
    }

    // ── XMP structure helpers ────────────────────────────────────────────────

    // The rdf:li items of an rdf:Bag or rdf:Seq container (or the rdf:li children of
    // a raw rdf:Bag/rdf:Seq). Unwraps the blank-node (rdf:Description) serialisation.
    private static IEnumerable<XElement> Items(XElement container)
    {
        var collection = container.Element(_rdf + "Bag") ?? container.Element(_rdf + "Seq");
        var source = collection ?? container;
        foreach (var li in source.Elements(_rdf + "li"))
            yield return Unwrap(li);
    }

    // Descends into a lone rdf:Description child (blank-node form); returns item unchanged
    // when it carries multiple child elements (fields-direct / parseType="Resource" form).
    private static XElement Unwrap(XElement item)
    {
        XElement? only = null;
        foreach (var child in item.Elements())
        {
            if (only is not null)
                return item;
            only = child;
        }
        return only is not null && only.Name == _rdf + "Description" ? only : item;
    }

    // Read a field value from the element (child element or attribute form).
    private static string? FieldText(XElement parent, XNamespace ns, string local)
    {
        var element = parent.Element(ns + local);
        if (element is not null)
            return element.Value.Trim();
        var attribute = parent.Attribute(ns + local);
        return attribute?.Value.Trim();
    }

    // ── Equality comparer ────────────────────────────────────────────────────

    private sealed class ValueTypeEqualityComparer : IEqualityComparer<(string Ns, string Local)>
    {
        public static readonly ValueTypeEqualityComparer Instance = new();

        public bool Equals((string Ns, string Local) x, (string Ns, string Local) y)
            => string.Equals(x.Ns, y.Ns, StringComparison.Ordinal)
            && string.Equals(x.Local, y.Local, StringComparison.Ordinal);

        public int GetHashCode((string Ns, string Local) obj)
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Ns),
                StringComparer.Ordinal.GetHashCode(obj.Local));
    }
}
