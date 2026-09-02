// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Cli;

namespace VellumPdf.Cli.Tests;

/// <summary>
/// What <c>vellum-preflight</c> does with a document it cannot decrypt. Since #97 the reader opens
/// encrypted files instead of refusing them, which moved the failure from "unsupported feature" to
/// "wrong password" — and moved it earlier, into profile auto-detection, which opens the document
/// before validation ever runs. Since #138 the tool can supply one via <c>--password</c>.
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

    /// <summary>
    /// No <c>--password</c> at all names the fix: "supply it with --password". A wrong one names a
    /// different one: "the supplied --password does not open it". Two distinct next steps, so two
    /// distinct messages — conflating them would send someone who mistyped a password down the
    /// "you never gave me one" path.
    /// </summary>
    [Fact]
    public void PasswordProtectedFile_withWrongPassword_namesTheSuppliedPasswordAsWrong()
    {
        var path = WriteTempPdf(PasswordProtectedPdf());
        try
        {
            var (code, _, err) = Run("--password", "not-u", path);

            Assert.Equal(2, code);
            Assert.Contains("the supplied --password does not open it", err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The no-password message points at the flag that would fix it.</summary>
    [Fact]
    public void PasswordProtectedFile_withNoPassword_namesTheFlagToSupplyIt()
    {
        var path = WriteTempPdf(PasswordProtectedPdf());
        try
        {
            var (code, _, err) = Run(path);

            Assert.Equal(2, code);
            Assert.Contains("supply it with --password", err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Both spellings — <c>--password u</c> and <c>--password=u</c> — reach the same parsed value,
    /// so the correct password lets the run past authentication regardless of which one was used.
    /// An explicit profile keeps the run from failing for the unrelated reason that this minimal
    /// fixture makes no PDF/A or PDF/UA claim.
    /// </summary>
    [Theory]
    [InlineData("--password", "u")]
    [InlineData("--password=u", null)]
    public void PasswordProtectedFile_withCorrectPassword_authenticatesAndRuns(string flag, string? value)
    {
        var path = WriteTempPdf(PasswordProtectedPdf());
        try
        {
            var args = value is null
                ? new[] { "-p", "2b", flag, path }
                : new[] { "-p", "2b", flag, value, path };
            var (code, _, err) = Run(args);

            Assert.NotEqual(2, code);
            Assert.DoesNotContain("password-protected", err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A <c>--password</c> with nothing after it is a usage error, like every other option
    /// that takes an argument.</summary>
    [Fact]
    public void PasswordFlag_withNoValue_isAUsageError()
    {
        var (code, _, err) = Run("--password");

        Assert.Equal(2, code);
        Assert.Contains("--password requires an argument", err, StringComparison.Ordinal);
    }

    /// <summary>The help text documents the flag.</summary>
    [Fact]
    public void HelpText_MentionsPassword()
    {
        Assert.Contains("--password", HelpText.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sibling condition on the same path. A public-key-encrypted document, or one naming a
    /// <c>/V</c> this library cannot implement, throws <c>UnsupportedPdfFeatureException</c> from the
    /// same auto-detection call — and that one reached <c>Main</c> as a stack trace and exit 127
    /// after the password case had already been fixed beside it.
    /// </summary>
    [Theory]
    [InlineData("<< /Filter /Adobe.PubSec /V 1 /R 2 >>", "public-key")]
    [InlineData("<< /Filter /Standard /V 3 /R 3 /Length 128 >>", "/V 3")]
    public void UnsupportedSecurityHandler_withNoProfileFlag_reportsAnError_ratherThanCrashing(
        string encryptDict, string expectedInMessage)
    {
        var path = WriteTempPdf(PasswordProtectedPdf(encryptDict));
        try
        {
            var (code, _, err) = Run(path);

            Assert.Equal(2, code);
            Assert.Contains(expectedInMessage, err, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// An encrypted document whose <c>/StmF</c> names a crypt filter its own <c>/CF</c> does not
    /// define. None of its streams can be decoded, so there is nothing to preflight — and the honest
    /// answer has to come from auto-detection, which opens the document before the validation loop
    /// that already knew how to say it. Until it did, this exited 2 having printed nothing at all.
    /// </summary>
    [Fact]
    public void UndefinedCryptFilter_withNoProfileFlag_reportsAnError_ratherThanNothing()
    {
        // An EMPTY user password, so the CLI — which has no way to supply one — gets past
        // authentication and actually reaches the crypt filter. With a password the password error
        // fires first and this says nothing about the guard. That is also the shape most encrypted
        // PDFs in the wild take. /O and /U were derived outside this library for the empty user
        // password, owner "o", /P -4 and the trailer /ID below.
        var path = WriteTempPdf(PasswordProtectedPdf(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /Nosuch /StrF /StdCF "
            + "/O <77b8fb098022d3ab34237ea5643c08710ea5123fc5f88bf993a68cca5f12b40f> "
            + "/U <1fd84f8c2906341c00abb1ed422f668f00000000000000000000000000000000> /P -4 >>"));
        try
        {
            var (code, _, err) = Run(path);

            Assert.Equal(2, code);
            Assert.NotEqual(string.Empty, err.Trim());
            Assert.Contains("crypt filter", err, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same silence, one layer out: a file that is not a PDF at all. The explicit-profile path
    /// has always named the file and the reason; the default invocation returned an empty profile
    /// list and exited 2 without a word.
    /// </summary>
    [Fact]
    public void MalformedFile_withNoProfileFlag_reportsAnError_ratherThanNothing()
    {
        var path = WriteTempPdf("this is not a PDF at all"u8.ToArray());
        try
        {
            var (code, _, err) = Run(path);

            Assert.Equal(2, code);
            Assert.Contains("is not a valid PDF", err, StringComparison.Ordinal);
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
    private static byte[] PasswordProtectedPdf(string? encryptDict = null)
    {
        var id = Convert.ToHexStringLower([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);
        var encrypt = encryptDict
            ?? "<< /Filter /Standard /V 2 /R 3 /Length 128 "
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
