// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Cli;

namespace VellumPdf.Cli.Tests;

/// <summary>
/// What <c>vellum-preflight</c> does with a document it cannot decrypt. Since #97 the reader opens
/// encrypted files instead of refusing them, which moved the failure from "unsupported feature" to
/// "wrong password" — and moved it earlier, into profile auto-detection, which opens the document
/// before validation ever runs.
/// </summary>
public sealed class EncryptedInputTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = PreflightRunner.Run(args, stdout, stderr, null);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// The default invocation, with no <c>-p</c>. Profile auto-detection opens the document first,
    /// with no password, so this is the path an encrypted file actually takes — and the one where
    /// an uncaught <c>PdfPasswordException</c> reached <c>Main</c> as a stack trace.
    /// </summary>
    [Fact]
    public void PasswordProtectedFile_withNoProfileFlag_reportsAnError_ratherThanCrashing()
    {
        var path = WriteTempPdf(PasswordProtectedPdf());
        try
        {
            var (code, _, err) = Run(path);

            Assert.Equal(2, code);
            Assert.Contains("password-protected", err, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The explicit-profile path reports the same condition in the same words.</summary>
    [Fact]
    public void PasswordProtectedFile_withExplicitProfile_reportsTheSameError()
    {
        var path = WriteTempPdf(PasswordProtectedPdf());
        try
        {
            var (code, _, err) = Run("-p", "2b", path);

            Assert.Equal(2, code);
            Assert.Contains("password-protected", err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vellum-cli-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // An RC4 document whose user password is "u" — /O, /U, /P and the trailer /ID are the values
    // from the reader's own enc-rc4-128.pdf fixture, which is what makes them authenticate. Nothing
    // here needs to decrypt; the CLI never gets past opening it.
    private static byte[] PasswordProtectedPdf()
    {
        var id = Convert.ToHexStringLower([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);
        var encrypt =
            "<< /Filter /Standard /V 2 /R 3 /Length 128 "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>";

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
