// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// Built-in ICC colour profiles VellumPdf can embed as PDF/A OutputIntents or
/// ICCBased colour space streams.
/// </summary>
public static class IccProfiles
{
    /// <summary>
    /// A built-in sRGB (IEC 61966-2.1) ICC profile (3 components).
    /// Returns a fresh copy of the profile bytes on each call.
    /// </summary>
    public static byte[] Srgb => (byte[])SrgbIccProfile.Bytes.Clone();

    /// <summary>
    /// A built-in generic CMYK ICC profile (4 components) for CMYK output intents
    /// and ICCBased colour spaces. Returns a fresh copy of the profile bytes on each call.
    /// </summary>
    public static byte[] GenericCmyk => (byte[])CmykIccProfile.Bytes.Clone();
}
