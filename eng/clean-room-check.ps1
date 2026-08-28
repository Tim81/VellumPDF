#!/usr/bin/env pwsh
# Clean-room guard.
#
# VellumPdf is an independent implementation written solely from open published
# specifications. To keep that promise verifiable, this check fails the build if
# the name of any disallowed reference library appears anywhere in the tree.
#
# The needles are assembled from fragments so this guard file itself stays free
# of the literal tokens it forbids.

$ErrorActionPreference = 'Stop'

$needles = @(
    ('i' + 'text'),
    ('i' + 'textsharp')
)
$pattern = ($needles -join '|')

$root = Split-Path $PSScriptRoot -Parent
$extensions = @('*.cs', '*.csproj', '*.props', '*.targets', '*.md', '*.txt',
                '*.json', '*.xml', '*.yml', '*.yaml', '*.slnx', '*.editorconfig')

$files = Get-ChildItem -Path $root -Recurse -File -Include $extensions |
    Where-Object { $_.FullName -notmatch '[\\/](\.git|\.claude|eng|bin|obj|artifacts)[\\/]' }

$hits = $files | Select-String -Pattern $pattern -CaseSensitive:$false

if ($hits) {
    Write-Host '❌ Clean-room check FAILED. Disallowed reference(s) found:' -ForegroundColor Red
    foreach ($h in $hits) {
        Write-Host ("   {0}:{1}" -f $h.Path, $h.LineNumber)
    }
    exit 1
}

# Commit messages, which CLAUDE.md forbids the names in as well. This is the one place where the
# check has to run BEFORE the merge to be worth anything: a working-tree hit can be corrected in a
# follow-up commit, while a merged commit message cannot be corrected at all without rewriting
# public history. Needs the branch's own commits, so CI checks out with fetch-depth 0.
#
# Skips silently when no base to compare against resolves — a shallow clone, or a checkout with no
# origin. That is deliberate: this is a second line of defence over the file scan above, and failing
# the build because a ref could not be resolved would fail it wherever the checkout differs, for no
# finding.
$candidates = @()
if ($env:GITHUB_BASE_REF) { $candidates += "origin/$($env:GITHUB_BASE_REF)" }
if ($env:GITHUB_EVENT_NAME -eq 'push') { $candidates += 'HEAD~1' }
$candidates += 'origin/main'
$candidates += 'main'

$base = $null
foreach ($candidate in $candidates) {
    $null = & git rev-parse --verify --quiet "$candidate" 2>$null
    if ($LASTEXITCODE -eq 0) {
        $base = $candidate
        break
    }
}

if ($base) {
    $messages = & git log --format='%H%n%B' "$base..HEAD" 2>$null
    if ($LASTEXITCODE -eq 0 -and $messages) {
        $msgHits = @($messages | Select-String -Pattern $pattern -CaseSensitive:$false)
        if ($msgHits.Count -gt 0) {
            Write-Host '❌ Clean-room check FAILED. Disallowed reference(s) in a commit message.' -ForegroundColor Red
            Write-Host ("   {0} matching line(s) in {1}..HEAD." -f $msgHits.Count, $base)
            Write-Host '   The line is not echoed here. Rewrite the message before merging: once it'
            Write-Host '   is in public history it cannot be corrected.'
            exit 1
        }
    }
}

Write-Host '✅ Clean-room check passed: no disallowed references found.' -ForegroundColor Green
exit 0
