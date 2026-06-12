// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace VellumPdf.Document;

/// <summary>
/// Builds a valid XMP metadata packet (ISO 16684-1 / XMP Spec Part 1).
///
/// <para>
/// The generated packet uses UTF-8, the standard RDF/XML serialisation, and is
/// wrapped in <c>&lt;?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?&gt;</c> /
/// <c>&lt;?xpacket end="r"?&gt;</c> as required by the XMP specification.
/// </para>
///
/// <para>
/// Namespaces emitted: <c>dc</c> (Dublin Core), <c>xmp</c> (XMP basic),
/// <c>pdf</c> (PDF schema), and — when a conformance level is specified —
/// <c>pdfaid</c> (PDF/A identification schema per ISO 19005-2).
/// </para>
/// </summary>
internal static class XmpMetadataWriter
{
    private const string XmpDateFormat = "yyyy-MM-ddTHH:mm:sszzz";

    /// <summary>
    /// Generates the XMP packet bytes (UTF-8, no BOM despite the <c>begin</c> marker
    /// which carries the BOM codepoint U+FEFF as a UTF-8 sequence so that byte-order
    /// can be detected).
    /// </summary>
    internal static byte[] BuildPacket(
        PdfDocumentInfo info,
        PdfConformance conformance,
        DateTimeOffset timestamp,
        string? language = null)
    {
        var sb = new StringBuilder(1024);

        // XMP packet header — the begin attribute contains U+FEFF encoded as UTF-8 (\xEF\xBB\xBF).
        sb.Append("<?xpacket begin=\"\xEF\xBB\xBF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("  <rdf:Description rdf:about=\"\"\n");
        sb.Append("    xmlns:dc=\"http://purl.org/dc/elements/1.1/\"\n");
        sb.Append("    xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"\n");
        sb.Append("    xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\"");

        if (conformance == PdfConformance.PdfUA1)
            sb.Append("\n    xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"");
        else if (conformance != PdfConformance.None)
            sb.Append("\n    xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"");

        sb.Append(">\n");

        // dc:title
        if (!string.IsNullOrEmpty(info.Title))
        {
            sb.Append("    <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">");
            sb.Append(XmlEscape(info.Title));
            sb.Append("</rdf:li></rdf:Alt></dc:title>\n");
        }

        // dc:creator
        if (!string.IsNullOrEmpty(info.Author))
        {
            sb.Append("    <dc:creator><rdf:Seq><rdf:li>");
            sb.Append(XmlEscape(info.Author));
            sb.Append("</rdf:li></rdf:Seq></dc:creator>\n");
        }

        // dc:description (Subject)
        if (!string.IsNullOrEmpty(info.Subject))
        {
            sb.Append("    <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">");
            sb.Append(XmlEscape(info.Subject));
            sb.Append("</rdf:li></rdf:Alt></dc:description>\n");
        }

        // dc:language
        var trimmedLanguage = language?.Trim();
        if (!string.IsNullOrEmpty(trimmedLanguage))
        {
            sb.Append("    <dc:language><rdf:Bag><rdf:li>");
            sb.Append(XmlEscape(trimmedLanguage));
            sb.Append("</rdf:li></rdf:Bag></dc:language>\n");
        }

        // xmp:CreatorTool
        if (!string.IsNullOrEmpty(info.Creator))
        {
            sb.Append("    <xmp:CreatorTool>");
            sb.Append(XmlEscape(info.Creator));
            sb.Append("</xmp:CreatorTool>\n");
        }

        // xmp:CreateDate and xmp:ModifyDate
        var dateStr = timestamp.ToString(XmpDateFormat, CultureInfo.InvariantCulture);
        sb.Append("    <xmp:CreateDate>");
        sb.Append(dateStr);
        sb.Append("</xmp:CreateDate>\n");
        sb.Append("    <xmp:ModifyDate>");
        sb.Append(dateStr);
        sb.Append("</xmp:ModifyDate>\n");

        // pdf:Producer
        var producer = info.Producer ?? "VellumPdf";
        sb.Append("    <pdf:Producer>");
        sb.Append(XmlEscape(producer));
        sb.Append("</pdf:Producer>\n");

        // Conformance identification schema — pdfuaid for PDF/UA-1, pdfaid for PDF/A levels.
        if (conformance == PdfConformance.PdfUA1)
        {
            sb.Append("    <pdfuaid:part>1</pdfuaid:part>\n");
        }
        else if (conformance != PdfConformance.None)
        {
            var (part, conf) = conformance switch
            {
                PdfConformance.PdfA2b => ("2", "B"),
                PdfConformance.PdfA2u => ("2", "U"),
                PdfConformance.PdfA2a => ("2", "A"),
                _ => ("2", "B"),
            };
            sb.Append("    <pdfaid:part>");
            sb.Append(part);
            sb.Append("</pdfaid:part>\n");
            sb.Append("    <pdfaid:conformance>");
            sb.Append(conf);
            sb.Append("</pdfaid:conformance>\n");
        }

        sb.Append("  </rdf:Description>\n");
        sb.Append("</rdf:RDF>\n");
        sb.Append("</x:xmpmeta>\n");

        // XMP packet trailer — "r" = read-only (no in-place editing).
        sb.Append("<?xpacket end=\"r\"?>");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string XmlEscape(string s)
    {
        // Strip XML 1.0 illegal control characters (U+0000–0008, U+000B, U+000C, U+000E–001F).
        // Keep \t (U+0009), \n (U+000A), \r (U+000D) which are valid XML.
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                continue; // drop XML-illegal control char
            sb.Append(c);
        }
        // Standard XML entity escaping.
        return sb.ToString()
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
