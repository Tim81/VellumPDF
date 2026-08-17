# Contributing to VellumPdf

Thank you for your interest in contributing. This document explains how to
build, test, and submit changes to VellumPdf.

## Prerequisites

- **.NET 10 SDK** (the only required runtime dependency).
- **Docker** — needed to run the veraPDF conformance gate locally.
- **qpdf** and **poppler-utils** — needed to run the structural-validator,
  text-extraction, signature, and barcode-rasterization oracle tests locally
  (`qpdf`, `pdftotext`, `pdfsig`, `pdftoppm`). On Debian/Ubuntu:
  `sudo apt-get install qpdf poppler-utils fonts-dejavu-core fonts-texgyre`
- **Python with zxing-cpp** — the barcode decode oracle:
  `python -m pip install zxing-cpp==3.0.0 pillow`. The version is pinned because
  the EAN add-on text format differs between zxing-cpp releases.

## Building and testing

```bash
# Build (all warnings are treated as errors)
dotnet build VellumPdf.slnx

# Run all tests (includes the external-validator oracle tests)
dotnet test VellumPdf.slnx

# Release build (matches CI)
dotnet build VellumPdf.slnx -c Release
dotnet test  VellumPdf.slnx -c Release
```

## Must-pass CI gates

Every pull request must pass **all** of the following gates before merge. Run
them locally before pushing to avoid round-trips.

### 1. Warnings-as-errors

The repository sets `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in
`Directory.Build.props`. A build that emits any warning is a failing build.

### 2. Format check

```bash
dotnet format VellumPdf.slnx --verify-no-changes
```

If this reports diffs, run `dotnet format VellumPdf.slnx` to apply them, then
commit the result. Do not submit column-aligned spacing or other non-standard
formatting.

### 3. Clean-room check

```pwsh
pwsh ./eng/clean-room-check.ps1
```

VellumPdf is implemented clean-room from open published specifications. The
check script scans the entire tree for the names of disallowed reference
libraries. **Do not paste or reference code from any third-party PDF library,
and do not name such libraries in any source file, document, or comment.** See
[docs/architecture.md](docs/architecture.md) for the full clean-room policy.

### 4. Vulnerable-package scan

CI runs `dotnet list package --vulnerable --include-transitive` and fails if any
vulnerable package is detected. Keep dependency additions to a minimum and
ensure every new package is free of known CVEs.

### 5. Tests (including veraPDF)

The oracle tests shell out to `verapdf`, `qpdf`, `pdftotext`, `pdfsig`,
`pdftoppm`, and the zxing-cpp Python module. Make sure these are on your PATH
(or use the Docker-backed `verapdf` shim described in [README.md](README.md))
before running the test suite.

A missing tool makes the test skip locally, so a green local run does not mean
the oracles ran. On CI the same tests fail instead: the qpdf and poppler oracles
detect `CI`/`GITHUB_ACTIONS`, and the veraPDF and barcode oracles are switched
by `REQUIRE_VERAPDF` and `REQUIRE_BARCODE_ORACLE`. Set those two locally to
reproduce the CI behaviour.

### 6. AOT smoke test

```pwsh
pwsh ./eng/aot/run-aot-smoke.ps1
```

This publishes the library with Native AOT and runs a smoke test to verify the
library is AOT- and trim-compatible. Requires Visual Studio Build Tools (the
script uses `vswhere` to locate the VS Installer directory).

## Public API workflow

The repository uses `Microsoft.CodeAnalysis.PublicApiAnalyzers` to lock the
public API surface of every shippable project. The analyzer compares the live
public surface against two baseline files in each project directory:

- `PublicAPI.Shipped.txt` — symbols in the last published release.
- `PublicAPI.Unshipped.txt` — symbols added or removed since then.

**If your change adds, removes, or renames any public symbol**, you must update
`PublicAPI.Unshipped.txt` in the affected project(s). The build is an error
otherwise. Use the IDE code-fix (`RS0016` / `RS0017`) or edit the file manually.
Do not add symbols to `PublicAPI.Shipped.txt` — that file is updated only at
release time.

## Branch and PR etiquette

- **Target `main`.** All pull requests should be opened against the `main`
  branch.
- **One logical change per PR.** Keep pull requests focused. Split unrelated
  fixes into separate PRs.
- **Link issues.** Reference the relevant GitHub issue in the PR description
  (e.g. `Closes #N`).
- **Update CHANGELOG.md.** Add an entry under `## [Unreleased]` in the
  appropriate subsection (`Added`, `Changed`, `Fixed`, `Security`, `Removed`,
  `Deprecated`).
- **Commit messages.** Use the imperative mood in the subject line
  (`Add foo`, not `Added foo`). Keep the subject under 72 characters.
- **Draft PRs are welcome** for early feedback on direction before a change is
  complete.

## Architecture notes

VellumPdf follows a strict layered architecture with inward-only, acyclic
dependencies. The kernel (`VellumPdf.Kernel`) depends only on the .NET base
class library; feature packages depend inward only. See
[docs/architecture.md](docs/architecture.md) for the full picture before making
structural changes.
