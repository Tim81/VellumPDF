// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// Shared by <see cref="UaEncryptionPermissionsRuleTests"/> and
/// <see cref="UaEncryptionPermissionsVeraPdfTests"/> so the two cannot drift into checking the
/// <c>/Encrypt</c> dictionary two different ways.
/// </summary>
internal static class EncryptDictionaryAssertions
{
    /// <summary>
    /// Reads <c>/R</c> and <c>/P</c> straight off the <c>/Encrypt</c> dictionary — the same values
    /// <c>UaEncryptionPermissionsRule</c> checks — and asserts <c>/R</c> is 6 and <c>/P</c> equals
    /// <paramref name="expectedP"/>, rather than trusting whatever the writer or a committed fixture
    /// happens to contain. Scoped to the <c>/Encrypt</c> object (<c>/Filter /Standard</c> to its
    /// <c>endobj</c>) and parsed as an int rather than matched as a substring: an unanchored
    /// <c>"/P -4"</c> also matches inside <c>"/P -44"</c>, a Table 22 value
    /// <c>StandardSecurityHandler</c> can genuinely produce, so a substring match would not catch a
    /// regression that dropped extra bits.
    /// </summary>
    public static void AssertEncryptDictionaryIsR6WithP(byte[] pdf, int expectedP)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var encryptStart = text.IndexOf("/Filter /Standard", StringComparison.Ordinal);
        Assert.True(encryptStart >= 0, "no /Encrypt dictionary (/Filter /Standard) found in the fixture.");
        var encryptEnd = text.IndexOf("endobj", encryptStart, StringComparison.Ordinal);
        Assert.True(encryptEnd >= 0, "the /Encrypt object has no endobj terminator.");
        var encryptObject = text[encryptStart..encryptEnd];

        var rMatch = Regex.Match(encryptObject, @"/R (\d+)");
        Assert.True(rMatch.Success, "the /Encrypt dictionary has no /R entry.");
        Assert.Equal(6, int.Parse(rMatch.Groups[1].Value, CultureInfo.InvariantCulture));

        var pMatch = Regex.Match(encryptObject, @"/P (-?\d+)");
        Assert.True(pMatch.Success, "the /Encrypt dictionary has no /P entry.");
        Assert.Equal(expectedP, int.Parse(pMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }
}
