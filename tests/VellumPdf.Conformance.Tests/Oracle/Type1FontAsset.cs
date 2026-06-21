// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>
/// Loads the embedded Noto Sans Shavian Type 1 (PFB) asset and converts it to the raw byte layout a
/// PDF <c>/FontFile</c> stream expects: the three PFB segments (clear text, binary eexec, fixed
/// trailer) concatenated, with the per-segment lengths reported as <c>/Length1</c>, <c>/Length2</c>,
/// <c>/Length3</c>. Shared by the parser unit test and the oracle fixtures.
/// </summary>
internal static class Type1FontAsset
{
    public const string LogicalName = "NotoSansShavian-Regular.pfb";

    /// <summary>The de-segmented font program with its three Type 1 portion lengths.</summary>
    public static (byte[] FontFile, int Length1, int Length2, int Length3) ToFontFile()
    {
        var pfb = LoadPfb();
        var ascii1 = new List<byte>();
        var binary = new List<byte>();
        var ascii2 = new List<byte>();

        var i = 0;
        var sawBinary = false;
        while (i < pfb.Length && pfb[i] == 0x80)
        {
            var type = pfb[i + 1];
            if (type == 3) // EOF marker
                break;
            var len = pfb[i + 2] | (pfb[i + 3] << 8) | (pfb[i + 4] << 16) | (pfb[i + 5] << 24);
            var payload = pfb.AsSpan(i + 6, len);
            if (type == 2)
            {
                binary.AddRange(payload);
                sawBinary = true;
            }
            else
            {
                (sawBinary ? ascii2 : ascii1).AddRange(payload);
            }

            i += 6 + len;
        }

        var fontFile = ascii1.Concat(binary).Concat(ascii2).ToArray();
        return (fontFile, ascii1.Count, binary.Count, ascii2.Count);
    }

    public static byte[] LoadPfb()
    {
        using var s = typeof(Type1FontAsset).Assembly.GetManifestResourceStream(LogicalName)
            ?? throw new InvalidOperationException($"Embedded asset '{LogicalName}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
