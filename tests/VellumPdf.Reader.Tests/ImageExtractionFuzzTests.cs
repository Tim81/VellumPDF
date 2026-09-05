// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using CsCheck;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <c>ExtractImages</c> (#98): byte-level mutation of a seed document built fresh for this file
/// (a Raw grey image, a DCT passthrough image, an Indexed image, and an inline
/// image), asserting the invariants a hostile or corrupted document must never violate. Every
/// assertion runs under a TIGHTENED <see cref="ReaderLimits.MinMaxDecodedBytes"/> (1 MiB): at the
/// 512 MiB default, the size invariants below would hold vacuously against fixtures this small.
/// </summary>
public sealed class ImageExtractionFuzzTests
{
    private sealed record Obj(int Num, string Dict, byte[]? Stream = null);

    private static byte[] BuildPdf(int rootObjectNumber, params Obj[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");

        var maxNum = objects.Max(o => o.Num);
        var offsets = new int?[maxNum + 1];
        foreach (var obj in objects.OrderBy(o => o.Num))
        {
            offsets[obj.Num] = (int)ms.Position;
            if (obj.Stream is null)
            {
                W($"{obj.Num} 0 obj\n{obj.Dict}\nendobj\n");
            }
            else
            {
                var trimmed = obj.Dict.TrimEnd();
                var withLength = trimmed[..^2].TrimEnd() + $" /Length {obj.Stream.Length} >>";
                W($"{obj.Num} 0 obj\n{withLength}\nstream\n");
                ms.Write(obj.Stream);
                W("\nendstream\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {maxNum + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= maxNum; i++)
        {
            W(offsets[i] is { } offset
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        }
        W($"trailer\n<< /Size {maxNum + 1} /Root {rootObjectNumber} 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    // The seed: one page drawing a Raw grey XObject, a DCT passthrough XObject, an Indexed
    // XObject, and one inline image, built fresh rather than reused from
    // ParserFuzzTests' embedded-resource corpus, which carries no images at all.
    private static readonly byte[] FuzzSeed = BuildPdf(1,
        new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new Obj(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
            + "/Resources << /XObject << /Im0 10 0 R /Im1 11 0 R /Im2 12 0 R >> >> /Contents 4 0 R >>"),
        new Obj(4, "<< >>",
            "/Im0 Do\n/Im1 Do\n/Im2 Do\nBI /W 2 /H 2 /CS /G /BPC 8 /L 4 ID \x01\x02\x03\x04 EI"u8.ToArray()),
        new Obj(10,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray >>", [0x11, 0x22, 0x33, 0x44]),
        new Obj(11,
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceRGB /Filter /DCTDecode >>", "NOT-REALLY-A-JPEG"u8.ToArray()),
        new Obj(12,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>] >>", [0, 1]));

    private readonly record struct MutationOp(int Kind, int Position, byte Value, int Length);

    private static readonly Gen<MutationOp> MutationOpGen =
        Gen.Select(
            Gen.Int[0, 5], Gen.Int[0, int.MaxValue], Gen.Byte, Gen.Int[1, 32],
            (kind, position, value, length) => new MutationOp(kind, position, value, length));

    private static readonly Gen<byte[]> FuzzInputGen =
        MutationOpGen.Array[1, 8].Select(ops => Mutate(FuzzSeed, ops));

    private static byte[] Mutate(byte[] seed, MutationOp[] ops)
    {
        var buffer = new List<byte>(seed);
        foreach (var op in ops)
        {
            if (buffer.Count == 0) { buffer.Add(op.Value); continue; }
            var position = op.Position % buffer.Count;
            switch (op.Kind)
            {
                case 0: buffer[position] ^= (byte)(1 << (op.Value % 8)); break;
                case 1: buffer[position] = op.Value; break;
                case 2: buffer.RemoveAt(position); break;
                case 3: buffer.Insert(position, op.Value); break;
                case 4:
                    var length = Math.Min(op.Length, buffer.Count - position);
                    if (length > 0 && buffer.Count + length <= 1 << 20)
                        buffer.InsertRange(position, buffer.GetRange(position, length));
                    break;
                case 5:
                    var cut = position + 1;
                    if (cut < buffer.Count)
                        buffer.RemoveRange(cut, buffer.Count - cut);
                    break;
            }
            if (buffer.Count > 1 << 20)
                buffer.RemoveRange(1 << 20, buffer.Count - (1 << 20));
        }
        return buffer.Count == 0 ? [0] : [.. buffer];
    }

    [Fact]
    public void Fuzz_extractImages_neverThrows_andRespectsEveryBound()
        // Block-bodied, matching ContentInterpreterTests' own reasoning: binds to CsCheck's
        // Action<T> Sample overload rather than the Func<T, bool> one.
        => FuzzInputGen.Sample(bytes => { AssertInvariants(bytes); }, iter: FuzzBudget.Iterations);

    private static void AssertInvariants(byte[] bytes)
    {
        PdfDocumentReader reader;
        try
        {
            reader = PdfReader.Open(
                bytes,
                new PdfReaderOptions
                {
                    AllowReconstruction = true,
                    MaxDecodedStreamBytes = ReaderLimits.MinMaxDecodedBytes,
                });
        }
        catch (InvalidDataException) { return; }
        catch (UnsupportedPdfFeatureException) { return; }
        catch (PdfPasswordException) { return; }

        using (reader)
        {
            // No catch: ExtractImages documents no exception beyond ObjectDisposedException, so
            // anything it throws on a mutated document fails the property.
            var result = reader.ExtractImages();

            long totalBytes = 0;
            foreach (var image in result.Images)
            {
                Assert.True(image.Width >= 1, $"Width {image.Width} < 1");
                Assert.True(image.Height >= 1, $"Height {image.Height} < 1");

                if (image.Encoding == PdfImageEncoding.Raw)
                {
                    Assert.True(
                        image.ColorSpace is not null || image.IsStencilMask,
                        "a Raw image with no colour space reached the returned list (the "
                        + "colour-space-resolution check should have skipped it)");
                    // No upper bound relative to rowBytes * Height: a Raw image's decode keeps a
                    // longer buffer exactly as decoded ("more bytes than expected are kept with no
                    // diagnostic... discarding them would be this reader inventing a rule"), since
                    // an unfiltered stream's own decode has no row-truncation step to bound it by.
                    // Only the aggregate MaxDecodedBytes ceiling checked below still applies.
                }

                Assert.True(
                    image.Data.Length <= ReaderLimits.MinMaxDecodedBytes,
                    $"image Data.Length {image.Data.Length} exceeds the tightened MaxDecodedBytes");

                totalBytes += image.Data.Length;
            }

            Assert.True(
                totalBytes <= ReaderLimits.MinMaxDecodedBytes,
                $"sum of Data.Length over every returned image ({totalBytes}) exceeds MaxDecodedBytes");
        }
    }

    private static class FuzzBudget
    {
        private const long DefaultIterations = 3_000;

        /// <summary>
        /// Iterations per fuzz case, overridable via <c>VELLUMPDF_FUZZ_ITER</c>. A third copy of
        /// <c>ParserFuzzTests.FuzzBudget</c> (the original; <c>ContentInterpreterTests</c> carries
        /// the second): lifting it into a shared file would touch <c>VellumPdf.TestSupport</c>,
        /// which the image and text lanes both build against in parallel this milestone, so a
        /// third small, independent copy here is cheaper than the cross-lane coupling a shared one
        /// would cost.
        /// </summary>
        internal static long Iterations
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("VELLUMPDF_FUZZ_ITER");
                return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultIterations;
            }
        }
    }
}
