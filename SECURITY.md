# Security Policy

## Scope

VellumPdf is a PDF **generation** library. It writes new PDF documents and embeds
caller-supplied font and image bytes. It does **not** parse or render untrusted PDF
documents.

The security-relevant attack surface is therefore the font and image parsers
(TrueType/OpenType, PNG, JPEG, BMP, GIF, TIFF). These are written to fail cleanly on
malformed or hostile input — throwing `InvalidDataException` (corrupt/truncated data) or
`NotSupportedException` (unsupported variant) — rather than crash with an unexpected
exception, hang, or exhaust memory.

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
