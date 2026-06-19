// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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
            var text = Encoding.UTF8.GetString(bytes);

            // Strip a leading byte-order mark, if the producer emitted one as a real character.
            if (text.Length > 0 && text[0] == '﻿')
                text = text[1..];

            // The XMP packet is wrapped in <?xpacket?> processing instructions; XDocument treats
            // them as prolog/epilog and parses the contained RDF. Ignore comments and whitespace.
            var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
            using var reader = XmlReader.Create(new StringReader(text), settings);
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
}
