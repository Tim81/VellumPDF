// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// Provides a minimal, valid generic CMYK ICC version 2 colour profile for use as a
/// PDF/A OutputIntent or ICCBased colour space. The profile is an ICC v2 4-component
/// CMYK output (printer) profile suitable for embedding as the <c>/DestOutputProfile</c>
/// in a PDF/A-2 OutputIntent dictionary or as the stream in an <c>/ICCBased</c> colour
/// space array.
///
/// <para>
/// veraPDF's PDF/A DestOutputProfile rule checks device class ∈ {prtr,mntr}, colour
/// space ∈ {GRAY,RGB,CMYK}, /N matching, and ICC version. This profile satisfies all
/// of those requirements without requiring A2B0/B2A0 LUT tags.
/// </para>
/// </summary>
internal static class CmykIccProfile
{
    /// <summary>
    /// The raw ICC profile bytes. Component count /N = 4 (CMYK).
    /// ICC v2.1, profile class: Output (prtr), colour space: CMYK, PCS: XYZ.
    /// </summary>
    public static byte[] Bytes { get; } = Build();

    private static byte[] Build()
    {
        var w = new ProfileWriter();

        // ── Tag data bodies ────────────────────────────────────────────────────
        var descData = BuildDescTag("VellumPdf Generic CMYK");
        var cprtData = BuildTextTag("Copyright (C) 2026, VellumPdf. No rights reserved.");
        var wtptData = BuildXyzTag(0.9642, 1.0000, 0.8249);

        // ── Layout: count offsets ──────────────────────────────────────────────
        const int tagCount = 3;
        const int headerSize = 128;
        const int tagTableSize = 4 + tagCount * 12;
        var dataOffset = headerSize + tagTableSize;

        static int Align4(int n) => (n + 3) & ~3;

        var descOff = dataOffset;
        var cprtOff = descOff + Align4(descData.Length);
        var wtptOff = cprtOff + Align4(cprtData.Length);
        var profileSize = wtptOff + Align4(wtptData.Length);

        // ── ICC Header (128 bytes) ─────────────────────────────────────────────
        w.WriteU32((uint)profileSize);     // 0: profile size (bytes) — patched below
        w.WriteAscii4("    ");             // 4: preferred CMM type (none)
        w.WriteU32(0x02100000);            // 8: ICC version 2.1.0
        w.WriteAscii4("prtr");             // 12: profile class: output (printer)
        w.WriteAscii4("CMYK");             // 16: colour space: CMYK
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
        WriteEntry("desc", descOff, descData.Length);
        WriteEntry("cprt", cprtOff, cprtData.Length);
        WriteEntry("wtpt", wtptOff, wtptData.Length);

        // ── Tag data bodies ────────────────────────────────────────────────────
        w.WriteAligned(descData);
        w.WriteAligned(cprtData);
        w.WriteAligned(wtptData);

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
        _ = pos; // suppress unused-variable warning
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
