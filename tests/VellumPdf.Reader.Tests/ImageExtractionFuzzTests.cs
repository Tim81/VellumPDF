// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using CsCheck;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Images;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <c>ExtractImages</c> (#98): byte-level mutation of two seed documents built fresh for this file
/// (one plaintext, covering a Raw grey image, a DCT passthrough image, an Indexed image reached
/// both directly and through a resource name, a Flate-compressed image under a TIFF predictor, a
/// soft-masked and an explicit-masked image, a /Decode array, two large Raw images, an annotation
/// appearance drawing its own image, and one inline image; one AES-256-encrypted, so the corpus
/// also reaches the decryption path), asserting the invariants a hostile or corrupted document
/// must never violate. Every assertion runs under a TIGHTENED
/// <see cref="ReaderLimits.MinMaxDecodedBytes"/> (1 MiB) rather than the 512 MiB default, so the
/// two large seed images below land within reach of the ceiling at a few hundred kilobytes each,
/// not hundreds of megabytes; the tightened number by itself proves nothing; what makes the size
/// assertions below capable of failing is a seed whose own decoded size is comparable to whichever
/// ceiling is in force.
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

    private static byte[] Flate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    // The seed: one page drawing a Raw grey XObject, a DCT passthrough XObject, an Indexed
    // XObject, a Flate-compressed Raw XObject under a TIFF predictor, a soft-masked XObject
    // carrying a /Decode array, a stencil-masked XObject, an Indexed XObject resolved through a
    // resource name, two large Flate-compressed Raw XObjects, an annotation whose /AP /N draws its
    // own image, and one inline image. Built fresh rather than reused from ParserFuzzTests'
    // embedded-resource corpus, which carries no images at all.
    //
    // /Im7 and /Im8 (objects 30, 31) exist so the aggregate byte ceiling this property checks is
    // reachable at all. Without them, every seed image inflates to a handful of bytes, and the
    // mutator (single-byte edits, a duplication capped at 32 bytes per op and 8 ops, a 1 MiB buffer
    // cap) cannot assemble a compressed body decoding past a few hundred bytes from any of them: no
    // number of iterations gets the sum-of-buffers assertion within striking distance of the 1 MiB
    // tightened ceiling, and ImageExtractionBudgetExhausted (510) never fires. A Flate body that
    // already decodes to 700,000 bytes closes most of that gap before any mutation runs, and puts
    // both under test. ImageDataUnreadable (508, a decode failure past the point a filter chain
    // commits to one) needed no such change: it already fires on a large minority of iterations
    // under arbitrary corruption alone, which is the one thing this corpus adds that a
    // known-answer test cannot. ImageOccurrenceLimitExceeded (511, 100,000 occurrences in one call)
    // stays out of reach by design at any seed size sane enough to keep this test fast; a
    // known-answer test is the right instrument for that one, not a larger corpus.
    private static byte[] LargeGrayBody(int length)
    {
        var body = new byte[length];
        for (var i = 0; i < length; i++)
            body[i] = (byte)(i % 251);
        return body;
    }

    private static byte[] FlateSmallest(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    private static readonly byte[] FuzzSeed = BuildPdf(1,
        new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new Obj(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
            + "/Resources << /XObject << /Im0 10 0 R /Im1 11 0 R /Im2 12 0 R /Im3 13 0 R "
            + "/Im4 15 0 R /Im5 17 0 R /Im6 18 0 R /Im7 30 0 R /Im8 31 0 R >> "
            + "/ColorSpace << /CS0 [/Indexed /DeviceRGB 1 <FF000000FF00>] >> >> "
            + "/Annots [20 0 R] /Contents 4 0 R >>"),
        new Obj(4, "<< >>",
            Encoding.Latin1.GetBytes(
                "/Im0 Do\n/Im1 Do\n/Im2 Do\n/Im3 Do\n/Im4 Do\n/Im5 Do\n/Im6 Do\n/Im7 Do\n/Im8 Do\n"
                + "BI /W 2 /H 2 /CS /G /BPC 8 /L 4 ID \x01\x02\x03\x04 EI")),
        new Obj(10,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray >>", [0x11, 0x22, 0x33, 0x44]),
        new Obj(11,
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceRGB /Filter /DCTDecode >>", "NOT-REALLY-A-JPEG"u8.ToArray()),
        new Obj(12,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>] >>", [0, 1]),
        // A Flate-compressed Raw image under a TIFF predictor (#376): 2x2 grey 8bpc,
        // horizontally differenced (byte0 unchanged, byte1 -= byte0 per row) then Flate-compressed,
        // so the fuzz corpus exercises a compressed image whose retained decode differs from its
        // stored body length.
        new Obj(13,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Filter /FlateDecode "
            + "/DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns 2 >> >>",
            Flate([0x10, 0x10, 0x30, 0x10])),
        // The soft mask a /SMask entry names (object 15 below).
        new Obj(14,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray >>", [0x01, 0x02, 0x03, 0x04]),
        // A base image carrying both /SMask and /Decode.
        new Obj(15,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /SMask 14 0 R /Decode [0 1] >>", [0x11, 0x22, 0x33, 0x44]),
        // The stencil mask a /Mask entry names (object 17 below).
        new Obj(16,
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true >>", [0b10101010]),
        // A base image carrying an explicit /Mask.
        new Obj(17,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Mask 16 0 R >>", [0xAA, 0xBB, 0xCC, 0xDD]),
        // An Indexed space resolved through the page's own /ColorSpace resource name /CS0.
        new Obj(18,
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /CS0 >>", [0, 1]),
        new Obj(20, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /AP << /N 21 0 R >> >>"),
        new Obj(21,
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
            + "/Resources << /XObject << /ImA 22 0 R >> >> >>", "/ImA Do"u8.ToArray()),
        new Obj(22,
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray >>", [0x55]),
        // 1000 x 700 x 8bpc DeviceGray, 700,000 bytes decoded each, compressed at
        // CompressionLevel.SmallestSize rather than the Fastest every other seed body uses above,
        // so the two of them add on the order of a kilobyte to this file rather than an order of
        // magnitude more.
        new Obj(30,
            "<< /Type /XObject /Subtype /Image /Width 1000 /Height 700 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", FlateSmallest(LargeGrayBody(700_000))),
        new Obj(31,
            "<< /Type /XObject /Subtype /Image /Width 1000 /Height 700 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", FlateSmallest(LargeGrayBody(700_000))));

    // A second seed built with the Kernel writer, encrypted (AES-256, empty user password), so the
    // fuzz corpus also reaches the DecryptedStreamView path an encrypted document's image bytes
    // travel through. The writer (StandardSecurityHandler) only ever produces V=5/R=6 (AES-256);
    // it exposes no algorithm knob, so an AES-128 seed would need a hand-rolled V=4/R=4 /Encrypt
    // dictionary computed independently of the writer, declined here as more risk than the extra
    // shape is worth.
    private static byte[] BuildEncryptedFuzzSeed()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var image = new PdfImageXObject(
            width: 2, height: 2, streamData: [0x11, 0x22, 0x33, 0x44], filter: PdfName.FlateDecode,
            colorSpace: ImageColorSpace.DeviceGray, bitsPerComponent: 8);
        doc.RegisterImageXObject(page, image, "Im0");
        var canvas = new PdfCanvas(page);
        canvas.DoXObject("Im0");
        canvas.Finish();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "" });
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static readonly byte[] EncryptedFuzzSeed = BuildEncryptedFuzzSeed();

    private readonly record struct MutationOp(int Kind, int Position, byte Value, int Length);

    private static readonly Gen<MutationOp> MutationOpGen =
        Gen.Select(
            Gen.Int[0, 5], Gen.Int[0, int.MaxValue], Gen.Byte, Gen.Int[1, 32],
            (kind, position, value, length) => new MutationOp(kind, position, value, length));

    // Half the sampled cases mutate the plaintext seed, half the encrypted one: two independent
    // corpora rather than one corpus diluted by a coin flip nobody can see the effect of.
    private static readonly Gen<byte[]> SeedGen =
        Gen.Int[0, 1].Select(i => i == 0 ? FuzzSeed : EncryptedFuzzSeed);

    private static readonly Gen<byte[]> FuzzInputGen =
        Gen.Select(SeedGen, MutationOpGen.Array[1, 8], (seed, ops) => Mutate(seed, ops));

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
                    // longer buffer exactly as decoded, with no diagnostic raised over the excess
                    // (the mirror case, a SHORT buffer, is what ImageSampleDataShort (504) reports;
                    // a long one is not a defect this reader has a rule to invent). An unfiltered
                    // stream's own decode has no row-truncation step to bound it by. Only the
                    // aggregate MaxDecodedBytes ceiling checked below still applies.
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
