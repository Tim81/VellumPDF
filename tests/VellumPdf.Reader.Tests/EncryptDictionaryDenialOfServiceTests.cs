// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// #208: an <c>/Encrypt</c> dictionary is parsed, copied by <c>EncryptionSetup.DereferenceValues</c>,
/// and read from before <c>PdfReader.Open</c> checks any password, on a file anyone can send.
/// <see cref="VellumPdf.Kernel.Tests.PdfDictionaryIndexTests"/> pins the fix inside
/// <c>PdfDictionary</c> itself; this pins that fixing <c>PdfDictionary</c> alone was enough, by
/// reaching the same key count through the actual pre-authentication path a hostile file would use.
/// <c>DereferenceValues</c> builds its copy entirely through <c>PdfDictionary.Set</c> and holds no
/// collection of its own, so what this pins is the write path. The read path is pinned separately by
/// <c>ShallowCopy_pastTheThreshold_carriesTheIndex</c>, which reads every key back;
/// <c>EncryptionSetup</c> makes about ten <c>Get</c> calls, far too few to notice a reverted index
/// there. The #208 pin is complete only as that pair.
///
/// <para>
/// The budget below is deliberately enormous relative to the work. The earlier version of this test
/// opened 100,000 keys under a ten-second budget and has been cancelled at that budget three times
/// on GitHub's shared runner, on branches touching neither the reader nor this test. #400 was opened
/// on the first two; the third arrived a week later, on a branch whose entire diff was a workflow
/// file and a CHANGELOG entry. A time budget only pins a regression if the pre-fix cost exceeds it
/// on the slowest machine the suite ever runs on, and ten seconds against a third of a second was
/// not the margin it looked like: #400 records the Reader assembly at 6m 21s on the runner against
/// 34s here, and it runs its classes in parallel with <c>ParserFuzzTests</c>, which uses CsCheck and
/// takes every core. #400's own body puts the pre-fix overrun at two to three times that budget;
/// remeasuring it here gave 3.4, and this doc's figures supersede it.
/// </para>
///
/// <para>
/// Raising the budget alone would have weakened the pin, which is why #400 rejected it. Raising the
/// key count instead widens the gap the budget has to sit in, because the cost this guards against
/// grows with the square of that count while the fixed cost grows with the count itself. Measured in
/// Release on the development machine, with <c>PdfDictionary</c>'s index disabled by raising
/// <c>IndexThreshold</c> to <c>int.MaxValue</c>: 538 ms at 12,500 keys, 1.6 s at 25,000, 6.7 s at
/// 50,000, 34 s of open cost at 100,000 and 126 s at 200,000, each doubling costing between 2.9 and
/// 5.1 times as much. The endpoints imply an exponent of 1.97 and a least-squares fit over all five
/// points gives 2.02, so one more doubling was projected at the quadratic's 4x rather than anywhere
/// in that band: 400,000 keys lands near eight minutes
/// broken, against about a quarter of a second of open cost fixed. The eight minutes is extrapolated,
/// not measured. What was measured is the direction: with <c>IndexThreshold</c> raised the test does
/// fail, cancelled at 120.2 s, which puts a floor under the broken cost and no ceiling.
/// </para>
///
/// <para>
/// What that buys, measured on the development machine, which is not the machine that flaked. Run
/// alone the whole test, building the fixture included, takes about half a second, and inside its own
/// assembly about two. Unloaded that is a couple of per cent of the budget. Under deliberate
/// saturation — the full assembly plus 256 busy loops on sixteen cores, which slowed the assembly
/// 13.7x and broke five other tests — it took 37.7 s and still passed, which is 31% of the budget and
/// the smallest margin any measurement here produced. Failing it unloaded would need about 240 times
/// the alone cost, or 60 times the in-assembly cost. The budget it replaces failed at about 30 times
/// its own alone cost. None of these figures is from GitHub's runner, and #400 exists because a local
/// figure mispredicted it once already.
/// </para>
///
/// <para>
/// The passing margin is what improves, and that is the one #400 is about. The failing margin barely
/// moves: the pre-fix cost overran the old ten-second budget about three and a half times at
/// 100,000 keys, and on the extrapolation above overruns this one about four times at 400,000.
/// </para>
///
/// <para>
/// A passing margin does not come from scaling a local timing by an assembly-level slowdown. Under a
/// lighter run, 64 busy loops rather than 256, the assembly slowed 2.4x while this single test slowed
/// 11x off its alone cost, and the heavier run says the same on those same baselines, 13.7x for the
/// assembly against 75x for this test. One CPU-bound region absorbs preemption far worse than an
/// average over many. Scaling by the assembly figure is the same mistake as a design rejected on the
/// way here, which compared two timings and asserted a ratio: taking the fastest of several samples
/// drags a short measurement to its floor under contention while a long one absorbs every preemption,
/// so the ratio grows instead of cancelling.
/// </para>
///
/// <para>
/// One consequence of the larger fixture is worth knowing before diagnosing a red build. .NET cannot
/// abort a synchronous test body, so xUnit reports <c>failed (canceled)</c> at the budget and the
/// work carries on to completion. A genuine regression now orphans minutes of a pegged core rather
/// than seconds of one, in the same process as the ten-second budget in <c>PdfObjectParserTests</c>.
/// Expect a cluster of timeouts rather than one clean failure, and read this test as the cause.
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
    // the gap between a linear cost and a quadratic one, and that gap widens with the key count. On
    // the class doc's figures, taking its quarter second at 400,000 as the fixed cost and scaling it
    // linearly, the gap is about five hundred at 100,000 and about two thousand at 400,000. Both
    // rest on the extrapolation the class doc explains, not on a measured fixed cost at either
    // count.
    private const int FillerKeyCount = 400_000;

    /// <summary>
    /// The document carries <see cref="FillerKeyCount"/> filler keys in <c>/Encrypt</c> and is opened
    /// with the correct user password, so it still has to authenticate and decrypt. That is what
    /// makes the timing mean anything. A fix that merely swallowed the slowdown behind an early
    /// exception would leave the real path, dereference through crypt filter table to key derivation,
    /// as slow as it was. The assertions below therefore check the encryption state the open
    /// produced, not just that it returned.
    /// </summary>
    // xUnit1069 wants TestContext.Current.CancellationToken threaded through so the Timeout can end
    // the test promptly; PdfReader.Open takes no CancellationToken, and there is nothing to thread
    // it into. The Timeout is the regression pin, and what it pins against is the broken cost the
    // class doc puts near eight minutes at this key count, not the half second the fixed path takes.
    // The class doc has both margins and why neither makes this a coin toss. So it stays.
