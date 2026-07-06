// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>
/// A rectangular grid of dark/light modules produced by a 2D symbology (QR, Micro QR, PDF417).
/// Bit-packed for compactness; row-major, <c>(0, 0)</c> is the top-left module.
/// </summary>
public sealed class BarcodeMatrix
{
    private readonly ulong[] _bits;
    private readonly int _wordsPerRow;

    internal BarcodeMatrix(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Matrix width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Matrix height must be positive.");

        Width = width;
        Height = height;
        _wordsPerRow = (width + 63) / 64;
        _bits = new ulong[_wordsPerRow * height];
    }

    /// <summary>The number of modules across.</summary>
    public int Width { get; }

    /// <summary>The number of modules down.</summary>
    public int Height { get; }

    /// <summary>Returns whether the module at <paramref name="x"/>, <paramref name="y"/> is dark.</summary>
    public bool IsDark(int x, int y)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x), x, "X is outside the matrix.");
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y), y, "Y is outside the matrix.");

        var index = (y * _wordsPerRow) + (x / 64);
        var bit = x % 64;
        return (_bits[index] & (1UL << bit)) != 0;
    }

    /// <summary>Sets the module at <paramref name="x"/>, <paramref name="y"/>. Used by the 2D matrix builders.</summary>
    internal void SetDark(int x, int y, bool value)
    {
        var index = (y * _wordsPerRow) + (x / 64);
        var bit = x % 64;
        if (value) _bits[index] |= 1UL << bit;
        else _bits[index] &= ~(1UL << bit);
    }
}
