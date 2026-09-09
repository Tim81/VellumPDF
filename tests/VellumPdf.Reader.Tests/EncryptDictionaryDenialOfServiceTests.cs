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
/// <c>DereferenceValues</c>'s own copy quadratic would pass the kernel-level test and still fail
/// here.
///
/// <para>
/// The budget below is deliberately enormous relative to the work. #400 was opened because the
/// earlier version of this test opened 100,000 keys under a ten-second budget and was cancelled at
/// that budget three times on GitHub's shared runner, on branches touching neither the reader nor
/// this test — most recently one whose entire diff was a workflow file and a CHANGELOG entry. A
/// time budget only pins a regression if the pre-fix cost exceeds it on the slowest machine the
/// suite ever runs on, and ten seconds against a third of a second was not the margin it looked
/// like: #400 records that assembly at 6m 21s on the runner against 34s here, and it runs its
/// classes in parallel with <c>ParserFuzzTests</c>, which uses CsCheck and takes every core.
/// </para>
///
/// <para>
/// Raising the budget alone would have weakened the pin, which is why #400 rejected it. Raising the
/// key count instead widens the gap the budget has to sit in, because the cost this guards against
/// grows with the square of that count while the fixed cost grows with the count itself. Measured
/// in Release on the development machine, with <c>PdfDictionary</c>'s index disabled: 538 ms at
/// 12,500 keys, 1.6 s at 25,000, 6.7 s at 50,000, 34 s at 100,000 and 126 s at 200,000, each
/// doubling costing between 2.9 and 5.1 times as much. Carried out one more doubling, 400,000 keys
/// lands near eight minutes broken, against about a quarter of a second fixed. The eight minutes is
/// extrapolated, not measured: the only direct observation at 400,000 broken is this test being
/// cancelled at its budget, which puts a floor under it and no ceiling.
/// </para>
///
/// <para>
/// What that buys, measured. Run alone the whole test takes about half a second, and inside its own
/// assembly about two, so it uses at most a couple of per cent of the budget. Under deliberate
/// saturation — the full assembly plus 256 busy loops on sixteen cores, which slowed the assembly
/// 13.7x and broke five other tests — it took 37.7 s and still passed. Failing it would need about
/// 240 times the alone cost, or 60 times the in-assembly cost. The budget it replaces failed at
/// about 30 times its own alone cost, which is the comparison that matters.
/// </para>
///
/// <para>
/// The passing margin is what improves, and that is the one #400 is about. The failing margin barely
/// moves: the pre-fix cost overran the old ten-second budget about three and a half times at
/// 100,000 keys, and on the extrapolation above overruns this one about four times at 400,000.
/// </para>
///
/// <para>
/// Note that a passing margin cannot be had by scaling a local timing by an assembly-level slowdown.
/// Under a lighter run, 64 busy loops rather than 256, the assembly slowed 2.4x while this single
/// test slowed 11x, because one CPU-bound region absorbs preemption far worse than an average over
/// many. That is the same mistake as the design this replaces, which compared two timings and
/// asserted a ratio: taking the fastest of several samples drags a short measurement to its floor
/// under contention while a long one absorbs every preemption, so the ratio grows instead of
/// cancelling.
/// </para>
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

    // 400,000 rather than the 100,000 this test used to build. The whole argument for the budget is
    // the gap between a linear cost and a quadratic one, and that gap widens with the key count: at
    // 100,000 it was a factor of about six hundred, and at 400,000 about two thousand on the
    // extrapolation the class doc explains.
    private const int FillerKeyCount = 400_000;

    /// <summary>
    /// The document — <see cref="FillerKeyCount"/> filler keys in <c>/Encrypt</c>, opened with the
    /// correct user password — still has to authenticate and decrypt for this to prove anything: a
    /// fix that merely swallowed the slowdown behind an early exception would not show that the real
    /// path — dereference, crypt filter table, key derivation — got fast too, so the assertions below
    /// check the encryption state the open produced rather than only that it returned.
    /// </summary>
    // xUnit1069 wants TestContext.Current.CancellationToken threaded through so the Timeout can end
    // the test promptly; PdfReader.Open takes no CancellationToken, and there is nothing to thread
    // it into. The Timeout is the regression pin — see the class doc for why two minutes against half
    // a second is a pin rather than a coin toss — so it stays.
#pragma warning disable xUnit1069
    [Fact(Timeout = 120_000)]
    public void HugeEncryptDictionary_opensWellInsideTheBudget()
    {
        var bytes = BuildDocumentWithHugeEncryptDict(FillerKeyCount);

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.IsOwnerAccess);
    }
#pragma warning restore xUnit1069

    private static byte[] BuildDocumentWithHugeEncryptDict(int fillerKeyCount)
    {
        var filler = new StringBuilder(fillerKeyCount * 10);
        for (var i = 0; i < fillerKeyCount; i++)
            filler.Append(" /Junk").Append(i).Append(' ').Append(i);

        return EncryptionParameterTests.BuildWithEncryptDict(Rc4EncryptDictPrefix + filler + " >>");
    }
}
