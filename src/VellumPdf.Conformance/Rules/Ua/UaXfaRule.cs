// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Xml;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.15 (AcroForm — dynamic XFA). A conforming PDF/UA-1 document shall not use
/// dynamic XFA forms. The <c>/AcroForm /XFA</c> stream is parsed: if the XFA configuration
/// element contains a <c>dynamicRender</c> child element whose text content is
/// <c>"required"</c>, the form is dynamic and the document is non-conformant.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.15 (PDAcroForm predicate:
/// <c>dynamicRender != 'required'</c>) and empirically validated against veraPDF 1.30.2
/// (clause 7.15, testNumber 1). Clean-room: derived from the specification text and the veraPDF
/// profile, not from any third-party implementation.
/// <para>
/// In the XFA packet, the <c>dynamicRender</c> setting lives inside the XCI (XML Configuration
/// Interface) <c>config</c> element under the <c>acrobat</c> or <c>acrobat7</c> sub-element.
/// The veraPDF model class (<c>GFPDAcroForm.getdynamicRender</c>) resolves the value by walking
/// the <c>xdp:xdp</c> element's children looking for an element named <c>acrobat</c>, then
/// inside it <c>acrobat7</c>, and finally the <c>dynamicRender</c> text node. This rule
/// replicates that traversal. When the <c>dynamicRender</c> element is absent, or when the XFA
/// stream cannot be parsed, the predicate is vacuously satisfied (no violation is reported).
/// </para>
/// <para>
/// Note: PDF/A-2 (<c>XfaRule</c>) forbids ANY <c>/XFA</c> entry; PDF/UA-1 is narrower —
/// only <em>dynamic</em> XFA is forbidden. This rule fires only when <c>dynamicRender</c> is
/// the string <c>"required"</c>.
/// </para>
/// </remarks>
internal sealed class UaXfaRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.15-1";

    public string Clause => "ISO 14289-1:2014, 7.15";

    private static readonly PdfName _acroForm = new("AcroForm");
    private static readonly PdfName _xfa = new("XFA");

    public void Evaluate(PreflightContext context)
    {
        if (context.Resolve(context.Catalog.Get(_acroForm)) is not PdfDictionary acroForm)
            return;

        var xfaObj = acroForm.Get(_xfa);
        if (xfaObj is null)
            return; // No XFA entry — no violation.

        // /XFA may be a single stream or an array of [name stream ...] pairs.
        // Collect all stream bytes; each segment may contain the config element.
        var xfaBytes = CollectXfaBytes(context, xfaObj);
        if (xfaBytes.Count == 0)
            return;

        foreach (var segment in xfaBytes)
        {
            if (IsDynamicRenderRequired(segment))
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "The AcroForm /XFA stream declares dynamic rendering (dynamicRender = \"required\"). "
                    + "PDF/UA-1 forbids dynamic XFA forms (ISO 14289-1:2014, 7.15).");
                return; // One report suffices.
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the XFA/XCI XML <paramref name="xmlBytes"/> contains
    /// a <c>dynamicRender</c> element with the text content <c>"required"</c>. The element is
    /// found under any path rooted at the document element, matching veraPDF's traversal of
    /// <c>xdp:xdp &gt; [config] &gt; acrobat &gt; acrobat7 &gt; dynamicRender</c>.
    /// </summary>
    private static bool IsDynamicRenderRequired(byte[] xmlBytes)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var ms = new System.IO.MemoryStream(xmlBytes);
            using var reader = XmlReader.Create(ms, settings);
            // Walk the document looking for the dynamicRender element via depth-first.
            // We need: root > ... > acrobat > acrobat7 > dynamicRender = "required"
            // The config element may appear at any depth. We walk the stream linearly and
            // track whether we are inside acrobat7 (matching veraPDF's field traversal logic).
            // This is deliberately simple: we scan for <dynamicRender> inside any <acrobat7>
            // element that is a descendant of <acrobat>.
            return ScanXml(reader);
        }
        catch
        {
            // Any XML parse failure → treat as no dynamicRender = "required" (no violation).
            return false;
        }
    }

    private static bool ScanXml(XmlReader reader)
    {
        // State machine: track nesting into <acrobat> and <acrobat7>.
        var inAcrobat = 0;
        var inAcrobat7 = 0;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var localName = reader.LocalName;
                if (localName == "acrobat") inAcrobat++;
                else if (localName == "acrobat7" && inAcrobat > 0) inAcrobat7++;
                else if (localName == "dynamicRender" && inAcrobat7 > 0)
                {
                    var text = reader.ReadElementContentAsString();
                    if (string.Equals(text, "required", StringComparison.OrdinalIgnoreCase))
                        return true;
                    // ReadElementContentAsString already advanced; continue.
                    continue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                var localName = reader.LocalName;
                if (localName == "acrobat7" && inAcrobat7 > 0) inAcrobat7--;
                else if (localName == "acrobat" && inAcrobat > 0) inAcrobat--;
            }
        }
        return false;
    }

    // Gathers the raw bytes of every XFA segment.  /XFA is either a single stream ref or
    // an array [name1 stream1 name2 stream2 …] (PDF 32000-1 Table 218).
    private static List<byte[]> CollectXfaBytes(PreflightContext context, PdfObject xfaObj)
    {
        var list = new List<byte[]>();

        if (context.Resolve(xfaObj) is PdfArray arr)
        {
            // Array form: [name stream name stream …], values at odd indices are streams.
            for (var i = 1; i < arr.Count; i += 2)
            {
                if (context.ResolveStream(arr[i]) is { } segment)
                {
                    var bytes = context.DecodeStream(segment);
                    if (bytes is not null)
                        list.Add(bytes);
                }
            }
        }
        else if (context.ResolveStream(xfaObj) is { } stream)
        {
            var bytes = context.DecodeStream(stream);
            if (bytes is not null)
                list.Add(bytes);
        }

        return list;
    }
}
