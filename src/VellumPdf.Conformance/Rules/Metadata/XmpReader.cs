// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Xml;
using System.Xml.Linq;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// Reads the few XMP properties the conformance rules need. The packet is parsed as XML and
/// properties are resolved by <em>namespace URI + local name</em>, so a producer that binds the
/// PDF/A or PDF/UA identification schema to a non-default prefix (e.g. <c>aid:part</c> instead of
/// <c>pdfaid:part</c>) is still matched — exactly as veraPDF does. Element-content and attribute
/// serialisations are both handled, and a value wrapped in an <c>rdf:Alt</c>/<c>rdf:li</c> language
/// alternative (as <c>dc:title</c> always is) is read through to its text.
/// </summary>
internal static class XmpReader
{
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public static readonly XNamespace Pdfaid = "http://www.aiim.org/pdfa/ns/id/";
    public static readonly XNamespace Pdfuaid = "http://www.aiim.org/pdfua/ns/id/";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    /// <summary>
    /// Parses the XMP packet bytes into an <see cref="XDocument"/>, or returns <see langword="null"/>
    /// when the metadata is not well-formed XML.
    /// </summary>
    public static XDocument? Parse(byte[] bytes)
    {
        try
        {
            // Parse from the raw bytes so the XML reader honours the byte-order mark / encoding
            // declaration. XMP packets may be serialised as UTF-8, UTF-16, or UTF-32 (ISO 16684-1);
            // decoding as UTF-8 up front would corrupt the latter two. The <?xpacket?> processing
            // instructions are treated as prolog/epilog; comments and whitespace are ignored.
            // The XMP packet is untrusted input, so disable DTD processing and external entity
            // resolution outright (defence in depth against XXE / entity-expansion). Modern .NET
            // defaults to Prohibit, but set it explicitly so the posture cannot silently change.
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the trimmed value of the property identified by <paramref name="ns"/> +
    /// <paramref name="localName"/> — whether serialised as an element or as an attribute on any
    /// <c>rdf:Description</c> — or <see langword="null"/> when the property is absent.
    /// </summary>
    public static string? Get(XDocument doc, XNamespace ns, string localName)
    {
        var name = ns + localName;

        var element = doc.Descendants(name).FirstOrDefault();
        if (element is not null)
            return element.Value.Trim();

        foreach (var node in doc.Descendants())
        {
            var attribute = node.Attribute(name);
            if (attribute is not null)
                return attribute.Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the XMP document contains at least one
    /// <c>rdf:Alt</c> language-alternative array that carries an <c>x-default</c> item
    /// (<c>&lt;rdf:li xml:lang="x-default"&gt;</c>). Confirmed by veraPDF 1.30.2 probe:
    /// any lang-alt with an x-default item in the catalog XMP triggers §7.2 testNumber 33
    /// when the document catalog has no <c>/Lang</c> — not just <c>dc:title</c>.
    /// </summary>
    public static bool HasXDefaultLangAlt(XDocument doc)
    {
        var xmlLang = XNamespace.Xml + "lang";
        foreach (var alt in doc.Descendants(Rdf + "Alt"))
        {
            foreach (var li in alt.Elements(Rdf + "li"))
            {
                var lang = (string?)li.Attribute(xmlLang);
                if (string.Equals(lang, "x-default", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }
}
