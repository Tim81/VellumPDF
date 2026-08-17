// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests <see cref="Asn1SerialNumber.Write"/>'s DER-minimal normalization (issue #167)
/// directly against the ASN.1 encoding it produces. This isn't tested through a real
/// signing pipeline: .NET's own
/// <see cref="System.Security.Cryptography.X509Certificates.CertificateRequest"/> already
/// normalizes whatever serial bytes it's given, so it can't build a certificate carrying
/// the redundant pad a mis-issued CA certificate might — producing one is possible (patch
/// the serial TLV in an existing certificate's <c>RawData</c> and re-emit the two enclosing
/// SEQUENCE lengths), just not through that API, so it's left for a follow-up end-to-end
/// test. Covering <see cref="Asn1SerialNumber"/> here exercises both of its callers,
/// <c>ExternalSignerCms</c> and <c>HttpRevocationClient</c>.
/// </summary>
public sealed class Asn1SerialNumberTests
{
    [Fact]
    public void Write_redundantZeroPad_stripsToMinimalForm()
    {
        // 0x00 followed by a byte whose high bit is clear: the pad changes nothing about
        // the represented value and DER forbids it.
        AssertEncodesInteger([0x00, 0x01, 0x02], [0x01, 0x02]);
    }

    [Fact]
    public void Write_redundantOnesPad_stripsToGenuineNegativeSerial()
    {
        // 0xFF followed by a byte whose high bit is set: the same redundancy rule as
        // above, just on the negative side of two's complement.
        AssertEncodesInteger([0xFF, 0x80, 0x01], [0x80, 0x01]);
    }

    [Fact]
    public void Write_genuineNegativeSerial_roundTripsUnchanged()
    {
        // 0x80 as the lead byte makes the value negative rather than padding it, so
        // nothing here should be stripped.
        AssertEncodesInteger([0x80, 0x01], [0x80, 0x01]);
    }

    [Fact]
    public void Write_legitimateZeroPadBeforeHighBit_roundTripsUnchanged()
    {
        // 0x00 followed by a byte whose high bit IS set: here the pad is load-bearing —
        // without it, 0x80 alone would read as a negative number — so DER requires it and
        // it must not be stripped. A naive "strip every leading 0x00" implementation would
        // mangle this; since CA serials are typically 16-20 random octets, roughly half of
        // all real-world serials carry exactly this pad.
        AssertEncodesInteger([0x00, 0x80], [0x00, 0x80]);
    }

    [Fact]
    public void Write_singleZeroByte_roundTripsUnchanged()
    {
        AssertEncodesInteger([0x00], [0x00]);
    }

    [Fact]
    public void Write_emptySerial_writesZero()
    {
        AssertEncodesInteger([], [0x00]);
    }

    [Fact]
    public void Write_normalPositiveSerial_roundTripsUnchanged()
    {
        AssertEncodesInteger([0x01, 0x02, 0x03], [0x01, 0x02, 0x03]);
    }

    [Fact]
    public void Write_regressionAnchor_rawAsnWriterRejectsTheRedundantPadThisFixes()
    {
        // Documents the exact failure Asn1SerialNumber.Write's normalization step exists to
        // avoid: AsnWriter.WriteInteger enforces DER minimality strictly and throws on the
        // certificate serial bytes this test class exercises above.
        var writer = new AsnWriter(AsnEncodingRules.DER);
        Assert.Throws<ArgumentException>(() => writer.WriteInteger([0x00, 0x01, 0x02]));
    }

    private static void AssertEncodesInteger(byte[] serial, byte[] expectedIntegerContent)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        Asn1SerialNumber.Write(writer, serial);

        var expected = new byte[2 + expectedIntegerContent.Length];
        expected[0] = 0x02; // universal INTEGER tag
        expected[1] = (byte)expectedIntegerContent.Length;
        expectedIntegerContent.CopyTo(expected, 2);

        Assert.Equal(expected, writer.Encode());
    }
}
