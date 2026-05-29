# Switch Azure CLI between multiple accounts (all stay in cache after login).
# Usage:
#   .\scripts\az-use.ps1 list
#   .\scripts\az-use.ps1 login personal
#   .\scripts\az-use.ps1 login-all
#   .\scripts\az-use.ps1 use zineps
param(
    [Parameter(Position = 0)]
    [ValidateSet('list', 'login', 'login-all', 'use', 'current')]
    [string]$Action = 'list',
    [Parameter(Position = 1)]
    [string]$Profile = 'personal',
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$configPath = Join-Path $PSScriptRoot 'az-profiles.json'
if (-not (Test-Path $configPath)) {
    throw "Missing $configPath"
}

$profiles = Get-Content $configPath -Raw | ConvertFrom-Json

function Write-Info([string]$Message) {
    if (-not $Quiet) {
        Write-Host $Message
    }
}

function Get-ProfileNames {
    return @($profiles.PSObject.Properties | ForEach-Object { $_.Name })
}

function Get-Profile([string]$Name) {
    if (-not $profiles.PSObject.Properties[$Name]) {
        throw "Unknown profile '$Name'. Known: $((Get-ProfileNames) -join ', ')"
    }
    return $profiles.$Name
}

function Get-AzAccounts {
    return @(az account list -o json | ConvertFrom-Json)
}

function Find-AccountForProfile([object]$P) {
    $accounts = Get-AzAccounts
    if ($accounts.Count -eq 0) {
        return $null
    }

    $user = $P.user
    if ([string]::IsNullOrWhiteSpace($user)) {
        $user = $P.label
    }

    $matches = @($accounts | Where-Object { $_.user.name -ieq $user })
    if ($matches.Count -eq 0) {
        return $null
    }

    if ($P.subscriptionId) {
        $byId = $matches | Where-Object { $_.id -ieq $P.subscriptionId }
        if ($byId) {
            return $byId | Select-Object -First 1
        }
    }

    if ($P.subscriptionName) {
        $byName = $matches | Where-Object { $_.name -ieq $P.subscriptionName }
        if ($byName) {
            return $byName | Select-Object -First 1
        }
    }

    $default = $matches | Where-Object { $_.isDefault -eq $true }
    if ($default) {
        return $default | Select-Object -First 1
    }

    return $matches | Select-Object -First 1
}

function Test-ProfileLoggedIn([object]$P) {
    return $null -ne (Find-AccountForProfile $P)
}

function Invoke-ProfileLogin([string]$Name) {
    $p = Get-Profile $Name
    $user = if ($p.user) { $p.user } else { $p.label }
    Write-Info "Login profile '$Name' ($user)..."

    if ($p.tenantId) {
        az login --use-device-code --tenant $p.tenantId --only-show-errors | Out-Host
    }
    else {
        # --username cannot be combined with --use-device-code; browser picker selects the account.
        az login --only-show-errors | Out-Host
    }

    if ($LASTEXITCODE -ne 0) {
        throw "az login failed for profile '$Name'."
    }

    & $PSCommandPath use $Name -Quiet:$Quiet
}

switch ($Action) {
    'list' {
        Write-Host "Configured profiles:"
        foreach ($name in Get-ProfileNames) {
            $p = Get-Profile $name
            $loggedIn = Test-ProfileLoggedIn $p
            $status = if ($loggedIn) { 'logged in' } else { 'not logged in' }
            $sub = if ($p.subscriptionId) { $p.subscriptionId } elseif ($p.subscriptionName) { $p.subscriptionName } else { '(default sub for user)' }
            Write-Host ("  {0,-10} {1,-30} {2,-12} {3}" -f $name, $p.label, $status, $sub)
        }
        Write-Host ""
        Write-Host "All subscriptions in CLI cache:"
        az account list --query "[].{user:user.name,subscription:name,id:id,isDefault:isDefault}" -o table
    }
    'login' {
        Invoke-ProfileLogin $Profile
    }
    'login-all' {
        foreach ($name in Get-ProfileNames) {
            try {
                Invoke-ProfileLogin $name
            }
            catch {
                Write-Warning "$name : $($_.Exception.Message)"
            }
            Write-Host ""
        }
        & $PSCommandPath list
    }
    'use' {
        $p = Get-Profile $Profile
        $account = Find-AccountForProfile $p
        if (-not $account) {
            $user = if ($p.user) { $p.user } else { $p.label }
            throw "Profile '$Profile' ($user) is not in the az cache. Run: .\scripts\az-use.ps1 login $Profile"
        }

        az account set --subscription $account.id | Out-Null
        $env:AZURE_SUBSCRIPTION_ID = $account.id
        $env:AZURE_SUBSCRIPTION_NAME = $account.name

        Write-Info "Active: $($account.name) as $($account.user.name) ($($account.id))"
        if (-not $Quiet) {
            Write-Output $account.id
        }
    }
    'current' {
        az account show --query "{user:user.name,subscription:name,id:id,tenant:tenantId}" -o table
    }
}
