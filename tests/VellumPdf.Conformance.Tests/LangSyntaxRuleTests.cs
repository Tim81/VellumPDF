// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Conformance.Rules.Ua;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// The two language-tag rules, and the decision that neither carries a wall clock.
///
/// Both held <c>TimeSpan.FromMilliseconds(50)</c> as a regular-expression match timeout. A match
/// timeout is measured against elapsed time rather than work done, so a thread that loses its
/// slice mid-match exceeds it on an input that needs microseconds. On CI, which runs seven test
/// assemblies at once, the eight-character tag <c>xyz!!bad</c> timed out and the rule reported
/// "Rule evaluation failed" instead of the finding it was about to produce. Any loaded machine can
/// do that to a consumer, so it was not only a flaky test.
/// </summary>
public sealed class LangSyntaxRuleTests
{
    /// <summary>
    /// Neither rule may carry a finite match timeout. This is the one part of the decision that
    /// can be asserted deterministically: a test cannot reliably provoke thread starvation, and a
    /// test that tried would be the very wall-clock assertion being removed.
    /// </summary>
    [Theory]
    [InlineData(typeof(A2aLangSyntaxRule))]
    [InlineData(typeof(UaLangSyntaxRule))]
    public void LangRule_carriesNoMatchTimeout(Type ruleType)
    {
        var field = ruleType.GetField("_bcp47", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        // IsAssignableFrom, not IsType: the latter is an exact-type assertion in xunit.v3, so
        // it fails against a [GeneratedRegex] field -- whose value is a generated subclass of
        // Regex -- with a message about the exact type rather than about the timeout. This
        // repository already uses [GeneratedRegex] in eight places, so that refactor is likely.
        var regex = Assert.IsAssignableFrom<Regex>(field!.GetValue(null));

        Assert.Equal(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
        Assert.True(regex.Options.HasFlag(RegexOptions.NonBacktracking),
            "a pattern with no timeout needs the engine's linear-time guarantee, not this "
            + "pattern's own bounded backtracking, which a later edit could lose");
    }

    /// <summary>
    /// Both rules read the same syntax, so both patterns must return the same verdict for the
    /// same tag. The expected values come from the syntax the rules document: a primary subtag of
    /// one to eight letters, then any number of hyphen-separated subtags of one to eight letters
    /// or digits.
    /// </summary>
    [Theory]
    // accepted
    [InlineData("en", true)]
    [InlineData("EN", true)]
    [InlineData("en-GB", true)]
    [InlineData("zh-Hans-CN", true)]
    [InlineData("de-CH-1901", true)]
    [InlineData("x-private", true)]
    [InlineData("abcdefgh", true)]
    [InlineData("abcdefgh-12345678", true)]
    [InlineData("a-1", true)]
    // refused
    [InlineData("", false)]
    [InlineData("-", false)]
    [InlineData("en-", false)]
    [InlineData("-en", false)]
    [InlineData("en--GB", false)]
    [InlineData("xyz!!bad", false)]          // the tag that timed out on CI
    [InlineData("abcdefghi", false)]         // nine letters in the primary subtag
    [InlineData("en-123456789", false)]      // nine characters in a later subtag
    [InlineData("en_GB", false)]
    [InlineData("1en", false)]
    public void LangRule_acceptsTheSyntaxItDocuments(string tag, bool expected)
    {
        foreach (var ruleType in new[] { typeof(A2aLangSyntaxRule), typeof(UaLangSyntaxRule) })
        {
            var field = ruleType.GetField("_bcp47", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var regex = (Regex)field!.GetValue(null)!;

            if (expected) Assert.Matches(regex, tag);
            else Assert.DoesNotMatch(regex, tag);
        }
    }

    /// <summary>
    /// A subtag one character over the limit is what stresses a backtracking engine: the
    /// quantifier retries eight times per subtag before the hyphen that anchors the next one
    /// forces it to give up. Chaining those is the shape that would run away if the pattern were
    /// ambiguous, so a refusal here is what shows it is not.
    ///
    /// No time is asserted, deliberately. The point is the verdict; the engine's linear-time
    /// guarantee is asserted by <see cref="LangRule_carriesNoMatchTimeout"/> instead.
    ///
    /// Worth knowing that this is intent rather than coverage: it fires on exactly the mutations
    /// the corpus test's nine-character cases already catch. It is here so that the shape the
    /// argument turns on is written down as a case, not only in a comment.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(40)]
    public void LangRule_chainedOverlongSubtags_areRefusedWithoutRunningAway(int subtags)
    {
        var tag = "en" + string.Concat(Enumerable.Repeat("-aaaaaaaaa", subtags));

        var field = typeof(A2aLangSyntaxRule)
            .GetField("_bcp47", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var regex = (Regex)field!.GetValue(null)!;

        Assert.DoesNotMatch(regex, tag);
    }
}
