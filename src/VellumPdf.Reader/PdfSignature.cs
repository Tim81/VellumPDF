// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// A digital signature found in the AcroForm of a parsed PDF document.
/// Carries the raw data Phase 5 (LTV) needs: <see cref="ByteRange"/>, <see cref="Contents"/>,
/// <see cref="SubFilter"/>, and optional signing time <see cref="SigningTime"/>.
/// </summary>
public sealed class PdfSignature
{
    /// <summary>The /SubFilter name (e.g. /ETSI.CAdES.detached or /adbe.pkcs7.detached).</summary>
    public PdfName? SubFilter { get; }

    /// <summary>
    /// The four values from the /ByteRange array: <c>[offset0 len0 offset1 len1]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="long"/> rather than <see cref="int"/> because these are byte offsets into the
    /// file: a signed PDF larger than 2 GB has offsets beyond <see cref="int.MaxValue"/>. The
    /// values were previously narrowed to <see cref="int"/> on the way in, which wrapped them
    /// silently, so a large file's signature was checked against the wrong bytes with no error
    /// reported.
    /// </para>
    /// <para>
    /// Exposed as <see cref="ReadOnlyMemory{T}"/>, matching <see cref="Contents"/>, so that the
    /// array backing it is not handed out for callers to mutate.
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<long> ByteRange { get; }

    /// <summary>The raw DER bytes from the /Contents hex string.</summary>
    public ReadOnlyMemory<byte> Contents { get; }

    /// <summary>The /M signing time string (PDF date format), or null if absent.</summary>
    public string? SigningTime { get; }

    internal PdfSignature(PdfName? subFilter, ReadOnlyMemory<long> byteRange, ReadOnlyMemory<byte> contents, string? signingTime)
    {
        SubFilter = subFilter;
        ByteRange = byteRange;
        Contents = contents;
        SigningTime = signingTime;
    }
}
