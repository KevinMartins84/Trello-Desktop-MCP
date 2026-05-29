# Multiple Azure logins (all accounts stay available)

The CLI keeps every account you sign into. Switch with named profiles — nothing is removed when you add another login.

## Your profiles

| Profile | Email |
|---------|--------|
| `personal` | mail@kevinmartins.nl |
| `zineps` | kevin@zineps.com |
| `etesian` | kevin.martins@etesian.nl |
| `ifaktor` | kevinmartins@ifaktor.nl |

Defined in `scripts/az-profiles.json` (add `tenantId` / `subscriptionId` when you know them).

## Commands

```powershell
cd tmp\Trello-Desktop-MCP

.\scripts\az-use.ps1 list                 # profiles + login status
.\scripts\az-use.ps1 login personal       # one account (device code)
.\scripts\az-use.ps1 login-all            # refresh all four (run after token expiry)
.\scripts\az-use.ps1 use etesian          # switch active subscription
.\scripts\az-use.ps1 current

.\scripts\deploy-trello-mcp-oauth-azure.ps1                    # deploy on personal
.\scripts\deploy-trello-mcp-oauth-azure.ps1 -AzureProfile zineps
```

## First-time setup

```powershell
.\scripts\az-use.ps1 login-all
```

Sign in for each prompt (four browsers/device codes). After that, only `use <profile>` is needed day to day.

## Trello deploy secrets

Copy `.trello-mcp.azure.env.example` → `.trello-mcp.azure.env` and set Trello + optional `AZURE_SUBSCRIPTION_ID`.
