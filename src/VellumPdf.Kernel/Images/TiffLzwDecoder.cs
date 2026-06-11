// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Images;

/// <summary>
/// TIFF-variant LZW decoder.
///
/// Differences from GIF LZW:
///   • Codes are packed MSB-first (high bits of each byte are consumed first).
///   • Code width starts at 9 bits (not minCodeSize+1).
///   • "Early change": the code width increases when the next code to be ASSIGNED
///     equals (1 &lt;&lt; currentWidth) - 1, i.e. one entry earlier than GIF's rule.
///   • ClearCode = 256, EndOfInformation = 257. First free entry = 258.
///   • Maximum code width = 12 bits (table size 4096).
/// </summary>
internal static class TiffLzwDecoder
{
    private const int ClearCode = 256;
    private const int EoiCode = 257;
    private const int FirstFreeCode = 258;
    private const int MaxTableSize = 4096;
    private const int StartCodeWidth = 9;
    private const int MaxCodeWidth = 12;

    /// <summary>
    /// Decodes a TIFF LZW-compressed strip.
    /// </summary>
    /// <param name="src">The source byte array containing the compressed strip data.</param>
    /// <param name="srcOffset">Byte offset within <paramref name="src"/> where the strip begins.</param>
    /// <param name="srcLength">Number of compressed bytes in the strip.</param>
    /// <param name="expectedOutput">Expected number of decompressed bytes (validated on completion).</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="InvalidDataException">Thrown on overflow, invalid codes, or output length mismatch.</exception>
    public static byte[] Decode(byte[] src, int srcOffset, int srcLength, int expectedOutput)
    {
        var output = new byte[expectedOutput];
        var outIdx = 0;

        // LZW table: linked-list encoding.
        // tablePrefix[i] = parent code (-1 for literal root entries)
        // tableSuffix[i] = last byte of this entry
        var tablePrefix = new int[MaxTableSize];
        var tableSuffix = new byte[MaxTableSize];

        // Initialise literal root entries 0..255.
        for (var i = 0; i < 256; i++)
        {
            tablePrefix[i] = -1;
            tableSuffix[i] = (byte)i;
        }
        tablePrefix[ClearCode] = -1;
        tablePrefix[EoiCode] = -1;

        // Decode state
        var codeWidth = StartCodeWidth;
        var nextCode = FirstFreeCode;
        var prevCode = -1;

        // MSB-first bit buffer
        var bitBuf = 0;
        var bitsLeft = 0;
        var streamPos = srcOffset;
        var streamEnd = srcOffset + srcLength;

        // Stack for reversing chain walks
        var stack = new byte[MaxTableSize];

        // Helper: read next bits from MSB-first stream
        int ReadCode()
        {
            while (bitsLeft < codeWidth && streamPos < streamEnd)
            {
                bitBuf = (bitBuf << 8) | src[streamPos++];
                bitsLeft += 8;
            }
            if (bitsLeft < codeWidth)
                return -1; // truncated
            bitsLeft -= codeWidth;
            return (bitBuf >> bitsLeft) & ((1 << codeWidth) - 1);
        }

        while (outIdx < expectedOutput)
        {
            var code = ReadCode();
            if (code < 0 || code == EoiCode)
                break;

            if (code == ClearCode)
            {
                // Reset table and code width
                codeWidth = StartCodeWidth;
                nextCode = FirstFreeCode;
                prevCode = -1;
                continue;
            }

            // Resolve the code into a byte sequence.
            // Special KwKwK case: code == nextCode means it refers to the entry
            // about to be created (prevCode + prevCode's first byte).
            int resolvedCode;
            if (code < nextCode)
            {
                resolvedCode = code;
            }
            else if (code == nextCode && prevCode >= 0)
            {
                resolvedCode = prevCode;
            }
            else
            {
                throw new InvalidDataException(
                    $"TIFF LZW invalid code {code} (nextCode={nextCode}); data may be corrupt.");
            }

            // Walk the chain and push bytes onto the stack (tail-first → reversed).
            // Cap to MaxTableSize iterations to guard against cyclic/corrupt chains.
            var stackTop = 0;
            var cur = resolvedCode;
            while (cur >= 0)
            {
                if (stackTop >= MaxTableSize)
                    throw new InvalidDataException(
                        "TIFF LZW chain exceeds maximum table size; data may be corrupt.");
                stack[stackTop++] = tableSuffix[cur];
                cur = tablePrefix[cur];
            }

            // Pop stack into output (stack is reversed — bottom is first byte).
            for (var i = stackTop - 1; i >= 0; i--)
            {
                if (outIdx >= expectedOutput)
                    throw new InvalidDataException(
                        $"TIFF LZW decompressed more bytes than expected ({expectedOutput}).");
                output[outIdx++] = stack[i];
            }

            // KwKwK: the new entry's string is prevCode's string + firstByte.
            // Emit firstByte after the rest of the string (stack[stackTop-1] is the first
            // byte of prevCode's decoded string, which equals the appended byte).
            if (code == nextCode)
            {
                if (outIdx >= expectedOutput)
                    throw new InvalidDataException(
                        $"TIFF LZW decompressed more bytes than expected ({expectedOutput}).");
                output[outIdx++] = stack[stackTop - 1];
            }

            // Add new table entry: prevCode's sequence + first byte of current entry.
            // stack[stackTop-1] is the first byte of the decoded string.
            if (prevCode >= 0 && nextCode < MaxTableSize)
            {
                var firstByte = stack[stackTop - 1];
                tablePrefix[nextCode] = prevCode;
                tableSuffix[nextCode] = firstByte;

                // TIFF early-change: grow code width when the next entry to be ASSIGNED
                // would be (1 << codeWidth) - 1, i.e. one entry before the table is full.
                nextCode++;
                if (nextCode == (1 << codeWidth) - 1 && codeWidth < MaxCodeWidth)
                    codeWidth++;
            }

            prevCode = code;
        }

        if (outIdx != expectedOutput)
            throw new InvalidDataException(
                $"TIFF LZW decoded {outIdx} bytes but expected {expectedOutput}.");

        return output;
    }
}
