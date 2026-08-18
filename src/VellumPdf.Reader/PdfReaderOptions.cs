// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>Options controlling how <see cref="PdfReader"/> parses a document.</summary>
public sealed class PdfReaderOptions
{
    /// <summary>
    /// When <see langword="true"/>, a document whose <c>startxref</c> is missing, unusable, or
    /// points at something unrecognisable as a classic xref table or a cross-reference stream is
    /// recovered by scanning the file for indirect-object headers instead of throwing
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. Reconstruction is a best-effort recovery over
    /// structure the file's own cross-reference table has already failed to describe correctly —
    /// it can synthesize the wrong catalog for a layout it does not yet fully understand (one
    /// packed into an object stream, or a document carrying another PDF as an embedded file), and
    /// a caller that needs to trust what it opens should choose that trade-off deliberately rather
    /// than have it happen implicitly (#184). Check <see cref="PdfDocumentReader.WasReconstructed"/>
    /// on the result before relying on it for anything security- or provenance-sensitive.
    /// </remarks>
    public bool AllowReconstruction { get; set; }
}
