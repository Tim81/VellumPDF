// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// Provides a minimal, valid sRGB ICC version 2 color profile for use as a PDF/A
/// OutputIntent. The profile is an ICC v2 3-component RGB profile conforming to
/// IEC 61966-2-1 (sRGB), suitable for embedding as the <c>/DestOutputProfile</c>
/// in a PDF/A-2 OutputIntent dictionary.
///
/// <para>
/// The profile is constructed programmatically from the well-known sRGB primaries,
/// D65/D50 white point, and the standard sRGB tone response curve (γ≈2.2 piece-wise).
/// All tags required by PDF/A-2 (ISO 19005-2 §6.2.2) are included:
/// <c>cprt</c>, <c>desc</c>, <c>wtpt</c>, <c>rXYZ</c>, <c>gXYZ</c>, <c>bXYZ</c>,
/// <c>rTRC</c>, <c>gTRC</c>, <c>bTRC</c>.
/// </para>
/// </summary>
internal static class SrgbIccProfile
{
    /// <summary>
    /// The raw ICC profile bytes. Component count /N = 3 (RGB).
    /// ICC v2.1, profile class: Display (mntr), color space: RGB, PCS: XYZ.
    /// </summary>
    public static byte[] Bytes { get; } = BuildMinimalSrgbV2();

    /// <summary>
    /// Constructs a minimal but complete sRGB ICC v2 profile with all tags required
    /// by PDF/A-2 (ISO 19005-2 §6.2.2).
    /// </summary>
    private static byte[] BuildMinimalSrgbV2()
    {
        // Tags included (PDF/A-2 mandatory set for an RGB display profile):
        //   cprt – copyright string
        //   desc – profile description
        //   wtpt – media white point (D50 PCS illuminant)
        //   rXYZ, gXYZ, bXYZ – chromatic primaries (D50-adapted)
        //   rTRC, gTRC, bTRC – tone response curves (sRGB γ≈2.2, 256-entry LUT)

        var w = new ProfileWriter();

        // ── Tag data bodies ────────────────────────────────────────────────────
        var cprtData = BuildTextTag("Copyright (C) 2026, VellumPdf. No rights reserved.");
        var descData = BuildDescTag("sRGB IEC61966-2.1");

        // D50 white point (PCS illuminant) as s15Fixed16: X=0.9642, Y=1.0000, Z=0.8249
        var wtptData = BuildXyzTag(0.9642, 1.0000, 0.8249);

        // sRGB primaries in XYZ PCS (D50-adapted, from IEC 61966-2-1 Annex A):
        // Red:   X=0.4361, Y=0.2225, Z=0.0139
        // Green: X=0.3851, Y=0.7169, Z=0.0971
        // Blue:  X=0.1431, Y=0.0606, Z=0.7139
        var rXyzData = BuildXyzTag(0.4361, 0.2225, 0.0139);
        var gXyzData = BuildXyzTag(0.3851, 0.7169, 0.0971);
        var bXyzData = BuildXyzTag(0.1431, 0.0606, 0.7139);

        // sRGB TRC: 256-point LUT approximating the piece-wise sRGB curve
        // V_lin = ((V+0.055)/1.055)^2.4 for V > 0.04045, else V/12.92
        var trcData = BuildSrgbCurvTag();

        // ── Layout: count offsets ──────────────────────────────────────────────
        const int tagCount = 9;
        const int headerSize = 128;
        const int tagTableSize = 4 + tagCount * 12;
        var dataOffset = headerSize + tagTableSize;

        static int Align4(int n) => (n + 3) & ~3;

        var cprtOff = dataOffset;
        var descOff = cprtOff + Align4(cprtData.Length);
        var wtptOff = descOff + Align4(descData.Length);
        var rXyzOff = wtptOff + Align4(wtptData.Length);
        var gXyzOff = rXyzOff + Align4(rXyzData.Length);
        var bXyzOff = gXyzOff + Align4(gXyzData.Length);
        var rTrcOff = bXyzOff + Align4(bXyzData.Length);
        var gTrcOff = rTrcOff + Align4(trcData.Length);
        var bTrcOff = gTrcOff + Align4(trcData.Length);
        var profileSize = bTrcOff + Align4(trcData.Length);

        // ── ICC Header (128 bytes) ─────────────────────────────────────────────
        w.WriteU32((uint)profileSize);     // 0: profile size (bytes)
        w.WriteAscii4("    ");             // 4: preferred CMM type (none)
        w.WriteU32(0x02100000);            // 8: ICC version 2.1.0
        w.WriteAscii4("mntr");             // 12: profile class: display
        w.WriteAscii4("RGB ");             // 16: color space: RGB
        w.WriteAscii4("XYZ ");             // 20: PCS: XYZ
        // 24: creation date-time (6 × u16): 2026-01-01 00:00:00
        w.WriteU16(2026); w.WriteU16(1); w.WriteU16(1);
        w.WriteU16(0); w.WriteU16(0); w.WriteU16(0);
        w.WriteAscii4("acsp");             // 36: file signature
        w.WriteAscii4("    ");             // 40: primary platform (none)
        w.WriteU32(0);                     // 44: profile flags
        w.WriteAscii4("    ");             // 48: device manufacturer
        w.WriteAscii4("    ");             // 52: device model
        w.WriteU32(0); w.WriteU32(0);      // 56: device attributes (64-bit)
        w.WriteU32(0);                     // 64: rendering intent: perceptual
        // 68: PCS illuminant = D50 (X=0.9642, Y=1.0, Z=0.8249)
        w.WriteS15F16(0.9642);
        w.WriteS15F16(1.0000);
        w.WriteS15F16(0.8249);
        w.WriteAscii4("VLLM");             // 80: profile creator
        // 84–127: profile ID (zeros) + reserved (zeros)
        for (var i = 0; i < 44; i++) w.WriteByte(0);

        // ── Tag count ─────────────────────────────────────────────────────────
        w.WriteU32((uint)tagCount);

        // ── Tag table entries ─────────────────────────────────────────────────
        void WriteEntry(string sig, int off, int len)
        {
            w.WriteAscii4(sig);
            w.WriteU32((uint)off);
            w.WriteU32((uint)len);
        }
        WriteEntry("cprt", cprtOff, cprtData.Length);
        WriteEntry("desc", descOff, descData.Length);
        WriteEntry("wtpt", wtptOff, wtptData.Length);
        WriteEntry("rXYZ", rXyzOff, rXyzData.Length);
        WriteEntry("gXYZ", gXyzOff, gXyzData.Length);
        WriteEntry("bXYZ", bXyzOff, bXyzData.Length);
        WriteEntry("rTRC", rTrcOff, trcData.Length);
        WriteEntry("gTRC", gTrcOff, trcData.Length);
        WriteEntry("bTRC", bTrcOff, trcData.Length);

        // ── Tag data bodies ────────────────────────────────────────────────────
        w.WriteAligned(cprtData);
        w.WriteAligned(descData);
        w.WriteAligned(wtptData);
        w.WriteAligned(rXyzData);
        w.WriteAligned(gXyzData);
        w.WriteAligned(bXyzData);
        w.WriteAligned(trcData); // rTRC
        w.WriteAligned(trcData); // gTRC (same curve)
        w.WriteAligned(trcData); // bTRC (same curve)

        var result = w.ToArray();

        // Patch the profile size header with the actual byte length (padding may vary).
        var actual = result.Length;
        result[0] = (byte)(actual >> 24);
        result[1] = (byte)((actual >> 16) & 0xFF);
        result[2] = (byte)((actual >> 8) & 0xFF);
        result[3] = (byte)(actual & 0xFF);

        return result;
    }

