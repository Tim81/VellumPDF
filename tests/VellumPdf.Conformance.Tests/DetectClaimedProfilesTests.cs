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

    // ── Damaged input: no reconstruction opt-in exists here ─────────────────────

    /// <summary>
    /// <c>DetectClaimedProfiles</c> takes only <c>byte[]</c>/<c>Stream</c> — see its own signature
    /// — so there is no <see cref="VellumPdf.Reader.PdfReaderOptions"/> overload to opt into
    /// <c>VellumPdf.Reader</c>'s cross-reference reconstruction (#184) through. This pins that a
    /// file whose only defect is a broken <c>startxref</c> still fails exactly as it did before
    /// #184, even though the reader underneath it now knows how to recover such a file when asked.
    /// The damage is built in-memory rather than loaded from
    /// <c>Fixtures/ThirdParty/broken-startxref.pdf</c>: that fixture lives in
    /// <c>VellumPdf.Reader.Tests</c>, which this project does not reference.
    /// </summary>
    [Fact]
    public void DetectClaimedProfiles_onABrokenStartxref_stillThrows_withNoReconstructionOptIn()
    {
        var bytes = AssemblePdf(_baseObjects, metadataBytes: null);
        var damaged = CorruptStartxrefOutOfRange(bytes);

        Assert.Throws<InvalidDataException>(() => PdfPreflight.DetectClaimedProfiles(damaged));
    }

    /// <summary>
    /// Rewrites the digits after the last <c>startxref</c> keyword to a same-length, out-of-range
    /// value — VellumPdf.Reader.Tests's own M1 damage mode for #184, reproduced here since it is
    /// not otherwise reachable from this project.
    /// </summary>
    private static byte[] CorruptStartxrefOutOfRange(byte[] original)
    {
        var keyword = "startxref"u8;
        var idx = -1;
        for (var i = original.Length - keyword.Length; i >= 0; i--)
        {
            if (original.AsSpan(i, keyword.Length).SequenceEqual(keyword))
            {
                idx = i;
                break;
            }
        }
        Assert.True(idx >= 0, "expected a 'startxref' keyword to corrupt");

        var pos = idx + keyword.Length;
        while (pos < original.Length && original[pos] is 0 or 9 or 10 or 12 or 13 or 32) pos++;
        var start = pos;
        while (pos < original.Length && original[pos] is >= (byte)'0' and <= (byte)'9') pos++;
        var length = pos - start;
        Assert.True(length > 0, "expected startxref to be followed by an offset");

        long maxForDigits = 1;
        for (var i = 0; i < length; i++) maxForDigits *= 10;
        maxForDigits -= 1;
        Assert.True(maxForDigits >= original.Length,
            $"a {length}-digit offset cannot be pushed out of range for a {original.Length}-byte file");

        var damaged = (byte[])original.Clone();
        for (var i = 0; i < length; i++)
            damaged[start + i] = (byte)'9';
        return damaged;
    }
}
