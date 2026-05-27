using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace TrelloMcp.Web.Mcp;

[McpServerToolType]
public sealed class TrelloMcpTools(
    IHttpContextAccessor httpContextAccessor,
    TrelloCredentialStore credentialStore,
    IHttpClientFactory httpClientFactory,
    IOptions<McpOAuthSettings> oauthSettings)
{
    private const string TrelloApiBase = "https://api.trello.com/1";

    [McpServerTool, Description("List boards for the authenticated Trello user.")]
    public async Task<string> TrelloGetUserBoards(CancellationToken cancellationToken)
    {
        var creds = RequireCredentials();
        var url = $"{TrelloApiBase}/members/me/boards?filter=open&fields=id,name,url&key={Uri.EscapeDataString(creds.ApiKey)}&token={Uri.EscapeDataString(creds.Token)}";
        var client = httpClientFactory.CreateClient();
        var boards = await client.GetFromJsonAsync<JsonElement>(url, cancellationToken).ConfigureAwait(false);
        return boards.GetRawText();
    }

    [McpServerTool, Description("Search Trello (boards, cards, members).")]
    public async Task<string> TrelloSearch(
        [Description("Search query")] string query,
        CancellationToken cancellationToken)
    {
        var creds = RequireCredentials();
        var url = $"{TrelloApiBase}/search?query={Uri.EscapeDataString(query)}&modelTypes=cards,boards&cards_limit=20&boards_limit=10&key={Uri.EscapeDataString(creds.ApiKey)}&token={Uri.EscapeDataString(creds.Token)}";
        var client = httpClientFactory.CreateClient();
        var result = await client.GetFromJsonAsync<JsonElement>(url, cancellationToken).ConfigureAwait(false);
        return result.GetRawText();
    }

    [McpServerTool, Description("Get Trello board details.")]
    public async Task<string> GetBoardDetails(
        [Description("24-character board id")] string boardId,
        CancellationToken cancellationToken)
    {
        var creds = RequireCredentials();
        var url = $"{TrelloApiBase}/boards/{Uri.EscapeDataString(boardId)}?lists=open&cards=open&key={Uri.EscapeDataString(creds.ApiKey)}&token={Uri.EscapeDataString(creds.Token)}";
        var client = httpClientFactory.CreateClient();
        var result = await client.GetFromJsonAsync<JsonElement>(url, cancellationToken).ConfigureAwait(false);
        return result.GetRawText();
    }

    [McpServerTool, Description("Get a Trello card by id.")]
    public async Task<string> GetCard(
        [Description("24-character card id")] string cardId,
        CancellationToken cancellationToken)
    {
        var creds = RequireCredentials();
        var url = $"{TrelloApiBase}/cards/{Uri.EscapeDataString(cardId)}?members=true&labels=true&key={Uri.EscapeDataString(creds.ApiKey)}&token={Uri.EscapeDataString(creds.Token)}";
        var client = httpClientFactory.CreateClient();
        var result = await client.GetFromJsonAsync<JsonElement>(url, cancellationToken).ConfigureAwait(false);
        return result.GetRawText();
    }

    [McpServerTool, Description("How to connect this Trello MCP server from Claude or Cursor.")]
    public string GetTrelloMcpConnectionGuide()
    {
        var s = oauthSettings.Value;
        return $"""
            ## Trello MCP (remote)

            **MCP URL:** `{s.GetMcpResourceUrl()}`

            ### Claude custom connector
            1. URL: `{s.GetMcpResourceUrl()}`
            2. Advanced → OAuth Client ID: `trello` (any string)
            3. Advanced → OAuth Client Secret: same value as server `McpOAuth:SharedSecret`
            4. Connect → enter Trello API key + token validity → approve on Trello

            ### Cursor (mcp-remote)
            Use OAuth via Claude connector, or run the stdio server locally with `TRELLO_API_KEY` and `TRELLO_TOKEN`.
            """;
    }

    private TrelloCredentials RequireCredentials()
    {
        var sub = httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContextAccessor.HttpContext?.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var creds = credentialStore.GetByClientId(sub);
        if (creds is null)
        {
            throw new InvalidOperationException("Trello credentials not found. Reconnect the MCP connector and complete Trello sign-in.");
        }

        return creds;
    }
}
