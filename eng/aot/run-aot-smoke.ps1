#!/usr/bin/env pwsh
# Copyright © Timothy van der Ham (@Tim81)
# SPDX-License-Identifier: Apache-2.0
#
# Builds the VellumPdf.AotSmoke harness with Native AOT and runs it, proving the
# library is AOT-safe end to end (layout engine, fonts, FlateDecode, writer).
#
# Windows note: the ILCompiler native link step shells out to vswhere.exe to locate
# the MSVC toolset. vswhere lives in the VS *Installer* directory, which vcvars does
# not put on PATH — so we prepend it here.

$ErrorActionPreference = 'Stop'

$installer = "C:\Program Files (x86)\Microsoft Visual Studio\Installer"
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

$rid  = if ($IsWindows -or $null -eq $IsWindows) { 'win-x64' } elseif ($IsMacOS) { if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'osx-arm64' } else { 'osx-x64' } } else { 'linux-x64' }
$proj = Join-Path $PSScriptRoot 'VellumPdf.AotSmoke\VellumPdf.AotSmoke.csproj'

Write-Host "Publishing Native AOT ($rid)..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r $rid
if ($LASTEXITCODE -ne 0) { throw "AOT publish failed (exit $LASTEXITCODE)" }

$exeName = if ($rid -like 'win-*') { 'VellumPdf.AotSmoke.exe' } else { 'VellumPdf.AotSmoke' }
$exe = Join-Path $PSScriptRoot "VellumPdf.AotSmoke\bin\Release\net10.0\$rid\publish\$exeName"

Write-Host "Running $exe" -ForegroundColor Cyan
& $exe
if ($LASTEXITCODE -ne 0) { throw "AOT smoke run failed (exit $LASTEXITCODE)" }

# The vellum-preflight CLI (issue #130) must also stay Native-AOT clean — it is the
# public front-end over VellumPdf.Conformance and ships as a per-platform native binary.
$cli = Join-Path $PSScriptRoot '..\..\src\VellumPdf.Cli\VellumPdf.Cli.csproj'
Write-Host "Publishing vellum-preflight CLI Native AOT ($rid)..." -ForegroundColor Cyan
dotnet publish $cli -c Release -r $rid
if ($LASTEXITCODE -ne 0) { throw "CLI AOT publish failed (exit $LASTEXITCODE)" }

$cliExeName = if ($rid -like 'win-*') { 'vellum-preflight.exe' } else { 'vellum-preflight' }
$cliExe = Join-Path $PSScriptRoot "..\..\src\VellumPdf.Cli\bin\Release\net10.0\$rid\publish\$cliExeName"
Write-Host "Running $cliExe --coverage 2b" -ForegroundColor Cyan
& $cliExe --coverage 2b
if ($LASTEXITCODE -ne 0) { throw "CLI AOT smoke run failed (exit $LASTEXITCODE)" }

Write-Host "AOT smoke PASSED." -ForegroundColor Green
