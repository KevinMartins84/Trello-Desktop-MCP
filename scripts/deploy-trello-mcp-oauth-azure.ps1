# Deploy Trello MCP Web (OAuth + /mcp) to Azure App Service on mail@kevinmartins.nl subscription.
param(
    [string]$ResourceGroup = 'trello-mcp-oauth-rg',
    [string]$Location = 'westeurope',
    [string]$PlanName = 'trello-mcp-oauth-plan',
    [string]$AppName = 'trello-mcp-oauth-km',
    [string]$SharedSecret = $env:MCP_OAUTH_SHARED_SECRET,
    [string]$DefaultTrelloApiKey = $env:TRELLO_API_KEY
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'host\TrelloMcp.Web\TrelloMcp.Web.csproj'
$publishDir = Join-Path $env:TEMP 'trello-mcp-publish'
$zip = Join-Path $env:TEMP 'trello-mcp-publish.zip'

if ([string]::IsNullOrWhiteSpace($SharedSecret)) {
    throw 'Set MCP_OAUTH_SHARED_SECRET (Claude connector OAuth Client Secret).'
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -o $publishDir
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip

$baseUrl = "https://$AppName.azurewebsites.net"
az group create --name $ResourceGroup --location $Location | Out-Null
$planExists = az appservice plan show --name $PlanName --resource-group $ResourceGroup 2>$null
if (-not $planExists) {
    az appservice plan create --name $PlanName --resource-group $ResourceGroup --sku F1 --is-linux | Out-Null
}
$appExists = az webapp show --name $AppName --resource-group $ResourceGroup 2>$null
if (-not $appExists) {
    az webapp create --resource-group $ResourceGroup --plan $PlanName --name $AppName --runtime 'DOTNET:10.0' | Out-Null
}

$settings = @(
    "McpOAuth__PublicBaseUrl=$baseUrl",
    "McpOAuth__SharedSecret=$SharedSecret",
    "ASPNETCORE_URLS=http://0.0.0.0:8080"
)
if (-not [string]::IsNullOrWhiteSpace($DefaultTrelloApiKey)) {
    $settings += "McpOAuth__DefaultTrelloApiKey=$DefaultTrelloApiKey"
}
az webapp config appsettings set --resource-group $ResourceGroup --name $AppName --settings $settings | Out-Null
az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $zip --type zip --clean true

Write-Host ""
Write-Host "MCP URL:      $baseUrl/mcp"
Write-Host "OAuth issuer: $baseUrl/oauth"
Write-Host "Claude: URL=$baseUrl/mcp  Client ID=trello  Client Secret=(MCP_OAUTH_SHARED_SECRET)"
