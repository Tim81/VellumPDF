// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Guards the checked-in encrypted corpus (#99) itself, before anything tries to decrypt it.
/// qpdf refuses to write RC4 without <c>--allow-weak-crypto</c> and still leaves a zero-byte file
/// behind, so a corpus can look complete on disk while three of its seven entries are empty. These
/// assertions fail loudly in that case rather than letting a decrypt test "pass" against nothing.
/// </summary>
public sealed class EncryptedFixtureCorpusTests
{
    public static TheoryData<string, int, int> Fixtures => new()
    {
        // file, expected /V, expected /R
        { "enc-rc4-40.pdf", 1, 2 },
        { "enc-rc4-128.pdf", 2, 3 },
        { "enc-rc4-128-v4.pdf", 4, 4 },
        { "enc-aes-128.pdf", 4, 4 },
        { "enc-aes-256-r5.pdf", 5, 5 },
        { "enc-aes-256-r6.pdf", 5, 6 },
        { "enc-256-cleartextmd.pdf", 5, 6 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_isPresent_nonEmpty_andDeclaresTheExpectedVandR(string name, int expectedV, int expectedR)
    {
        var bytes = Load(name);

        // The zero-byte trap: a qpdf refusal leaves the file created but empty.
        Assert.True(bytes.Length > 1000, $"{name} is {bytes.Length} bytes — qpdf likely refused to write it.");

        var text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Encrypt", text, StringComparison.Ordinal);
        Assert.Contains($"/V {expectedV}", text, StringComparison.Ordinal);
        Assert.Contains($"/R {expectedR}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextBaseline_isPresent_andNotEncrypted()
    {
        var bytes = Load("plaintext-baseline.pdf");
        Assert.True(bytes.Length > 1000);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("/Encrypt", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--cleartext-metadata</c> must leave the XMP packet readable in the raw bytes; the R6 fixture
    /// built without it must not. This is the read-side mirror of the writer defect in #182.
    /// </summary>
    [Fact]
    public void CleartextMetadataFixture_exposesXmp_whereTheR6FixtureDoesNot()
    {
        var clear = Encoding.Latin1.GetString(Load("enc-256-cleartextmd.pdf"));
        var opaque = Encoding.Latin1.GetString(Load("enc-aes-256-r6.pdf"));

        Assert.Contains("xpacket", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("xpacket", opaque, StringComparison.Ordinal);
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
