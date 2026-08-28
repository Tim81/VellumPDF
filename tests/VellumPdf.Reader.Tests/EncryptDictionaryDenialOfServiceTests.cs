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
/// Before this fix, opening the huge <c>/Encrypt</c> dictionary built below took two to three times
/// this test's 10s budget, measured on the development machine (Release build).
/// </summary>
public sealed class EncryptDictionaryDenialOfServiceTests
{
    // A genuine RC4 /Encrypt dictionary — /O and /U are EncryptionParameterTests.Rc4128_O and
    // .Rc4128_U, enc-rc4-128.pdf's values under user password "u" and owner password "o" against
    // EncryptionParameterTests.Id0 — so opening it below exercises the real authentication path, not
    // just a thrown exception on the way there. The huge number of filler keys is appended before the
    // closing '>>' and does not touch /V, /R, /O, /U or /P, so authentication is unaffected by them.
    private const string Rc4EncryptDictPrefix =
        "<< /Filter /Standard /Length 128 /O <" + EncryptionParameterTests.Rc4128_O + "> "
        + "/P -4 /R 3 /U <" + EncryptionParameterTests.Rc4128_U + "> /V 2";

    /// <summary>
    /// 100,000 filler keys in <c>/Encrypt</c>, opened with the correct user password. The document
    /// still has to authenticate and decrypt for this to prove anything: a fix that merely swallowed
    /// the slowdown behind an early exception would not show that the real path — dereference, crypt
    /// filter table, key derivation — got fast too.
    ///
    /// 100,000 rather than a round 80,000: at 80,000 keys, the pre-fix cost measured under two times
    /// this test's budget on a fast enough machine, which would let a machine twice as fast as the
    /// development one pass the pre-fix code by accident. Filler count feeds a quadratic cost, so
    /// this modest increase over 80,000 restores comfortable headroom above that budget.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void HugeEncryptDictionary_opensUnderTimeout()
    {
        var bytes = BuildDocumentWithHugeEncryptDict(fillerKeyCount: 100_000);

        using var reader = PdfReader.Open(bytes, "u");

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.IsOwnerAccess);
    }

    private static byte[] BuildDocumentWithHugeEncryptDict(int fillerKeyCount)
    {
        var filler = new StringBuilder(fillerKeyCount * 10);
        for (var i = 0; i < fillerKeyCount; i++)
            filler.Append(" /Junk").Append(i).Append(' ').Append(i);

        return EncryptionParameterTests.BuildWithEncryptDict(Rc4EncryptDictPrefix + filler + " >>");
    }
}
