// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using VellumPdf.Conformance.Rules.Metadata;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// The XMP Real pattern in <c>PropertyValueTypeRule</c> (#506). Its value comes from the document's
/// own XMP packet, and so does the choice of type, so a file picks both the pattern and the input.
///
/// The original pattern, <c>\d+\.?\d*</c>, split a run of digits between <c>\d+</c> and
/// <c>\d*</c> at every position when no decimal point was present, which made a failing match
/// quadratic in the length of the run. No time is asserted here, deliberately: a clock measures
/// scheduling, and this repository removed the same kind of assertion from the language-tag rules
/// (#400). The guarantee is asserted structurally instead, by the engine option, and the verdict
/// is pinned on every input shape the rewrite could have changed.
/// </summary>
public sealed class PropertyValueTypeRealPatternTests
{
    // The pattern as 2.3.3 shipped it, kept here so the rewrite is compared against what it
    // replaced rather than against a reading of it.
    private const string PreviousPattern = @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$";

    private static Regex Current()
    {
        var field = typeof(PropertyValueTypeRule)
            .GetField("_realRegex", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<Regex>(field!.GetValue(null));
    }

    [Fact]
    public void RealPattern_isLinearByConstruction_andCarriesNoTimeout()
    {
        var regex = Current();

        Assert.True(regex.Options.HasFlag(RegexOptions.NonBacktracking),
            "the value is attacker-controlled; the engine's linear-time guarantee must not depend "
            + "on the pattern staying unambiguous");
        Assert.Equal(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
    }

    [Fact]
    public void RealPattern_hasNoOptionalPointFollowedByDigitStar()
    {
        // The ambiguous shape itself. With the engine option above this is belt and braces, but it
        // is the defect #506 names, so a later edit that reintroduces it should fail by name.
        Assert.DoesNotContain(@"\.?\d*", Current().ToString());
    }

    [Theory]
    // accepted
    [InlineData("0", true)]
    [InlineData("12", true)]
    [InlineData("+12", true)]
    [InlineData("-12", true)]
    [InlineData("1.", true)]
    [InlineData("1.5", true)]
    [InlineData("-1.5", true)]
    [InlineData(".5", true)]
    [InlineData("+.5", true)]
    [InlineData("1e5", true)]
    [InlineData("1E-5", true)]
    [InlineData("1.5e+10", true)]
    [InlineData(".5e3", true)]
    [InlineData("1.e3", true)]
    // refused
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("+", false)]
    [InlineData("-", false)]
    [InlineData("e5", false)]
    [InlineData("1e", false)]
    [InlineData("1e+", false)]
    [InlineData("1.5.5", false)]
    [InlineData("1,5", false)]
    [InlineData("1 5", false)]
    [InlineData("--1", false)]
    [InlineData("1!", false)]
    [InlineData("1.5!", false)]
    [InlineData("1e5!", false)]
    [InlineData("NaN", false)]
    public void RealPattern_knownAnswers(string value, bool expected)
    {
        Assert.Equal(expected, Current().IsMatch(value));
    }

    /// <summary>
    /// The rewrite must accept and refuse exactly what the pattern it replaced did. Every string up
    /// to length 7 over an alphabet holding each token the grammar distinguishes (a digit, the
    /// point, both exponent letters, both signs, one character it never accepts, and ARABIC-INDIC
    /// DIGIT THREE, which .NET's <c>\d</c> matches and <c>[0-9]</c> would not): 8^7 = 2,097,152
    /// strings plus the shorter ones, 2,396,745 in all.
    /// </summary>
    [Fact]
    public void RealPattern_agreesWithThePreviousPatternOnEveryShortString()
    {
        var previous = new Regex(PreviousPattern, RegexOptions.CultureInvariant);
        var current = Current();
        char[] alphabet = ['0', '.', 'e', 'E', '+', '-', '!', (char)0x0663];

        var buffer = new char[7];
        var checkedCount = 0;
        for (var length = 0; length <= buffer.Length; length++)
        {
            var total = (int)Math.Pow(alphabet.Length, length);
            for (var n = 0; n < total; n++)
            {
                var rest = n;
                for (var i = 0; i < length; i++)
                {
                    buffer[i] = alphabet[rest % alphabet.Length];
                    rest /= alphabet.Length;
                }

                var s = new string(buffer, 0, length);
                Assert.True(previous.IsMatch(s) == current.IsMatch(s),
                    $"verdicts differ on \"{s}\": previous {previous.IsMatch(s)}, current {current.IsMatch(s)}");
                checkedCount++;
            }
        }

        Assert.Equal(2_396_745, checkedCount); // 8^0 + ... + 8^7
    }

    /// <summary>
    /// The shapes that cost the old pattern: a long digit run ending in a character that is not
    /// part of a number, with and without a point or an exponent in front. Only the verdict is
    /// asserted. The length is the one at which the old pattern measured in seconds (#506), so a
    /// regression shows as a suite that stops, not as a number that moved.
    /// </summary>
    [Theory]
    [InlineData("", "9", "!", false)]
    [InlineData("1.", "9", "!", false)]
    [InlineData("", "9", "e!", false)]
    [InlineData("1e", "9", "!", false)]
    [InlineData("", "9", "", true)]
    [InlineData("1.", "9", "", true)]
    [InlineData("1e", "9", "", true)]
    public void RealPattern_longDigitRuns_returnTheRightVerdict(
        string prefix, string unit, string suffix, bool expected)
    {
        var value = prefix + string.Concat(Enumerable.Repeat(unit, 32_000)) + suffix;

        Assert.Equal(expected, Current().IsMatch(value));
    }
}
