namespace TrelloMcp.Web.Mcp;

/// <summary>RFC 9728 discovery URLs and WWW-Authenticate challenges for MCP clients (Cursor, Claude).</summary>
public static class McpOAuthDiscovery
{
    public static string GetProtectedResourceMetadataUrl(McpOAuthSettings settings) =>
        $"{settings.PublicBaseUrl.TrimEnd('/')}/.well-known/oauth-protected-resource";

    public static string BuildWwwAuthenticate(McpOAuthSettings settings) =>
        $"Bearer realm=\"mcp\", resource_metadata=\"{GetProtectedResourceMetadataUrl(settings)}\"";
}
