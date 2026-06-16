// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Reader;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for the <see cref="PadesLevel"/> enum wired through
/// <see cref="SigningExtensions.Sign"/> and <see cref="PdfSignatureSettings"/>.
/// All signing tests are fully offline and deterministic.
/// </summary>
public sealed class PadesLevelTests : IDisposable
{
    private static readonly DateTimeOffset s_pinnedTime =
        new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static readonly byte[] s_cannedOcsp = [0x30, 0x03, 0x0A, 0x01, 0x00];
    private static readonly byte[] s_cannedCrl = [0x30, 0x04, 0x02, 0x01, 0x2A];

    private readonly string _tempDir;

    public PadesLevelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumpades_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Certificate helpers ──────────────────────────────────────────────────────

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf PadesLevel Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    private static byte[] SignOnePageDoc(PdfSignatureSettings settings)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFont(font, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("PadesLevel test page")
              .EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        return ms.ToArray();
    }

    // ── B-B ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sign_BB_unchanged_has_one_signature_and_no_dss()
    {
        using var cert = CreateTestCertificate();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_B,
        };

        var signedBytes = SignOnePageDoc(settings);

        using var reader = PdfReader.Open(signedBytes);

        // Exactly one signature (the CMS signature field).
        Assert.Single(reader.Signatures);

