---
_layout: landing
---

# VellumPdf

[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

A modern, **dependency-free PDF generation library for .NET 10**, implemented
clean-room from the open **ISO 32000** standard.

> **Status: stable.** The public API is locked (analyzer-enforced) and the
> library targets .NET 10. Core features are CI-validated, including
> PDF/A-2a/2b/2u and PDF/UA-1 conformance proven on every push with veraPDF.

## Packages

| Package | Status | Description |
|---|---|---|
| `VellumPdf.Kernel` | Stable | Object model, canvas, Standard-14 fonts, TrueType/OpenType embedding, images, encryption, AcroForm, tagged-PDF, and PDF/A-2 colour and metadata. |
| `VellumPdf.Fonts.Standard14` | Stable | Embeddable, metric-compatible substitutes for the standard-14 fonts, for PDF/A's font-embedding rule. |
| `VellumPdf.Layout` | Stable | High-level document builder: paragraphs, headings, lists, tables, images, header/footer, pagination. |
| `VellumPdf.Signing` | Stable | PAdES / PKCS#7 detached digital signatures with RFC-3161 timestamps and long-term validation. |
| `VellumPdf.Reader` | Preview | Reads existing PDFs (cross-reference tables and streams, hybrid-reference files, encrypted documents given the password) and exposes the catalog and signatures. |
| `VellumPdf.Conformance` | Stable | In-process PDF/A-2b/2u/2a and PDF/UA-1 preflight, cross-validated against veraPDF in CI. |
| `VellumPdf.Cli` | Stable | The `vellum-preflight` command-line validator, with Native-AOT binaries. |
| `VellumPdf.Barcodes` | Stable | QR (including Micro QR and GS1 Digital Link), Data Matrix, Aztec, PDF417, Code 128/GS1-128, Code 39, EAN/UPC (including UPC-E), and ITF-14, decode-verified against zxing-cpp. |

## Getting started

Browse the **API Reference** section in the navigation for all public types, or read the conceptual docs:

- [Architecture](docs/architecture.md) — design decisions and layer structure.
- [Kernel Guide](docs/kernel-guide.md) — low-level canvas and font API walkthrough.
