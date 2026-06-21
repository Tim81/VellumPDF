// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>
/// A single cross-validation fixture: a real PDF whose in-process preflight verdict is expected to
/// equal the verdict from veraPDF for the same conformance level.
/// </summary>
/// <param name="Name">Stable identifier (also the temp file name used for the veraPDF run).</param>
/// <param name="Bytes">The PDF bytes.</param>
/// <param name="Level">The in-process conformance level to validate against.</param>
/// <param name="VeraFlavour">The matching veraPDF flavour code (e.g. <c>2b</c>, <c>ua1</c>).</param>
/// <param name="ExpectedCompliant">The expected verdict both validators must agree on.</param>
public sealed record OracleFixture(
    string Name,
    byte[] Bytes,
    PdfConformance Level,
    string VeraFlavour,
    bool ExpectedCompliant);
