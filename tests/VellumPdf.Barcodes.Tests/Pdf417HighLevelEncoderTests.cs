// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Pdf417;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Vector tests for <see cref="Pdf417HighLevelEncoder"/>'s text, byte and numeric compaction,
/// and its automatic mode-switching heuristics.
/// </summary>
public sealed class Pdf417HighLevelEncoderTests
{
    [Fact]
    public void EncodeText_numericVector_matchesUssSpecWorkedExample()
    {
        // ISO/IEC 15438 section 2.2.4.6: "000213298174000" -> prepend a leading 1 -> base-900
        // -> (1, 624, 434, 632, 282, 200). The latch codeword (902) precedes it.
        var codewords = Pdf417HighLevelEncoder.EncodeText("000213298174000");
        Assert.Equal([902, 1, 624, 434, 632, 282, 200], codewords);
    }

    [Fact]
    public void EncodeBytes_sixByteMultiple_usesLatch924()
    {
        // grandzebu.net's PDF417 page: "alcool" (6 ASCII bytes, a multiple of six) fed straight
        // into byte compaction (bypassing text/numeric mode selection) -> latch 924, then
        // base-900 codewords (163, 238, 432, 766, 244), most significant first.
        var codewords = Pdf417HighLevelEncoder.EncodeBytes("alcool"u8.ToArray());
        Assert.Equal([924, 163, 238, 432, 766, 244], codewords);
    }

    [Fact]
    public void EncodeBytes_notAMultipleOfSix_usesLatch901AndCarriesTheRemainderVerbatim()
    {
        // ISO/IEC 15438 section 2.2.4.5's own worked example: 9 bytes (01H..08H, 04H) -> the same
        // six-byte group as the 6-byte example, then the remaining three bytes one per codeword.
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 4];
        var codewords = Pdf417HighLevelEncoder.EncodeBytes(data);
        Assert.Equal([901, 1, 620, 89, 74, 846, 7, 8, 4], codewords);
    }

    [Fact]
    public void EncodeText_pureUppercaseAndDigits_matchesHandComputedVector()
    {
        // "PDF417": P, D, F direct in Alpha; a run of digits latches to Mixed (no shift exists
        // for Alpha -> Mixed). Values: 15, 3, 5, [ml]28, 4, 1, 7 (odd count, padded with 29).
        // Pairs: (15,3)=453 (5,28)=178 (4,1)=121 (7,29)=239.
        var codewords = Pdf417HighLevelEncoder.EncodeText("PDF417");
        Assert.Equal([453, 178, 121, 239], codewords);
    }

    [Fact]
    public void EncodeText_mixedCaseAndPunctuation_matchesGrandzebuWorkedExample()
    {
        // grandzebu.net's PDF417 page: "Super !" -> S(Alpha) [ll]latch u,p,e,r(Lower) SPACE(Lower)
        // [ps]shift !(Punctuation, pad 29). Values: 18,27,20,15,4,17,26,29,10,29 (padded).
        // Pairs: (18,27)=567 (20,15)=615 (4,17)=137 (26,29)=809 (10,29)=329.
        var codewords = Pdf417HighLevelEncoder.EncodeText("Super !");
        Assert.Equal([567, 615, 137, 809, 329], codewords);
    }

    [Fact]
    public void EncodeText_lowercaseThenSharedCharThenDigits_matchesUssSpecWorkedExample()
    {
        // ISO/IEC 15438 section 2.2.4.4.2's own worked example: "Ad:102" -> A(Alpha) [ll]latch
        // d(Lower) [ml]latch (':' shared with Punctuation, but digits follow so Mixed wins) 1,0,2
        // (Mixed). Values: 0,27,3,28,14,1,0,2 (even, no padding). Pairs: (0,27)=27 (3,28)=118
        // (14,1)=421 (0,2)=2.
        var codewords = Pdf417HighLevelEncoder.EncodeText("Ad:102");
        Assert.Equal([27, 118, 421, 2], codewords);
    }

    [Fact]
    public void EncodeText_emptyContent_producesNoCodewords() =>
        Assert.Empty(Pdf417HighLevelEncoder.EncodeText(string.Empty));

    [Fact]
    public void EncodeText_characterOutsideLatin1_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Pdf417HighLevelEncoder.EncodeText("cafĀ")); // U+0100, beyond Latin-1

    [Fact]
    public void EncodeText_longDigitRun_switchesToNumericCompaction()
    {
        // Thirteen digits reach the Annex P numeric threshold, so the whole run should switch to
        // Numeric Compaction (latch 902) rather than being packed two-per-codeword as text.
        var thirteenDigits = new string('7', 13);
        var codewords = Pdf417HighLevelEncoder.EncodeText(thirteenDigits);
        Assert.Equal(902, codewords[0]);

        var twelveDigits = new string('7', 12);
        var shortRun = Pdf417HighLevelEncoder.EncodeText(twelveDigits);
        Assert.DoesNotContain(902, shortRun);
    }

    [Fact]
    public void EncodeText_shortTextRunBetweenByteRuns_isAbsorbedIntoByteCompaction()
    {
        // A four-character text run (below the five-character Annex P threshold) sandwiched
        // between two runs that must be byte-compacted (extended Latin-1, outside the text range)
        // should be folded into a single byte run rather than round-tripping through a Text latch.
        var content = "éè" + "abcd" + "êë";
        var codewords = Pdf417HighLevelEncoder.EncodeText(content);
        Assert.True(codewords[0] is 901 or 924);
        Assert.DoesNotContain(900, codewords);
    }

    [Fact]
    public void EncodeText_isDeterministic()
    {
        var a = Pdf417HighLevelEncoder.EncodeText("Mixed 123, Text! And more.");
        var b = Pdf417HighLevelEncoder.EncodeText("Mixed 123, Text! And more.");
        Assert.Equal(a, b);
    }
}
