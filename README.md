# Trello MCP (remote)

Single **ASP.NET Core** app: HTTP MCP at `/mcp` and OAuth 2.0 at `/oauth` so **Claude** and **Cursor** can sign in with Trello (no client secret).

Fork of [kocakli/Trello-Desktop-MCP](https://github.com/kocakli/Trello-Desktop-MCP); the original Node desktop server was replaced by this one project for cloud + OAuth.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Trello Power-Up API key + OAuth secret from https://trello.com/power-ups/admin

## Local run

```bash
cd TrelloMcp.Web
dotnet user-secrets set "McpOAuth:PublicBaseUrl" "http://127.0.0.1:5088"
dotnet user-secrets set "McpOAuth:DefaultTrelloApiKey" "<api-key>"
dotnet user-secrets set "McpOAuth:TrelloOAuthSecret" "<oauth-secret>"
dotnet run
```

- MCP: `http://127.0.0.1:5088/mcp`
- OAuth metadata: `http://127.0.0.1:5088/.well-known/oauth-authorization-server`

## Claude / Cursor

| Field | Value |
|--------|--------|
| MCP URL | `https://trello-mcp-oauth-km.azurewebsites.net/mcp` |
| OAuth Client ID | leave empty (dynamic registration) |
| OAuth Client Secret | **leave empty** — not used |

Connect → choose token duration → approve on Trello.

OAuth tokens, refresh tokens, Trello credentials, and signing keys persist under `/home/site/data/trello-mcp` across zip deploys.

## Deploy (Azure)

```powershell
.\scripts\deploy-trello-mcp-oauth-azure.ps1
```

Requires Trello keys in `.trello-mcp.azure.env` (see `.trello-mcp.azure.env.example`).

## Configuration

| Setting | Env var |
|---------|---------|
| `McpOAuth:PublicBaseUrl` | `McpOAuth__PublicBaseUrl` |
| `McpOAuth:DefaultTrelloApiKey` | `McpOAuth__DefaultTrelloApiKey` |
| `McpOAuth:TrelloOAuthSecret` | `McpOAuth__TrelloOAuthSecret` |

## Project layout

```
TrelloMcp.Web/          # the only application
scripts/                # Azure deploy
```
