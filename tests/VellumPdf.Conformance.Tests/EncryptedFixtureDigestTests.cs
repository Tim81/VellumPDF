// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// Pins the four committed §7.16-1 encryption fixtures to the SHA-256 values recorded in
/// <c>Assets/README.md</c>. That file's own provenance sections say a regenerated fixture must be
/// re-checked against veraPDF before its SHA-256 is updated — a value mismatch here is what turns
/// that rule from a comment someone can skip into a test that fails the build.
/// </summary>
public sealed class EncryptedFixtureDigestTests
{
    private static readonly (string Name, string Sha256)[] Fixtures =
    [
        ("enc-aes-256-p-bit10-clear.pdf", "d7a788dc6463cc3f63325aaf27b0b71d56c0bc1501b1174e6334bad2fe66e324"),
        ("enc-aes-256-emptyuser-p-all.pdf", "ac213bd477b160d4fa0c6dbdbc5a59492045b58b38d562b8da2fd41fde26fc8b"),
        ("enc-aes-256-userpw-u-p-all.pdf", "6f4a6b3fdd247842faf1712144392d81137d95990f185dec104b543ae11868f6"),
        ("enc-aes-128-userpw-u.pdf", "c525e277fdbfb1d332eda71df6f9894c80d1a11b34512967a44df1d81fd14f9a"),
    ];

    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (name, sha) in Fixtures) data.Add(name, sha);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture_matchesTheShaRecordedInTheReadme(string logicalName, string expectedSha256)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} embedded resource not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);

        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())));
    }
}
