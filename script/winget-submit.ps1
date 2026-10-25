#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Opens the microsoft/winget-pkgs PR for a released version: renders the manifest templates in
    winget/ (version, tag, release date, installer SHA256), pushes them to a branch of the caller's
    fork of winget-pkgs through the GitHub API, and opens the PR. Replaces winget-releaser/komac,
    which regenerated the manifest from the Velopack Setup.exe with the wrong architecture and
    switches (see winget/README.md).

.PARAMETER Version
    Version without the "v", e.g. 0.5.0.

.PARAMETER Tag
    Release tag, default "v<Version>".

.PARAMETER DryRun
    Only renders the manifests into -OutDir and prints them; no GitHub writes.

.PARAMETER Sha256
    Skip downloading the installer and use this hash (useful with -DryRun).

.NOTES
    Needs the gh CLI authenticated through GH_TOKEN with a classic PAT (public_repo + workflow) and
    a fork of microsoft/winget-pkgs on the token's account.
#>
param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$Tag = "v$Version",
    [string]$Repo = "LuigiElleBalotta/AIUsage.NET",
    [string]$Identifier = "LuigiElleBalotta.AIUsageNET",
    [string]$AssetName = "AIUsage.NET-win-Setup.exe",
    [string]$Sha256,
    [switch]$DryRun,
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$Upstream = "microsoft/winget-pkgs"
$UpstreamBranch = "master"

function Invoke-Gh {
    $out = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed (exit $LASTEXITCODE)." }
    $out
}

# 1. installer hash
$url = "https://github.com/$Repo/releases/download/$Tag/$AssetName"
if (-not $Sha256) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "winget-submit-$([guid]::NewGuid()).exe"
    Write-Host "==> downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    $Sha256 = (Get-FileHash $tmp -Algorithm SHA256).Hash
    Remove-Item $tmp
}
$Sha256 = $Sha256.ToUpperInvariant()
Write-Host "==> SHA256 $Sha256"

# 2. render templates (LF, no BOM — winget rejects odd encodings)
$values = @{
    "{{VERSION}}"      = $Version
    "{{TAG}}"          = $Tag
    "{{RELEASE_DATE}}" = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    "{{SHA256}}"       = $Sha256
}
$names = @("installer", "locale.en-US", "")
$rendered = @{}
foreach ($n in $names) {
    $suffix = if ($n) { ".$n" } else { "" }
    $file = "$Identifier$suffix.yaml"
    $text = [IO.File]::ReadAllText((Join-Path $RootDir "winget\$file")).Replace("`r`n", "`n")
    foreach ($k in $values.Keys) { $text = $text.Replace($k, $values[$k]) }
    if ($text -match "\{\{") { throw "Unreplaced placeholder in $file." }
    $rendered[$file] = $text
}
$publisher, $app = $Identifier.Split(".", 2)
$manifestDir = "manifests/$($publisher.Substring(0,1).ToLower())/$publisher/$app/$Version"

if ($DryRun) {
    if ($OutDir) {
        New-Item -ItemType Directory -Force $OutDir | Out-Null
        foreach ($f in $rendered.Keys) { [IO.File]::WriteAllText((Join-Path $OutDir $f), $rendered[$f], (New-Object Text.UTF8Encoding $false)) }
    }
    foreach ($f in $rendered.Keys) { Write-Host "---- $manifestDir/$f"; Write-Host $rendered[$f] }
    return
}

# 3. fork: sync, branch from upstream master, commit manifests through the contents API
$login = (Invoke-Gh api user -q .login).Trim()
$fork = "$login/winget-pkgs"
$branch = "$Identifier-$Version"
Write-Host "==> syncing fork $fork"
Invoke-Gh repo sync $fork --branch $UpstreamBranch | Out-Null
$baseSha = (Invoke-Gh api "repos/$Upstream/git/ref/heads/$UpstreamBranch" -q .object.sha).Trim()

& gh api "repos/$fork/git/ref/heads/$branch" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "==> resetting existing branch $branch to upstream"
    Invoke-Gh api -X PATCH "repos/$fork/git/refs/heads/$branch" -f sha=$baseSha -F force=true | Out-Null
} else {
    Invoke-Gh api -X POST "repos/$fork/git/refs" -f ref="refs/heads/$branch" -f sha=$baseSha | Out-Null
}

foreach ($f in $rendered.Keys) {
    $path = "$manifestDir/$f"
    $b64 = [Convert]::ToBase64String((New-Object Text.UTF8Encoding $false).GetBytes($rendered[$f]))
    $existing = & gh api "repos/$fork/contents/$path`?ref=$branch" -q .sha 2>$null
    $args2 = @("api", "-X", "PUT", "repos/$fork/contents/$path", "-f", "message=New version: $Identifier version $Version", "-f", "branch=$branch", "-f", "content=$b64")
    if ($LASTEXITCODE -eq 0 -and $existing) { $args2 += @("-f", "sha=$existing") }
    Invoke-Gh @args2 | Out-Null
}

# 4. PR (skip if one is already open for this branch)
$open = (Invoke-Gh pr list -R $Upstream --head "$login`:$branch" --state open --json url -q ".[0].url")
if ($open) { Write-Host "==> PR already open: $open"; return }
$body = "Automated submission of $Identifier $Version from https://github.com/$Repo/releases/tag/$Tag (manifests generated from the templates in the repo's winget/ folder)."
$pr = Invoke-Gh pr create -R $Upstream --head "$login`:$branch" --base $UpstreamBranch --title "New version: $Identifier version $Version" --body $body
Write-Host "==> $pr"
