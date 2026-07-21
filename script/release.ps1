#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds a distributable AIUsage.NET release: publishes the tray app and CLI (self-contained,
    single-file, win-x64) and zips the result. Windows counterpart of the Swift edition's
    script/release.sh — no notarization or DMG step exists on Windows; code signing (signtool) is
    deliberately left out for now (see PORTING_NOTES.md: packaging/signing is future work, not yet
    needed for a working port) and can be added here once a certificate is available.

.PARAMETER Version
    Version string stamped into the output zip name, e.g. 0.1.0. Required.

.PARAMETER Runtime
    Target RID. Default: win-x64.

.EXAMPLE
    script/release.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $RootDir "dist"
$StageDir = Join-Path $DistDir "AIUsage-$Version"

Write-Host "==> cleaning $DistDir"
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Path $StageDir | Out-Null

$trayProject = Join-Path $RootDir "src\AIUsage.Tray\AIUsage.Tray.csproj"
$cliProject = Join-Path $RootDir "src\AIUsage.Cli\AIUsage.Cli.csproj"

Write-Host "==> publishing tray app ($Runtime, self-contained, single-file)"
dotnet publish $trayProject -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $StageDir "tray")
if ($LASTEXITCODE -ne 0) { throw "Tray publish failed." }

Write-Host "==> publishing CLI ($Runtime, self-contained, single-file)"
dotnet publish $cliProject -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $StageDir "cli")
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }

# Flatten into one folder: AIUsage.exe (tray) + aiusage.exe (CLI) side by side, mirroring how the
# Swift release puts the app bundle's helper CLI under Contents/Helpers next to the main binary.
Write-Host "==> staging flat release folder"
Copy-Item (Join-Path $StageDir "tray\AIUsage.exe") $StageDir
Copy-Item (Join-Path $StageDir "cli\aiusage.exe") $StageDir
Remove-Item -Recurse -Force (Join-Path $StageDir "tray")
Remove-Item -Recurse -Force (Join-Path $StageDir "cli")

$zipPath = Join-Path $DistDir "AIUsage-$Version-$Runtime.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
Write-Host "==> zipping $zipPath"
Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $zipPath

Write-Host "==> done"
Write-Host "    Zip: $zipPath"
Write-Host "    NOTE: unsigned build. Code signing (signtool) is not yet wired up — see PORTING_NOTES.md."
