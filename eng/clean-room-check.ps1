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

Write-Host '✅ Clean-room check passed: no disallowed references found.' -ForegroundColor Green
exit 0
