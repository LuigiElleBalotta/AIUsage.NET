#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds AIUsage.NET and launches the tray app (or the CLI) in place. Windows counterpart of the
    Swift edition's script/build_and_run.sh — no app bundle staging, code signing, or Sparkle to deal
    with; `dotnet build` + `dotnet run` covers the same "build, then run without installing" workflow.

.PARAMETER Mode
    run (default): build then launch the tray app.
    build: build only, no launch.
    cli: build then run the CLI (aiusage) with any extra -Args passed through.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER Args
    Extra arguments forwarded to the CLI when -Mode cli is used (e.g. -Args "claude","--force").

.EXAMPLE
    script/build_and_run.ps1
    script/build_and_run.ps1 -Mode build -Configuration Release
    script/build_and_run.ps1 -Mode cli -Args claude, --force
#>
param(
    [ValidateSet("run", "build", "cli")]
    [string]$Mode = "run",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$SolutionPath = Join-Path $RootDir "AIUsage.sln"

Write-Host "==> dotnet build ($Configuration)"
dotnet build $SolutionPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit $LASTEXITCODE
}

switch ($Mode) {
    "build" {
        Write-Host "==> build only, not launching"
    }
    "cli" {
        $cliProject = Join-Path $RootDir "src\AIUsage.Cli\AIUsage.Cli.csproj"
        Write-Host "==> running CLI"
        dotnet run --project $cliProject -c $Configuration --no-build -- @Args
    }
    default {
        $trayExe = Join-Path $RootDir "src\AIUsage.Tray\bin\$Configuration\net8.0-windows\AIUsage.exe"
        if (-not (Test-Path $trayExe)) {
            Write-Error "Tray executable not found: $trayExe"
            exit 1
        }
        Write-Host "==> launching $trayExe"
        Start-Process -FilePath $trayExe
    }
}
