## Summary

Describe what this PR changes and why.

## Linked issue

Closes #

## Checklist

- [ ] Builds clean with warnings-as-errors (`dotnet build VellumPdf.slnx -warnaserror`)
- [ ] All tests pass (`dotnet test VellumPdf.slnx`)
- [ ] Format is clean (`dotnet format VellumPdf.slnx --verify-no-changes`)
- [ ] Clean-room check passes (`pwsh ./eng/clean-room-check.ps1`)
- [ ] `PublicAPI.Unshipped.txt` updated if the public API surface changed
- [ ] `CHANGELOG.md` updated under `[Unreleased]`
- [ ] AOT smoke test run if kernel or runtime code changed (`pwsh ./eng/aot/run-aot-smoke.ps1`)
