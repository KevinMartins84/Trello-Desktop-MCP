using System.Security.Cryptography;
using System.Text;

namespace TrelloMcp.Web.Mcp;

/// <summary>Trello REST API OAuth 1.0a (request token → authorize → access token).</summary>
public sealed class TrelloOAuth1Client(IHttpClientFactory httpClientFactory)
{
    private const string RequestTokenUrl = "https://trello.com/1/OAuthGetRequestToken";
    private const string AccessTokenUrl = "https://trello.com/1/OAuthGetAccessToken";

    public async Task<OAuthTokenPair> GetRequestTokenAsync(
        string consumerKey,
        string consumerSecret,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        var oauthParams = CreateOAuthParams(consumerKey);
        oauthParams["oauth_callback"] = callbackUrl;

        var body = await SendSignedGetAsync(
            RequestTokenUrl,
            oauthParams,
            consumerSecret,
            tokenSecret: null,
            cancellationToken).ConfigureAwait(false);

        return ParseTokenResponse(body);
    }

    public async Task<OAuthTokenPair> GetAccessTokenAsync(
        string consumerKey,
        string consumerSecret,
        string requestToken,
        string requestTokenSecret,
        string verifier,
        CancellationToken cancellationToken = default)
    {
        var oauthParams = CreateOAuthParams(consumerKey);
        oauthParams["oauth_token"] = requestToken;
        oauthParams["oauth_verifier"] = verifier;

        var body = await SendSignedGetAsync(
            AccessTokenUrl,
            oauthParams,
            consumerSecret,
            requestTokenSecret,
            cancellationToken).ConfigureAwait(false);

        return ParseTokenResponse(body);
    }

    private async Task<string> SendSignedGetAsync(
        string url,
        Dictionary<string, string> oauthParams,
        string consumerSecret,
        string? tokenSecret,
        CancellationToken cancellationToken)
    {
        var signature = ComputeSignature("GET", url, oauthParams, consumerSecret, tokenSecret);
        oauthParams["oauth_signature"] = signature;

        var header = "OAuth " + string.Join(
            ", ",
            oauthParams.OrderBy(static p => p.Key, StringComparer.Ordinal)
                .Select(static p => $"{PercentEncode(p.Key)}=\"{PercentEncode(p.Value)}\""));

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", header);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Trello OAuth request failed ({(int)response.StatusCode}): {body}");
        }

        return body;
    }

    private static Dictionary<string, string> CreateOAuthParams(string consumerKey)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["oauth_nonce"] = Guid.NewGuid().ToString("N"),
            ["oauth_version"] = "1.0",
        };
    }

    private static string ComputeSignature(
        string httpMethod,
        string url,
        IDictionary<string, string> parameters,
        string consumerSecret,
        string? tokenSecret)
    {
        var normalized = string.Join(
            "&",
            parameters
                .OrderBy(static p => p.Key, StringComparer.Ordinal)
                .Select(static p => $"{PercentEncode(p.Key)}={PercentEncode(p.Value)}"));

        var signatureBase = string.Join(
            "&",
            httpMethod.ToUpperInvariant(),
            PercentEncode(url),
            PercentEncode(normalized));

        var signingKey = $"{PercentEncode(consumerSecret)}&{PercentEncode(tokenSecret ?? string.Empty)}";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(signingKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase));
        return Convert.ToBase64String(hash);
    }

    private static OAuthTokenPair ParseTokenResponse(string body)
    {
        var values = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var idx = part.IndexOf('=');
                if (idx < 0)
                {
                    return new KeyValuePair<string, string>(part, string.Empty);
                }

                return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(part[..idx]),
                    Uri.UnescapeDataString(part[(idx + 1)..]));
            })
            .ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);

        if (!values.TryGetValue("oauth_token", out var token) || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Trello OAuth response did not include oauth_token.");
        }

        values.TryGetValue("oauth_token_secret", out var tokenSecret);
        return new OAuthTokenPair(token, tokenSecret ?? string.Empty);
    }

    private static string PercentEncode(string value) => Uri.EscapeDataString(value);
}

public readonly record struct OAuthTokenPair(string Token, string TokenSecret);
