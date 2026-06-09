#!/usr/bin/env pwsh
# Copyright 2026 Timothy van der Ham (@Tim81)
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

Write-Host "AOT smoke PASSED." -ForegroundColor Green