        // No /DSS in the catalog.
        var dssRaw = reader.Catalog.Get(new PdfName("DSS"));
        Assert.Null(dssRaw);
    }

    // ── B-T ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sign_BT_has_timestamp_unsigned_attribute()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_T,
            TimestampClient = tsaClient,
        };

        var signedBytes = SignOnePageDoc(settings);

        // Decode the CMS signature and look for the timestamp unsigned attribute OID.
        using var reader = PdfReader.Open(signedBytes);
        Assert.Single(reader.Signatures);

        var sig = reader.Signatures[0];
        var contentsDer = sig.Contents.ToArray();

        var br = sig.ByteRange;
        var signedContent = new byte[br[1] + br[3]];
        Buffer.BlockCopy(signedBytes, br[0], signedContent, 0, br[1]);
        Buffer.BlockCopy(signedBytes, br[2], signedContent, br[1], br[3]);

        var outerCms = new SignedCms(new ContentInfo(signedContent), detached: true);
        outerCms.Decode(contentsDer);
        var si = outerCms.SignerInfos[0];

        CryptographicAttributeObject? tsAttr = null;
        foreach (CryptographicAttributeObject attr in si.UnsignedAttributes)
        {
            if (attr.Oid.Value == "1.2.840.113549.1.9.16.2.14")
            {
                tsAttr = attr;
                break;
            }
        }
        Assert.NotNull(tsAttr);
        Assert.True(tsAttr.Values.Count > 0);
        var tokenDer = tsAttr.Values[0].RawData;
        Assert.True(Rfc3161TimestampToken.TryDecode(tokenDer, out var token, out _));
        Assert.NotNull(token);
    }

    // ── B-LT ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sign_BLT_has_dss_with_certs_and_vri_and_original_signature_validates()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);
        var revClient = new CannedRevocationClient();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_LT,
            TimestampClient = tsaClient,
            RevocationClient = revClient,
        };

        var signedBytes = SignOnePageDoc(settings);

        using var reader = PdfReader.Open(signedBytes);

        // /DSS must be in the catalog.
        var dssRaw = reader.Catalog.Get(new PdfName("DSS"));
        Assert.NotNull(dssRaw);

        var dssRef = dssRaw as PdfIndirectReference;
        Assert.NotNull(dssRef);
        var dssDict = reader.Resolve(dssRef.ObjectNumber) as PdfDictionary;
        Assert.NotNull(dssDict);

        // /Certs must be non-empty.
        var certsRaw = dssDict.Get(new PdfName("Certs"));
        Assert.NotNull(certsRaw);
        var certsArr = certsRaw as PdfArray;
        Assert.NotNull(certsArr);
        Assert.True(certsArr.Count > 0, "/DSS /Certs must be non-empty");

        // /VRI must be present.
        var vriRaw = dssDict.Get(new PdfName("VRI"));
        Assert.NotNull(vriRaw);

        // Original signature must still verify.
        var sig = reader.Signatures[0];
        var br = sig.ByteRange;
        var signedContent = new byte[br[1] + br[3]];
        Buffer.BlockCopy(signedBytes, br[0], signedContent, 0, br[1]);
        Buffer.BlockCopy(signedBytes, br[2], signedContent, br[1], br[3]);

        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(sig.Contents.ToArray());
        verify.CheckSignature(verifySignatureOnly: true);
    }

    // ── B-LTA ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sign_BLTA_has_docTimestamp_and_dss_and_original_signature_validates()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);
        var revClient = new CannedRevocationClient();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_LTA,
            TimestampClient = tsaClient,
            RevocationClient = revClient,
        };

        var signedBytes = SignOnePageDoc(settings);

        using var reader = PdfReader.Open(signedBytes);

        // Two signatures: the CMS signature and the /DocTimeStamp.
        Assert.Equal(2, reader.Signatures.Count);

        // One of them must have /SubFilter /ETSI.RFC3161.
        var docTs = reader.Signatures.FirstOrDefault(s => s.SubFilter?.Value == "ETSI.RFC3161");
        Assert.NotNull(docTs);

        // /DSS must still be present.
        var dssRaw = reader.Catalog.Get(new PdfName("DSS"));
        Assert.NotNull(dssRaw);

        // Original CMS signature (not the DocTimeStamp) must still verify.
        var origSig = reader.Signatures.First(s => s.SubFilter?.Value != "ETSI.RFC3161");
        var br = origSig.ByteRange;
        var signedContent = new byte[br[1] + br[3]];
        Buffer.BlockCopy(signedBytes, br[0], signedContent, 0, br[1]);
        Buffer.BlockCopy(signedBytes, br[2], signedContent, br[1], br[3]);

        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(origSig.Contents.ToArray());
        verify.CheckSignature(verifySignatureOnly: true);
    }

    // ── Validation errors ────────────────────────────────────────────────────────

    [Fact]
    public void Level_BT_without_TimestampClient_throws_ArgumentException()
    {
        using var cert = CreateTestCertificate();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_T,
            TimestampClient = null,
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var ms = new MemoryStream();
            doc.Sign(ms, settings);
        });
        Assert.Contains("TimestampClient", ex.Message);
    }

    [Fact]
    public void Level_BLT_without_TimestampClient_throws_ArgumentException()
    {
        using var cert = CreateTestCertificate();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_LT,
            TimestampClient = null,
            RevocationClient = new CannedRevocationClient(),
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var ms = new MemoryStream();
            doc.Sign(ms, settings);
        });
        Assert.Contains("TimestampClient", ex.Message);
    }

    [Fact]
    public void Level_BLT_without_RevocationClient_throws_ArgumentException()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_LT,
            TimestampClient = tsaClient,
            RevocationClient = null,
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var ms = new MemoryStream();
            doc.Sign(ms, settings);
        });
        Assert.Contains("RevocationClient", ex.Message);
    }

    [Fact]
    public void Level_BLT_without_RevocationClient_throws_via_Layout_overload()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_LT,
            TimestampClient = tsaClient,
            RevocationClient = null,
        };

        var doc = new VellumPdf.Layout.Document();

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var ms = new MemoryStream();
            doc.Sign(ms, settings);
        });
        Assert.Contains("RevocationClient", ex.Message);
    }

    // ── pdfsig oracle (CI-gated, skipped when pdfsig not on PATH) ────────────────

    [Fact]
    public void Sign_BLTA_pdfsig_reports_valid_signature()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);
        var revClient = new CannedRevocationClient();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Level = PadesLevel.B_LTA,
            TimestampClient = tsaClient,
            RevocationClient = revClient,
        };

        var signedBytes = SignOnePageDoc(settings);

        var pdfPath = Path.Combine(_tempDir, "blta_pdfsig_oracle.pdf");
        File.WriteAllBytes(pdfPath, signedBytes);

        if (!TryRunTool("pdfsig", $"\"{pdfPath}\"", out _, out var stdout, out var stderr))
        {
            GateOnCi("pdfsig");
            return;
        }

        // pdfsig (poppler-utils) outputs "Signature is Valid" for a valid digest.
        // An untrusted self-signed cert still produces a valid digest, so this confirms
        // /ByteRange and /Contents are structurally correct.
        Assert.True(
            stdout.Contains("Signature is Valid", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Signature is Valid", StringComparison.OrdinalIgnoreCase),
            $"pdfsig did not report 'Signature is Valid'.\nstdout: {stdout}\nstderr: {stderr}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns canned OCSP and CRL DER for every (cert, issuer) pair without any
    /// network calls.
    /// </summary>
    private sealed class CannedRevocationClient : IRevocationClient
    {
        public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer)
            => new()
            {
                Ocsp = new ReadOnlyMemory<byte>(s_cannedOcsp),
                Crl = new ReadOnlyMemory<byte>(s_cannedCrl),
            };
    }

    /// <summary>
    /// Attempts to run an external CLI tool and captures its output.
    /// Returns false if the process cannot be started (tool not installed).
    /// </summary>
    private static bool TryRunTool(
        string exe,
        string args,
        out int exitCode,
        out string stdout,
        out string stderr)
    {
        exitCode = -1;
        stdout = string.Empty;
        stderr = string.Empty;

        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            return false;
        }

        if (process is null) return false;

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var completed = process.WaitForExit(milliseconds: 30_000);
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();

            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                exitCode = -1;
                return true;
            }

            exitCode = process.ExitCode;
        }

        return true;
    }

    /// <summary>
    /// Asserts failure when running on CI and the required tool is absent.
    /// On a local dev machine (non-CI), returns silently (test is skipped).
    /// </summary>
    private static void GateOnCi(string toolName)
    {
        var isCI = string.Equals(
            Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase);
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
            StringComparison.OrdinalIgnoreCase);

        if (isCI || isGitHubActions)
        {
            Assert.Fail(
                $"Required external tool '{toolName}' is not available on CI. " +
                "Ensure the CI workflow installs it (e.g. sudo apt-get install -y poppler-utils).");
        }

        // Local dev: tool not installed — silently skip.
    }
}
