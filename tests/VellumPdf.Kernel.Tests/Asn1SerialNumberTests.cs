// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests <see cref="Asn1SerialNumber"/>'s DER-minimal normalization (issue #167) directly against
/// the ASN.1 encoding it produces.
/// </summary>
/// <remarks>
/// <para>
/// These are unit tests of the helper alone; they do not exercise its callers, of which there are
/// three — <c>ExternalSignerCms</c>, <c>SigningCertificateV2</c> and <c>HttpRevocationClient</c>
/// (both the OCSP <c>CertID.serialNumber</c> it writes and the CRL <c>revokedCertificates</c>
/// serials it compares). An earlier version of this comment claimed covering the helper "exercises
/// both of its callers", which was wrong on the count and on the principle.
/// </para>
/// <para>
/// The end-to-end coverage that was once deferred from here now exists in
/// <see cref="NonMinimalSerialSigningTests"/>: <see cref="NonMinimalSerialCertificate"/> builds a
/// certificate carrying the redundant pad by re-emitting the TBS at the DER level, which
/// <see cref="System.Security.Cryptography.X509Certificates.CertificateRequest"/> cannot do because
/// it normalizes whatever serial it is given.
/// </para>
/// </remarks>
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
    public void Write_emptySerial_throws()
    {
        // Previously this wrote INTEGER 0, which silently names a *different* certificate rather
        // than failing. Unreachable from a parsed certificate either way — X.690 §8.3.1 requires at
        // least one content octet, so .NET's X.509 parser never yields an empty serial — so the
        // safer contract costs nothing.
        var writer = new AsnWriter(AsnEncodingRules.DER);

        var ex = Assert.Throws<ArgumentException>(() => Asn1SerialNumber.Write(writer, []));
        Assert.Equal("serial", ex.ParamName);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 }, true)]  // ordinary positive serial
    [InlineData(new byte[] { 0x00, 0x80 }, true)]        // load-bearing pad: required by DER
    [InlineData(new byte[] { 0x00 }, true)]              // zero
    [InlineData(new byte[] { 0x80, 0x01 }, true)]        // genuinely negative
    [InlineData(new byte[] { 0x00, 0x01 }, false)]       // redundant zero pad
    [InlineData(new byte[] { 0x00, 0x00, 0x01 }, false)] // two redundant pads
    [InlineData(new byte[] { 0xFF, 0x80 }, false)]       // redundant ones pad
    public void IsMinimal_matchesWhatADerEncoderAccepts(byte[] serial, bool expected)
    {
        Assert.Equal(expected, Asn1SerialNumber.IsMinimal(serial));

        // Cross-check against the real authority rather than restating the predicate: AsnWriter
        // enforces the DER minimal-encoding rule, so it accepts exactly the minimal encodings.
        var writer = new AsnWriter(AsnEncodingRules.DER);
        var accepted = true;
        try
        {
            writer.WriteInteger(serial);
        }
        catch (ArgumentException)
        {
            accepted = false;
        }

        Assert.Equal(accepted, Asn1SerialNumber.IsMinimal(serial));
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
