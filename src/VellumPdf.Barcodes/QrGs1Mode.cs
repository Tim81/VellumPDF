// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>
/// How <see cref="QrCode.Gs1"/> carries GS1 application-identifier data. Modelled as an enum
/// rather than a boolean because a QR symbol has three distinct states here, not two: plain text,
/// GS1 element-string data flagged with the FNC1-in-first-position mode indicator (ISO/IEC 18004
/// §7.4.8.2), and a GS1 Digital Link URI, which needs no special mode indicator at all since it is
/// encoded as ordinary text. A single on/off switch cannot express that third, URI-shaped state.
/// </summary>
public enum QrGs1Mode
{
    /// <summary><see cref="QrCode.Text"/> is encoded verbatim, unchanged. The default.</summary>
    None,

    /// <summary>
    /// <see cref="QrCode.Text"/> is parsed as a GS1 element string (raw-payload or parenthesised-AI
    /// form) and encoded with the FNC1-in-first-position mode indicator ahead of the data, marking
    /// the symbol as GS1-formatted (ISO/IEC 18004 §7.4.8.2; GS1 General Specifications).
    /// </summary>
    ElementString,

    /// <summary>
    /// <see cref="QrCode.Text"/> is parsed as a GS1 element string and rewritten as its canonical
    /// <c>https://id.gs1.org/...</c> GS1 Digital Link URI, then encoded as an ordinary QR Code —
    /// no FNC1 indicator, since a Digital Link is just a URL.
    /// </summary>
    DigitalLink,
}
