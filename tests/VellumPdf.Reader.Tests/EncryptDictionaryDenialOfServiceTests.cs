// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// #208: an <c>/Encrypt</c> dictionary is parsed, copied by
/// <c>EncryptionSetup.DereferenceValues</c>, and read from before <c>PdfReader.Open</c> checks any
/// password, on a file anyone can send. <see cref="VellumPdf.Kernel.Tests.PdfDictionaryIndexTests"/>
/// pins the fix inside <c>PdfDictionary</c> itself; this pins that fixing <c>PdfDictionary</c> alone
/// was enough, by reaching the same key count through the actual pre-authentication path a hostile
/// file would use. A fix that sped up <c>PdfDictionary</c> in isolation while leaving
/// <c>DereferenceValues</c>'s own copy quadratic would pass the kernel-level test and still time out
/// here.
///
/// Before this fix, opening an 80,000-key <c>/Encrypt</c> dictionary this way took ~26.9s on the
/// development machine (Release build), against this test's 10s budget.
/// </summary>
public sealed class EncryptDictionaryDenialOfServiceTests
{
    private static readonly byte[] Id0 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    // A genuine RC4 /Encrypt dictionary (/O, /U, /P derived from the empty owner password and user
    // password "u" against Id0), so opening it below exercises the real authentication path, not just
    // a thrown exception on the way there. The huge number of filler keys is appended before the
    // closing '>>' and does not touch /V, /R, /O, /U or /P, so authentication is unaffected by them.
    private const string Rc4EncryptDictPrefix =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 3 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /V 2";

    /// <summary>
    /// 80,000 filler keys in <c>/Encrypt</c>, opened with the correct user password. The document
    /// still has to authenticate and decrypt for this to prove anything: a fix that merely swallowed
    /// the slowdown behind an early exception would not show that the real path — dereference, crypt
    /// filter table, key derivation — got fast too.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void HugeEncryptDictionary_opensUnderTimeout()
    {
        var bytes = BuildDocumentWithHugeEncryptDict(fillerKeyCount: 80_000);

        using var reader = PdfReader.Open(bytes, "u");

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.IsOwnerAccess);
    }

    private static byte[] BuildDocumentWithHugeEncryptDict(int fillerKeyCount)
    {
        var filler = new StringBuilder(fillerKeyCount * 10);
        for (var i = 0; i < fillerKeyCount; i++)
            filler.Append(" /Junk").Append(i).Append(' ').Append(i);
        var encryptDict = Rc4EncryptDictPrefix + filler + " >>";

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");

        var offsets = new List<int>();
        string[] objBodies =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
        ];
        for (var i = 0; i < objBodies.Length; i++)
        {
            offsets.Add((int)ms.Position);
            W($"{i + 1} 0 obj\n{objBodies[i]}\nendobj\n");
        }

        var encryptObjectNumber = objBodies.Length + 1;
        offsets.Add((int)ms.Position);
        W($"{encryptObjectNumber} 0 obj\n{encryptDict}\nendobj\n");

        var xref = (int)ms.Position;
        W($"xref\n0 {encryptObjectNumber + 1}\n{0:D10} 65535 f \n");
        foreach (var offset in offsets)
            W($"{offset:D10} 00000 n \n");
        W($"trailer\n<< /Size {encryptObjectNumber + 1} /Root 1 0 R /Encrypt {encryptObjectNumber} 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
