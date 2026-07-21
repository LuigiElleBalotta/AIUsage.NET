#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regenerates the bundled pricing snapshots from the live feeds:

        src/AIUsage.Core/Resources/pricing_litellm_snapshot.json      (LiteLLM model_prices)
        src/AIUsage.Core/Resources/pricing_models_dev_snapshot.json   (models.dev api.json)

    Direct port of the Swift script/update_pricing_snapshots.sh's compact-format encoding, translated
    from its embedded Python to PowerShell (no python dependency introduced for a Windows-only repo).
    The compact format must stay in sync with PricingCatalogCodecs.cs (compact codec + the defaulting
    rules of the LiteLLM/models.dev parsers): per-million rates, cache write defaults to the input
    rate, cache read to a tenth of it, and "cre": false marks a synthesized (not published) cache-read
    rate so providers can tell it apart from a real discount.

.EXAMPLE
    script/update_pricing_snapshots.ps1
#>
$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$Resources = Join-Path $RootDir "src\AIUsage.Core\Resources"

$LiteLLMUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"
$ModelsDevUrl = "https://models.dev/api.json"

function Get-Number($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [bool]) { return $null }
    if ($value -is [double] -or $value -is [int] -or $value -is [long]) { return [double]$value }
    return $null
}

function New-CompactModel($input_pm, $output_pm, $cacheWrite_pm, $cacheRead_pm, $cacheReadExplicit, $ia, $oa, $cwa, $cra, $fast) {
    $model = [ordered]@{ i = $input_pm; o = $output_pm; cw = $cacheWrite_pm; cr = $cacheRead_pm }
    if (-not $cacheReadExplicit) { $model["cre"] = $false }
    if ($null -ne $ia) { $model["ia"] = $ia }
    if ($null -ne $oa) { $model["oa"] = $oa }
    if ($null -ne $cwa) { $model["cwa"] = $cwa }
    if ($null -ne $cra) { $model["cra"] = $cra }
    if ($null -ne $fast) { $model["fast"] = $fast }
    return $model
}

$retrievedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

Write-Host "Fetching LiteLLM pricing..."
$litellm = Invoke-RestMethod -Uri $LiteLLMUrl -TimeoutSec 120
Write-Host "Fetching models.dev pricing..."
$modelsDev = Invoke-RestMethod -Uri $ModelsDevUrl -TimeoutSec 120

# LiteLLM: costs are per token; entries without both input and output cost are stubs -> skipped.
$litellmModels = [ordered]@{}
foreach ($prop in $litellm.PSObject.Properties) {
    $entry = $prop.Value
    if ($entry -isnot [System.Management.Automation.PSCustomObject]) { continue }
    $i = Get-Number $entry.input_cost_per_token
    $o = Get-Number $entry.output_cost_per_token
    if ($null -eq $i -or $null -eq $o) { continue }
    $cw = Get-Number $entry.cache_creation_input_token_cost
    $cr = Get-Number $entry.cache_read_input_token_cost
    $providerSpecific = $entry.provider_specific_entry
    $fast = if ($providerSpecific) { Get-Number $providerSpecific.fast } else { $null }

    $cwValue = if ($null -ne $cw) { $cw } else { $i }
    $crValue = if ($null -ne $cr) { $cr } else { $i * 0.1 }

    $litellmModels[$prop.Name] = New-CompactModel `
        ($i * 1e6) ($o * 1e6) ($cwValue * 1e6) ($crValue * 1e6) ($null -ne $cr) `
        $(if ($null -ne (Get-Number $entry.input_cost_per_token_above_200k_tokens)) { (Get-Number $entry.input_cost_per_token_above_200k_tokens) * 1e6 } else { $null }) `
        $(if ($null -ne (Get-Number $entry.output_cost_per_token_above_200k_tokens)) { (Get-Number $entry.output_cost_per_token_above_200k_tokens) * 1e6 } else { $null }) `
        $(if ($null -ne (Get-Number $entry.cache_creation_input_token_cost_above_200k_tokens)) { (Get-Number $entry.cache_creation_input_token_cost_above_200k_tokens) * 1e6 } else { $null }) `
        $(if ($null -ne (Get-Number $entry.cache_read_input_token_cost_above_200k_tokens)) { (Get-Number $entry.cache_read_input_token_cost_above_200k_tokens) * 1e6 } else { $null }) `
        $fast
}
if ($litellmModels.Count -eq 0) { throw "LiteLLM feed produced no usable entries - aborting." }
$litellmOut = [ordered]@{ retrieved_at = $retrievedAt; models = $litellmModels }
$litellmOut | ConvertTo-Json -Depth 10 -Compress | Set-Content -Path (Join-Path $Resources "pricing_litellm_snapshot.json") -NoNewline
Write-Host "pricing_litellm_snapshot.json: $($litellmModels.Count) models"

# models.dev: costs are already per million; ids stored bare, first provider (sorted) wins.
$modelsDevModels = [ordered]@{}
foreach ($providerName in ($modelsDev.PSObject.Properties.Name | Sort-Object)) {
    $provider = $modelsDev.$providerName
    if (-not $provider.models) { continue }
    foreach ($modelProp in $provider.models.PSObject.Properties) {
        $modelId = $modelProp.Name
        if ($modelsDevModels.Contains($modelId)) { continue }
        $model = $modelProp.Value
        $cost = $model.cost
        if (-not $cost) { continue }
        $i = Get-Number $cost.input
        $o = Get-Number $cost.output
        if ($null -eq $i -or $null -eq $o) { continue }
        $cw = Get-Number $cost.cache_write
        $cr = Get-Number $cost.cache_read
        $cwValue = if ($null -ne $cw) { $cw } else { $i }
        $crValue = if ($null -ne $cr) { $cr } else { $i * 0.1 }
        $modelsDevModels[$modelId] = New-CompactModel $i $o $cwValue $crValue ($null -ne $cr) $null $null $null $null $null
    }
}
if ($modelsDevModels.Count -eq 0) { throw "models.dev feed produced no usable entries - aborting." }
$modelsDevOut = [ordered]@{ retrieved_at = $retrievedAt; models = $modelsDevModels }
$modelsDevOut | ConvertTo-Json -Depth 10 -Compress | Set-Content -Path (Join-Path $Resources "pricing_models_dev_snapshot.json") -NoNewline
Write-Host "pricing_models_dev_snapshot.json: $($modelsDevModels.Count) models"

Get-ChildItem (Join-Path $Resources "pricing_*_snapshot.json") | Format-Table Name, Length
