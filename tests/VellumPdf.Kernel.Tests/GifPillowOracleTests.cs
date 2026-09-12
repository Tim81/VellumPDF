// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Images;
using VellumPdf.TestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Independent-codec oracle for the GIF LZW width-growth rule, in both directions, via
/// <c>eng/gif-oracle.py</c> and Pillow.
///
/// <para>
/// A round trip through this package's own encoder and decoder is the weakest instrument
/// available for this rule: both sides share one convention, so a matched drift in both stays
/// green (<see cref="GifSpecificationTests"/>'s remarks describe a real case of exactly that).
/// The suite's own mutation testing measured how weak. Narrowing the decoder's code-width cap at
/// <c>GifImageLoader.cs</c> from 12 bits to 11, or to 10, passes the whole Kernel suite; only a
/// cap of 9 was ever caught by an in-process test. A raster large enough to fill the 4,096-entry
/// LZW table forces a real GIF encoder to widen codes past 9 bits, and Pillow, pinned in CI at
/// 12.3.0 for the barcode decode oracle already, is a second implementation with no stake in
/// this package's own width bookkeeping.
/// </para>
///
/// <para>
/// Uses the shared <see cref="ExternalTool"/>/<see cref="OracleGate"/> pair (#198): a missing
/// <c>python</c> or Pillow skips visibly on a local dev machine, but fails the build on CI
/// (<c>CI</c>/<c>GITHUB_ACTIONS</c>/<c>REQUIRE_ORACLES</c>), so this oracle can never silently
/// pass vacuously. <c>python</c> is tried first, then <c>python3</c> (Windows has no
/// <c>python3</c> alias); a distinct exit code (3) from the script means Pillow is not installed,
/// which gates the same way as a missing executable.
/// </para>
/// </summary>
public sealed class GifPillowOracleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _scriptPath;

    public GifPillowOracleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumgiforacle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "gif-oracle.py");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup: temp dir may already be gone */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup: locked file on Windows */ }
    }

    /// <summary>
    /// Pillow writes a 150x150 raster whose pseudo-random content is known, ahead of time, to
    /// fill the 4,096-entry LZW table (<see cref="GifSpecificationTests.Encode_writesACodeStreamAppendixFAccepts"/>
    /// forces the same table-filling behaviour from this package's own encoder on an identical
    /// raster). Decoding Pillow's file with <see cref="GifImageLoader"/> and recovering the exact
    /// original pixels is proof this decoder reads a code wider than 9 bits from a real encoder,
    /// not from a fixture built by the same convention it is meant to check.
    /// </summary>
    [Fact]
    public void Decode_pillowWrittenTableFillingRaster_matchesTheOriginalPixels()
    {
        const int w = 150, h = 150;
        var rgb = TableFillingRaster(w, h);

        var rawPath = Path.Combine(_tempDir, "source.raw");
        var gifPath = Path.Combine(_tempDir, "pillow-written.gif");
        File.WriteAllBytes(rawPath, rgb);

        RunOracle(["encode", rawPath, w.ToString(), h.ToString(), gifPath]);

        var img = GifImageLoader.Load(File.ReadAllBytes(gifPath));
        Assert.Equal(w, img.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(rgb, DecodeRgb(img));
    }

    /// <summary>
    /// The other direction: <see cref="GifEncoder"/> writes the same table-filling raster, and
    /// Pillow reads it back byte-exactly. Closes the direction
    /// <see cref="Decode_pillowWrittenTableFillingRaster_matchesTheOriginalPixels"/> does not:
    /// a decoder bug that happened to match this package's own encoder's width bookkeeping would
    /// pass every in-process round trip and the test above, but not this one.
    /// </summary>
    [Fact]
    public void Encode_tableFillingRaster_pillowDecodesToTheOriginalPixels()
    {
        const int w = 150, h = 150;
        var rgb = TableFillingRaster(w, h);

        var gif = GifEncoder.Encode(rgb, w, h);
        var gifPath = Path.Combine(_tempDir, "vellumpdf-written.gif");
        var rawPath = Path.Combine(_tempDir, "pillow-decoded.raw");
        File.WriteAllBytes(gifPath, gif);

        RunOracle(["decode", gifPath, rawPath]);

        var decoded = File.ReadAllBytes(rawPath);
        Assert.Equal(rgb, decoded);
    }

    /// <summary>
    /// The same pseudo-random pixel-per-index function <see cref="GifSpecificationTests"/> uses
    /// for its own table-filling fixture (measured there to peak the LZW table at 4,096 entries
    /// on a 150x150 raster), so this oracle exercises the identical boundary rather than a
    /// differently-shaped one.
    /// </summary>
    private static byte[] TableFillingRaster(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            var v = (byte)((i * 1103515245 + 12345) >> 16);
            rgb[i * 3] = v;
            rgb[i * 3 + 1] = (byte)(v / 2);
            rgb[i * 3 + 2] = (byte)(255 - v);
        }
        return rgb;
    }

    /// <summary>
    /// The decoded raster is the image XObject's stream, deflated by <c>PdfStream</c>, and the
    /// only way to its bytes from outside the package is to write the object and inflate what
    /// lands between the stream keywords. <see cref="GifSpecificationTests"/> reads it the same
    /// way.
    /// </summary>
    private static byte[] DecodeRgb(PdfImageXObject img)
    {
        using var pdfMs = new MemoryStream();
        img.BuildStream().WriteTo(new VellumPdf.IO.PdfWriter(pdfMs));
        var raw = pdfMs.ToArray();
        var start = IndexOf(raw, "\nstream\n"u8) + 8;
        var end = IndexOf(raw, "\nendstream"u8);

        using var input = new MemoryStream(raw[start..end]);
        using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        throw new InvalidOperationException("stream marker not found");
    }

    /// <summary>
    /// Runs <c>eng/gif-oracle.py</c> with <paramref name="arguments"/>, trying <c>python</c> then
    /// <c>python3</c>. Gates through <see cref="OracleGate"/> when neither interpreter can be
    /// launched, or when the script's exit code (3) reports Pillow itself missing; any other
    /// non-zero exit is a defect in VellumPdf's own output, not an environment problem, and fails
    /// the test directly instead of skipping.
    /// </summary>
    private void RunOracle(IReadOnlyList<string> arguments)
    {
        var fullArgs = new List<string>(arguments.Count + 1) { _scriptPath };
        fullArgs.AddRange(arguments);

        foreach (var python in new[] { "python", "python3" })
        {
            if (ExternalTool.TryRun(python, fullArgs, out var exitCode, out var stdout, out var stderr,
                    out var timedOut, outputEncoding: Encoding.UTF8))
            {
                if (exitCode == 3)
                    OracleGate.Unavailable("Pillow");

                Assert.True(exitCode == 0 && !timedOut,
                    timedOut
                        ? $"gif-oracle.py timed out running '{string.Join(' ', arguments)}'."
                        : $"gif-oracle.py failed (exit {exitCode}) running '{string.Join(' ', arguments)}'.\nstdout: {stdout}\nstderr: {stderr}");
                return;
            }
        }

        OracleGate.Unavailable("python");
    }

    /// <summary>Locates the repository root by walking up from the test assembly's directory to find <c>VellumPdf.slnx</c>.</summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VellumPdf.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate VellumPdf.slnx by walking up from AppContext.BaseDirectory.");
    }
}
