// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.8 (Embedded files). A file specification dictionary that carries an <c>/EF</c>
/// (embedded-file) entry shall contain both the <c>/F</c> and <c>/UF</c> file-name keys (§6.8-2).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.8 and ISO 32000-1:2008, 7.11.3. Clean-room: derived from the
/// specification text, not from any third-party validation profile.
/// <para>
/// The requirement applies only to actual file specification dictionaries — not to any dictionary
/// that merely happens to carry an <c>/EF</c> key. Empirically (cross-checked against veraPDF 1.30.2)
/// a dictionary is treated as a file specification when it is explicitly typed <c>/Type /Filespec</c>
/// (this holds even for an unreferenced object) <em>or</em> when it is reached through a file-spec
/// reference slot — a value node of the <c>/Names /EmbeddedFiles</c> name tree, or a file-attachment
/// annotation's <c>/FS</c> — in which case the <c>/Type</c> may be omitted. Keying purely on the
/// presence of an <c>/EF</c> key would over-report (an <c>/EF</c> on, say, a page dictionary is not a
/// file specification and veraPDF accepts it), so the rule identifies file specifications the way
/// veraPDF does instead.
/// </para>
/// <para>
/// Deferred: §6.8-5 (the embedded file itself shall be a valid PDF/A-1 or PDF/A-2 document) needs a
/// recursive validation of the embedded stream and is a separate, later vector. A file specification
/// reached only through an <c>/AF</c> associated-files array (a PDF 2.0 construct, not part of
/// PDF/A-2) is not identified here.
/// </para>
/// </remarks>
internal sealed class EmbeddedFileRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.8-2-embedded-file-names";

    public string Clause => "ISO 19005-2:2011, 6.8";

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
        // Collect the dictionaries that are genuinely file specifications, then apply the F/UF
        // requirement to each. Each distinct dictionary is examined once.
        var filespecs = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        // 1. Any object explicitly typed /Type /Filespec, anywhere in the object graph (the typing is
        //    authoritative even for an unreferenced object). Only inline children are recursed into —
        //    every indirect object is already visited at top level.
        foreach (var obj in context.EnumerateIndirectObjects())
            CollectTyped(obj, 0, filespecs, seen);

        // 2. Value nodes of the /Names /EmbeddedFiles name tree are file specifications by definition,
        //    even when untyped.
        if (context.Resolve(context.Catalog.Get(_names)) is PdfDictionary names
            && context.Resolve(names.Get(_embeddedFiles)) is PdfDictionary tree)
            CollectNameTree(context, tree, 0, filespecs, seen);

        // 3. A file-attachment annotation's /FS value is a file specification.
        foreach (var annot in context.EnumerateAnnotations())
            if (context.Resolve(annot.Get(_fs)) is PdfDictionary fs && seen.Add(fs))
                filespecs.Add(fs);

        foreach (var filespec in filespecs)
            if (filespec.Get(_ef) is not null && (filespec.Get(_f) is null || filespec.Get(_uf) is null))
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "An embedded-file specification dictionary (with an /EF entry) is missing the /F "
                    + "or /UF file-name key, both of which PDF/A-2 requires (§6.8).");
                return; // One report suffices; the verdict is unaffected by the count.
            }
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

    // Walks a name-tree node (/Names leaf pairs and /Kids intermediate nodes), collecting every value
    // node as a file specification.
    private void CollectNameTree(
        PreflightContext context, PdfDictionary node, int depth, List<PdfDictionary> filespecs, HashSet<PdfDictionary> seen)
    {
        if (depth > MaxDepth)
            return;

        if (context.Resolve(node.Get(_names)) is PdfArray entries)
            for (var i = 1; i < entries.Count; i += 2) // [key, value, key, value, …]
                if (context.Resolve(entries[i]) is PdfDictionary filespec && seen.Add(filespec))
                    filespecs.Add(filespec);

        if (context.Resolve(node.Get(_kids)) is PdfArray kids)
            for (var i = 0; i < kids.Count; i++)
                if (context.Resolve(kids[i]) is PdfDictionary kid)
                    CollectNameTree(context, kid, depth + 1, filespecs, seen);
    }
}
