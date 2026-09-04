// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// The <see cref="PdfReaderOptions"/> surface that replaced <c>PdfReader.Open</c>'s <c>string?</c>
/// password parameter (#184). What a password does is covered across the encryption suites; what is
/// new here is the options object itself: its argument guards, and evidence that <c>Password</c>
/// reaches the security handler rather than being accepted and dropped.
/// </summary>
public sealed class PdfReaderOptionsTests
{
    [Fact]
    public void Open_bytes_withNullOptions_throwsArgumentNullException()
    {
        var bytes = Load("plaintext-baseline.pdf");

        var ex = Assert.Throws<ArgumentNullException>(() => PdfReader.Open(bytes, null!));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Open_stream_withNullOptions_throwsArgumentNullException()
    {
        using var stream = new MemoryStream(Load("plaintext-baseline.pdf"));

        var ex = Assert.Throws<ArgumentNullException>(() => PdfReader.Open(stream, null!));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Open_withNullBytes_throwsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => PdfReader.Open((byte[])null!, new PdfReaderOptions()));

        Assert.Equal("bytes", ex.ParamName);
    }

    [Fact]
    public void Open_withNullStream_throwsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => PdfReader.Open((Stream)null!, new PdfReaderOptions()));

        Assert.Equal("stream", ex.ParamName);
    }

    [Fact]
    public void DefaultOptions_carryNoPassword()
    {
        Assert.Null(new PdfReaderOptions().Password);
    }

    [Fact]
    public void DefaultOptions_readTheSameDocumentAsTheOneArgumentOverload()
    {
        var bytes = Load("plaintext-baseline.pdf");

        using var viaOptions = PdfReader.Open(bytes, new PdfReaderOptions());
        using var viaShorthand = PdfReader.Open(bytes);

        Assert.Equal(
            viaShorthand.Catalog.Get(PdfName.Type)?.ToString(),
            viaOptions.Catalog.Get(PdfName.Type)?.ToString());
        Assert.Null(viaOptions.Encryption);
    }

    /// <summary>
    /// The assertion a no-op <c>Password</c> property would fail and every other test here would
    /// pass: one fixture, opening with the password and refusing without it. A property that is
    /// accepted and never forwarded still satisfies the guards and the unencrypted round-trip above.
    /// </summary>
    [Fact]
    public void Password_reachesTheSecurityHandler()
    {
        var bytes = Load("enc-aes-128.pdf");

        using var opened = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        Assert.NotNull(opened.Encryption);

        Assert.Throws<PdfPasswordException>(() => PdfReader.Open(bytes, new PdfReaderOptions()));
    }

    /// <summary>
    /// The regression this file exists to guard: <c>PdfReaderOptions</c> is a class rather than a
    /// record so <c>ToString</c> is not synthesised over <see cref="PdfReaderOptions.Password"/>.
    /// Reverting to a record trips the PublicAPI analyzer (RS0016) today because the surface is still
    /// in <c>PublicAPI.Unshipped.txt</c>, but that backstop weakens once #187 moves it to
    /// <c>Shipped.txt</c>: a record's synthesised members are not themselves new public symbols.
    /// </summary>
    [Fact]
    public void ToString_doesNotContainThePassword()
    {
        var options = new PdfReaderOptions { Password = "correct horse battery staple" };

        Assert.DoesNotContain("correct horse battery staple", options.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void MaxFormXObjectDepth_outsideRange_throwsArgumentOutOfRangeException(int value)
    {
        var bytes = Load("plaintext-baseline.pdf");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfReader.Open(bytes, new PdfReaderOptions { MaxFormXObjectDepth = value }));

        Assert.Equal(nameof(PdfReaderOptions.MaxFormXObjectDepth), ex.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void MaxFormXObjectDepth_atTheFloorOrCeiling_isAccepted(int value)
    {
        var bytes = Load("plaintext-baseline.pdf");

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { MaxFormXObjectDepth = value });

        Assert.NotNull(reader.Catalog);
    }

    [Fact]
    public void DefaultOptions_carryTheDefaultMaxFormXObjectDepth()
    {
        Assert.Equal(32, new PdfReaderOptions().MaxFormXObjectDepth);
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
