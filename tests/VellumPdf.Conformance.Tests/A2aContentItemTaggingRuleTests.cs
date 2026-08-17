// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Tests.Oracle;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// Tests the PDF/A-2a content-item check added after the #120 writer change removed the accidental
/// detector it replaced.
/// </summary>
/// <remarks>
/// The oracle corpus can only assert that <c>IsCompliant</c> agrees with veraPDF, and this rule is
/// deliberately a warning so that it does agree. So the oracle cannot see the rule fire at all —
/// these tests are the only thing that proves it works, and the only thing that would notice if it
/// silently stopped firing.
/// </remarks>
public sealed class A2aContentItemTaggingRuleTests
{
    private const string RuleId = "ISO19005-2:6.7.3.3-content-items";

    [Fact]
    public void UntaggedRealContent_reportsWarning_butStaysCompliant()
    {
        var result = PdfPreflight.Validate(OracleCorpus.A2aUntaggedRealContentPublic(), PdfConformance.PdfA2A);

        var assertion = Assert.Single(result.Assertions, a => a.RuleId == RuleId);
        Assert.Equal(PreflightSeverity.Warning, assertion.Severity);
        Assert.Equal("ISO 19005-2:2011, 6.7.3.3", assertion.Clause);

        // The whole point of the severity choice: the verdict still matches veraPDF, which reports
        // this file compliant because its PDF/A-2a profile implements no content-item rule.
        Assert.True(
            result.IsCompliant,
            "A warning must not change the verdict — pdfa2a-untagged-real-content asserts veraPDF parity on it.");
    }

    [Fact]
    public void TaggedContent_doesNotReport()
    {
        var result = PdfPreflight.Validate(OracleCorpus.Pdfa2aTaggedPublic(), PdfConformance.PdfA2A);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == RuleId);
        Assert.True(result.IsCompliant);
    }

    [Fact]
    public void EmptyTaggedDocument_withNoContentAtAll_doesNotReport()
    {
        // The #120 document: /StructTreeRoot present, /Document element with an empty /K, and no
        // content operators on the page. There is nothing undescribed, so the rule must stay
        // silent — firing here would make the rule a false positive on every blank tagged page.
        var result = PdfPreflight.Validate(OracleCorpus.A2aEmptyTaggedPublic(), PdfConformance.PdfA2A);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == RuleId);
        Assert.True(result.IsCompliant);
    }

    [Fact]
    public void UntaggedRealContent_underPdfA2b_doesNotReport()
    {
        // Level B carries no logical-structure requirement, so the rule must not be registered for
        // it. Guards against the rule being added to the shared 2b/2u rule list by mistake.
        var result = PdfPreflight.Validate(OracleCorpus.A2aUntaggedRealContentPublic(), PdfConformance.PdfA2B);

        Assert.DoesNotContain(result.Assertions, a => a.RuleId == RuleId);
    }
}
