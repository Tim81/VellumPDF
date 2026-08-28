# Security Policy

## Scope

VellumPdf writes PDF documents, and since v1.6 it also reads them. Both halves take input
the caller may not control, so both are in scope:

- **Font and image bytes** embedded during generation — TrueType/OpenType, PNG, JPEG, BMP,
  GIF, TIFF.
- **Whole PDF documents** parsed by `VellumPdf.Reader`, by the `VellumPdf.Conformance`
  preflight validator, and by the `vellum-preflight` CLI, which exists to be pointed at
  files you did not produce.

None of these render, execute embedded JavaScript, or resolve external references, so the
risk is confined to what a parser can be made to do: read out of bounds, recurse without
end, loop forever, or allocate without limit. Every parser is written to fail cleanly
instead — throwing `InvalidDataException` (corrupt or truncated data), `NotSupportedException`
(an unsupported variant), or `PdfPasswordException` (an encrypted document whose password was
wrong or absent) rather than crashing with an unexpected exception, hanging, or exhausting
memory. An encrypted document is parsed with the same limits as any other once its password
authenticates. The encryption dictionary is necessarily read before that — `/O`, `/P` and the
trailer `/ID` are inputs to the key derivation, and `/Filter`, `/V` and `/R` decide which algorithm
runs — but every one of those is range-checked first, no length, offset or filter name taken from
it is acted on unvalidated, and the number of crypt filters it may declare is capped.

Dictionary lookup was a linear scan through v2.1, so a `/Encrypt` dictionary carrying very many keys
cost time quadratic in the key count before its password was ever checked — worth stating exactly,
because "fails cleanly" is not the same as "fails quickly". `PdfDictionary` now builds a hash index
once a dictionary passes 16 entries, so lookup stays constant and construction stays linear past that
point: building one directly with a million keys takes about 0.5 s, measured here (#208). Bounding
input size remains the caller's responsibility more generally. The reader bounds indirect-reference
nesting and AcroForm field-tree depth, rejects object-stream cycles, and range-checks every offset
taken from a cross-reference table before using it.

A crash, hang, or unbounded allocation on malformed or hostile input is a bug. Please report
it, whichever of those entry points reaches it.

## Supported versions

Security fixes are applied to the latest released minor version on NuGet.

## Reporting a vulnerability

Please report security issues **privately** through GitHub Security Advisories:

1. Open the [**Security** tab](https://github.com/Tim81/VellumPDF/security/advisories) of the repository.
2. Click **Report a vulnerability** and include a description, reproduction steps, and — if
   possible — a minimal sample input that triggers the issue.

Please do **not** open a public issue for security reports.

We aim to acknowledge reports within a few business days and will coordinate a fix and a
disclosure timeline with you. Thank you for helping keep VellumPdf and its users safe.
