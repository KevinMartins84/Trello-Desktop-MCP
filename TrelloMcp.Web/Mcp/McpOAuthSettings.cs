namespace TrelloMcp.Web.Mcp;

public sealed class McpOAuthSettings
{
    public const string SectionName = "McpOAuth";

    public string PublicBaseUrl { get; set; } = "http://localhost:5088";

    public string McpPath { get; set; } = "/mcp";

    public string OAuthPath { get; set; } = "/oauth";

    /// <summary>Trello Power-Up API key (oauth_consumer_key).</summary>
    public string DefaultTrelloApiKey { get; set; } = string.Empty;

    /// <summary>Trello Power-Up OAuth secret for OAuth 1.0a signing.</summary>
    public string TrelloOAuthSecret { get; set; } = string.Empty;

    /// <summary>Optional PKCS#8 RSA private key (base64). Survives redeploys when set as an App Service setting.</summary>
    public string SigningKeyPkcs8Base64 { get; set; } = string.Empty;

    /// <summary>JWT <c>kid</c> when <see cref="SigningKeyPkcs8Base64"/> is set.</summary>
    public string SigningKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Multipart upload endpoint that returns JSON with a public <c>url</c> field.
    /// Used by attachment download rehosting so agents can open private Trello files.
    /// </summary>
    public string ArtifactUploadUrl { get; set; } = "https://developer.kevinmartins.nl/api/artifacts";

    public string GetMcpResourceUrl() => $"{PublicBaseUrl.TrimEnd('/')}{McpPath}";

    public string GetOAuthIssuerUrl() => $"{PublicBaseUrl.TrimEnd('/')}{OAuthPath}";
}
