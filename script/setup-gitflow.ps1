#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configures git flow (AVH edition) for this clone with the repo's conventions: main = production,
    develop = integration, feature/ bugfix/ release/ hotfix/ support/ prefixes, version tags like
    v0.5.0. Run once after cloning; then use `git flow feature start <name>`, `git flow release
    start X.Y.Z`, `git flow hotfix start X.Y.Z` instead of creating branches by hand (the server
    rejects branch names that don't follow the convention).
#>
$ErrorActionPreference = "Stop"

git flow version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "git flow (AVH edition) is not installed. Windows: 'winget install petervanderdoes.git-flow-avh' or it ships with Git for Windows."
}

git fetch origin | Out-Null
if (-not (git branch --list develop)) { git branch develop origin/develop }

$cfg = @{
    "gitflow.branch.master"      = "main"
    "gitflow.branch.develop"     = "develop"
    "gitflow.prefix.feature"     = "feature/"
    "gitflow.prefix.bugfix"      = "bugfix/"
    "gitflow.prefix.release"     = "release/"
    "gitflow.prefix.hotfix"      = "hotfix/"
    "gitflow.prefix.support"     = "support/"
    "gitflow.prefix.versiontag"  = "v"
}
foreach ($k in $cfg.Keys) { git config $k $cfg[$k] }

# Client-side guard: rejects hand-made branches (git branch / checkout -b) and non-git-flow names on push.
git config core.hooksPath .githooks

git flow config | Out-Null
if ($LASTEXITCODE -ne 0) { throw "git flow config check failed." }
Write-Host "git flow configured: main/develop, tag prefix 'v'."
Write-Host "Release: git flow release start X.Y.Z -> bump <Version> -> git flow release finish X.Y.Z -> push main, develop, tags."
