namespace TrelloMcp.Web.Mcp;

public sealed class McpOAuthSettings
{
    public const string SectionName = "McpOAuth";

    public string PublicBaseUrl { get; set; } = "http://localhost:5088";

    public string McpPath { get; set; } = "/mcp";

    public string OAuthPath { get; set; } = "/oauth";

    /// <summary>Claude connector → Advanced → OAuth Client Secret (same value).</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Optional prefill for the Trello API key field on the login page.</summary>
    public string DefaultTrelloApiKey { get; set; } = string.Empty;

    public string GetMcpResourceUrl() => $"{PublicBaseUrl.TrimEnd('/')}{McpPath}";

    public string GetOAuthIssuerUrl() => $"{PublicBaseUrl.TrimEnd('/')}{OAuthPath}";
}
