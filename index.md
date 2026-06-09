---
_layout: landing
---

# VellumPdf

[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

A modern, **dependency-free PDF generation library for .NET 10**, implemented
clean-room from the open **ISO 32000** standard.

> **Status: beta.** Core features are implemented and CI-validated. The public
> API may still change before 1.0.

## Packages

| Package | Description |
|---|---|
| `VellumPdf.Kernel` | Object model, canvas, fonts, images, encryption, AcroForm, tagged-PDF, and PDF/A-2 scaffolding. |
| `VellumPdf.Layout` | High-level document builder: paragraphs, headings, lists, tables, images, header/footer, pagination. |
| `VellumPdf.Signing` | PAdES / PKCS#7 detached digital signatures over an incremental-update revision. |

## Getting started

Browse the **API Reference** section in the navigation for all public types, or read the conceptual docs:

- [Architecture](docs/architecture.md) — design decisions and layer structure.
- [Kernel Guide](docs/kernel-guide.md) — low-level canvas and font API walkthrough.
