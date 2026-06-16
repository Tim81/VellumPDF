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

    /// <summary>The four integers from the /ByteRange array: [offset0 len0 offset1 len1].</summary>
    public int[] ByteRange { get; }

    /// <summary>The raw DER bytes from the /Contents hex string.</summary>
    public ReadOnlyMemory<byte> Contents { get; }

    /// <summary>The /M signing time string (PDF date format), or null if absent.</summary>
    public string? SigningTime { get; }

    internal PdfSignature(PdfName? subFilter, int[] byteRange, ReadOnlyMemory<byte> contents, string? signingTime)
    {
        SubFilter = subFilter;
        ByteRange = byteRange;
        Contents = contents;
        SigningTime = signingTime;
    }
}
