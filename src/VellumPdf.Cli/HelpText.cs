// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Cli;

internal static class HelpText
{
    internal static readonly string Text =
        """
        vellum-preflight — PDF/A and PDF/UA preflight validator

        USAGE
          vellum-preflight <input>... [options]

        INPUTS
          <path>        PDF file path
          <glob>        Glob pattern, e.g. "reports/*.pdf"
          <dir>         Directory (use -r to recurse)
          -             Read one PDF from stdin

        OPTIONS
          -p, --profile <list>   Profiles to check: 2b 2u 2a ua1 auto all
                                 Comma-separated or repeated. Default: auto
          -f, --format <fmt>     Output format: text json sarif. Default: text
          -o, --output <path>    Write output to file instead of stdout
          --password <pw>        Password for an encrypted input. Default: none (empty user password)
          --severity <sev>       Min severity of failures to show: error warning info. Default: error
          --fail-on <sev>        Exit-code threshold: error warning info none. Default: error
          -r, --recurse          Recurse into directories
          -q, --quiet            Suppress informational output
          -v, --verbose          Show full passed-check list
          --no-color             Disable ANSI colour (also honoured via NO_COLOR env var)
          --list-profiles        Print available profiles and exit
          --coverage [profile]   Print rule coverage for a profile (or all) and exit
          --version              Print tool version and exit
          -h, --help             Show this help and exit

        PROFILES
          2b    PDF/A-2 Level B (basic)
          2u    PDF/A-2 Level U (Unicode)
          2a    PDF/A-2 Level A (accessible)
          ua1   PDF/UA-1 (universal accessibility)
          auto  Detect from the file's XMP claim (default)
          all   Run all four profiles

        EXIT CODES
          0   All files conformant
          1   One or more files non-conformant (failure >= --fail-on threshold)
          2   Usage or I/O error (bad args, file not found, not a PDF)
        """;
}
