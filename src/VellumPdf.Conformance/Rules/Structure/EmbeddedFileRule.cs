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
/// specification text, not from any third-party validation profile. File specifications can be
/// reached from many places (the <c>/Names /EmbeddedFiles</c> name tree, an <c>/AF</c> associated-
/// files array, a file-attachment annotation's <c>/FS</c>), so the rule keys on the defining signal —
/// the presence of an <c>/EF</c> key on a dictionary — by walking every indirect object's value
/// graph, mirroring veraPDF's <c>containsEF</c> predicate.
/// <para>
/// Deferred: §6.8-5 (the embedded file itself shall be a valid PDF/A-1 or PDF/A-2 document) needs a
/// recursive validation of the embedded stream and is a separate, later vector.
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

    public void Evaluate(PreflightContext context)
    {
        // Every indirect object is yielded here already, so the walk recurses only into inline
        // (non-reference) children — referenced objects are reached at top level instead, which keeps
        // the traversal acyclic without a visited set.
        var reported = false;
        foreach (var obj in context.EnumerateIndirectObjects())
        {
            Walk(context, obj, 0, ref reported);
            if (reported)
                return;
        }
    }

    private void Walk(PreflightContext context, PdfObject? value, int depth, ref bool reported)
    {
        if (reported || depth > MaxDepth)
            return;

        switch (value)
        {
            case PdfDictionary dict:
                // A dictionary carrying an /EF key is an embedded-file specification: it must name the
                // file with both /F and /UF.
                if (dict.Get(_ef) is not null && (dict.Get(_f) is null || dict.Get(_uf) is null))
                {
                    context.Report(
                        RuleId, Clause, PreflightSeverity.Error,
                        "An embedded-file specification dictionary (with an /EF entry) is missing the /F "
                        + "or /UF file-name key, both of which PDF/A-2 requires (§6.8).");
                    reported = true;
                    return;
                }
                foreach (var entry in dict.Entries)
                    Walk(context, entry.Value, depth + 1, ref reported);
                break;
            case PdfArray array:
                for (var i = 0; i < array.Count && !reported; i++)
                    Walk(context, array[i], depth + 1, ref reported);
                break;
        }
    }
}