#pragma warning disable xUnit1069
    [Fact(Timeout = 120_000)]
    public void HugeEncryptDictionary_opensWellInsideTheBudget()
    {
        var bytes = BuildDocumentWithHugeEncryptDict(FillerKeyCount);

        // Without this the test cannot tell "the fix works" from "the fixture stopped being huge",
        // which a mis-edit of the builder above would do silently. It checks the bytes and not the
        // parsed document, so it does not catch a reader that later starts discarding entries; that
        // would still return fast with both assertions below intact. Catching it needs an assertion
        // on what the open produced, and the /Encrypt dictionary is not reachable from here.
        Assert.Contains(
            $"/Junk{FillerKeyCount - 1} {FillerKeyCount - 1}",
            Encoding.Latin1.GetString(bytes),
            StringComparison.Ordinal);

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.IsOwnerAccess);
    }
#pragma warning restore xUnit1069

    private static byte[] BuildDocumentWithHugeEncryptDict(int fillerKeyCount)
    {
        // 19 chars per key, not the 10 an earlier version reserved: " /JunkN N" is 7 fixed characters
        // plus the index twice, and at 400,000 keys that is 7.4 million characters. Under-reserving
        // spills the tail into ~400 chunks, and building the prefix and suffix outside the builder
        // copies all 7.4 million twice more before Latin1.GetBytes copies them again.
        var doc = new StringBuilder(Rc4EncryptDictPrefix, fillerKeyCount * 19);
        for (var i = 0; i < fillerKeyCount; i++)
            doc.Append(" /Junk").Append(i).Append(' ').Append(i);
        doc.Append(" >>");

        return EncryptionParameterTests.BuildWithEncryptDict(doc.ToString());
    }
}
