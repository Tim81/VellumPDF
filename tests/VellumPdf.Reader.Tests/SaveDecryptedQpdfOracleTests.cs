// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.TestSupport;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <c>qpdf --check</c> on every <see cref="PdfDocumentReader.SaveDecrypted(Stream)"/> output (#186).
/// A structural backstop, not the acceptance — the issue explains at length why <c>qpdf --check</c>
/// is clean on every corruption that matters here (ciphertext left in strings, wrong-object-identity
/// decryption, objects the lazy cache never read, flattened generations). The real known-answer
/// tests are <see cref="SaveDecryptedFixtureRoundTripTests"/>'s value-level comparisons; this class
/// only proves qpdf finds nothing wrong with the file's own structure.
/// </summary>
public sealed class SaveDecryptedQpdfOracleTests : IDisposable
{
    private readonly string _tempDir;

    public SaveDecryptedQpdfOracleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellum_savedecrypted_qpdf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Reuses SaveDecryptedFixtureRoundTripTests's own fixture/password matrix rather than
    // maintaining a second copy that could silently drift from the current 17-fixture corpus.
    public static TheoryData<string, string?> Fixtures
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var (name, password) in SaveDecryptedFixtureRoundTripTests.AllFixtures)
                data.Add(name, password);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveDecrypted_output_passesQpdfCheck(string fixtureName, string? password)
    {
        using var reader = PdfReader.Open(Load(fixtureName), new PdfReaderOptions { Password = password });

        var path = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(fixtureName) + "-decrypted.pdf");
        using (var fs = File.Create(path))
            reader.SaveDecrypted(fs);

        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);

        Assert.False(timedOut, "qpdf --check timed out, or its output could not be fully captured.");
        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on {fixtureName}'s decrypted output.\n"
            + $"stdout: {stdout}\nstderr: {stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout);
    }

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
