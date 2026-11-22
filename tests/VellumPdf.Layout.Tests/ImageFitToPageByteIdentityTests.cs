// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #471: a document that does not set <c>FitToPage</c> is saved as it was before the member
/// existed. Each hash below was taken from the build before the change, with the same code, and
/// is of the file with its streams inflated and its offsets blanked.
/// </summary>
public sealed class ImageFitToPageByteIdentityTests
{
    private static readonly TextStyle Helv = new() { Font = Standard14.Helvetica, FontSize = 10 };

    private static byte[] Bmp(int size)
    {
        var stride = (size * 3 + 3) / 4 * 4;
        var b = new byte[54 + stride * size];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BitConverter.GetBytes(b.Length).CopyTo(b, 2);
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(size).CopyTo(b, 18);
        BitConverter.GetBytes(size).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);
        BitConverter.GetBytes(stride * size).CopyTo(b, 34);
        return b;
    }

    private static byte[] Save(double pageW, double pageH, EdgeInsets margins, bool paragraph, double? width, double? height)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, pageW, pageH),
            Margins = margins,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        if (paragraph)
            doc.Add(new Paragraph("Hello", Helv));
        doc.Add(new LayoutImage(BmpImageLoader.Load(Bmp(2))) { Width = width, Height = height });
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("width 300", 900, 400, 0, false, 300.0, null, "8706DA165DB99CFE34903A11406C835E69E03850BCBCA6902ABD3ACA58D3FDBD")]
    [InlineData("width 400", 900, 400, 0, false, 400.0, null, "CFA5654FB8779DEA0997ABF24E402EBD24697D0D4749DBECB1D58C73C74F94DE")]
    [InlineData("height 300", 900, 400, 0, false, null, 300.0, "4F4569F9484A2CA3DED442A73865B78CE28147EC24A937B115BC6BD1234CFCCE")]
    [InlineData("width 401, height 300", 900, 400, 0, false, 401.0, 300.0, "9BF7F6821C5EA4934B71B0FBA6D2CC6AADE8787D1CA562134DB4A80B2B06B97D")]
    [InlineData("A4, no width", 595.28, 841.89, 72, false, null, null, "92258CF19571AA3C78E54E1D96C5CA2D874079EBC9C2D493B00E3D8AD31DB39F")]
    [InlineData("width 380 after a paragraph", 900, 400, 0, true, 380.0, null, "5814304C1866B32F91D0EC64CDED7CE06937026C4174522D8FBCA90A0150EE5D")]
    public void WithoutFitToPage_theFileIsAsItWasBeforeTheMemberExisted(
        string name, double pageW, double pageH, double margin, bool paragraph, double? width, double? height, string sha)
    {
        _ = name;

        var pdf = Save(pageW, pageH, new EdgeInsets(margin), paragraph, width, height);

        Assert.Equal(sha, NormalisedSha256(pdf));
    }
}
