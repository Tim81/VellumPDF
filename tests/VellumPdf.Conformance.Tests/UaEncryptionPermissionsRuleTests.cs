// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Reader;
using static VellumPdf.Conformance.Tests.EncryptDictionaryAssertions;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// ISO 14289-1 §7.16-1: an encrypted document's <c>/Encrypt</c> dictionary must have <c>/P</c> bit
/// 10 set. Expected <c>/P</c> values below are derived from ISO 32000-2 Table 22 arithmetic
/// (<c>StandardSecurityHandler</c>'s <c>P = (0xFFFFF2C0 | (enabledBits &amp; 0xFFF)) &amp; ~3</c>,
/// bit 10 forced on since #397), not read back from whatever the writer happened to produce.
/// </summary>
public sealed class UaEncryptionPermissionsRuleTests
{
    private const string RuleId = "ISO14289-1:7.16-1";

    // ── Fixture 1: compliant, writer-built ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Permissions = All</c> sets every bit <c>StandardSecurityHandler</c> can set, including
    /// bit 10 (<c>PdfPermissions.Extract</c>) — the writer's ordinary output already satisfies
    /// §7.16-1. <c>P = (0xFFFFF2C0 | (0xF3C &amp; 0xFFF)) &amp; ~3 = -4</c> by hand.
    /// </summary>
    [Fact]
    public void CompliantDocument_AllPermissions_bit10Set_noFinding()
    {
        var bytes = BuildEncryptedOnePagePdf(PdfPermissions.All);

        AssertEncryptDictionaryIsR6WithP(bytes, -4);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1, password: null);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == RuleId);
    }

    // ── Fixture 2: violating, committed binary ────────────────────────────────────────────────────

    /// <summary>
    /// <c>Assets/enc-aes-256-p-bit10-clear.pdf</c> was built once with the pre-#397 writer, with
    /// <c>Permissions = All &amp; ~Extract</c>: <c>P = (0xFFFFF0C0 | (0xD3C &amp; 0xFFF)) &amp; ~3
    /// = -516</c> by hand under that writer's mask. It is committed rather than regenerated because
    /// #397 made the writer set bit 10 unconditionally, so there is no longer a way to produce this
    /// shape from the writer itself (see <c>Assets/README.md</c>).
    /// </summary>
    [Fact]
    public void ViolatingFixture_bit10Clear_reportsOneError()
    {
        var bytes = ReadEmbeddedFixture("enc-aes-256-p-bit10-clear.pdf");

        // The rule reads the trailer's /Encrypt /P directly, so this precondition — checked the same
        // way the rule itself reads it — is what stops a regenerated fixture whose bit accidentally
        // came back set from turning the assertion below vacuous.
        AssertEncryptDictionaryIsR6WithP(bytes, -516);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1, password: null);

        var finding = Assert.Single(result.Assertions, a => a.RuleId == RuleId);
        Assert.Equal(PreflightSeverity.Error, finding.Severity);
    }

    // ── Fixture 3: reads /P, not /Perms ───────────────────────────────────────────────────────────

    /// <summary>
    /// An R6 document written with <c>Permissions = All &amp; ~Assemble</c> has <c>/P -1028</c>
    /// (bit 10 still SET — only bit 11, Assemble, is cleared) and authenticates fine, so the rule
    /// passes on it unpatched. Patching the dictionary's ASCII <c>-1028</c> to <c>-1540</c> —
    /// same byte length, so every cross-reference offset stays valid — clears bit 10 in the
    /// dictionary value alone: <c>-1540 = -1028 - 512</c>, and 512 is exactly bit 10's weight. The
    /// sealed <c>/Perms</c> copy is untouched, so <c>EncryptionSetup</c> still reports
    /// <see cref="PdfPermissions.Extract"/> as granted — proving the rule reads the dictionary
    /// <c>/P</c>, not the reader's authenticated <see cref="PdfEncryptionInfo.Permissions"/>, which
    /// the reader deliberately reports even when the two disagree (never refuses; see
    /// <c>EncryptionSetup.cs</c>).
    /// </summary>
    [Fact]
    public void PatchedDictionaryP_disagreeingWithPerms_stillReportsError()
    {
        var original = BuildEncryptedOnePagePdf(PdfPermissions.All & ~PdfPermissions.Assemble);
        AssertEncryptDictionaryIsR6WithP(original, -1028);

        var text = Encoding.Latin1.GetString(original);
        Assert.Equal(1, CountOccurrences(text, "-1028"));
        var patchedText = text.Replace("-1028", "-1540", StringComparison.Ordinal);
        var patched = Encoding.Latin1.GetBytes(patchedText);
        Assert.Equal(original.Length, patched.Length);

        var result = PdfPreflight.Validate(patched, PdfConformance.PdfUA1, password: null);
        var finding = Assert.Single(result.Assertions, a => a.RuleId == RuleId);
        Assert.Equal(PreflightSeverity.Error, finding.Severity);

        using var reader = PdfReader.Open(patched, new PdfReaderOptions { Password = "" });
        Assert.NotNull(reader.Encryption);
        Assert.True(reader.Encryption!.Permissions.HasFlag(PdfPermissions.Extract));
    }

    // ── KATs ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// /P is Required (ISO 32000-2 Table 20); <c>EncryptionSetup.RequireInt</c> throws
    /// <see cref="InvalidDataException"/> before any rule runs, so a document missing it never
    /// reaches <see cref="UaEncryptionPermissionsRule"/> through <see cref="PdfPreflight"/> at all.
    /// This diverges from veraPDF, whose <c>P != null &amp;&amp; (P &amp; 512) == 512</c> test would
    /// report 7.16-1 failed on such a file rather than refusing to open it.
    /// </summary>
    [Fact]
    public void EncryptDictionary_missingP_throwsAtOpen_ratherThanReportingTheRule()
    {
        var bytes = BuildHandWrittenEncryptedPdf_missingP();

        Assert.Throws<InvalidDataException>(() => PdfPreflight.Validate(bytes, PdfConformance.PdfUA1, password: null));
    }

    /// <summary>An unencrypted document has no /Encrypt to constrain, so the rule reports nothing.</summary>
    [Fact]
    public void UnencryptedDocument_noFinding()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);

        var result = PdfPreflight.Validate(ms.ToArray(), PdfConformance.PdfUA1, password: null);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == RuleId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static byte[] BuildEncryptedOnePagePdf(PdfPermissions permissions)
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings
        {
            UserPassword = "",
            OwnerPassword = "vellum-test-owner",
            Permissions = permissions,
        });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static byte[] ReadEmbeddedFixture(string logicalName)
    {
        using var s = typeof(UaEncryptionPermissionsRuleTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} embedded resource not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // A minimal hand-written /Encrypt dictionary that omits /P entirely. /O and /U carry
    // syntactically valid hex strings but are never read: EncryptionSetup.RequireInt("/P") throws
    // before authentication is attempted, so their content is immaterial.
    private static byte[] BuildHandWrittenEncryptedPdf_missingP()
    {
        var id = Convert.ToHexStringLower([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);
        const string encrypt = "<< /Filter /Standard /V 2 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> >>";

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W($"3 0 obj\n{encrypt}\nendobj\n");
        var xref = (int)ms.Position;
        W($"xref\n0 4\n{0:D10} 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n{o3:D10} 00000 n \n");
        W($"trailer\n<< /Size 4 /Root 1 0 R /Encrypt 3 0 R /ID [<{id}><{id}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
