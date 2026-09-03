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
/// budget for cross-reference reconstruction (ISO 32000-2 Annex C.4, informative).
/// </param>
/// <param name="MaxDiagnostics">
/// The cap <see cref="DiagnosticSink"/> enforces on <see cref="PdfDocumentReader.Diagnostics"/> —
/// see <see cref="PdfReaderOptions.MaxDiagnostics"/>.
/// </param>
/// <param name="MaxFormXObjectDepth">
/// The nesting-depth ceiling <c>ContentInterpreter</c> enforces on recursive Form XObject <c>Do</c>
/// invocations (ISO 32000-2 §8.10); see <see cref="PdfReaderOptions.MaxFormXObjectDepth"/>.
/// </param>
internal readonly record struct ReaderLimits(
    long MaxDecodedBytes,
    long MaxAggregateReconstructionDecodeBytes,
    int ReconstructionBudgetMultiplier,
    int MaxDiagnostics,
    int MaxFormXObjectDepth)
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

    /// <summary>The processor's own choice of default diagnostics cap: 1000 entries.</summary>
    internal const int DefaultMaxDiagnostics = 1000;

    /// <summary>
    /// The floor a caller may tighten <see cref="PdfReaderOptions.MaxDiagnostics"/> down to: 1.
    /// Zero would turn every report into a suppression count, disabling the channel entirely.
    /// </summary>
    internal const int MinMaxDiagnostics = 1;

    /// <summary>The processor's own choice of default Form XObject recursion depth ceiling: 32.</summary>
    internal const int DefaultMaxFormXObjectDepth = 32;

    /// <summary>
    /// The floor a caller may tighten <see cref="PdfReaderOptions.MaxFormXObjectDepth"/> down to: 1
    /// (a Form XObject may still be invoked, but may not itself invoke another one).
    /// </summary>
    internal const int MinMaxFormXObjectDepth = 1;

    /// <summary>The library's built-in ceilings — what every read used before this option existed.</summary>
    internal static ReaderLimits Defaults { get; } =
        new(
            DefaultMaxDecodedBytes, DefaultMaxDecodedBytes, DefaultReconstructionBudgetMultiplier,
            DefaultMaxDiagnostics, DefaultMaxFormXObjectDepth);

    /// <summary>
    /// Validates <paramref name="options"/>'s four resource knobs and resolves them into the
    /// limits threaded through one read.
    /// </summary>
    /// <remarks>
    /// Tighten-only: ISO 32000-2 Annex C.1 (informative) states that "a particular PDF processor
    /// running on a particular device and in a particular operating environment will always have
    /// practical limits", and Annex C.3 (informative) adds that available memory is "often much less
    /// in mobile devices than desktop computers" — the ceiling is this processor's own choice, not a
    /// spec requirement, so <see cref="DefaultMaxDecodedBytes"/>,
    /// <see cref="DefaultReconstructionBudgetMultiplier"/>, <see cref="DefaultMaxDiagnostics"/>, and
    /// <see cref="DefaultMaxFormXObjectDepth"/> are each a safe upper bound a caller may only lower, never raise. A value under the
    /// corresponding floor is rejected too: below <see cref="MinMaxDecodedBytes"/> or
    /// <see cref="MinReconstructionBudgetMultiplier"/>, an otherwise ordinary document routinely
    /// fails to decode or reconstruct at all; below <see cref="MinMaxDiagnostics"/> every report
    /// would turn into a suppression count, disabling the channel entirely; below
    /// <see cref="MinMaxFormXObjectDepth"/> no Form XObject could be entered at all. Each of these is a
    /// configuration mistake worth surfacing immediately, here, rather than as a confusing
    /// exception from a different layer downstream.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/>,
    /// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/>,
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/>, or
    /// <see cref="PdfReaderOptions.MaxFormXObjectDepth"/> is outside its allowed range.
    /// </exception>
    internal static ReaderLimits Resolve(PdfReaderOptions options)
    {
        var maxDecodedBytes = options.MaxDecodedStreamBytes;
        if (maxDecodedBytes < MinMaxDecodedBytes || maxDecodedBytes > DefaultMaxDecodedBytes)
            throw new ArgumentOutOfRangeException(
                nameof(PdfReaderOptions.MaxDecodedStreamBytes), maxDecodedBytes,
                $"{nameof(PdfReaderOptions.MaxDecodedStreamBytes)} must be between {MinMaxDecodedBytes} "
                + $"and {DefaultMaxDecodedBytes} bytes ({MinMaxDecodedBytes / 1024.0 / 1024.0:F0} MiB to "
                + $"{DefaultMaxDecodedBytes / 1024.0 / 1024.0:F0} MiB).");

        var multiplier = options.ReconstructionBudgetMultiplier;
        if (multiplier < MinReconstructionBudgetMultiplier || multiplier > DefaultReconstructionBudgetMultiplier)
            throw new ArgumentOutOfRangeException(
                nameof(PdfReaderOptions.ReconstructionBudgetMultiplier), multiplier,
                $"{nameof(PdfReaderOptions.ReconstructionBudgetMultiplier)} must be between "
                + $"{MinReconstructionBudgetMultiplier} and {DefaultReconstructionBudgetMultiplier}.");

        var maxDiagnostics = options.MaxDiagnostics;
        if (maxDiagnostics < MinMaxDiagnostics || maxDiagnostics > DefaultMaxDiagnostics)
            throw new ArgumentOutOfRangeException(
                nameof(PdfReaderOptions.MaxDiagnostics), maxDiagnostics,
                $"{nameof(PdfReaderOptions.MaxDiagnostics)} must be between "
                + $"{MinMaxDiagnostics} and {DefaultMaxDiagnostics}.");

        var maxFormXObjectDepth = options.MaxFormXObjectDepth;
        if (maxFormXObjectDepth < MinMaxFormXObjectDepth || maxFormXObjectDepth > DefaultMaxFormXObjectDepth)
            throw new ArgumentOutOfRangeException(
                nameof(PdfReaderOptions.MaxFormXObjectDepth), maxFormXObjectDepth,
                $"{nameof(PdfReaderOptions.MaxFormXObjectDepth)} must be between "
                + $"{MinMaxFormXObjectDepth} and {DefaultMaxFormXObjectDepth}.");

        return new ReaderLimits(
            maxDecodedBytes, maxDecodedBytes, multiplier, maxDiagnostics, maxFormXObjectDepth);
    }
}
