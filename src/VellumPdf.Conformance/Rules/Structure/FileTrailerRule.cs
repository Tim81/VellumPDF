// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.3 (File trailer). The file trailer dictionary shall contain a <c>/ID</c>
/// entry whose value is an array of exactly two byte strings.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.3 and ISO 32000-1:2008, 7.5.5 / 14.4. Clean-room: derived
/// from the specification text, not from any third-party validation profile.
/// <para>
/// §6.1.3 also forbids the <c>/Encrypt</c> key. That case is enforced earlier, at the reader
/// layer (<see cref="Reader.PdfDocumentReader"/> rejects encrypted documents), so an encrypted
/// file never reaches rule evaluation; it is therefore not duplicated here.
/// </para>
/// </remarks>
internal sealed class FileTrailerRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.3-id-present";

    public string Clause => "ISO 19005-2:2011, 6.1.3";

    public void Evaluate(PreflightContext context)
    {
        var id = context.Resolve(context.Trailer.Get(PdfName.ID));

        if (id is not PdfArray array
            || array.Count != 2
            || !IsByteString(context.Resolve(array[0]))
            || !IsByteString(context.Resolve(array[1])))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The file trailer dictionary shall contain a /ID entry that is an array of two byte strings.");
        }
    }

    private static bool IsByteString(PdfObject? value) => value is PdfLiteralString or PdfHexString;
}
