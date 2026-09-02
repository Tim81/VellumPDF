// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.16-1. An encrypted conforming file's encryption dictionary shall carry a
/// <c>/P</c> entry with bit 10 set.
/// </summary>
/// <remarks>
/// Authored from ISO 32000-2:2020 §7.6.4.2, Table 22, bit position 10: "Not used. This bit was
/// previously used to determine whether content could be extracted for the purposes of
/// accessibility, however, that restriction has been deprecated in PDF 2.0. PDF readers shall
/// ignore this bit and PDF writers shall always set this bit to 1 to ensure compatibility with PDF
/// readers following earlier specifications." PDF/UA-1 (ISO 14289-1, based on ISO 32000-1) still
/// requires the bit — it predates the PDF 2.0 deprecation — so a document can be simultaneously a
/// spec-legal PDF 2.0 file and a §7.16-1 violation. Clean-room: derived from the specification
/// text, not from any third-party validation profile.
/// <para>
/// A document with no <c>/Encrypt</c> key draws no finding: §7.16-1 constrains the encryption
/// dictionary, and an unencrypted file has none to constrain.
/// </para>
/// </remarks>
internal sealed class UaEncryptionPermissionsRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.16-1";

    public string Clause => "ISO 14289-1:2014, 7.16";

    private static readonly PdfName _encryptKey = new("Encrypt");

    private static readonly PdfName _pKey = new("P");

    public void Evaluate(PreflightContext context)
    {
        // Presence alone decides whether the rule applies; an unresolvable /Encrypt reference is
        // reported rather than thrown on, matching FileTrailerRule's treatment of the same key.
        var encryptRef = context.Trailer.Get(_encryptKey);
        if (encryptRef is null)
            return;

        if (context.Resolve(encryptRef) is not PdfDictionary encrypt)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The trailer's /Encrypt entry does not resolve to a dictionary, so its /P entry "
                + "cannot be checked for the required bit 10.");
            return;
        }

        // /P is a required entry (ISO 32000-2 Table 20); a document missing it fails to open at all
        // (VellumPdf.Reader.EncryptionSetup requires it before any rule runs), so this branch is
        // unreachable through PdfPreflight — it exists to state the clause's full requirement, and
        // documents the divergence from veraPDF, which reports 7.16-1 failed for a missing /P
        // rather than refusing to open the file.
        if (context.Resolve(encrypt.Get(_pKey)) is not PdfInteger p)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The encryption dictionary does not contain a /P entry.");
            return;
        }

        // PdfInteger.Value is a long; a 32-bit /P value such as 4294967292 (the unsigned reading of
        // -4) has to fold back into the signed int32 the bit test operates on, matching how the
        // reader itself narrows /P (VellumPdf.Reader.EncryptionSetup).
        var pValue = unchecked((int)p.Value);

        if ((pValue & 0x200) == 0)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The encryption dictionary's /P entry does not have bit 10 set (P = "
                + $"{pValue}); PDF writers shall always set this bit to 1.");
        }
    }
}
