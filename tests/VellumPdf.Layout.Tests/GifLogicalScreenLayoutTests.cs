// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Images;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A GIF whose frame is much smaller than its logical screen loads at the frame's own size by
/// default, as on 2.3.3, so a Layout document that places it keeps its size and saves. Placing the
/// frame on the screen is opt-in (<see cref="GifLoadOptions.PlaceOnLogicalScreen"/>) and is what
/// changes the image's size, so it is the caller's choice.
/// </summary>
public sealed class GifLogicalScreenLayoutTests
{
    /// <summary>A 40x20 frame on a 40x2000 screen: the file the review used to show the placement growing a Layout image.</summary>
    private static byte[] FrameOnATallScreen()
    {
        var rgb = new byte[40 * 20 * 3];
        for (var i = 0; i < 40 * 20; i++)
        {
            rgb[i * 3] = (byte)(i * 7);
            rgb[i * 3 + 1] = (byte)(i * 3);
            rgb[i * 3 + 2] = (byte)i;
        }

        var gif = GifEncoder.Encode(rgb, 40, 20);
        gif[8] = (byte)(2000 & 0xFF);
        gif[9] = (byte)(2000 >> 8);
        return gif;
    }

    private static string Save(PdfImageXObject image, double? width)
    {
        using var doc = new Document();
        doc.Add(new LayoutImage(image) { Width = width });
        using var ms = new MemoryStream();
        doc.Save(ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(100.0)]
    public void DefaultLoad_frameOnATallScreen_savesAtTheFramesOwnSize(double? width)
    {
        var image = GifImageLoader.Load(FrameOnATallScreen());

        var pdf = Save(image, width);

        Assert.Equal((40, 20), (image.Width, image.Height));
        Assert.Contains("/Width 40", pdf);
        Assert.Contains("/Height 20", pdf);
        Assert.DoesNotContain("/Height 2000", pdf);
        Assert.Contains("/Count 1", pdf);
    }

    /// <summary>
    /// The option is what changes the image's size, so it is what changes whether Layout can place
    /// it: a 40x2000 image is taller than a page, and Layout refuses an element too tall for one.
    /// This is the consequence a caller opts into, pinned so the documentation's warning has a
    /// measurement behind it: a 40 x 2,000 screen, with and without an explicit width.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(100.0)]
    public void PlacedLoad_frameOnATallScreen_isTheScreensSize_andLayoutRefusesItAsTooTall(double? width)
    {
        var image = GifImageLoader.Load(FrameOnATallScreen(), new GifLoadOptions { PlaceOnLogicalScreen = true });

        Assert.Equal((40, 2000), (image.Width, image.Height));
        var ex = Assert.Throws<InvalidOperationException>(() => Save(image, width));
        Assert.Contains("too tall to fit on a single page", ex.Message);
    }
}
