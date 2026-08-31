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
  (`qpdf`, `pdftotext`, `pdfsig`, `pdftoppm`). CI pins these (#230) rather than taking whatever
  the runner image ships, so matching CI locally means matching its versions: qpdf 12.4.1
  (installed in CI from the [official release
  artifact](https://github.com/qpdf/qpdf/releases/tag/v12.4.1), since apt's qpdf on
  `ubuntu-24.04` is 11.9.0 and `--check`'s output has changed across qpdf majors), poppler-utils
  24.02.0-1ubuntu9.9, fonts-dejavu-core 2.37-8, and fonts-texgyre 20180621-6. On Debian/Ubuntu,
  install qpdf from the same release artifact and pin the rest with
  `sudo apt-get install poppler-utils=24.02.0-1ubuntu9.9 fonts-dejavu-core=2.37-8 fonts-texgyre=20180621-6`.

  **CI's poppler/font pins have a known maintenance cost.** noble's `-updates`/`-security`
  pockets keep only the newest revision of each package, so the exact revisions above eventually
  vanish from the live apt archive on their own, with no code change involved. When that happens,
  `ci.yml`'s "Install poppler and fonts" step still resolves them, because that step passes `-o
  APT::Snapshot=$APT_SNAPSHOT` on both `apt-get update` and `apt-get install`, which adds the
  dated [Ubuntu snapshot service](https://snapshot.ubuntu.com/) alongside whatever sources the
  runner already has — additive, not a replacement, and `ubuntu-24.04`'s own sources resolve
  through `mirror+file:/etc/apt/apt-mirrors.txt` rather than a literal `archive.ubuntu.com` host,
  so do not assume which host actually serves a given fetch. A snapshot never evicts what it once
  published, even after the live archive moves on, but `apt-get update` reports success (exit 0)
  even when the snapshot fetch itself fails, so the step also greps `/var/lib/apt/lists/` for the
  snapshot's own index files right after — that is what makes the snapshot actually having been
  used something you can check rather than assume. The failure mode to watch for instead is a
  genuine version bump: if poppler or a font package needs a newer release on purpose, run
  `apt-cache policy poppler-utils fonts-dejavu-core fonts-texgyre` against a fresh `ubuntu:24.04`
  container to get the new versions, pick an `APT_SNAPSHOT` stamp that is a UTC day at or after
  the new revision's publication (stamps are midnight UTC, so a same-day publication needs the
  next day's stamp), and confirm the stamp actually resolves with `curl -sI
  https://snapshot.ubuntu.com/ubuntu/<stamp>/dists/noble/InRelease` before using it — a
  future-dated or mistyped stamp does not error, it silently serves whatever is latest. Then
  update `POPPLER_VERSION` / `FONTS_DEJAVU_VERSION` / `FONTS_TEXGYRE_VERSION` and `APT_SNAPSHOT`
  together in `ci.yml`'s job-level `env:`, and this paragraph to match.

  On Windows, PATH order between shells is not reliable — the same bare
  `pdftotext` can resolve to a completely different program depending on which
  shell launched the test host, so point `QPDF_HOME` and `POPPLER_HOME` at
  the qpdf/poppler installs to make resolution deterministic. Each accepts
  the directory holding the executable directly, its parent with a `bin`
  subdirectory under it, or its grandparent with a `Library\bin` subdirectory
  under it (the shape a Windows poppler build installed via winget uses) — so
  `QPDF_HOME` can name either `...\qpdf-12.4.1-msvc64` or
  `...\qpdf-12.4.1-msvc64\bin`, and `POPPLER_HOME` can name either the
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
  `python -m pip install zxing-cpp==3.1.1 pillow==12.3.0`. zxing-cpp is pinned because the EAN
  add-on text format differs between releases; the barcode oracle test asserts only the main
  digits for that reason (`EanBarcode_Ean13WithAddOn_MainDigitsExact_AddOnTolerant`), so the pin
  exists to keep CI reproducible rather than to work around a currently-failing assertion.

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

A present, correctly-identified tool still is not proof it ran: a disabled
filter, a stale gate condition, or a refactor that quietly drops the call all
leave the same pass/skip counts an engaged oracle would (#228). CI closes
that gap from the workflow side rather than in-process, since the suite runs
each test assembly as its own process and nothing guarantees which one runs
last. `ci.yml`'s Test step sets `ORACLE_INVOCATION_LOG`, which makes
`ExternalTool.RunProcess` append one `tool<TAB>first-argument` line per
successful, non-probe call to a per-process file — an identity probe and a
launch that never started are both excluded, so neither can pad a tool's
count without the tool having actually validated anything; a step after
Test concatenates those files, sums a count per tool, and fails the build if
any tool falls below a floor pinned next to that count. The env var is
unset locally, so this is a no-op outside CI.

A tool that runs and answers is not the same as a tool that discriminated.
`qpdf --show-linearization` exits 0 and prints no `WARNING` for both a
linearized and a non-linearized file (the negative case just prints
`<file> is not linearized` instead), so an assertion that stops at the exit
code passes either way (#234). Before an oracle assertion ships, run the
oracle by hand against a deliberately broken copy of the fixture — a
truncated file, a flipped conformance flag — and note what changes in its
output. Assert on that value, not just the exit code:
`Assert.True(stdout.Contains("linearization data:"), ...)` fails on the
broken copy where `exit == 0` alone does not. Not every command needs this:
`qpdf --check` already prints `File is linearized` or `File is not
linearized` directly, so a test that reads that line already discriminates;
the exit code is the part of `--check` that doesn't (a warning just adds
`WARNING` lines and exit 3, on top of whichever of those two lines fired).

The `Enforce coverage threshold` step that follows the `Test` step in `ci.yml` reads whatever that
run wrote, so reproducing its percentage locally needs the same invocation. `coverlet.MTP` (#200)
reads its instrumentation scope from `coverlet.testconfig.json` (copied to every test host's
own output directory as `testconfig.json` by `tests/Directory.Build.props`) rather than a
`--settings` runsettings file; on the command line, `--coverlet` after `--` is only the activation
switch:

```bash
dotnet test VellumPdf.slnx -c Release --results-directory <dir> -- --coverlet
```

`coverlet.testconfig.json` scopes instrumentation to the eight shipping assemblies, the same
scope `coverlet.runsettings` held under the old VSTest collector (#229), as an include allow-list
rather than an exclude list. That distinction matters here specifically because coverlet.MTP's
command-line mode, active whenever no config file is present, merges in its own default
exclude-by-attribute list (`GeneratedCodeAttribute`, `CompilerGeneratedAttribute`), which silently
drops every compiler- and source-generator-emitted member from instrumentation — VellumPdf.Cli's
entire System.Text.Json source-generated `CliJsonContext` partial among them. A config file is
authoritative and does not get that default list injected, so scoping through `testconfig.json`
instead of `--coverlet-include`/`--coverlet-exclude` command-line options is what keeps generated
code counted the way `coverlet.collector` counted it under VSTest; measured, every one of the eight
assemblies' valid-line counts matched what this gate already carried before the migration (both
have since moved independently as unrelated PRs added code; parity is about the migration dropping
nothing, not about the two figures staying pinned together). Delete or rename `testconfig.json` out
of the test host's output directory and the run
still produces a number, just a different one: coverlet.MTP's own defaults, not what the gate
script in `ci.yml` actually thresholds.

#### Fuzzing (#99)

`VellumPdf.Reader.Tests/ParserFuzzTests.cs` runs CsCheck-driven byte-level mutation fuzzing against
`PdfLexer`, `PdfObjectParser`, and `PdfReader.Open` as ordinary `dotnet test` cases, seeded from the
committed `Fixtures/Encrypted`, `Fixtures/ThirdParty`, and `Fixtures/Fuzz` corpora.

**A robustness oracle, not a conformance one.** The only thing it asserts is that no crash-class
exception (`IndexOutOfRangeException`, `NullReferenceException`, `OutOfMemoryException`,
`OverflowException`, and similar) ever escapes those three entry points — see the class doc for why
throwing one of the three declared types is not required to be the correct outcome for a given
mutated input. The default budget (`VELLUMPDF_FUZZ_ITER`, a few thousand iterations per case) is
fast enough to run in every PR; `.github/workflows/fuzz-nightly.yml` runs the same tests on a
schedule with a budget roughly two orders of magnitude larger.

A failing case prints a CsCheck seed and its own minimized (shrunk) input directly in the assertion
message. Reproduce it locally by setting `CsCheck_Seed` to that value and re-running — see
`ParserFuzzTests.cs`'s "Determinism" doc. Finding a crash is not, on its own, a fix: per
`Fixtures/Fuzz/README.md`'s capture rule, the fixing PR must minimize the input, fix the underlying
defect, and commit the minimized bytes to `Fixtures/Fuzz/` (SHA-256-pinned and token-scanned, like
the other two corpora) so the regression is fuzzed forever after rather than replayed once.

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
