param(
    [switch]$GrantDevelopmentIdentityAccess,
    [string]$BackendPrincipalId = '',
    [switch]$CreateCosmosAccount,
    [switch]$CosmosOnly,
    [string]$CosmosLocation = 'westeurope',
    [switch]$ValidateOnly,
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
$subscription = 'c81a5703-6b68-42ee-8011-30eb43cb6748'
$tenant = '0071de0e-b827-493b-a8d9-6db970b9cd15'
$cosmosGroup = 'seachpoi-rg'
function Assert-AzureSuccess($step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed. Resolve the Azure error above (including MFA/RBAC) before retrying." }
}

if ($GrantDevelopmentIdentityAccess) {
    if ($BackendPrincipalId.Length -gt 0) { throw 'Choose a backend principal or the current development identity, not both.' }
    $raw = az account get-access-token --tenant $tenant --resource https://cosmos.azure.com/ --output json
    Assert-AzureSuccess 'Resolving backend development identity'
    $token = $raw | ConvertFrom-Json
    $encoded = $token.accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/')
    $padding = [int]([Math]::Ceiling($encoded.Length / 4.0) * 4)
    $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded.PadRight($padding, '='))) | ConvertFrom-Json
    if ($claims.tid -ne $tenant -or -not $claims.oid) { throw 'The token does not identify a principal in the trusted tenant.' }
    $BackendPrincipalId = $claims.oid
    $token = $null; $raw = $null; $encoded = $null
}

if ($ValidateOnly -and $WhatIf) { throw 'Choose validation or preview, not both.' }
if ($CreateCosmosAccount -and [string]::IsNullOrWhiteSpace($CosmosLocation)) { throw 'Supply the Cosmos account region.' }
$location = az group show --subscription $subscription --name $cosmosGroup --query location --output tsv
Assert-AzureSuccess 'Reading deployment location'
$template = Join-Path $PSScriptRoot 'infra\main.bicep'
$parameters = Join-Path $PSScriptRoot 'infra\main.parameters.json'
$operation = 'create'
if ($WhatIf) { $operation = 'what-if' }
if ($ValidateOnly) { $operation = 'validate' }
$createAccount = $CreateCosmosAccount.IsPresent.ToString().ToLowerInvariant()
$includeStorage = (-not $CosmosOnly.IsPresent).ToString().ToLowerInvariant()
az deployment sub $operation --subscription $subscription --name dev2lead-storage --location $location --template-file $template --parameters "@$parameters" "backendPrincipalId=$BackendPrincipalId" "createCosmosAccount=$createAccount" "cosmosLocation=$CosmosLocation" "deployStorage=$includeStorage"
Assert-AzureSuccess 'Processing the declarative Dev2Lead storage configuration'
Write-Output "Bicep operation '$operation' completed. No resources were deleted or keys read; the Cosmos data role remains container-scoped."
