// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Thrown by <see cref="PdfReader.Open(byte[], string?)"/> (and its <see cref="System.IO.Stream"/>
/// overload) when a document's <c>/Encrypt</c> dictionary cannot be authenticated against either the
/// owner or the user password: none of the tried password encodings matched <c>/O</c> or <c>/U</c>
/// (ISO 32000-1 §7.6.3.3 Algorithms 4–7; ISO 32000-2 §7.6.4.3.3 Algorithm 2.A).
///
/// Deliberately not <see cref="UnsupportedPdfFeatureException"/>, and not
/// <see cref="System.NotSupportedException"/> at all: <c>vellum-preflight</c> catches
/// <see cref="System.NotSupportedException"/> to report an unsupported PDF feature as a plain error
/// line, and a wrong password is not that — it is a document the reader fully understands but was
/// not given the credentials to open. Also deliberately not
/// <see cref="System.IO.InvalidDataException"/>: that means the bytes do not form a well-formed PDF,
/// which is a different failure from a well-formed, fully-understood <c>/Encrypt</c> dictionary that
/// the supplied password just does not satisfy.
/// </summary>
public sealed class PdfPasswordException : Exception
{
    /// <summary>Creates a new instance with the specified message.</summary>
    public PdfPasswordException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public PdfPasswordException(string message, Exception inner) : base(message, inner) { }
}
