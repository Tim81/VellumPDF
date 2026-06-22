// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.11 (File specifications). A file-specification dictionary that carries an
/// <c>/EF</c> (embedded-file) entry shall also carry non-empty <c>/F</c> and <c>/UF</c>
/// file-name entries.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.11 (CosFileSpecification predicate:
/// <c>containsEF == false || (F != null &amp;&amp; F != '' &amp;&amp; UF != null &amp;&amp; UF != '')</c>)
/// and empirically validated against veraPDF 1.30.2 (clause 7.11, testNumber 1). Clean-room:
/// derived from the specification text and the veraPDF profile, not from any third-party
/// implementation.
/// <para>
/// The rule identifies file specifications the same way as
/// <c>EmbeddedFileRule</c> (ISO 19005-2 §6.8-2): a dictionary is a file specification
/// when it is explicitly typed <c>/Type /Filespec</c> OR when it is reached through a known
/// file-spec reference slot (<c>/Names /EmbeddedFiles</c> name tree or a file-attachment
/// annotation's <c>/FS</c> entry). Keying purely on the presence of an <c>/EF</c> key would
/// over-report (e.g. an <c>/EF</c> on a page dictionary is not a file specification and veraPDF
/// accepts it).
/// </para>
/// </remarks>
internal sealed class UaEmbeddedFileRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.11-1";

    public string Clause => "ISO 14289-1:2014, 7.11";

    private const int MaxDepth = 64;

    private static readonly PdfName _ef = new("EF");
    private static readonly PdfName _f = new("F");
    private static readonly PdfName _uf = new("UF");
    private static readonly PdfName _names = new("Names");
    private static readonly PdfName _embeddedFiles = new("EmbeddedFiles");
    private static readonly PdfName _kids = new("Kids");
    private static readonly PdfName _fs = new("FS");
    private static readonly PdfName _filespec = new("Filespec");

    public void Evaluate(PreflightContext context)
    {
        var filespecs = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        // 1. Any object explicitly typed /Type /Filespec, anywhere in the object graph.
        foreach (var obj in context.EnumerateIndirectObjects())
            CollectTyped(obj, 0, filespecs, seen);

        // 2. Value nodes of the /Names /EmbeddedFiles name tree (untyped filespecs).
        if (context.Resolve(context.Catalog.Get(_names)) is PdfDictionary names
            && context.Resolve(names.Get(_embeddedFiles)) is PdfDictionary tree)
            CollectNameTree(context, tree, 0, filespecs, seen);

        // 3. File-attachment annotation /FS values.
        foreach (var annot in context.EnumerateAnnotations())
            if (context.Resolve(annot.Get(_fs)) is PdfDictionary fs && seen.Add(fs))
                filespecs.Add(fs);

        foreach (var filespec in filespecs)
        {
            if (filespec.Get(_ef) is null)
                continue; // No /EF — rule does not apply.

            // Both /F and /UF must be present and non-empty (resolved through any indirect reference,
            // matching veraPDF — a filespec whose /F or /UF is an indirect string is still conformant).
            if (!HasNonEmptyString(context.Resolve(filespec.Get(_f))) || !HasNonEmptyString(context.Resolve(filespec.Get(_uf))))
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "A file-specification dictionary with an /EF (embedded-file) entry is missing a "
                    + "non-empty /F or /UF file-name entry. PDF/UA-1 requires both to be present "
                    + "(ISO 14289-1:2014, 7.11).");
                return; // One report suffices.
            }
        }
    }

    private static bool HasNonEmptyString(PdfObject? obj) => obj switch
    {
        PdfLiteralString s => s.Bytes.Length > 0,
        PdfHexString s => s.Bytes.Length > 0,
        _ => false,
    };

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
}
