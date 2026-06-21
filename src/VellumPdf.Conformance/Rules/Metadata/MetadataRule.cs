// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// ISO 19005-2 §6.6.2.1 (XMP packet serialisation). The document's XMP metadata packet shall not
/// declare a <c>bytes</c> or <c>encoding</c> pseudo-attribute in its <c>&lt;?xpacket?&gt;</c> header,
/// and shall be serialised as UTF-8.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.6.2.1 and ISO 16684-1 (XMP). Clean-room: derived from the
/// specification text, not from any third-party validation profile. The packet is parsed by
/// <see cref="XmpPacket"/>. The well-formedness requirement of §6.6.2.1 is enforced by
/// <see cref="XmpConformanceRule"/> (which must parse the packet to read the PDF/A identification),
/// so it is not duplicated here. The extension-schema requirements (§6.6.2.3) need RDF structure
/// parsing and are deferred.
/// <para>
/// This slice validates the document <c>/Metadata</c> packet. Metadata streams attached to other
/// objects (pages, form fields, embedded files) are a later expansion.
/// </para>
/// </remarks>
internal sealed class MetadataRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.6.2.1-xmp-packet";

    public string Clause => "ISO 19005-2:2011, 6.6.2.1";

    private static readonly PdfName _metadata = new("Metadata");

    public void Evaluate(PreflightContext context)
    {
        var stream = context.ResolveStream(context.Catalog.Get(_metadata));
        if (stream is null)
            return; // A missing /Metadata stream is reported by XmpConformanceRule.

        var bytes = context.DecodeStream(stream);
        if (bytes is null)
            return;

        var packet = XmpPacket.Parse(bytes);

        // §6.6.2.1-2: the bytes pseudo-attribute shall not be used in the xpacket header.
        if (packet.HasBytesAttribute)
            context.Report(
                "ISO19005-2:6.6.2.1-xmp-bytes",
                Clause,
                PreflightSeverity.Error,
                "The XMP packet header contains a 'bytes' pseudo-attribute, which is not permitted in PDF/A-2.");

        // §6.6.2.1-3: the encoding pseudo-attribute shall not be used in the xpacket header.
        if (packet.HasEncodingAttribute)
            context.Report(
                "ISO19005-2:6.6.2.1-xmp-encoding",
                Clause,
                PreflightSeverity.Error,
                "The XMP packet header contains an 'encoding' pseudo-attribute, which is not permitted in PDF/A-2.");

        // §6.6.2.1-5: the XMP packet shall be serialised as UTF-8. (Only assert when the packet is
        // well-formed; an unparseable packet is already reported as non-conformant by
        // XmpConformanceRule, and its encoding cannot be determined reliably.)
        if (packet.IsWellFormed && !packet.IsUtf8)
            context.Report(
                "ISO19005-2:6.6.2.1-xmp-encoding-utf8",
                Clause,
                PreflightSeverity.Error,
                "The XMP metadata packet is not serialised as UTF-8, which is required in PDF/A-2.");
    }
}
