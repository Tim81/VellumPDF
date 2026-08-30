// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Hand-built encrypted documents shared across the reader test suite. The one PR3
/// (#184's cross-reference reconstruction of an encrypted document) needs is
/// <see cref="BuildCatalogInObjectStream"/> — the catalog-inside-an-<c>/ObjStm</c> shape, the layout
/// every modern producer emits. No committed fixture can carry it: qpdf 12.3.2 measurably pulls the
/// catalog out of every object stream it writes whenever it also encrypts (checked across
/// RC4-128 with preserve, AES-256 with <c>--object-streams=generate</c>, and re-packing an
/// already-encrypted file — all three emit a top-level catalog), and this library's own writer
/// refuses <c>UseObjectStreams</c> together with <c>Encrypt</c>. See
/// Fixtures/Encrypted/README.md, "Known gaps", for both halves of that finding.
///
/// <para>
/// Extracted out of <see cref="EncryptedExemptionTests"/>, which built this document first (to prove
/// the reader can reach <c>/Root</c> before the catalog exists, for
/// <c>IsDocumentMetadataStream</c>'s pre-catalog guard) and still calls this copy. #184's own use is
/// the same document under damage: the only route to the catalog is Phase B's object-stream
/// decryption, so recovering it at all is a value-level proof that reconstruction decrypts before
/// electing a catalog candidate, not just before returning one.
/// </para>
/// </summary>
internal static class HandBuiltEncryptedDocuments
{
    // Copied verbatim out of enc-rc4-128.pdf so every document built here authenticates under the
    // same file encryption key: /O, /U, /P, /R and the trailer /ID are all inputs to Algorithm 2.
    internal static readonly byte[] Id0 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    internal const string Rc4EncryptDict =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 3 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /V 2 >>";

    // Object 1 is an /ObjStm holding the catalog (object 2) and the page tree (object 3); object 4 is
    // the cross-reference stream; object 5 is an ordinary encrypted object outside the container.
    //
    // The two generation parameters exist to build a container whose own `N G obj` header disagrees
    // with what the cross-reference stream says about it, in both directions. The body is always
    // encrypted under the identity the reader is supposed to ARRIVE at, so a reader that picks the
    // other one decrypts to noise and cannot parse the members at all.
    internal static byte[] BuildCatalogInObjectStream(
        int containerHeaderGeneration = 0,
        long containerXrefGeneration = 0)
    {
        // The row's generation where the row can express one, the object header's where it cannot:
        // field 3 is three bytes wide below, so anything above 65535 does not fit and XrefParser
        // records it as unknown rather than guessing.
        var effectiveGeneration = containerXrefGeneration is >= 0 and <= 65535
            ? (int)containerXrefGeneration
            : containerHeaderGeneration;

        var members = "<< /Type /Catalog /Pages 3 0 R >> << /Type /Pages /Kids [] /Count 0 >>";
        var header = "2 0 3 34 ";
        var objStmBody = Encrypt(1, effectiveGeneration, Encoding.Latin1.GetBytes(header + members));

        var probe = Encrypt(5, 0, "OBJSTM-CATALOG"u8.ToArray());

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.5\n");

        var o1 = (int)ms.Position;
        W($"1 {containerHeaderGeneration} obj\n<< /Type /ObjStm /N 2 /First {header.Length} "
          + $"/Length {objStmBody.Length} >>\nstream\n");
        ms.Write(objStmBody);
        W("\nendstream\nendobj\n");

        var o6 = (int)ms.Position;
        W($"6 0 obj\n{Rc4EncryptDict}\nendobj\n");

        var o5 = (int)ms.Position;
        W($"5 0 obj\n<< /Probe <{Convert.ToHexStringLower(probe)}> >>\nendobj\n");

        var rows = new List<byte>();
        // /W [1 4 3]: field 3 is three bytes, which is what lets a row carry a generation ABOVE the
        // 65535 the format can represent — the shape XrefParser records as unknown.
        void Row(byte type, int field2, long field3) => rows.AddRange(
        [
            type,
            (byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2,
            (byte)(field3 >> 16), (byte)(field3 >> 8), (byte)field3,
        ]);

        var xrefOffset = (int)ms.Position;
        Row(0, 0, 65535);          // 0: free
        Row(1, o1, containerXrefGeneration);   // 1: the object stream
        Row(2, 1, 0);              // 2: catalog, member 0 of object 1
        Row(2, 1, 1);              // 3: page tree, member 1 of object 1
        Row(1, xrefOffset, 0);     // 4: this cross-reference stream
        Row(1, o5, 0);             // 5: the probe object
        Row(1, o6, 0);             // 6: the /Encrypt dictionary

        var rowBytes = rows.ToArray();
        W($"4 0 obj\n<< /Type /XRef /Size 7 /W [1 4 3] /Root 2 0 R /Encrypt 6 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] /Length {rowBytes.Length} >>\n"
          + "stream\n");
        ms.Write(rowBytes);
        W("\nendstream\nendobj\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    // RC4 is symmetric, so the reader's own decrypt path doubles as the encryptor these documents
    // need: open the fixture whose /Encrypt dictionary they copy, take its armed decryptor and file
    // key, and run the plaintext through it. Producing the ciphertext any other way would mean a
    // second, hand-rolled RC4 in the test project — a copy of the thing under test.
    private static byte[] Encrypt(int objectNumber, int generation, byte[] plaintext)
    {
        using var reader = PdfReader.Open(Load("enc-rc4-128.pdf"), new PdfReaderOptions { Password = "u" });
        var type = typeof(PdfDocumentReader);
        var decryptor = (StandardSecurityDecryptor)type
            .GetField("_decryptor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        var fileKey = (byte[])type
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return decryptor.DecryptString(fileKey, objectNumber, generation, plaintext);
    }

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
