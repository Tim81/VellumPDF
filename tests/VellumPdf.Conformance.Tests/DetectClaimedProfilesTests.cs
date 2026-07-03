// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance;

namespace VellumPdf.Conformance.Tests;

public sealed class DetectClaimedProfilesTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record PdfObj(string Dict, byte[]? Stream = null);

    private static byte[] AssemblePdf(IReadOnlyList<PdfObj> objects, byte[]? metadataBytes = null)
    {
        var all = new List<PdfObj>(objects);

        if (metadataBytes is not null)
        {
            var metaObjNum = all.Count + 1;
            all.Add(new PdfObj("/Type /Metadata /Subtype /XML", metadataBytes));
            var dict0 = all[0].Dict;
            var insertAt = dict0.LastIndexOf(">>", StringComparison.Ordinal);
            if (insertAt >= 0)
                dict0 = string.Concat(dict0[..insertAt], $"/Metadata {metaObjNum} 0 R ", dict0[insertAt..]);
            all[0] = all[0] with { Dict = dict0 };
        }

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var offsets = new int[all.Count + 1];
        for (var i = 0; i < all.Count; i++)
        {
            offsets[i + 1] = (int)ms.Position;
            var n = i + 1;
            if (all[i].Stream is { } body)
            {
                W($"{n} 0 obj\n<< {all[i].Dict} /Length {body.Length} >>\nstream\n");
                ms.Write(body);
                W("\nendstream\nendobj\n");
            }
            else
            {
                W($"{n} 0 obj\n{all[i].Dict}\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        var size = all.Count + 1;
        W($"xref\n0 {size}\n");
        W($"{0:D10} 65535 f \n");
        for (var i = 1; i <= all.Count; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R " +
          "/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static readonly PdfObj[] _baseObjects =
    [
        new("<< /Type /Catalog /Pages 2 0 R >>"),
        new("<< /Type /Pages /Kids [] /Count 0 >>"),
    ];

    private static byte[] BuildPdfAXmp(string part, string conformance)
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + $"<pdfaid:part>{part}</pdfaid:part>"
            + $"<pdfaid:conformance>{conformance}</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    private static byte[] BuildPdfUaXmp()
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">"
            + "<pdfuaid:part>1</pdfuaid:part>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectClaim_PdfA2B_Returns2B()
    {
        var bytes = AssemblePdf(_baseObjects, BuildPdfAXmp("2", "B"));
        var result = PdfPreflight.DetectClaimedProfiles(bytes);
        Assert.Equal([PdfConformance.PdfA2B], result);
    }

    [Fact]
    public void DetectClaim_PdfA2U_Returns2U()
    {
        var bytes = AssemblePdf(_baseObjects, BuildPdfAXmp("2", "U"));
        var result = PdfPreflight.DetectClaimedProfiles(bytes);
        Assert.Equal([PdfConformance.PdfA2U], result);
    }

    [Fact]
    public void DetectClaim_PdfA2A_Returns2A()
    {
        var bytes = AssemblePdf(_baseObjects, BuildPdfAXmp("2", "A"));
        var result = PdfPreflight.DetectClaimedProfiles(bytes);
        Assert.Equal([PdfConformance.PdfA2A], result);
    }

    [Fact]
    public void DetectClaim_PdfUA1_ReturnsUA1()
    {
        var bytes = AssemblePdf(_baseObjects, BuildPdfUaXmp());
        var result = PdfPreflight.DetectClaimedProfiles(bytes);
        Assert.Equal([PdfConformance.PdfUA1], result);
    }

    [Fact]
    public void DetectClaim_NoClaim_ReturnsEmpty()
    {
        // No /Metadata stream at all.
        var bytes = AssemblePdf(_baseObjects, metadataBytes: null);
        var result = PdfPreflight.DetectClaimedProfiles(bytes);
        Assert.Empty(result);
    }

    [Fact]
    public void DetectClaim_StreamOverload_Works()
    {
        var bytes = AssemblePdf(_baseObjects, BuildPdfAXmp("2", "B"));

        var fromBytes = PdfPreflight.DetectClaimedProfiles(bytes);
        IReadOnlyList<PdfConformance> fromStream;
        using (var ms = new MemoryStream(bytes))
            fromStream = PdfPreflight.DetectClaimedProfiles(ms);

        Assert.Equal(fromBytes, fromStream);
    }
}
