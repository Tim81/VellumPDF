// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>
/// How a <see cref="QrCode"/> constructed from a string chooses byte-mode content and whether an
/// Extended Channel Interpretation (ECI) header names it, when the text is not fully representable
/// in numeric or alphanumeric mode. See the QR charset policy in the barcodes guide for how each
/// value round-trips through common decoders.
/// </summary>
public enum QrTextEncoding
{
    /// <summary>
    /// ISO/IEC 8859-1 (Latin-1) without an ECI header when the text is fully representable in it;
    /// otherwise UTF-8 with an ECI header naming it (ECI designator 26). This is the default and
    /// matches how widely deployed decoders guess an unmarked byte-mode symbol's charset.
    /// </summary>
    Auto,

    /// <summary>ISO/IEC 8859-1 (Latin-1), no ECI header. Throws <see cref="FormatException"/> if the text is not fully representable.</summary>
    Latin1,

    /// <summary>UTF-8, no ECI header. Round-trips only through decoders that default to UTF-8 or re-guess the charset from the bytes.</summary>
    Utf8,

    /// <summary>UTF-8 with an ECI header naming it (ECI designator 26), for decoders that honour ECI.</summary>
    Utf8Eci,
}