    // ── Tag builders ──────────────────────────────────────────────────────────

    /// <summary>Builds an ICC v2 <c>text</c> tag body.</summary>
    private static byte[] BuildTextTag(string text)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(text);
        // 'text' type: sig(4) + reserved(4) + ASCII text + NUL
        var data = new byte[8 + ascii.Length + 1];
        data[0] = (byte)'t'; data[1] = (byte)'e'; data[2] = (byte)'x'; data[3] = (byte)'t';
        // reserved 4..7 = zeros
        ascii.CopyTo(data, 8);
        // NUL at end is zero-initialized
        return data;
    }

    /// <summary>
    /// Builds an ICC v2 <c>desc</c> (profileDescriptionTag) tag body.
    /// Per ICC spec 4.14 (v2.4): sig(4) + reserved(4) + invariantLength(4) + ASCII(n+1) +
    /// unicodeLanguageCode(4) + unicodeCount(4) + unicodeDescription(2×count) +
    /// scriptCodeCode(2) + macDescriptionCount(1) + macDescription(67).
    /// </summary>
    private static byte[] BuildDescTag(string text)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(text);
        var invariantLen = ascii.Length + 1; // includes NUL terminator
        // Full ICC v2 desc tag:
        // sig(4) + reserved(4) + invariantLen(4) + ASCII+NUL
        // + unicodeLanguageCode(4) + unicodeCount(4)   [no unicode data since count=0]
        // + scriptCodeCode(2) + macDescriptionCount(1) + macDescription(67)
        const int trailingSize = 4 + 4 + 2 + 1 + 67; // 78 bytes
        var total = 4 + 4 + 4 + invariantLen + trailingSize;
        var data = new byte[total];
        var pos = 0;
        data[pos++] = (byte)'d'; data[pos++] = (byte)'e'; data[pos++] = (byte)'s'; data[pos++] = (byte)'c';
        // reserved 4 bytes
        pos += 4;
        // invariant length (big-endian u32)
        data[pos++] = (byte)((invariantLen >> 24) & 0xFF);
        data[pos++] = (byte)((invariantLen >> 16) & 0xFF);
        data[pos++] = (byte)((invariantLen >> 8) & 0xFF);
        data[pos++] = (byte)(invariantLen & 0xFF);
        // ASCII text + NUL (NUL is zero-initialized)
        foreach (var b in ascii) data[pos++] = b;
        pos++; // NUL
        // unicodeLanguageCode = 0 (4 bytes, zero-initialized)
        pos += 4;
        // unicodeCount = 0 (4 bytes, zero-initialized)
        pos += 4;
        // scriptCodeCode = 0 (2 bytes), macDescriptionCount = 0 (1 byte),
        // macDescription = 67 zero bytes — all already zero-initialized
        return data;
    }

    /// <summary>Builds an ICC v2 <c>XYZ </c> tag body (3 s15Fixed16 values).</summary>
    private static byte[] BuildXyzTag(double x, double y, double z)
    {
        // 'XYZ ': sig(4) + reserved(4) + x(4) + y(4) + z(4) = 20 bytes
        var data = new byte[20];
        data[0] = (byte)'X'; data[1] = (byte)'Y'; data[2] = (byte)'Z'; data[3] = (byte)' ';
        // reserved 4..7 = zeros
        WriteS15F16Into(data, 8, x);
        WriteS15F16Into(data, 12, y);
        WriteS15F16Into(data, 16, z);
        return data;
    }

    /// <summary>
    /// Builds an ICC v2 <c>curv</c> tag with 256 entries approximating the sRGB TRC.
    /// sRGB formula: linear = ((V + 0.055)/1.055)^2.4 for V &gt; 0.04045, else V/12.92.
    /// </summary>
    private static byte[] BuildSrgbCurvTag()
    {
        const int count = 256;
        // 'curv': sig(4) + reserved(4) + count(4) + values(count × 2)
        var data = new byte[12 + count * 2];
        data[0] = (byte)'c'; data[1] = (byte)'u'; data[2] = (byte)'r'; data[3] = (byte)'v';
        // reserved 4..7 = zeros
        // count (u32 BE)
        data[8] = 0; data[9] = 0; data[10] = (byte)(count >> 8); data[11] = (byte)(count & 0xFF);

        for (var i = 0; i < count; i++)
        {
            var v = i / 255.0;
            double linear = v <= 0.04045
                ? v / 12.92
                : Math.Pow((v + 0.055) / 1.055, 2.4);
            linear = Math.Max(0.0, Math.Min(1.0, linear));
            var u16 = (ushort)Math.Round(linear * 65535.0);
            data[12 + i * 2] = (byte)(u16 >> 8);
            data[12 + i * 2 + 1] = (byte)(u16 & 0xFF);
        }
        return data;
    }

    private static void WriteS15F16Into(byte[] data, int offset, double value)
    {
        var raw = (int)Math.Round(value * 65536.0);
        data[offset] = (byte)((raw >> 24) & 0xFF);
        data[offset + 1] = (byte)((raw >> 16) & 0xFF);
        data[offset + 2] = (byte)((raw >> 8) & 0xFF);
        data[offset + 3] = (byte)(raw & 0xFF);
    }

    // ── ProfileWriter ─────────────────────────────────────────────────────────

    private sealed class ProfileWriter
    {
        private readonly System.IO.MemoryStream _ms = new();

        public void WriteByte(byte b) => _ms.WriteByte(b);

        public void WriteU16(int v)
        {
            _ms.WriteByte((byte)((v >> 8) & 0xFF));
            _ms.WriteByte((byte)(v & 0xFF));
        }

        public void WriteU32(uint v)
        {
            _ms.WriteByte((byte)((v >> 24) & 0xFF));
            _ms.WriteByte((byte)((v >> 16) & 0xFF));
            _ms.WriteByte((byte)((v >> 8) & 0xFF));
            _ms.WriteByte((byte)(v & 0xFF));
        }

        public void WriteAscii4(string s)
        {
            for (var i = 0; i < 4; i++)
                _ms.WriteByte(i < s.Length ? (byte)s[i] : (byte)' ');
        }

        public void WriteS15F16(double value)
        {
            var raw = (int)Math.Round(value * 65536.0);
            _ms.WriteByte((byte)((raw >> 24) & 0xFF));
            _ms.WriteByte((byte)((raw >> 16) & 0xFF));
            _ms.WriteByte((byte)((raw >> 8) & 0xFF));
            _ms.WriteByte((byte)(raw & 0xFF));
        }

        /// <summary>Writes the byte array and zero-pads to the next 4-byte boundary.</summary>
        public void WriteAligned(byte[] data)
        {
            _ms.Write(data);
            var rem = (4 - (data.Length % 4)) % 4;
            for (var i = 0; i < rem; i++) _ms.WriteByte(0);
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}
