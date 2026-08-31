// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// The resource ceilings in force for one <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/>
/// call, resolved from <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> and
/// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/> by <see cref="Resolve"/>. Threaded
/// into <see cref="XrefParser.Parse"/>, <see cref="XrefReconstructor.Reconstruct"/>,
/// <see cref="PdfDocumentReader"/>, and <see cref="PdfFilters"/> so a single caller-chosen setting
/// governs every decode and every reconstruction budget check made while reading one document,
/// instead of each site reading its own fixed constant.
/// </summary>
/// <param name="MaxDecodedBytes">
/// The per-decode ceiling <see cref="PdfFilters"/> enforces on FlateDecode, LZWDecode, and
/// RunLengthDecode output.
/// </param>
/// <param name="MaxAggregateReconstructionDecodeBytes">
/// The Phase B aggregate cap on raw object-stream body bytes decoded while expanding reconstructed
/// containers (<c>PdfDocumentReader.ReconstructionPhaseB</c>'s B1). Deliberately the same value as
/// <see cref="MaxDecodedBytes"/> — <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> is one
/// caller-facing knob for both.
/// </param>
/// <param name="ReconstructionBudgetMultiplier">
/// The multiplier in <see cref="XrefReconstructor"/>'s <c>max(1 MiB, N × file length)</c> work
/// budget for cross-reference reconstruction (ISO 32000-2 Annex C.4).
/// </param>
internal readonly record struct ReaderLimits(
    long MaxDecodedBytes, long MaxAggregateReconstructionDecodeBytes, int ReconstructionBudgetMultiplier)
{
    /// <summary>The processor's own choice of default per-decode ceiling: 512 MiB.</summary>
    internal const long DefaultMaxDecodedBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The floor a caller may tighten <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> down to:
    /// 1 MiB. Below this, ordinary Flate- or LZW-compressed page content routinely fails to decode.
    /// </summary>
    internal const long MinMaxDecodedBytes = 1L * 1024 * 1024;

    /// <summary>The processor's own choice of default reconstruction budget multiplier: 8.</summary>
    internal const int DefaultReconstructionBudgetMultiplier = 8;

    /// <summary>
    /// The floor a caller may tighten <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/>
    /// down to: 1. Below this the multiplier would stop scaling with file size at all.
    /// </summary>
    internal const int MinReconstructionBudgetMultiplier = 1;

    /// <summary>The library's built-in ceilings — what every read used before this option existed.</summary>
    internal static ReaderLimits Defaults { get; } =
        new(DefaultMaxDecodedBytes, DefaultMaxDecodedBytes, DefaultReconstructionBudgetMultiplier);

    /// <summary>
    /// Validates <paramref name="options"/>'s two resource knobs and resolves them into the limits
    /// threaded through one read.
    /// </summary>
    /// <remarks>
    /// Tighten-only: ISO 32000-2 Annex C.1 states plainly that "this PDF standard does not restrict
    /// the size or quantity of things described in the PDF file format", and C.3 notes that
    /// available memory "vary[ies] from one PDF processor to another" — the ceiling is this
    /// processor's own choice, not a spec requirement, so <see cref="DefaultMaxDecodedBytes"/> and
    /// <see cref="DefaultReconstructionBudgetMultiplier"/> are a safe upper bound a caller may only
    /// lower, never raise. A value under the corresponding floor is rejected too: below
    /// <see cref="MinMaxDecodedBytes"/> or <see cref="MinReconstructionBudgetMultiplier"/>, an
    /// otherwise ordinary document routinely fails to decode or reconstruct at all, which is a
    /// configuration mistake worth surfacing immediately rather than as a confusing downstream
    /// <see cref="InvalidDataException"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> or
    /// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/> is outside its allowed range.
    /// </exception>
    internal static ReaderLimits Resolve(PdfReaderOptions options)
    {
        var maxDecodedBytes = options.MaxDecodedStreamBytes;
        if (maxDecodedBytes < MinMaxDecodedBytes || maxDecodedBytes > DefaultMaxDecodedBytes)
            throw new ArgumentOutOfRangeException(
                nameof(PdfReaderOptions.MaxDecodedStreamBytes), maxDecodedBytes,
                $"{nameof(PdfReaderOptions.MaxDecodedStreamBytes)} must be between {MinMaxDecodedBytes} "
                + $"and {DefaultMaxDecodedBytes} bytes (1 MiB to 512 MiB).");

        var multiplier = options.ReconstructionBudgetMultiplier;
        if (multiplier < MinReconstructionBudgetMultiplier || multiplier > DefaultReconstructionBudgetMultiplier)
            throw new ArgumentOutOfRangeException(
                nameof(PdfReaderOptions.ReconstructionBudgetMultiplier), multiplier,
                $"{nameof(PdfReaderOptions.ReconstructionBudgetMultiplier)} must be between "
                + $"{MinReconstructionBudgetMultiplier} and {DefaultReconstructionBudgetMultiplier}.");

        return new ReaderLimits(maxDecodedBytes, maxDecodedBytes, multiplier);
    }
}
