#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds a distributable AIUsage.NET release using Velopack: publishes the tray app
    self-contained, then packages it with `vpk pack` into an installer (Setup.exe), a portable
    zip, and the delta-update feed files GitHub Releases serves to UpdateChecker on every future
    launch. Windows counterpart of the Swift edition's script/release.sh — no notarization/DMG step
    exists on Windows; code signing (signtool) is deliberately left out for now (see
    PORTING_NOTES.md: packaging/signing is future work) and can be wired in via `vpk pack
    --signParams` once a certificate is available.

    The CLI (`aiusage.exe`) is still published and zipped separately alongside the Velopack output,
    since it's a standalone tool most users invoke from a terminal, not something Velopack should
    manage the lifecycle of.

.PARAMETER Version
    Version string stamped into the Velopack package and the CLI zip name, e.g. 0.3.0. Required.

.PARAMETER Runtime
    Target RID. Default: win-x64.

.EXAMPLE
    script/release.ps1 -Version 0.3.0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $RootDir "dist"
$PublishDir = Join-Path $DistDir "publish-$Version"
$ReleasesDir = Join-Path $DistDir "Releases"

Write-Host "==> cleaning $DistDir"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
New-Item -ItemType Directory -Path $PublishDir | Out-Null

$trayProject = Join-Path $RootDir "src\AIUsage.Tray\AIUsage.Tray.csproj"
$cliProject = Join-Path $RootDir "src\AIUsage.Cli\AIUsage.Cli.csproj"

# Velopack needs the app's individual files (not a single self-extracting exe) to compute delta
# patches between versions, so PublishSingleFile is intentionally NOT used here for the tray app
# (unlike the CLI below, which Velopack does not manage).
Write-Host "==> publishing tray app ($Runtime, self-contained, version $Version)"
dotnet publish $trayProject -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -o (Join-Path $PublishDir "tray")
if ($LASTEXITCODE -ne 0) { throw "Tray publish failed." }

Write-Host "==> publishing CLI ($Runtime, self-contained, single-file, version $Version)"
$cliStageDir = Join-Path $DistDir "cli-$Version"
if (Test-Path $cliStageDir) { Remove-Item -Recurse -Force $cliStageDir }
dotnet publish $cliProject -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version `
    -o $cliStageDir
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }

$cliZipPath = Join-Path $DistDir "aiusage-cli-$Version-$Runtime.zip"
if (Test-Path $cliZipPath) { Remove-Item $cliZipPath }
Write-Host "==> zipping CLI: $cliZipPath"
Compress-Archive -Path (Join-Path $cliStageDir "aiusage.exe") -DestinationPath $cliZipPath

# dnx requires the .NET 10 SDK; this project targets .NET 8, so vpk is installed as a regular
# global tool instead (works from the .NET 8 SDK onward). Pinned to the same version as the
# Velopack package reference in AIUsage.Tray.csproj / AIUsage.Core.csproj, per Velopack's own advice.
$vpkVersion = "1.2.0"
Write-Host "==> installing/checking vpk CLI tool $vpkVersion"
$installedVpk = dotnet tool list -g | Select-String "^vpk\s"
if (-not $installedVpk) {
    dotnet tool install -g vpk --version $vpkVersion
    if ($LASTEXITCODE -ne 0) { throw "Failed to install vpk global tool." }
} elseif ($installedVpk -notmatch [regex]::Escape($vpkVersion)) {
    dotnet tool update -g vpk --version $vpkVersion
    if ($LASTEXITCODE -ne 0) { throw "Failed to update vpk global tool to $vpkVersion." }
}

Write-Host "==> packing Velopack release ($ReleasesDir)"
if (Test-Path $ReleasesDir) { Remove-Item -Recurse -Force $ReleasesDir }
vpk pack `
    --packId AIUsage.NET `
    --packVersion $Version `
    --packDir (Join-Path $PublishDir "tray") `
    --packTitle "AIUsage.NET" `
    --mainExe AIUsage.exe `
    --outputDir $ReleasesDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host "==> done"
Write-Host "    Velopack release: $ReleasesDir (Setup.exe, portable zip, releases.win.json, ...)"
Write-Host "    CLI zip: $cliZipPath"
Write-Host "    NOTE: unsigned build. Code signing (signtool) is not yet wired up - see PORTING_NOTES.md."
