// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// One direct test per <c>PdfPreflight</c> password overload, against a real user-password
/// fixture — <c>enc-aes-128-userpw-u.pdf</c>, a byte-identical copy of
/// <c>VellumPdf.Reader.Tests/Fixtures/Encrypted/enc-aes-128.pdf</c> (AESv2, user password "u",
/// owner "o", <c>/P -4</c>). Each test asserts a non-vacuous outcome, not merely "did not throw":
/// the wrong or absent password throws <see cref="PdfPasswordException"/>, and the right one
/// authenticates and produces a specific, measured result rather than an unchecked one — this
/// fixture makes no PDF/A or PDF/UA claim, and fails a fixed, pinned number of PDF/A-2b and
/// PDF/UA-1 rules once opened with its real password.
/// </summary>
public sealed class PdfPreflightPasswordOverloadTests
{
    [Fact]
    public void DetectClaimedProfiles_byteArray_withRightPassword_authenticatesAndDetectsNoClaim()
    {
        var bytes = ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf");

        var claimed = PdfPreflight.DetectClaimedProfiles(bytes, "u");

        Assert.Empty(claimed);
    }

    [Fact]
    public void DetectClaimedProfiles_byteArray_withWrongOrAbsentPassword_throws()
    {
        var bytes = ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf");

        Assert.Throws<PdfPasswordException>(() => PdfPreflight.DetectClaimedProfiles(bytes, "wrong"));
        Assert.Throws<PdfPasswordException>(() => PdfPreflight.DetectClaimedProfiles(bytes, password: null));
    }

    [Fact]
    public void DetectClaimedProfiles_stream_withRightPassword_authenticatesAndDetectsNoClaim()
    {
        using var stream = new MemoryStream(ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf"));

        var claimed = PdfPreflight.DetectClaimedProfiles(stream, "u");

        Assert.Empty(claimed);
    }

    [Fact]
    public void DetectClaimedProfiles_stream_withWrongOrAbsentPassword_throws()
    {
        Assert.Throws<PdfPasswordException>(() =>
        {
            using var stream = new MemoryStream(ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf"));
            PdfPreflight.DetectClaimedProfiles(stream, "wrong");
        });
        Assert.Throws<PdfPasswordException>(() =>
        {
            using var stream = new MemoryStream(ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf"));
            PdfPreflight.DetectClaimedProfiles(stream, password: null);
        });
    }

    /// <summary>
    /// PDF/A-2b: 7 assertions, non-compliant — measured directly against the fixture, not assumed.
    /// A wrong count would mean the rule set stopped seeing this file's real content (a password
    /// that silently failed to decrypt everything but the strings, for instance) rather than
    /// genuinely evaluating it.
    /// </summary>
    [Fact]
    public void Validate_byteArray_withRightPassword_authenticatesAndEvaluatesRules()
    {
        var bytes = ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B, "u");

        Assert.False(result.IsCompliant);
        Assert.Equal(7, result.Assertions.Count);
    }

    [Fact]
    public void Validate_byteArray_withWrongOrAbsentPassword_throws()
    {
        var bytes = ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf");

        Assert.Throws<PdfPasswordException>(() => PdfPreflight.Validate(bytes, PdfConformance.PdfA2B, "wrong"));
        Assert.Throws<PdfPasswordException>(() => PdfPreflight.Validate(bytes, PdfConformance.PdfA2B, password: null));
    }

    /// <summary>PDF/UA-1: 10 assertions, non-compliant — the stream overload's own pinned count, not
    /// copied from the byte-array test, so the two overloads are proven to reach the same rule
    /// engine independently rather than one delegating silently to the other with the count carried
    /// along by coincidence.</summary>
    [Fact]
    public void Validate_stream_withRightPassword_authenticatesAndEvaluatesRules()
    {
        using var stream = new MemoryStream(ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf"));

        var result = PdfPreflight.Validate(stream, PdfConformance.PdfUA1, "u");

        Assert.False(result.IsCompliant);
        Assert.Equal(10, result.Assertions.Count);
    }

    [Fact]
    public void Validate_stream_withWrongOrAbsentPassword_throws()
    {
        Assert.Throws<PdfPasswordException>(() =>
        {
            using var stream = new MemoryStream(ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf"));
            PdfPreflight.Validate(stream, PdfConformance.PdfUA1, "wrong");
        });
        Assert.Throws<PdfPasswordException>(() =>
        {
            using var stream = new MemoryStream(ReadEmbeddedFixture("enc-aes-128-userpw-u.pdf"));
            PdfPreflight.Validate(stream, PdfConformance.PdfUA1, password: null);
        });
    }

    /// <summary>
    /// <see langword="null"/> and <see cref="string.Empty"/> both mean "the empty user password",
    /// per the four overloads' own XML docs — measured here rather than merely documented, on
    /// <c>jpx-encrypted-emptyuser.pdf</c> (an empty-user-password fixture already embedded in this
    /// project). Both spellings detect the same claim (<c>[PdfA2B]</c>) and produce byte-for-byte
    /// equal validation results (6 assertions, non-compliant).
    /// </summary>
    [Fact]
    public void EmptyStringAndNullPassword_produceIdenticalResults_onEmptyUserPasswordFile()
    {
        var bytes = ReadEmbeddedFixture("jpx-encrypted-emptyuser.pdf");

        var claimedEmpty = PdfPreflight.DetectClaimedProfiles(bytes, "");
        var claimedNull = PdfPreflight.DetectClaimedProfiles(bytes, password: null);
        Assert.Equal(claimedNull, claimedEmpty);
        Assert.Equal([PdfConformance.PdfA2B], claimedEmpty);

        var validatedEmpty = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B, "");
        var validatedNull = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B, password: null);
        Assert.Equal(validatedNull.IsCompliant, validatedEmpty.IsCompliant);
        Assert.Equal(6, validatedEmpty.Assertions.Count);
        Assert.Equal(
            validatedNull.Assertions.Select(a => (a.RuleId, a.Severity, a.Message)),
            validatedEmpty.Assertions.Select(a => (a.RuleId, a.Severity, a.Message)));
    }

    private static byte[] ReadEmbeddedFixture(string logicalName)
    {
        using var s = typeof(PdfPreflightPasswordOverloadTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} embedded resource not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
