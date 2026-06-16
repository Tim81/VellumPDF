// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Thrown when a PDF feature is encountered that is not yet supported by VellumPdf.Reader.
/// Each message names the feature and includes a link to the tracking GitHub issue.
/// </summary>
public sealed class UnsupportedPdfFeatureException : NotSupportedException
{
    /// <summary>Creates a new instance with the specified message.</summary>
    public UnsupportedPdfFeatureException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public UnsupportedPdfFeatureException(string message, Exception inner) : base(message, inner) { }
}
