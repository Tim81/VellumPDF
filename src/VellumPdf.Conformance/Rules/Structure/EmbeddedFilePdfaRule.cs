// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.8 test 5 (Embedded file PDF/A conformance). A file specification dictionary that
/// carries an <c>/EF</c> (embedded-file) entry may be present only when the embedded file itself is
/// compliant with either ISO 19005-1 or ISO 19005-2.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.8 and the veraPDF 1.30.2 behaviour (test id <c>6.8-5</c>,
/// test expression <c>isValidPDFA12 == true</c>). Clean-room: derived from the specification text
/// and oracle observation, not from any third-party validation profile.
/// <para>
/// The rule applies to every embedded-file stream, not only to those whose content happens to begin
/// with the <c>%PDF-</c> header. veraPDF flags a plain text or image attachment with the same
/// error because such files cannot be valid PDF/A-1 or PDF/A-2 documents — confirmed by probing
/// (STEP 0) before implementing this rule.
/// </para>
/// <para>
/// <strong>Embedded PDF/A-2 validation (recursive):</strong> When the embedded file is itself a
/// well-formed PDF that declares <c>pdfaid:part == 2</c>, this rule opens the embedded bytes via a
/// fresh <see cref="VellumPdf.Reader.PdfReader"/> and calls <see cref="PdfPreflight.Validate(byte[], PdfConformance)"/> on
/// it recursively. If the inner document is non-compliant the outer document is flagged. The nesting
/// depth is capped at 2 to prevent an adversarially crafted self-referencing archive from recursing
/// infinitely. Any exception thrown while opening or validating the embedded document is caught and
/// treated as a non-PDF/A finding rather than propagating to the outer validator.
/// </para>
/// <para>
/// <strong>Parity caveat:</strong> this validator implements approximately 83 % of the veraPDF rule
/// set. A marginally-conformant embedded PDF/A-2 document could therefore produce divergent verdicts
/// (the in-process pass, veraPDF fail). For the oracle fixtures the embedded files are chosen to be
/// clear-cut (a fully-conformant writer-produced PDF/A-2b, or a plain non-PDF/A PDF) so both
/// validators agree. Do not introduce fixtures for marginal embedded files.
/// </para>
/// <para>
/// <strong>Embedded PDF/A-1 deferral:</strong> when the embedded PDF declares
/// <c>pdfaid:part == 1</c>, this rule does not flag it. The in-process validator does not implement
/// a PDF/A-1 profile; flagging part-1 claims without being able to validate them would risk false
/// positives. An outer PDF that embeds a genuinely non-conformant PDF/A-1 document will not be
/// caught by this check.
/// </para>
/// </remarks>
internal sealed class EmbeddedFilePdfaRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.8-5-embedded-pdfa";

    public string Clause => "ISO 19005-2:2011, 6.8";

    // Maximum nesting depth for recursive embedded-PDF validation. Once depth reaches this value
    // no further recursive calls are made (inner embedded files are silently accepted), so a PDF
    // that embeds itself (or an arbitrarily deep chain) cannot cause a StackOverflow.
    private const int MaxRecursionDepth = 2;

    private const int MaxDepth = 64;

    // Recursion depth across PdfPreflight.Validate re-entry. [ThreadStatic] rather than a method
    // parameter or instance field: each recursive PdfPreflight.Validate call builds a fresh rule
    // instance, so a parameter/field would reset to 0 and let a self-embedding archive recurse without
    // bound (an uncatchable StackOverflow). Validation is synchronous and single-threaded per call.
    [System.ThreadStatic]
    private static int _recursionDepth;

    private static readonly PdfName _ef = new("EF");
    private static readonly PdfName _names = new("Names");
    private static readonly PdfName _embeddedFiles = new("EmbeddedFiles");
    private static readonly PdfName _kids = new("Kids");
    private static readonly PdfName _fs = new("FS");
    private static readonly PdfName _filespec = new("Filespec");

    // The %PDF- file magic — only a stream starting with these bytes can be a PDF.
    private static readonly byte[] PdfMagic = [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'];

    public void Evaluate(PreflightContext context)
    {
        // Collect distinct file specification dictionaries using the same three-path strategy as
        // EmbeddedFileRule (§6.8-2): typed /Type /Filespec objects; name-tree values under
        // /Names /EmbeddedFiles; and /FS values of file-attachment annotations.
        var filespecs = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        foreach (var obj in context.EnumerateIndirectObjects())
            CollectTyped(obj, 0, filespecs, seen);

        if (context.Resolve(context.Catalog.Get(_names)) is PdfDictionary names
            && context.Resolve(names.Get(_embeddedFiles)) is PdfDictionary tree)
            CollectNameTree(context, tree, 0, filespecs, seen);

        foreach (var annot in context.EnumerateAnnotations())
            if (context.Resolve(annot.Get(_fs)) is PdfDictionary fs && seen.Add(fs))
                filespecs.Add(fs);

        // Track which EF stream object numbers we have already evaluated, to avoid re-checking a
        // stream that is shared across multiple file specifications (unusual but possible).
        var evaluatedStreams = new HashSet<int>();

        foreach (var filespec in filespecs)
        {
            if (context.Resolve(filespec.Get(_ef)) is not PdfDictionary ef)
                continue;

            // The /EF dictionary may carry /F, /UF, /DOS, /Mac, /Unix entries; each is a reference
            // to an embedded-file stream. §6.8-5 applies to any embedded file stream. Walk all
            // entries of the /EF dictionary and validate each distinct stream.
            foreach (var entry in ef.Entries)
            {
                if (entry.Value is not PdfIndirectReference streamRef)
                    continue;
                if (!evaluatedStreams.Add(streamRef.ObjectNumber))
                    continue; // Already checked this stream — skip duplicate reference.

                var stream = context.ResolveStream(entry.Value);
                if (stream is null)
                    continue; // Cannot read the stream — no finding (conservative).

                var bytes = context.DecodeStream(stream);
                if (bytes is null)
                    continue; // Decode failed (unsupported filter) — no finding (conservative).

                ValidateEmbeddedFileBytes(context, bytes);
            }
        }
    }

    /// <summary>
    /// Checks whether <paramref name="embeddedBytes"/> satisfies §6.8-5 and reports a finding if
    /// not. For a file that begins with the PDF magic bytes, pdfaid metadata is inspected; for any
    /// other file (text, image, …) a finding is reported immediately because such content cannot
    /// possibly be a valid PDF/A-1 or PDF/A-2 document.
    /// </summary>
    private void ValidateEmbeddedFileBytes(PreflightContext context, byte[] embeddedBytes)
    {
        // Any file that does not begin with %PDF- is not a PDF and therefore cannot be a valid
        // PDF/A-1 or PDF/A-2 document. Flag it immediately.
        if (!StartsWith(embeddedBytes, PdfMagic))
        {
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                "An embedded file is not a PDF document and therefore cannot be a valid PDF/A-1 or "
                + "PDF/A-2 document, as required by §6.8-5.");
            return;
        }

        // The embedded stream is a PDF. Open it to inspect pdfaid metadata.
        EmbeddedPdfAKind kind;
        PdfConformance? embeddedLevel;
        try
        {
            (kind, embeddedLevel) = ReadEmbeddedPdfAInfo(embeddedBytes);
        }
        catch
        {
            // The embedded PDF could not be opened (malformed, encrypted, unsupported). A malformed
            // file cannot be a valid PDF/A document — flag it.
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                "An embedded PDF file could not be opened and is therefore not a valid PDF/A document, "
                + "as required by §6.8-5.");
            return;
        }

        switch (kind)
        {
            case EmbeddedPdfAKind.NoPdfAId:
                // The embedded PDF carries no pdfaid identification at all — it cannot be a valid
                // PDF/A document.
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "An embedded PDF file carries no PDF/A identification (no pdfaid:part property) "
                    + "and is therefore not a valid PDF/A-1 or PDF/A-2 document, as required by §6.8-5.");
                return;

            case EmbeddedPdfAKind.OtherPart:
                // The embedded PDF claims a PDF/A part other than 1 or 2 (e.g. part 3/4) — not
                // a valid PDF/A-1 or PDF/A-2 document.
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "An embedded PDF file claims a PDF/A part other than 1 or 2 and is therefore "
                    + "not a valid PDF/A-1 or PDF/A-2 document, as required by §6.8-5.");
                return;

            case EmbeddedPdfAKind.Part1:
                // Embedded PDF/A-1 (part == 1): the in-process validator has no PDF/A-1 profile.
                // Flagging without being able to validate risks false positives, so we defer.
                // A genuine non-conformant PDF/A-1 attachment is not caught here.
                // (Documented limitation; ConformanceCatalog marks §6.8-5 as Partial.)
                return;

            case EmbeddedPdfAKind.Part2:
                // Embedded PDF/A-2 (part == 2): validate recursively unless too deep.
                break;
        }

        // Recursion depth guard: once already nested MaxRecursionDepth levels deep, accept the
        // embedded file without further validation. _recursionDepth is [ThreadStatic] so it survives
        // the PdfPreflight.Validate re-entry below — a self-embedding archive stops here rather than
        // recursing without bound.
        if (_recursionDepth >= MaxRecursionDepth)
            return;

        // Recursively validate the embedded PDF/A-2 document using the in-process validator.
        // Re-entrancy is safe because PdfPreflight.Validate(byte[], …) opens a new, independent
        // PdfDocumentReader and has no shared mutable state with the outer validation pass.
        PreflightResult innerResult;
        _recursionDepth++;
        try
        {
            innerResult = PdfPreflight.Validate(embeddedBytes, embeddedLevel!.Value);
        }
        catch
        {
            // If the recursive validation itself throws (malformed, unsupported feature), treat
            // the embedded file as non-compliant.
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                "An embedded PDF/A-2 document could not be validated recursively and is therefore "
                + "treated as non-conformant (§6.8-5).");
            return;
        }
        finally
        {
            _recursionDepth--;
        }

        if (!innerResult.IsCompliant)
        {
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                "An embedded PDF file claims to be PDF/A-2 but fails in-process PDF/A-2 "
                + "validation and is therefore not a valid PDF/A-2 document, as required by §6.8-5. "
                + "Note: the in-process validator covers approximately 83 % of the veraPDF rule set; "
                + "a marginally non-conformant embedded file may not be detected here.");
        }
    }

    /// <summary>
    /// Result of reading the embedded PDF's PDF/A identification from its XMP metadata.
    /// </summary>
    private enum EmbeddedPdfAKind
    {
        /// <summary>No <c>pdfaid:part</c> property found, or the embedded PDF could not be opened.</summary>
        NoPdfAId,
        /// <summary>The embedded PDF claims to be PDF/A-1 (part 1).</summary>
        Part1,
        /// <summary>The embedded PDF claims to be PDF/A-2 (part 2).</summary>
        Part2,
        /// <summary>The embedded PDF claims some other part (3+) — not PDF/A-1 or PDF/A-2.</summary>
        OtherPart,
    }

    /// <summary>
    /// Opens the embedded PDF at <paramref name="pdfBytes"/> and reads the <c>pdfaid:part</c>
    /// property from the document <c>/Metadata</c> XMP stream. Also returns the
    /// <see cref="PdfConformance"/> level to use for recursive validation when part is 2.
    /// </summary>
    /// <remarks>The caller must catch all exceptions and handle them as non-PDF/A.</remarks>
    private static (EmbeddedPdfAKind Kind, PdfConformance? Level) ReadEmbeddedPdfAInfo(byte[] pdfBytes)
    {
        using var reader = VellumPdf.Reader.PdfReader.Open(pdfBytes);
        var metadataObj = reader.Catalog.Get(new PdfName("Metadata"));
        if (metadataObj is null)
            return (EmbeddedPdfAKind.NoPdfAId, null);

        if (metadataObj is not PdfIndirectReference metaRef)
            return (EmbeddedPdfAKind.NoPdfAId, null);
        var stream = reader.ResolveStream(metaRef.ObjectNumber);
        if (stream is null)
            return (EmbeddedPdfAKind.NoPdfAId, null);

        var metaBytes = reader.GetDecodedStreamData(stream);
        if (metaBytes is null)
            return (EmbeddedPdfAKind.NoPdfAId, null);

        var doc = XmpReader.Parse(metaBytes);
        if (doc is null)
            return (EmbeddedPdfAKind.NoPdfAId, null);

        var partStr = XmpReader.Get(doc, XmpReader.Pdfaid, "part");
        if (partStr is null)
            return (EmbeddedPdfAKind.NoPdfAId, null);

        if (partStr == "1")
            return (EmbeddedPdfAKind.Part1, null);

        if (partStr == "2")
        {
            var conformanceStr = XmpReader.Get(doc, XmpReader.Pdfaid, "conformance");
            var level = conformanceStr?.ToUpperInvariant() switch
            {
                "U" => PdfConformance.PdfA2U,
                "A" => PdfConformance.PdfA2A,
                _ => PdfConformance.PdfA2B, // default to B when conformance is missing/unrecognised
            };
            return (EmbeddedPdfAKind.Part2, level);
        }

        return (EmbeddedPdfAKind.OtherPart, null);
    }

    private void CollectTyped(PdfObject? value, int depth, List<PdfDictionary> filespecs, HashSet<PdfDictionary> seen)
    {
        if (depth > MaxDepth)
            return;

        switch (value)
        {
            case PdfDictionary dict:
                if (dict.Get(PdfName.Type) is PdfName type && type.Value == _filespec.Value && seen.Add(dict))
                    filespecs.Add(dict);
                foreach (var entry in dict.Entries)
                    CollectTyped(entry.Value, depth + 1, filespecs, seen);
                break;
            case PdfArray array:
                for (var i = 0; i < array.Count; i++)
                    CollectTyped(array[i], depth + 1, filespecs, seen);
                break;
        }
    }

    private void CollectNameTree(
        PreflightContext context, PdfDictionary node, int depth, List<PdfDictionary> filespecs, HashSet<PdfDictionary> seen)
    {
        if (depth > MaxDepth)
            return;

        if (context.Resolve(node.Get(_names)) is PdfArray entries)
            for (var i = 1; i < entries.Count; i += 2)
                if (context.Resolve(entries[i]) is PdfDictionary filespec && seen.Add(filespec))
                    filespecs.Add(filespec);

        if (context.Resolve(node.Get(_kids)) is PdfArray kids)
            for (var i = 0; i < kids.Count; i++)
                if (context.Resolve(kids[i]) is PdfDictionary kid)
                    CollectNameTree(context, kid, depth + 1, filespecs, seen);
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
            if (bytes[i] != prefix[i])
                return false;
        return true;
    }
}
