# Deploy Trello MCP Web (OAuth + /mcp) to Azure App Service.
# Use -SubscriptionId or AZURE_SUBSCRIPTION_ID when you have multiple az logins.
param(
    [string]$ResourceGroup = 'trello-mcp-oauth-rg',
    [string]$Location = 'westeurope',
    [string]$PlanName = 'trello-mcp-oauth-plan',
    [string]$AppName = 'trello-mcp-oauth-km',
    [string]$AzureProfile = 'personal',
    [string]$SubscriptionId = $env:AZURE_SUBSCRIPTION_ID,
    [string]$SubscriptionName = $env:AZURE_SUBSCRIPTION_NAME,
    [string]$DefaultTrelloApiKey = $env:TRELLO_API_KEY,
    [string]$TrelloOAuthSecret = $env:TRELLO_OAUTH_SECRET
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localEnv = Join-Path $root '.trello-mcp.azure.env'
if (Test-Path $localEnv) {
    Get-Content $localEnv | ForEach-Object {
        if ($_ -match '^\s*([^#=]+)=(.*)$') {
            $name = $matches[1].Trim()
            $value = $matches[2].Trim()
            if ($name -eq 'TRELLO_API_KEY' -and [string]::IsNullOrWhiteSpace($DefaultTrelloApiKey)) { $DefaultTrelloApiKey = $value }
            if ($name -eq 'TRELLO_OAUTH_SECRET' -and [string]::IsNullOrWhiteSpace($TrelloOAuthSecret)) { $TrelloOAuthSecret = $value }
            if ($name -eq 'AZURE_SUBSCRIPTION_ID' -and [string]::IsNullOrWhiteSpace($SubscriptionId)) { $SubscriptionId = $value }
            if ($name -eq 'AZURE_SUBSCRIPTION_NAME' -and [string]::IsNullOrWhiteSpace($SubscriptionName)) { $SubscriptionName = $value }
        }
    }
}

function Get-AzSubscriptionArgs {
    if (-not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
        return @('--subscription', $SubscriptionId)
    }
    if (-not [string]::IsNullOrWhiteSpace($SubscriptionName)) {
        return @('--subscription', $SubscriptionName)
    }
    return @()
}

if (-not [string]::IsNullOrWhiteSpace($AzureProfile)) {
    $azUse = Join-Path $PSScriptRoot 'az-use.ps1'
    if (-not (Test-Path $azUse)) {
        throw "Missing $azUse"
    }
    Write-Host "Azure profile: $AzureProfile"
    & $azUse use $AzureProfile -Quiet
    $active = az account show -o json | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
        $SubscriptionId = $active.id
    }
}

$azSub = Get-AzSubscriptionArgs
if ($azSub.Count -gt 0) {
    Write-Host "Using subscription: $($azSub[1])"
    az account set @azSub | Out-Null
}
else {
    $current = az account show -o json | ConvertFrom-Json
    Write-Host "Using default subscription: $($current.name) ($($current.user.name))"
}

$project = Join-Path $root 'TrelloMcp.Web\TrelloMcp.Web.csproj'
$publishDir = Join-Path $env:TEMP 'trello-mcp-publish'
$zip = Join-Path $env:TEMP 'trello-mcp-publish.zip'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -o $publishDir
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip

$baseUrl = "https://$AppName.azurewebsites.net"
az group create --name $ResourceGroup --location $Location @azSub | Out-Null

function Test-AzResourceExists {
    param([scriptblock]$Command)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $Command 2>$null | Out-Null
    $ok = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $prev
    return $ok
}

if (-not (Test-AzResourceExists { az appservice plan show --name $PlanName --resource-group $ResourceGroup @azSub })) {
    az appservice plan create --name $PlanName --resource-group $ResourceGroup --sku F1 --is-linux @azSub | Out-Null
}
if (-not (Test-AzResourceExists { az webapp show --name $AppName --resource-group $ResourceGroup @azSub })) {
    az webapp create --resource-group $ResourceGroup --plan $PlanName --name $AppName --runtime 'DOTNETCORE:10.0' @azSub | Out-Null
}

$settings = @(
    "McpOAuth__PublicBaseUrl=$baseUrl",
    "ASPNETCORE_URLS=http://0.0.0.0:8080",
    "WEBSITES_ENABLE_APP_SERVICE_STORAGE=true"
)
if ($DefaultTrelloApiKey -match '^[A-Za-z0-9_-]{20,128}$' -and $DefaultTrelloApiKey -notmatch '--') {
    $settings += "McpOAuth__DefaultTrelloApiKey=$DefaultTrelloApiKey"
}
if (-not [string]::IsNullOrWhiteSpace($TrelloOAuthSecret)) {
    $settings += "McpOAuth__TrelloOAuthSecret=$TrelloOAuthSecret"
}
az webapp config appsettings set --resource-group $ResourceGroup --name $AppName --settings $settings @azSub | Out-Null
az webapp config appsettings delete --resource-group $ResourceGroup --name $AppName --setting-names McpOAuth__SharedSecret @azSub 2>$null | Out-Null
az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $zip --type zip --clean true @azSub

Write-Host ""
Write-Host "MCP URL:      $baseUrl/mcp"
Write-Host "OAuth issuer: $baseUrl/oauth"
Write-Host "Claude/Cursor: URL=$baseUrl/mcp (no client secret; leave OAuth secret empty)"
