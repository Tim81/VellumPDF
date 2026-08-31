# Contributing to VellumPdf

Thank you for your interest in contributing. This document explains how to
build, test, and submit changes to VellumPdf.

## Prerequisites

- **.NET 10 SDK**, feature band 10.0.4xx — the exact pin is in `global.json`;
  earlier feature bands are rejected.
- Before tagging a release: merge and CI-validate any `global.json`
  feature-band bump first. `release.yml` reads `global.json` from the tag's
  tree, so a stale pin ships a release built on a superseded SDK band.
- **Docker** — needed to run the veraPDF conformance gate locally.
- **qpdf** and **poppler-utils** — needed to run the structural-validator,
  text-extraction, signature, and barcode-rasterization oracle tests locally
  (`qpdf`, `pdftotext`, `pdfsig`, `pdftoppm`). On Debian/Ubuntu:
  `sudo apt-get install qpdf poppler-utils fonts-dejavu-core fonts-texgyre`.
  On Windows, PATH order between shells is not reliable — the same bare
  `pdftotext` can resolve to a completely different program depending on which
  shell launched the test host, so point `QPDF_HOME` and `POPPLER_HOME` at
  the qpdf/poppler installs to make resolution deterministic. Each accepts
  the directory holding the executable directly, its parent with a `bin`
  subdirectory under it, or its grandparent with a `Library\bin` subdirectory
  under it (the shape a Windows poppler build installed via winget uses) — so
  `QPDF_HOME` can name either `...\qpdf-12.3.2-msvc64` or
  `...\qpdf-12.3.2-msvc64\bin`, and `POPPLER_HOME` can name either the
  poppler install root or its `Library\bin` folder directly. A `*_HOME` that
  is set but does not resolve through any of these is reported as a
  misconfiguration (the same skip-locally/fail-on-CI outcome a wrong-tool
  resolution gets) rather than silently falling back to PATH. The veraPDF
  oracle has the same env-var escape hatch, `VERAPDF_HOME`, pointing at the
  directory holding `verapdf.bat` (no `bin` probe there — veraPDF's own
  installer puts the launcher at the root); resolving the CLI itself only
  reads it on Windows, and the Linux CI image resolves `verapdf` from PATH
  instead. `VERAPDF_HOME` also has a second, platform-independent reader:
  `ConformanceCatalogTests` reads it on every OS to locate the veraPDF CLI
  jar that carries the validation profiles. Setting it on Linux is not a
  no-op; it just does not affect which `verapdf` executable a test shells
  out to. A gated theory in `VellumPdf.Conformance.Tests`
  (`ExternalToolResolutionTests`) checks that each tool resolves to itself
  on every run.
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
`pdftoppm`, and the zxing-cpp Python module. Put them on your PATH, or point
`QPDF_HOME` / `POPPLER_HOME` / `VERAPDF_HOME` at each install directory (see
Prerequisites above) — the `*_HOME` variables are the more reliable option on
Windows, where PATH order between shells is not deterministic. For resolving
the `verapdf` CLI itself, `VERAPDF_HOME` is read only on Windows, where the
installer's launcher is `verapdf.bat`; `QPDF_HOME` and `POPPLER_HOME` work on
every OS. (`VERAPDF_HOME` is read on every OS for a different purpose; see
Prerequisites above.) Or use the Docker-backed `verapdf` shim described in
[README.md](README.md).

A missing tool, or one that resolves to something other than what it claims,
makes the test skip locally, so a green local run does not mean the oracles
ran. On CI the same tests fail instead: `CI`, `GITHUB_ACTIONS`, and
`REQUIRE_ORACLES` all fail every oracle test, while `REQUIRE_VERAPDF` and
`REQUIRE_BARCODE_ORACLE` fail only the veraPDF and barcode-decode oracles
respectively. Set `REQUIRE_ORACLES=1` locally to reproduce the CI behaviour
across the board, or one of the two scoped variables to reproduce just that
oracle's CI behaviour.

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
