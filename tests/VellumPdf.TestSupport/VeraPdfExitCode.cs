// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.TestSupport;

/// <summary>
/// Names what a veraPDF exit code outside the expected 0 (compliant) or 1 (non-compliant) means,
/// so a failing exit-code assertion can say why the run is an environment problem or a defect in
/// the PDF VellumPdf itself emitted, rather than leaving the reader to look the code up. Measured
/// against veraPDF 1.30.2: 0 valid, 1 non-compliant, 2 a rejected <c>--flavour</c> argument, 4 no
/// file found, 7 a file veraPDF could not parse, 8 one it refused as encrypted.
/// <c>PdfValidatorOracleTests</c> and <c>ImageCodecOracleTests</c> each carried a byte-identical
/// copy of this switch before this type existed — the exact kind of piecemeal duplication #198
/// itself exists to remove, found again in that fix's own follow-up round (#198 review, round 7,
/// finding 8).
/// </summary>
public static class VeraPdfExitCode
{
    public static string Describe(int exitCode) => exitCode switch
    {
        7 => "veraPDF could not parse the file at all: points at the file VellumPdf emitted, not the environment.",
        8 => "veraPDF refused the file as encrypted: points at the file VellumPdf emitted, not the environment.",
        2 => "veraPDF rejected the --flavour argument, an environment/harness mistake, not a library defect.",
        4 => "veraPDF found no file to validate, an environment/harness mistake, not a library defect.",
        _ => "see veraPDF's own exit-code documentation for what this code means.",
    };
}
