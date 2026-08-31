// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Entry point for opening existing PDF documents for reading.
/// </summary>
public static class PdfReader
{
    /// <summary>Opens a PDF document from a byte array, with no password.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown for the same unsupported features as
    /// <see cref="Open(byte[], PdfReaderOptions)"/>.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and no supplied
    /// password authenticates as either the owner or the user password.</exception>
    public static PdfDocumentReader Open(byte[] bytes) => Open(bytes, new PdfReaderOptions());

    /// <summary>Opens a PDF document from a byte array.</summary>
    /// <param name="bytes">The document's raw bytes.</param>
    /// <param name="options">
    /// Settings for this read. Pass <see cref="PdfReaderOptions.Password"/> for an encrypted
    /// document; leave <see cref="PdfReaderOptions.Password"/> null for one that uses no password,
    /// or whose empty user password is enough. Set <see cref="PdfReaderOptions.AllowReconstruction"/>
    /// to recover a document whose cross-reference table is missing or broken.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> or
    /// <paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure — including a
    /// missing or unusable <c>startxref</c> when <see cref="PdfReaderOptions.AllowReconstruction"/>
    /// is left false (the message names the option), and, when it is true, exhaustion of
    /// reconstruction's own cost budget on a file with no encryption evidence.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when the document's security handler
    /// is not the Standard one (e.g. a public-key handler), when <c>/V</c> is 3 — the one value that
    /// names a real but unpublished algorithm, where the other illegal values are malformed rather
    /// than unimplementable — or when <c>/StrF</c> names a crypt filter whose <c>/CFM</c> it
    /// does not implement. An unresolvable <c>/StmF</c> does NOT throw here: streams are not decoded
    /// until something asks for them, and that failure is an
    /// <see cref="System.IO.InvalidDataException"/> at the decode call. Also thrown when
    /// <see cref="PdfReaderOptions.AllowReconstruction"/> is true and reconstruction finds any
    /// evidence — declared or structural — that the document is encrypted; rebuilding the
    /// cross-reference table of an encrypted document is not supported yet.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and
    /// <see cref="PdfReaderOptions.Password"/> authenticates as neither the owner nor the user
    /// password.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when
    /// <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> or
    /// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/> is set above its default or
    /// below its floor — see each property's own documentation for the allowed range.</exception>
    public static PdfDocumentReader Open(byte[] bytes, PdfReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(options);
        var limits = ReaderLimits.Resolve(options);
        return OpenCore(bytes, limits, options.AllowReconstruction, options.Password);
    }

    /// <summary>
    /// Opens a PDF document from bytes found INSIDE another document already under this library's
    /// control — the one caller today is <c>VellumPdf.Conformance</c>'s recursive PDF/A validation
    /// of an embedded-file attachment — using the SAME resolved <see cref="ReaderLimits"/> the outer
    /// read was opened with (<see cref="PdfDocumentReader.Limits"/>). Without this overload, a nested
    /// open has no <see cref="PdfReaderOptions"/> of its own to construct, and reaching for
    /// <see cref="Open(byte[])"/> would silently widen a caller's tightened
    /// <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> or
    /// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/> back to the 512 MiB / 8×
    /// defaults for attacker-supplied bytes nested inside the outer document — exactly the escape
    /// hatch tightening those options was meant to close.
    /// <para>
    /// Reconstruction is never attempted for a nested open, matching every nested-open call site
    /// before this overload existed, all of which used <see cref="Open(byte[])"/>'s
    /// <see cref="PdfReaderOptions.AllowReconstruction"/> default of <see langword="false"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    internal static PdfDocumentReader Open(byte[] bytes, ReaderLimits limits, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return OpenCore(bytes, limits, allowReconstruction: false, password);
    }

    private static PdfDocumentReader OpenCore(
        byte[] bytes, ReaderLimits limits, bool allowReconstruction, string? password)
    {
        var data = new ReadOnlyMemory<byte>(bytes);
        var parseResult = XrefParser.Parse(data, allowReconstruction, limits);
        return new PdfDocumentReader(data, parseResult, limits, password);
    }

    /// <summary>
    /// Opens a PDF document by reading all bytes from <paramref name="stream"/>, with no password.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown for the same unsupported features as
    /// <see cref="Open(byte[], PdfReaderOptions)"/>.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and no supplied
    /// password authenticates as either the owner or the user password.</exception>
    public static PdfDocumentReader Open(Stream stream) => Open(stream, new PdfReaderOptions());

    /// <summary>Opens a PDF document by reading all bytes from <paramref name="stream"/>.</summary>
    /// <param name="stream">The stream to read the document from.</param>
    /// <param name="options">
    /// Settings for this read. See <see cref="Open(byte[], PdfReaderOptions)"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or
    /// <paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown for the same unsupported features as
    /// <see cref="Open(byte[], PdfReaderOptions)"/>.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and
    /// <see cref="PdfReaderOptions.Password"/> authenticates as neither the owner nor the user
    /// password.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for the same out-of-range
    /// <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/> or
    /// <see cref="PdfReaderOptions.ReconstructionBudgetMultiplier"/> as
    /// <see cref="Open(byte[], PdfReaderOptions)"/>.</exception>
    public static PdfDocumentReader Open(Stream stream, PdfReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        // Resolved (and validated) before buffering: an out-of-range MaxDecodedStreamBytes or
        // ReconstructionBudgetMultiplier should reject immediately, not after CopyTo has already
        // read an unbounded stream fully into memory for an option value that was never usable.
        var limits = ReaderLimits.Resolve(options);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return OpenCore(ms.ToArray(), limits, options.AllowReconstruction, options.Password);
    }
}
