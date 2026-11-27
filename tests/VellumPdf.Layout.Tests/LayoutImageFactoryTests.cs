// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <see cref="LayoutImage.FromBytes(byte[])"/>, <see cref="LayoutImage.FromFile(string)"/> and
/// <see cref="LayoutImage.FromStream(Stream)"/> load an image from its signature: each format
/// reaches the loader a caller would have chosen, with the same options, and what that loader
/// refuses is refused here with the same type and message.
/// </summary>
public sealed class LayoutImageFactoryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"vellum_imgfactory_{Guid.NewGuid():N}");

    public LayoutImageFactoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // ── What each format is read as ──────────────────────────────────────────────────────

    public sealed record Case(string Name, byte[] Data, Func<byte[], PdfImageXObject> Explicit)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<Case> Cases() =>
    [
        new("PNG", ImageFixtures.Png(), PngImageLoader.Load),
        new("JPEG", ImageFixtures.Jpeg(), JpegImageLoader.Load),
        new("TIFF little-endian", ImageFixtures.TiffGrey(true, 8), TiffImageLoader.Load),
        new("TIFF big-endian", ImageFixtures.TiffGrey(false, 8), TiffImageLoader.Load),
        new("BMP", ImageFixtures.Bmp(), BmpImageLoader.Load),
        new("GIF89a", ImageFixtures.Gif("GIF89a"), GifImageLoader.Load),
        new("GIF87a", ImageFixtures.Gif("GIF87a"), GifImageLoader.Load),
        new("JBIG2", ImageFixtures.Jbig2(), Jbig2ImageLoader.Load),
        new("JP2", ImageFixtures.Jp2(), JpxImageLoader.Load),
        new("JPEG 2000 codestream", ImageFixtures.JpxCodestream(), JpxImageLoader.Load),
    ];

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases())
            data.Add(c.Name);
        return data;
    }

    private static Case Get(string name) => Cases().Single(c => c.Name == name);

    private static byte[] Save(LayoutImage image)
    {
        using var doc = new Document
        {
            Timestamp = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        doc.Add(image);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private string Write(byte[] data)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".img");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryFormat_SavesToTheBytesTheExplicitLoaderGives(string name)
    {
        var c = Get(name);
        var expected = Save(new LayoutImage(c.Explicit(c.Data)));

        Assert.Equal(expected, Save(LayoutImage.FromBytes(c.Data)));
        Assert.Equal(expected, Save(LayoutImage.FromFile(Write(c.Data))));
        using var stream = new MemoryStream(c.Data);
        Assert.Equal(expected, Save(LayoutImage.FromStream(stream)));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryFormat_AcceptsTheDefaultOptions_AndSavesTheSameBytes(string name)
    {
        var c = Get(name);
        var expected = Save(new LayoutImage(c.Explicit(c.Data)));

        Assert.Equal(expected, Save(LayoutImage.FromBytes(c.Data, ImageLoadOptions.Default)));
        Assert.Equal(expected, Save(LayoutImage.FromBytes(c.Data, GifLoadOptions.Default)));
        Assert.Equal(expected, Save(LayoutImage.FromFile(Write(c.Data), ImageLoadOptions.Default)));
        Assert.Equal(expected, Save(LayoutImage.FromFile(Write(c.Data), GifLoadOptions.Default)));
        using var a = new MemoryStream(c.Data);
        Assert.Equal(expected, Save(LayoutImage.FromStream(a, ImageLoadOptions.Default)));
        using var b = new MemoryStream(c.Data);
        Assert.Equal(expected, Save(LayoutImage.FromStream(b, GifLoadOptions.Default)));
    }

    [Fact]
    public void Jpeg_IsPassedThroughAsDctDecode_NotReEncoded()
    {
        var jpeg = ImageFixtures.Jpeg(width: 40, height: 20);

        var image = LayoutImage.FromBytes(jpeg).Image;

        Assert.Equal(40, image.Width);
        Assert.Equal(20, image.Height);
        using var ms = new MemoryStream();
        image.BuildStream().WriteTo(new VellumPdf.IO.PdfWriter(ms));
        var written = ms.ToArray();
        var text = Encoding.Latin1.GetString(written);
        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FlateDecode", text, StringComparison.Ordinal);
        Assert.Contains($"/Length {jpeg.Length}", text, StringComparison.Ordinal);
        Assert.True(written.AsSpan().IndexOf(jpeg) >= 0, "the JPEG bytes are not in the stream verbatim");
    }

    // ── Options reach the loader that reads them ─────────────────────────────────────────

    [Fact]
    public void Png_BitDepthOption_ReachesTheLoader()
    {
        var png = ImageFixtures.Png16();
        var reduce = new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight };
        var viaFactory = Save(LayoutImage.FromBytes(png, reduce));

        Assert.Equal(Save(new LayoutImage(PngImageLoader.Load(png, reduce))), viaFactory);
        // The option must change the output, or the line above would hold for a factory that
        // dropped it.
        Assert.NotEqual(Save(new LayoutImage(PngImageLoader.Load(png))), viaFactory);
        Assert.Equal(Save(new LayoutImage(PngImageLoader.Load(png))), Save(LayoutImage.FromBytes(png)));
    }

    [Fact]
    public void Tiff_BitDepthOption_ReachesTheLoader()
    {
        var tiff = ImageFixtures.TiffGrey(true, 16);
        var reduce = new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight };
        var viaFactory = Save(LayoutImage.FromBytes(tiff, reduce));

        Assert.Equal(Save(new LayoutImage(TiffImageLoader.Load(tiff, reduce))), viaFactory);
        Assert.NotEqual(Save(new LayoutImage(TiffImageLoader.Load(tiff))), viaFactory);
    }

    [Fact]
    public void Gif_PlaceOnLogicalScreenOption_ReachesTheLoader()
    {
        var gif = ImageFixtures.Gif(screen: 3, left: 1, top: 1);
        var place = new GifLoadOptions { PlaceOnLogicalScreen = true };

        var placed = LayoutImage.FromBytes(gif, place);

        Assert.Equal(3, placed.Image.Width);
        Assert.Equal(3, placed.Image.Height);
        Assert.Equal(Save(new LayoutImage(GifImageLoader.Load(gif, place))), Save(placed));
        Assert.Equal(1, LayoutImage.FromBytes(gif).Image.Width);
        using var s = new MemoryStream(gif);
        Assert.Equal(3, LayoutImage.FromStream(s, place).Image.Width);
        Assert.Equal(3, LayoutImage.FromFile(Write(gif), place).Image.Width);
    }

    [Fact]
    public void Jbig2_DecodeModeOption_ReachesTheLoader()
    {
        var data = ImageFixtures.Jbig2();
        var raster = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        var viaFactory = Save(LayoutImage.FromBytes(data, raster));

        Assert.Equal(Save(new LayoutImage(Jbig2ImageLoader.Load(data, raster))), viaFactory);
        Assert.NotEqual(Save(new LayoutImage(Jbig2ImageLoader.Load(data))), viaFactory);
    }

    [Fact]
    public void Jpx_DecodeModeOption_ReachesTheLoader_WhichRefusesIt()
    {
        var raster = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        foreach (var data in new[] { ImageFixtures.Jp2(), ImageFixtures.JpxCodestream() })
        {
            var expected = Capture(() => JpxImageLoader.Load(data, raster));
            var actual = Capture(() => LayoutImage.FromBytes(data, raster));

            Assert.IsType<NotSupportedException>(expected);
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Message, actual.Message);
        }
    }

    // ── Options go to the loader as given; a loader with none is refused any ───────────────

    public static TheoryData<string, ImageLoadOptions> OptionsForLoadersThatTakeNone() => new()
    {
        { "JPEG", new() { BitDepth = ImageBitDepth.ReduceToEight } },
        { "JPEG", new() { DecodeMode = ImageDecodeMode.DecodeToRaster } },
        { "BMP", new() { BitDepth = ImageBitDepth.ReduceToEight } },
        { "BMP", new() { DecodeMode = ImageDecodeMode.DecodeToRaster } },
        { "GIF89a", new() { BitDepth = ImageBitDepth.ReduceToEight } },
        { "GIF89a", new() { DecodeMode = ImageDecodeMode.DecodeToRaster } },
    };

    [Theory]
    [MemberData(nameof(OptionsForLoadersThatTakeNone))]
    public void ImageLoadOptions_ForAFormatWhoseLoaderTakesNone_AreRefusedAtTheCall(
        string name, ImageLoadOptions options)
    {
        var data = Get(name).Data;
        var formatName = name.StartsWith("GIF", StringComparison.Ordinal) ? "GIF" : name;

        var ex = Assert.Throws<ArgumentException>(() => LayoutImage.FromBytes(data, options));

        Assert.Equal("options", ex.ParamName);
        Assert.StartsWith($"The data is {formatName}, whose loader takes no ImageLoadOptions. " +
            "Pass ImageLoadOptions.Default or no options.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("options", Assert.Throws<ArgumentException>(
            () => LayoutImage.FromFile(Write(data), options)).ParamName);
        using var s = new MemoryStream(data);
        Assert.Equal("options", Assert.Throws<ArgumentException>(
            () => LayoutImage.FromStream(s, options)).ParamName);
    }

    public static TheoryData<string> FormatsWhoseLoadersTakeImageLoadOptions() =>
        new() { "PNG", "TIFF little-endian", "JBIG2", "JP2", "JPEG 2000 codestream" };

    [Theory]
    [MemberData(nameof(FormatsWhoseLoadersTakeImageLoadOptions))]
    public void ImageLoadOptions_ForAFormatWhoseLoaderTakesThem_GoThroughWhateverTheMember(string name)
    {
        // Each loader decides what a member means for its input, a member it ignores included;
        // the factory refuses none of the four combinations, and the result is the loader's.
        var c = Get(name);
        foreach (var options in new[]
        {
            new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight },
            new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster },
        })
        {
            var expected = Outcome(() => Save(new LayoutImage(Invoke(c, options))));
            var actual = Outcome(() => Save(LayoutImage.FromBytes(c.Data, options)));

            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected is byte[] e ? e : null, actual is byte[] a ? a : null);
            Assert.Equal((expected as Exception)?.Message, (actual as Exception)?.Message);
        }
    }

    private static PdfImageXObject Invoke(Case c, ImageLoadOptions options) => c.Name switch
    {
        "PNG" => PngImageLoader.Load(c.Data, options),
        "JBIG2" => Jbig2ImageLoader.Load(c.Data, options),
        "JP2" or "JPEG 2000 codestream" => JpxImageLoader.Load(c.Data, options),
        _ => TiffImageLoader.Load(c.Data, options),
    };

    [Fact]
    public void ATiffStripTheLoaderPassesThrough_GivesTheExplicitBytes_WhateverTheOptionsAsk()
    {
        var raster = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        var reduce = new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight };
        foreach (var tiff in new[] { ImageFixtures.TiffGroup4(), ImageFixtures.TiffJpegStrip() })
        {
            foreach (var options in new[] { raster, reduce, ImageLoadOptions.Default })
            {
                Assert.Equal(
                    Save(new LayoutImage(TiffImageLoader.Load(tiff, options))),
                    Save(LayoutImage.FromBytes(tiff, options)));
            }
        }
        // The two fixtures keep their compressed filters, so the requests above were passed on and
        // not met.
        Assert.Contains("/CCITTFaxDecode", Encoding.Latin1.GetString(
            Save(LayoutImage.FromBytes(ImageFixtures.TiffGroup4(), raster))), StringComparison.Ordinal);
        Assert.Contains("/DCTDecode", Encoding.Latin1.GetString(
            Save(LayoutImage.FromBytes(ImageFixtures.TiffJpegStrip(), raster))), StringComparison.Ordinal);
    }

    public static TheoryData<string> AllFormats() => CaseNames();

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void AnUndefinedEnumValue_InAnyMember_IsRefusedAtTheCall_ForEveryFormat(string name)
    {
        var data = Get(name).Data;
        var badDepth = new ImageLoadOptions { BitDepth = (ImageBitDepth)7 };
        var badMode = new ImageLoadOptions { DecodeMode = (ImageDecodeMode)9 };

        var depth = Assert.Throws<ArgumentOutOfRangeException>(() => LayoutImage.FromBytes(data, badDepth));
        var mode = Assert.Throws<ArgumentOutOfRangeException>(() => LayoutImage.FromBytes(data, badMode));

        Assert.Equal("options", depth.ParamName);
        Assert.StartsWith("ImageLoadOptions.BitDepth is not a defined ImageBitDepth.", depth.Message,
            StringComparison.Ordinal);
        Assert.Equal(badDepth.BitDepth, depth.ActualValue);
        Assert.Equal("options", mode.ParamName);
        Assert.StartsWith("ImageLoadOptions.DecodeMode is not a defined ImageDecodeMode.", mode.Message,
            StringComparison.Ordinal);
        Assert.Equal(badMode.DecodeMode, mode.ActualValue);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayoutImage.FromFile(Write(data), badDepth));
        using var s = new MemoryStream(data);
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutImage.FromStream(s, badMode));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GifLoadOptions_ThatDifferFromTheDefault_ForAFormatThatIsNotGif_AreRefused(string name)
    {
        var data = Get(name).Data;
        var place = new GifLoadOptions { PlaceOnLogicalScreen = true };
        if (name.StartsWith("GIF", StringComparison.Ordinal))
        {
            Assert.NotNull(LayoutImage.FromBytes(data, place));
            return;
        }

        var ex = Assert.Throws<ArgumentException>(() => LayoutImage.FromBytes(data, place));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("whose loader takes no GifLoadOptions. Pass GifLoadOptions.Default or no options.",
            ex.Message, StringComparison.Ordinal);
        Assert.Equal("options", Assert.Throws<ArgumentException>(
            () => LayoutImage.FromFile(Write(data), place)).ParamName);
        using var s = new MemoryStream(data);
        Assert.Equal("options", Assert.Throws<ArgumentException>(
            () => LayoutImage.FromStream(s, place)).ParamName);
    }

    [Fact]
    public void ImageLoadOptions_OnAnUncompressedEightBitTiff_AreAccepted()
    {
        // The loader reads neither member for this file; the test pins only that the factory
        // does not refuse a TIFF for options.
        var tiff = ImageFixtures.TiffGrey(true, 8);

        Assert.NotNull(LayoutImage.FromBytes(tiff, new ImageLoadOptions
        {
            BitDepth = ImageBitDepth.ReduceToEight,
            DecodeMode = ImageDecodeMode.DecodeToRaster,
        }));
    }

    [Fact]
    public void AJp2WhoseSignatureBoxIsLongerThanTwelveAndHoldsItsBytes_IsRefusedHere_AlthoughTheLoaderReadsIt()
    {
        var jp2 = ImageFixtures.Jp2(signatureBoxLength: 0x0D);

        Assert.Equal(16, JpxImageLoader.Load(jp2).Width);
        var ex = Assert.Throws<InvalidDataException>(() => LayoutImage.FromBytes(jp2));
        Assert.StartsWith("The image data is not a format with a signature this library reads: " +
            "it begins 00 00 00 0D 6A 50 20 20.", ex.Message, StringComparison.Ordinal);
    }

    // ── Refusals at the call ──────────────────────────────────────────────────────────────

    [Fact]
    public void Null_IsRefusedByEveryFactory_WithTheParameterName()
    {
        var img = ImageLoadOptions.Default;
        var gif = GifLoadOptions.Default;
        using var s = new MemoryStream(ImageFixtures.Png());

        Assert.Equal("data", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromBytes(null!)).ParamName);
        Assert.Equal("data", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromBytes(null!, img)).ParamName);
        Assert.Equal("data", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromBytes(null!, gif)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => LayoutImage.FromBytes(ImageFixtures.Png(), (ImageLoadOptions)null!)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => LayoutImage.FromBytes(ImageFixtures.Png(), (GifLoadOptions)null!)).ParamName);
        Assert.Equal("data", Assert.Throws<ArgumentNullException>(
            () => LayoutImage.FromBytes(null!, (ImageLoadOptions)null!)).ParamName);

        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromFile(null!)).ParamName);
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromFile(null!, img)).ParamName);
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromFile(null!, gif)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => LayoutImage.FromFile("x", (ImageLoadOptions)null!)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => LayoutImage.FromFile("x", (GifLoadOptions)null!)).ParamName);

        Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromStream(null!)).ParamName);
        Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromStream(null!, img)).ParamName);
        Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => LayoutImage.FromStream(null!, gif)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => LayoutImage.FromStream(s, (ImageLoadOptions)null!)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(
            () => LayoutImage.FromStream(s, (GifLoadOptions)null!)).ParamName);
        Assert.Equal(0, s.Position);
    }

    [Fact]
    public void Empty_IsRefusedByEveryFactory_AsAnArgumentException()
    {
        var empty = Write([]);
        using var s = new MemoryStream();

        var bytes = Assert.Throws<ArgumentException>(() => LayoutImage.FromBytes([]));
        Assert.Equal("data", bytes.ParamName);
        Assert.StartsWith("The image data is empty.", bytes.Message, StringComparison.Ordinal);
        Assert.Equal("data", Assert.Throws<ArgumentException>(() => LayoutImage.FromBytes([], ImageLoadOptions.Default)).ParamName);
        Assert.Equal("data", Assert.Throws<ArgumentException>(() => LayoutImage.FromBytes([], GifLoadOptions.Default)).ParamName);

        var file = Assert.Throws<ArgumentException>(() => LayoutImage.FromFile(empty));
        Assert.Equal("path", file.ParamName);
        Assert.StartsWith("The file has no bytes to read.", file.Message, StringComparison.Ordinal);

        var stream = Assert.Throws<ArgumentException>(() => LayoutImage.FromStream(s));
        Assert.Equal("stream", stream.ParamName);
        Assert.StartsWith("The stream has no bytes to read.", stream.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSignature_IsRefused_NamingTheFirstEightBytesInHex()
    {
        byte[] data = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];

        var ex = Assert.Throws<InvalidDataException>(() => LayoutImage.FromBytes(data));

        Assert.Equal(
            "The image data is not a format with a signature this library reads: it begins " +
            "00 01 02 03 04 05 06 07. Recognised are PNG, JPEG, TIFF, BMP, GIF, JBIG2 with its " +
            "file header and JPEG 2000.",
            ex.Message);
        Assert.Equal(ex.Message, Assert.Throws<InvalidDataException>(() => LayoutImage.FromFile(Write(data))).Message);
        using var s = new MemoryStream(data);
        Assert.Equal(ex.Message, Assert.Throws<InvalidDataException>(() => LayoutImage.FromStream(s)).Message);
    }

    [Fact]
    public void UnknownSignature_ShorterThanEightBytes_NamesOnlyWhatIsThere()
    {
        var ex = Assert.Throws<InvalidDataException>(() => LayoutImage.FromBytes([0xDE, 0xAD, 0xBE]));

        Assert.Contains("it begins DE AD BE.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSignature_IsRefusedBeforeAnUndefinedEnumValueIsLookedAt()
    {
        var ex = Assert.Throws<InvalidDataException>(() => LayoutImage.FromBytes(
            [1, 2, 3], new ImageLoadOptions { BitDepth = (ImageBitDepth)7 }));

        Assert.Contains("it begins 01 02 03.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSignature_IsRefusedBeforeTheOptionsAreLookedAt()
    {
        byte[] data = [0x01, 0x02, 0x03];

        Assert.Throws<InvalidDataException>(() => LayoutImage.FromBytes(
            data, new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight }));
        Assert.Throws<InvalidDataException>(() => LayoutImage.FromBytes(
            data, new GifLoadOptions { PlaceOnLogicalScreen = true }));
    }

    public static TheoryData<string, byte[], string> TruncatedSignatures() => new()
    {
        { "PNG", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A], "its 7 byte(s), 89 50 4E 47 0D 0A 1A, begin the signature of PNG" },
        { "JPEG or JPEG 2000", [0xFF], "its 1 byte(s), FF, begin the signature of JPEG or JPEG 2000" },
        { "TIFF II", [0x49, 0x49, 0x2A], "its 3 byte(s), 49 49 2A, begin the signature of TIFF" },
        { "TIFF MM", [0x4D, 0x4D, 0x00], "its 3 byte(s), 4D 4D 00, begin the signature of TIFF" },
        { "BMP", [0x42], "its 1 byte(s), 42, begin the signature of BMP" },
        { "GIF", [0x47, 0x49, 0x46, 0x38], "its 4 byte(s), 47 49 46 38, begin the signature of GIF" },
        { "JBIG2", [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A], "its 7 byte(s), 97 4A 42 32 0D 0A 1A, begin the signature of JBIG2" },
        { "JPEG 2000 box", [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87], "begin the signature of JPEG 2000" },
    };

    [Theory]
    [MemberData(nameof(TruncatedSignatures))]
    public void TruncatedSignature_IsRefused_SayingTheDataEndsInsideOne(string name, byte[] data, string fragment)
    {
        _ = name;

        var ex = Assert.Throws<InvalidDataException>(() => LayoutImage.FromBytes(data));

        Assert.StartsWith("The image data ends inside a signature: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(", which is longer.", ex.Message, StringComparison.Ordinal);
    }

    // ── What a loader refuses is refused here, unchanged ─────────────────────────────────

    public static TheoryData<string, byte[]> MalformedByFormat() => new()
    {
        { "PNG", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] },
        { "JPEG", [0xFF, 0xD8] },
        { "TIFF", [0x49, 0x49, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { "BMP", [0x42, 0x4D] },
        { "GIF", "GIF89a"u8.ToArray() },
        { "JBIG2", [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A] },
        { "JP2", [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A] },
        { "JPEG 2000 codestream", [0xFF, 0x4F] },
    };

    [Theory]
    [MemberData(nameof(MalformedByFormat))]
    public void ALoaderRefusal_ReachesTheCaller_WithTheSameTypeAndMessage(string name, byte[] data)
    {
        Func<byte[], PdfImageXObject> explicitLoader = name switch
        {
            "PNG" => PngImageLoader.Load,
            "JPEG" => JpegImageLoader.Load,
            "TIFF" => TiffImageLoader.Load,
            "BMP" => BmpImageLoader.Load,
            "GIF" => GifImageLoader.Load,
            "JBIG2" => Jbig2ImageLoader.Load,
            _ => JpxImageLoader.Load,
        };
        var expected = Capture(() => explicitLoader(data));

        var viaBytes = Capture(() => LayoutImage.FromBytes(data));
        var viaFile = Capture(() => LayoutImage.FromFile(Write(data)));
        using var s = new MemoryStream(data);
        var viaStream = Capture(() => LayoutImage.FromStream(s));

        Assert.IsType<InvalidDataException>(expected);
        foreach (var actual in new[] { viaBytes, viaFile, viaStream })
        {
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Message, actual.Message);
        }
    }

    [Fact]
    public void ANotSupportedVariant_KeepsItsType()
    {
        // 8-bit BMP with a 108-byte header: a variant BmpImageLoader refuses with NotSupportedException.
        var bmp = ImageFixtures.Bmp();
        BitConverter.GetBytes(108).CopyTo(bmp, 14);
        var expected = Capture(() => BmpImageLoader.Load(bmp));

        var actual = Capture(() => LayoutImage.FromBytes(bmp));

        Assert.IsType<NotSupportedException>(expected);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
    }

    private static object Outcome(Func<byte[]> action)
    {
        try { return action(); }
        catch (Exception e) { return e; }
    }

    private static Exception Capture(Action action)
    {
        try { action(); }
        catch (Exception e) { return e; }
        throw new Xunit.Sdk.XunitException("The call did not throw.");
    }

    // ── Streams ───────────────────────────────────────────────────────────────────────────

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        // One byte at a time, to show the reader does not depend on a single read filling it.
        public override int Read(byte[] buffer, int offset, int count) =>
            count == 0 ? 0 : _inner.Read(buffer, offset, 1);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ANonSeekableStream_LoadsToTheSameBytes(string name)
    {
        var c = Get(name);
        using var s = new NonSeekableStream(c.Data);

        var image = LayoutImage.FromStream(s);

        Assert.Equal(Save(new LayoutImage(c.Explicit(c.Data))), Save(image));
    }

    [Fact]
    public void AStreamNotAtItsStart_IsReadFromWhereItIs()
    {
        var png = ImageFixtures.Png();
        byte[] junk = [0xDE, 0xAD, 0xBE, 0xEF, 0x00];
        using var seekable = new MemoryStream([.. junk, .. png]) { Position = junk.Length };

        var image = LayoutImage.FromStream(seekable);

        Assert.Equal(Save(new LayoutImage(PngImageLoader.Load(png))), Save(image));
        Assert.Equal(seekable.Length, seekable.Position);

        // The bytes before the position take no part in the signature: the same stream rewound
        // reads the junk first and is refused.
        seekable.Position = 0;
        Assert.Contains("it begins DE AD BE EF 00 89 50 4E.",
            Assert.Throws<InvalidDataException>(() => LayoutImage.FromStream(seekable)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANonSeekableStreamAlreadyPartlyRead_IsReadFromWhereItIs()
    {
        var png = ImageFixtures.Png();
        using var s = new NonSeekableStream([0x01, .. png]);
        Assert.Equal(0x01, s.ReadByte());

        Assert.Equal(Save(new LayoutImage(PngImageLoader.Load(png))), Save(LayoutImage.FromStream(s)));
    }

    [Fact]
    public void TheStream_IsNotDisposedOrClosed_AndIsLeftAtItsEnd()
    {
        using var ms = new MemoryStream(ImageFixtures.Png());
        using var ns = new NonSeekableStream(ImageFixtures.Png());

        _ = LayoutImage.FromStream(ms);
        _ = LayoutImage.FromStream(ns);

        Assert.True(ms.CanRead);
        Assert.Equal(ms.Length, ms.Position);
        Assert.False(ns.Disposed);
        Assert.True(ns.CanRead);
        Assert.Equal(-1, ns.ReadByte());
        // And one refused as unknown is not closed either.
        using var unknown = new MemoryStream([1, 2, 3, 4]);
        Assert.Throws<InvalidDataException>(() => LayoutImage.FromStream(unknown));
        Assert.True(unknown.CanRead);
    }

    [Fact]
    public void AStreamThatCannotBeRead_IsRefused()
    {
        using var writeOnly = new WriteOnlyStream();

        var ex = Assert.Throws<ArgumentException>(() => LayoutImage.FromStream(writeOnly));

        Assert.Equal("stream", ex.ParamName);
        Assert.StartsWith("The stream cannot be read.", ex.Message, StringComparison.Ordinal);
    }

    private sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    [Fact]
    public void ADisposedStream_IsRefusedAsOneThatCannotBeRead()
    {
        var ms = new MemoryStream(ImageFixtures.Png());
        ms.Dispose();

        var ex = Assert.Throws<ArgumentException>(() => LayoutImage.FromStream(ms));

        Assert.Equal("stream", ex.ParamName);
    }

    // ── The bound ─────────────────────────────────────────────────────────────────────────

    private sealed class EndlessStream : Stream
    {
        public long Produced { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            Produced += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ClaimsLengthStream(long length) : Stream
    {
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void TheBound_IsTwoHundredFiftySixMebibytes()
    {
        Assert.Equal(268_435_456L, ImageSource.MaxBytes);
    }

    [Fact]
    public void ASeekableStreamClaimingMoreThanTheBound_IsRefusedBeforeAnythingIsRead()
    {
        var s = new ClaimsLengthStream(ImageSource.MaxBytes + 1);

        var ex = Assert.Throws<InvalidDataException>(() => LayoutImage.FromStream(s));

        Assert.Equal(
            "The stream holds more than 268435456 bytes, which is the most this method reads. " +
            "Read the bytes yourself and pass them to the byte-array overload.",
            ex.Message);
        Assert.Equal(0, s.Reads);
        Assert.Equal(0, s.Position);
    }

    [Fact]
    public void TheBoundIsCountedFromThePosition_NotFromTheStart()
    {
        // 110 bytes long and 10 in: exactly the bound of 100 remains, which passes the size check,
        // and the stream then yields nothing. One more byte long is over.
        var atBound = new ClaimsLengthStream(110) { Position = 10 };
        var over = new ClaimsLengthStream(111) { Position = 10 };

        Assert.StartsWith("The stream has no bytes to read.",
            Assert.Throws<ArgumentException>(
                () => ImageSource.ReadAll(atBound, 100, "stream", "stream")).Message,
            StringComparison.Ordinal);
        Assert.StartsWith("The stream holds more than 100 bytes",
            Assert.Throws<InvalidDataException>(
                () => ImageSource.ReadAll(over, 100, "stream", "stream")).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(81_920L)]
    [InlineData(300_000L)]
    public void AnEndlessStream_IsRefusedOnceItPassesTheBound_AndIsNotReadFarPastIt(long bound)
    {
        var s = new EndlessStream();

        var ex = Assert.Throws<InvalidDataException>(
            () => ImageSource.ReadAll(s, bound, "stream", "stream"));

        Assert.Equal(
            $"The stream holds more than {bound} bytes, which is the most this method reads. " +
            "Read the bytes yourself and pass them to the byte-array overload.",
            ex.Message);
        // The reader asks for no more than one byte past the bound, whatever its chunk sizes.
        Assert.Equal(bound + 1, s.Produced);
    }

    // Non-zero, position-dependent bytes: a read that reorders, drops or overwrites a chunk
    // changes them, where an array of zeros reassembles to zeros in any order.
    internal static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 31 + (i >> 8) + 1);
        return data;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(81_920)]
    [InlineData(81_921)]
    [InlineData(300_000)]
    public void AStreamOfExactlyTheBound_IsRead_AndOneByteMoreIsRefused(int bound)
    {
        var exact = Pattern(bound);

        Assert.Equal(exact, ImageSource.ReadAll(new NonSeekableStream(exact), bound, "stream", "stream"));
        Assert.Equal(exact, ImageSource.ReadAll(new MemoryStream(exact), bound, "stream", "stream"));
        Assert.Equal(exact, ImageSource.ReadAll(new ShortReadStream(exact, false, 1), bound, "stream", "stream"));
        Assert.Throws<InvalidDataException>(
            () => ImageSource.ReadAll(new NonSeekableStream(Pattern(bound + 1)), bound, "stream", "stream"));
        Assert.Throws<InvalidDataException>(
            () => ImageSource.ReadAll(new MemoryStream(Pattern(bound + 1)), bound, "stream", "stream"));
    }

    // Returns between 1 and 70,000 bytes per call, from a seeded sequence.
    private sealed class ShortReadStream(byte[] data, bool seekable, int seed) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        private readonly Random _random = new(seed);
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? _inner.Length : throw new NotSupportedException();
        public override long Position
        {
            get => seekable ? _inner.Position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            count == 0 ? 0 : _inner.Read(buffer, offset, Math.Min(count, _random.Next(1, 70_001)));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The chunks grow 64 KiB, 128 KiB, 256 KiB, 512 KiB, 1 MiB, 2 MiB, then 4 MiB each, so the
    // sums below are where one chunk ends and the next begins.
    public static TheoryData<int> ChunkSteps()
    {
        var data = new TheoryData<int>();
        foreach (var step in new[] { 65_536, 196_608, 458_752, 983_040, 2_031_616, 4_128_768, 8_323_072 })
            foreach (var delta in new[] { -1, 0, 1 })
                data.Add(step + delta);
        return data;
    }

    [Theory]
    [MemberData(nameof(ChunkSteps))]
    public void AStreamOfASizeNearEveryChunkStep_ReassemblesExactly_WhateverTheReadSizes(int size)
    {
        var expected = Pattern(size);

        Assert.Equal(expected, ImageSource.ReadAll(
            new ShortReadStream(expected, false, size), ImageSource.MaxBytes, "stream", "stream"));
        Assert.Equal(expected, ImageSource.ReadAll(
            new ShortReadStream(expected, true, size + 1), ImageSource.MaxBytes, "stream", "stream"));
        Assert.Equal(expected, ImageSource.ReadAll(
            new MemoryStream(expected), ImageSource.MaxBytes, "stream", "stream"));
    }

    [Theory]
    [InlineData(300_000)]
    [InlineData(2_500_000)]
    public void AnImageLargerThanTheFirstChunk_LoadsToTheExplicitBytes_ThroughEveryReader(int extra)
    {
        var png = ImageFixtures.PngWithTextChunk(extra);
        Assert.True(png.Length > 65_536);
        var expected = Save(new LayoutImage(PngImageLoader.Load(png)));

        Assert.Equal(expected, Save(LayoutImage.FromBytes(png)));
        Assert.Equal(expected, Save(LayoutImage.FromStream(new MemoryStream(png))));
        Assert.Equal(expected, Save(LayoutImage.FromStream(new ShortReadStream(png, false, 7))));
        Assert.Equal(expected, Save(LayoutImage.FromStream(new ShortReadStream(png, true, 8))));
        Assert.Equal(expected, Save(LayoutImage.FromFile(Write(png))));
    }

    [Fact]
    public void AFileOverTheBound_IsRefusedWithTheFileNamedInTheMessage()
    {
        var path = Write(new byte[11]);

        var ex = Assert.Throws<InvalidDataException>(() => ImageSource.ReadFile(path, 10));

        Assert.StartsWith("The file holds more than 10 bytes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[10], ImageSource.ReadFile(Write(new byte[10]), 10));
    }

    // A seekable stream that reports a Length and then serves its own bytes, whatever the Length.
    private sealed class LyingLengthStream(long claimed, byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => claimed;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ALengthThatClaimsTheBound_ForAFewBytes_CostsTheBytes_NotTheBound()
    {
        var png = ImageFixtures.Png();
        var s = new LyingLengthStream(ImageSource.MaxBytes, png);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var all = ImageSource.ReadAll(s, ImageSource.MaxBytes, "stream", "stream");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(png, all);
        Assert.True(allocated < 512 * 1024, $"allocated {allocated} bytes for {png.Length}");
    }

    [Fact]
    public void ALengthThatClaimsZero_ForMoreThanTheBound_IsRefusedAfterReadingOneBytePast()
    {
        var s = new LyingLengthStream(0, new byte[101]) { Position = 0 };

        Assert.Throws<InvalidDataException>(() => ImageSource.ReadAll(s, 100, "stream", "stream"));

        Assert.Equal(101, s.Position);
    }

    [Fact]
    public void ThePositionAfterEachOutcome_IsTheOneTheDocsName()
    {
        var png = ImageFixtures.Png();

        // success: the end
        var ok = new MemoryStream(png);
        _ = LayoutImage.FromStream(ok);
        Assert.Equal(png.Length, ok.Position);

        // Length over the bound: unchanged, nothing read
        var over = new ClaimsLengthStream(ImageSource.MaxBytes + 6) { Position = 5 };
        Assert.Throws<InvalidDataException>(() => LayoutImage.FromStream(over));
        Assert.Equal(5, over.Position);

        // nothing left: unchanged
        var spent = new MemoryStream(png) { Position = png.Length };
        Assert.Throws<ArgumentException>(() => LayoutImage.FromStream(spent));
        Assert.Equal(png.Length, spent.Position);

        // bound found by reading: one byte past the bound from where the call began
        var found = new LyingLengthStream(0, new byte[300]) { Position = 10 };
        Assert.Throws<InvalidDataException>(() => ImageSource.ReadAll(found, 100, "stream", "stream"));
        Assert.Equal(111, found.Position);

        // a refusal after the read (unknown signature, options, loader): the end
        var unknown = new MemoryStream([1, 2, 3, 4, 5]);
        Assert.Throws<InvalidDataException>(() => LayoutImage.FromStream(unknown));
        Assert.Equal(5, unknown.Position);
        var badOptions = new MemoryStream(ImageFixtures.Bmp());
        Assert.Throws<ArgumentException>(() => LayoutImage.FromStream(
            badOptions, new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight }));
        Assert.Equal(badOptions.Length, badOptions.Position);
        var malformed = new MemoryStream([0xFF, 0xD8]);
        Assert.Throws<InvalidDataException>(() => LayoutImage.FromStream(malformed));
        Assert.Equal(2, malformed.Position);
    }

    private sealed class ThrowingStream(Exception toThrow, bool seekable) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? throw toThrow : throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set { }
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw toThrow;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    [Fact]
    public void AnExceptionTheStreamItselfThrows_PropagatesUnchanged()
    {
        var disposed = new ObjectDisposedException("stream");
        var unsupported = new NotSupportedException("no length");
        var io = new IOException("read failed");

        Assert.Same(disposed, Assert.Throws<ObjectDisposedException>(
            () => LayoutImage.FromStream(new ThrowingStream(disposed, seekable: false))));
        Assert.Same(unsupported, Assert.Throws<NotSupportedException>(
            () => LayoutImage.FromStream(new ThrowingStream(unsupported, seekable: true))));
        Assert.Same(io, Assert.Throws<IOException>(
            () => LayoutImage.FromStream(new ThrowingStream(io, seekable: false))));
    }

    // ── Files ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMissingFile_ThrowsFileNotFound_AndAMissingDirectory_DirectoryNotFound()
    {
        Assert.Throws<FileNotFoundException>(
            () => LayoutImage.FromFile(Path.Combine(_dir, "absent.png")));
        Assert.Throws<DirectoryNotFoundException>(
            () => LayoutImage.FromFile(Path.Combine(_dir, "absent", "x.png")));
    }

    [Fact]
    public void AnEmptyPath_IsRefused_AsAnArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => LayoutImage.FromFile(""));

        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void AFileAnotherReaderHasOpen_IsStillRead()
    {
        var path = Write(ImageFixtures.Png());
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(2, LayoutImage.FromFile(path).Image.Width);
    }

    [Fact]
    public void AFileOpenedWithNoSharingElsewhere_ThrowsIOException()
    {
        var path = Write(ImageFixtures.Png());
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => LayoutImage.FromFile(path));
    }

    [Fact]
    public void TheFile_IsClosedAfterTheCall_EvenWhenTheLoaderRefusesIt()
    {
        var good = Write(ImageFixtures.Png());
        var bad = Write([1, 2, 3, 4]);

        _ = LayoutImage.FromFile(good);
        Assert.Throws<InvalidDataException>(() => LayoutImage.FromFile(bad));

        // Opening for exclusive write fails if either handle were still held.
        using (new FileStream(good, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(good);
        File.Delete(bad);
    }

    [Fact]
    public void TheFormat_IsReadFromTheBytes_NotTheExtension()
    {
        var path = Path.Combine(_dir, "picture.jpg");
        File.WriteAllBytes(path, ImageFixtures.Png());

        Assert.Equal(2, LayoutImage.FromFile(path).Image.Width);
    }

    // ── What the new element carries ──────────────────────────────────────────────────────

    [Fact]
    public void TheElementHasTheDefaults_AndTheLoadedImage()
    {
        var image = LayoutImage.FromBytes(ImageFixtures.Bmp());

        Assert.Null(image.Width);
        Assert.Null(image.Height);
        Assert.False(image.FitToPage);
        Assert.Equal(HorizontalAlignment.Left, image.Alignment);
        Assert.Equal(1, image.Image.Width);
        Assert.Equal(1, image.Image.Height);
    }

    [Fact]
    public void FromBytes_LeavesTheCallersArrayAlone()
    {
        var png = ImageFixtures.Png();
        var copy = (byte[])png.Clone();

        _ = LayoutImage.FromBytes(png);

        Assert.Equal(copy, png);
    }
}
