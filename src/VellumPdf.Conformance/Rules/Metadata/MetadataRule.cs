// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// ISO 19005-2 §6.6.2.1 (XMP packet serialisation). Every XMP metadata packet in the document — the
/// catalog <c>/Metadata</c>, each page's <c>/Metadata</c>, each interactive-form field's
/// <c>/Metadata</c>, and each embedded-file stream's <c>/Metadata</c> — shall not declare a
/// <c>bytes</c> or <c>encoding</c> pseudo-attribute in its <c>&lt;?xpacket?&gt;</c> header, and shall
/// be serialised as UTF-8.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.6.2.1 and ISO 16684-1 (XMP). Clean-room: derived from the
/// specification text, not from any third-party validation profile. Each packet is parsed by
/// <see cref="XmpPacket"/>. The well-formedness requirement of §6.6.2.1 is enforced by
/// <see cref="XmpConformanceRule"/> (which must parse the packet to read the PDF/A identification),
/// so it is not duplicated here. The extension-schema requirements (§6.6.2.3) need RDF structure
/// parsing and live in <see cref="ExtensionSchemaRule"/>.
/// <para>
/// §6.6.2.1-4 requires the checks to cover every metadata stream, not just the document
/// <c>/Metadata</c>. Metadata streams are located via the catalog, the page tree, the AcroForm field
/// tree (recursively, including the <c>/Kids</c> of terminal fields), and the embedded-file name tree
/// / annotation file attachments. Each stream is decoded and checked at most once (deduplicated by
/// object number) so a stream shared across objects is not reported twice.
/// </para>
/// </remarks>
internal sealed class MetadataRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.6.2.1-xmp-packet";

    public string Clause => "ISO 19005-2:2011, 6.6.2.1";

    private static readonly PdfName _metadata = new("Metadata");
    private static readonly PdfName _acroForm = new("AcroForm");
    private static readonly PdfName _fields = new("Fields");
    private static readonly PdfName _names = new("Names");
    private static readonly PdfName _embeddedFiles = new("EmbeddedFiles");
    private static readonly PdfName _ef = new("EF");
    private static readonly PdfName _f = new("F");
    private static readonly PdfName _uf = new("UF");
    private static readonly PdfName _fs = new("FS");

    // Guards the AcroForm field tree and embedded-file name tree traversals against cycles.
    private const int MaxDepth = 64;

    public void Evaluate(PreflightContext context)
    {
        // Deduplicate by the metadata stream's object number so a stream referenced from several
        // objects is decoded and reported once.
        var reported = new HashSet<int>();

        // Catalog /Metadata. A missing catalog packet is reported by XmpConformanceRule.
        CheckMetadataOf(context, context.Catalog, reported);

        // Page /Metadata.
        foreach (var page in context.EnumeratePages())
            CheckMetadataOf(context, page, reported);

        // AcroForm interactive-form field /Metadata (walk the field tree, including terminal-field kids).
        if (context.Resolve(context.Catalog.Get(_acroForm)) is PdfDictionary acroForm)
            WalkFieldTree(context, acroForm.Get(_fields), reported, 0);

        // Embedded-file stream /Metadata (name tree under /Names /EmbeddedFiles, plus annotation
        // file attachments — both reach an embedded-file stream through a filespec /EF /F or /UF).
        CheckEmbeddedFileMetadata(context, reported);
    }

    // Runs the §6.6.2.1 packet checks on the /Metadata stream of <paramref name="owner"/>, if present.
    private void CheckMetadataOf(PreflightContext context, PdfDictionary owner, HashSet<int> reported)
    {
        var metaObj = owner.Get(_metadata);
        if (metaObj is PdfIndirectReference r && !reported.Add(r.ObjectNumber))
            return; // already checked this stream via another owner
        CheckPacket(context, context.ResolveStream(metaObj));
    }

    // Parses and validates a single XMP metadata stream against §6.6.2.1-2/-3/-5.
    private void CheckPacket(PreflightContext context, ParsedStream? stream)
    {
        if (stream is null)
            return;

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

    // Recursively walks an AcroForm field array, checking each field's /Metadata. Terminal fields may
    // carry /Kids (their widget annotations or child fields), which are followed too.
    private void WalkFieldTree(PreflightContext context, PdfObject? fieldsObj, HashSet<int> reported, int depth)
    {
        if (depth > MaxDepth)
            return;
        if (context.Resolve(fieldsObj) is not PdfArray fields)
            return;

        for (var i = 0; i < fields.Count; i++)
        {
            if (context.Resolve(fields[i]) is not PdfDictionary field)
                continue;
            CheckMetadataOf(context, field, reported);
            WalkFieldTree(context, field.Get(PdfName.Kids), reported, depth + 1);
        }
    }

    // Locates embedded-file streams (via the /Names /EmbeddedFiles name tree and via annotation
    // file-attachment filespecs) and checks each stream's /Metadata.
    private void CheckEmbeddedFileMetadata(PreflightContext context, HashSet<int> reported)
    {
        // /Names /EmbeddedFiles name tree.
        if (context.Resolve(context.Catalog.Get(_names)) is PdfDictionary names)
            WalkNameTree(context, names.Get(_embeddedFiles), reported, 0);

        // Annotation file attachments: a /FileAttachment annotation carries an /FS filespec.
        foreach (var annot in context.EnumerateAnnotations())
            if (context.Resolve(annot.Get(_fs)) is PdfDictionary filespec)
                CheckFilespec(context, filespec, reported);
    }

    // Walks a name tree whose values are filespec dictionaries (or references to them), checking the
    // /Metadata of each embedded-file stream reached through the filespec's /EF.
    private void WalkNameTree(PreflightContext context, PdfObject? nodeObj, HashSet<int> reported, int depth)
    {
        if (depth > MaxDepth)
            return;
        if (context.Resolve(nodeObj) is not PdfDictionary node)
            return;

        if (context.Resolve(node.Get(_names)) is PdfArray pairs)
        {
            // The array alternates name, value, name, value, …; the values are filespecs.
            for (var i = 1; i < pairs.Count; i += 2)
                if (context.Resolve(pairs[i]) is PdfDictionary filespec)
                    CheckFilespec(context, filespec, reported);
        }

        if (context.Resolve(node.Get(PdfName.Kids)) is PdfArray kids)
            for (var i = 0; i < kids.Count; i++)
                WalkNameTree(context, kids[i], reported, depth + 1);
    }

    // Checks the /Metadata of the embedded-file streams referenced by a filespec's /EF /F and /UF.
    private void CheckFilespec(PreflightContext context, PdfDictionary filespec, HashSet<int> reported)
    {
        if (context.Resolve(filespec.Get(_ef)) is not PdfDictionary ef)
            return;
        CheckEmbeddedFileStreamMetadata(context, ef.Get(_f), reported);
        CheckEmbeddedFileStreamMetadata(context, ef.Get(_uf), reported);
    }

    // The embedded-file stream itself is a stream whose dictionary may carry a /Metadata entry.
    private void CheckEmbeddedFileStreamMetadata(PreflightContext context, PdfObject? streamRef, HashSet<int> reported)
    {
        if (context.ResolveStream(streamRef) is not ParsedStream stream)
            return;
        CheckMetadataOf(context, stream.Dictionary, reported);
    }
}
