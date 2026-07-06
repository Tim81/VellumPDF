// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="Gs1DigitalLink"/>: canonical host, primary key first, <c>/{ai}/{value}</c>
/// segment pairs, and percent-encoding of path values.
/// </summary>
public sealed class Gs1DigitalLinkTests
{
    [Fact]
    public void Build_gtinPlusBatch_producesCanonicalUri()
    {
        var uri = Gs1DigitalLink.Build("(01)09520123456788(10)ABC");
        Assert.Equal("https://id.gs1.org/01/09520123456788/10/ABC", uri);
    }

    [Fact]
    public void Build_gtinOnly_producesKeyOnlyPath()
    {
        var uri = Gs1DigitalLink.Build("(01)09520123456788");
        Assert.Equal("https://id.gs1.org/01/09520123456788", uri);
    }

    [Fact]
    public void Build_primaryKeyNotFirst_isReorderedToLeadThePath()
    {
        // The primary identification key (GTIN) leads the path even when it appears later in the
        // element string; the remaining AIs keep their original order after it.
        var uri = Gs1DigitalLink.Build("(10)LOT7(01)09520123456788(21)SER9");
        Assert.Equal("https://id.gs1.org/01/09520123456788/10/LOT7/21/SER9", uri);
    }

    [Fact]
    public void Build_gtinExpiryBatch_keepsFixedLengthValuesIntact()
    {
        var uri = Gs1DigitalLink.Build("(01)09520123456788(17)261231(10)LOT7");
        Assert.Equal("https://id.gs1.org/01/09520123456788/17/261231/10/LOT7", uri);
    }

    [Fact]
    public void Build_valueWithReservedCharacters_isPercentEncoded()
    {
        // A space and a slash in a variable value must be escaped so each stays inside one path
        // segment; the unreserved characters around them pass through.
        var uri = Gs1DigitalLink.Build("(01)09520123456788(21)A B/C");
        Assert.Equal("https://id.gs1.org/01/09520123456788/21/A%20B%2FC", uri);
    }

    [Fact]
    public void Build_valueWithUnreservedPunctuation_isNotEncoded()
    {
        var uri = Gs1DigitalLink.Build("(01)09520123456788(21)A-b_9.z~");
        Assert.Equal("https://id.gs1.org/01/09520123456788/21/A-b_9.z~", uri);
    }

    [Fact]
    public void Build_fromRawPayloadForm_matchesParenthesizedForm()
    {
        const string gs = "";
        var fromRaw = Gs1DigitalLink.Build("0109520123456788" + "10LOT7" + gs + "21SER9");
        var fromParens = Gs1DigitalLink.Build("(01)09520123456788(10)LOT7(21)SER9");
        Assert.Equal(fromParens, fromRaw);
    }

    [Fact]
    public void Build_fromParsedElements_matchesStringOverload()
    {
        var parsed = Gs1ElementString.Parse("(01)09520123456788(10)LOT7");
        var fromElements = Gs1DigitalLink.Build(parsed.Elements);
        var fromString = Gs1DigitalLink.Build("(01)09520123456788(10)LOT7");
        Assert.Equal(fromString, fromElements);
    }

    [Fact]
    public void Build_withoutPrimaryKey_throwsFormatException()
    {
        // AI 10 is a data attribute, not a primary identification key.
        Assert.Throws<FormatException>(() => Gs1DigitalLink.Build("(10)LOT7"));
    }

    [Fact]
    public void Build_nullElements_throwsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Gs1DigitalLink.Build((IReadOnlyList<Gs1Element>)null!));
    }
}
