using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace TrelloMcp.Web.Mcp;

/// <summary>
/// MCP OAuth 2.0 authorization server for Claude custom connectors, with Trello API key + token capture.
/// </summary>
public sealed class TrelloMcpOAuthServer
{
    private static readonly string[] Scopes = ["mcp:tools", "offline_access"];
    private static readonly HashSet<string> ClaudeRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude.ai",
        "www.claude.ai",
        "claude.com",
        "www.claude.com",
    };

    private static readonly Dictionary<string, string> DurationLabels = new(StringComparer.Ordinal)
    {
        ["1d"] = "1 day",
        ["1w"] = "1 week",
        ["1m"] = "1 month",
        ["6m"] = "6 months",
        ["1y"] = "1 year",
        ["always"] = "always",
    };

    private static readonly Dictionary<string, int> DurationSeconds = new(StringComparer.Ordinal)
    {
        ["1d"] = 24 * 60 * 60,
        ["1w"] = 7 * 24 * 60 * 60,
        ["1m"] = 30 * 24 * 60 * 60,
        ["6m"] = 180 * 24 * 60 * 60,
        ["1y"] = 365 * 24 * 60 * 60,
    };

    private readonly McpOAuthSettings _settings;
    private readonly TrelloCredentialStore _credentials;
    private readonly McpRefreshTokenStore _refreshTokens;
    private readonly TrelloOAuth1Client _trelloOAuth1;
    private readonly RSA _rsa;
    private readonly string _keyId;
    private readonly ConcurrentDictionary<string, AuthorizationCodeEntry> _authCodes = new();
    private readonly ConcurrentDictionary<string, ClientRegistration> _clients = new();
    private readonly ConcurrentDictionary<string, PendingOAuthSession> _pendingOAuth = new();
    private readonly ConcurrentDictionary<string, PendingTrelloLogin> _pendingTrello = new();

    private static readonly Dictionary<string, string> TrelloOAuthExpiration = new(StringComparer.Ordinal)
    {
        ["1d"] = "1day",
        ["1w"] = "30days",
        ["1m"] = "30days",
        ["6m"] = "never",
        ["1y"] = "never",
        ["always"] = "never",
    };

    public TrelloMcpOAuthServer(
        IOptions<McpOAuthSettings> settings,
        TrelloCredentialStore credentials,
        McpRefreshTokenStore refreshTokens,
        TrelloOAuth1Client trelloOAuth1,
        McpOAuthSigningKeyStore signingKeyStore)
    {
        _settings = settings.Value;
        _credentials = credentials;
        _refreshTokens = refreshTokens;
        _trelloOAuth1 = trelloOAuth1;
        var material = signingKeyStore.GetMaterial();
        _rsa = material.Rsa;
        _keyId = material.KeyId;
    }

    public string Issuer => _settings.GetOAuthIssuerUrl();

    public string ResourceUrl => _settings.GetMcpResourceUrl();

    public SecurityKey SigningKey => new RsaSecurityKey(_rsa) { KeyId = _keyId };

    public IResult HandleAuthorizationServerMetadata() =>
        Results.Json(new
        {
            issuer = Issuer,
            authorization_endpoint = $"{Issuer}/authorize",
            token_endpoint = $"{Issuer}/token",
            jwks_uri = $"{Issuer}/.well-known/jwks.json",
            registration_endpoint = $"{Issuer}/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            code_challenge_methods_supported = new[] { "S256" },
            scopes_supported = Scopes,
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        });

    public IResult HandleProtectedResourceMetadata() =>
        Results.Json(new
        {
            resource = ResourceUrl,
            authorization_servers = new[] { Issuer },
            bearer_methods_supported = new[] { "header" },
            scopes_supported = Scopes,
        });

    public IResult HandleJwks() =>
        Results.Json(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = _keyId,
                    alg = "RS256",
                    e = Base64UrlEncoder.Encode(_rsa.ExportParameters(false).Exponent ?? []),
                    n = Base64UrlEncoder.Encode(_rsa.ExportParameters(false).Modulus ?? []),
                },
            },
        });

    public IResult HandleAuthorize(
        string? clientId,
        string? redirectUri,
        string? responseType,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? scope,
        string? state,
        string? resource)
    {
        var validation = ValidateAuthorizeRequest(clientId, redirectUri, responseType, codeChallenge, codeChallengeMethod, resource, state);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        EnsureClientRegistered(clientId!, redirectUri!);

        var sessionId = GenerateToken();
        _pendingOAuth[sessionId] = new PendingOAuthSession
        {
            ClientId = clientId!,
            RedirectUri = redirectUri!,
            CodeChallenge = codeChallenge!,
            Scopes = ParseScopes(scope),
            State = state,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        if (!TryGetTrelloAppCredentials(out _, out var configError))
        {
            return Results.Content(configError!, "text/html; charset=utf-8");
        }

        return Results.Content(BuildLoginHtml(sessionId), "text/html; charset=utf-8");
    }

    public async Task<IResult> HandleTrelloStartAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var sessionId = form["sid"].ToString();
        var duration = form["duration"].ToString().Trim();

        if (!TryGetTrelloAppCredentials(out var appCreds, out var configError))
        {
            return Results.Content(configError!, "text/html");
        }

        if (string.IsNullOrWhiteSpace(sessionId) || !_pendingOAuth.TryGetValue(sessionId, out var pending))
        {
            return Results.Content("<p class=\"error\">Session expired. Close this tab and connect again from Claude.</p>", "text/html");
        }

        if (pending.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            _pendingOAuth.TryRemove(sessionId, out _);
            return Results.Content("<p class=\"error\">Session expired.</p>", "text/html");
        }

        if (!DurationLabels.ContainsKey(duration))
        {
            duration = "1w";
        }

        var trelloSid = GenerateToken();
        var callbackUrl = $"{Issuer}/trello/oauth1/callback?sid={Uri.EscapeDataString(trelloSid)}";

        OAuthTokenPair requestToken;
        try
        {
            requestToken = await _trelloOAuth1.GetRequestTokenAsync(
                appCreds.ApiKey,
                appCreds.OAuthSecret,
                callbackUrl,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Results.Content(
                "<p class=\"error\">Could not start Trello OAuth 1.0a. Check API key, secret, and allowed origins on trello.com/power-ups/admin.</p>" +
                $"<p class=\"muted\">{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>",
                "text/html");
        }

        _pendingTrello[trelloSid] = new PendingTrelloLogin
        {
            OAuthSessionId = sessionId,
            ApiKey = appCreds.ApiKey,
            Duration = duration,
            RequestToken = requestToken.Token,
            RequestTokenSecret = requestToken.TokenSecret,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        var expiration = TrelloOAuthExpiration.GetValueOrDefault(duration, "30days");
        var authorizeUrl = new UriBuilder("https://trello.com/1/OAuthAuthorizeToken")
        {
            Query = string.Join("&", new[]
            {
                $"oauth_token={Uri.EscapeDataString(requestToken.Token)}",
                "name=" + Uri.EscapeDataString("Trello MCP OAuth"),
                "scope=" + Uri.EscapeDataString("read,write"),
                $"expiration={Uri.EscapeDataString(expiration)}",
                $"return_url={Uri.EscapeDataString(callbackUrl)}",
            }),
        };

        return Results.Redirect(authorizeUrl.ToString());
    }

    public async Task<IResult> HandleTrelloOAuth1CallbackAsync(string? sid, string? oauthToken, string? oauthVerifier)
    {
        if (string.IsNullOrWhiteSpace(sid) || !_pendingTrello.TryRemove(sid, out var trelloPending))
        {
            return Results.Content("<p class=\"error\">Trello session expired.</p>", "text/html");
        }

        if (!_pendingOAuth.TryGetValue(trelloPending.OAuthSessionId, out var oauthPending))
        {
            return Results.Content("<p class=\"error\">OAuth session expired. Reconnect from Claude.</p>", "text/html");
        }

        if (string.IsNullOrWhiteSpace(oauthToken) || string.IsNullOrWhiteSpace(oauthVerifier))
        {
            return Results.Content("<p class=\"error\">Trello authorization was denied or incomplete.</p>", "text/html");
        }

        if (!TryGetTrelloAppCredentials(out var appCreds, out var configError))
        {
            return Results.Content(configError!, "text/html");
        }

        OAuthTokenPair accessToken;
        try
        {
            accessToken = await _trelloOAuth1.GetAccessTokenAsync(
                appCreds.ApiKey,
                appCreds.OAuthSecret,
                oauthToken,
                trelloPending.RequestTokenSecret,
                oauthVerifier).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Results.Content(
                "<p class=\"error\">Could not complete Trello OAuth 1.0a token exchange.</p>" +
                $"<p class=\"muted\">{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>",
                "text/html");
        }

        _pendingOAuth.TryRemove(trelloPending.OAuthSessionId, out _);

        var expiresAt = trelloPending.Duration == "always"
            ? (DateTimeOffset?)null
            : DateTimeOffset.UtcNow.AddSeconds(DurationSeconds[trelloPending.Duration]);

        _credentials.Set(oauthPending.ClientId, appCreds.ApiKey, accessToken.Token, expiresAt);

        return Results.Redirect(BuildClaudeCallbackUrl(oauthPending, GenerateToken()));
    }

    private string BuildClaudeCallbackUrl(PendingOAuthSession oauthPending, string code)
    {
        _authCodes[code] = new AuthorizationCodeEntry
        {
            ClientId = oauthPending.ClientId,
            RedirectUri = oauthPending.RedirectUri,
            CodeChallenge = oauthPending.CodeChallenge,
            Scopes = oauthPending.Scopes,
            Resource = ResourceUrl,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        };

        return QueryHelpers.AddQueryString(oauthPending.RedirectUri, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = oauthPending.State,
            ["iss"] = Issuer,
        });
    }

    public async Task<IResult> HandleTokenAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var clientId = GetClientId(context, form);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Results.Json(new { error = "invalid_client", error_description = "client_id is required." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var resource = form["resource"].ToString();
        if (!string.IsNullOrWhiteSpace(resource) && !string.Equals(resource.Trim(), ResourceUrl, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_target" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var grantType = form["grant_type"].ToString();
        if (grantType == "authorization_code")
        {
            return HandleAuthorizationCodeGrant(form, clientId);
        }

        if (grantType == "refresh_token")
        {
            return HandleRefreshGrant(form, clientId);
        }

        return Results.Json(new { error = "unsupported_grant_type" }, statusCode: StatusCodes.Status400BadRequest);
    }

    public async Task<IResult> HandleRegisterAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<ClientRegistrationRequest>(context.RequestAborted).ConfigureAwait(false);
        if (request?.RedirectUris is null || request.RedirectUris.Count == 0)
        {
            return Results.Json(new { error = "invalid_redirect_uri" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var acceptedRedirectUris = request.RedirectUris
            .Where(IsAllowedRedirectUri)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (acceptedRedirectUris.Count == 0)
        {
            return Results.Json(
                new { error = "invalid_redirect_uri", error_description = "No acceptable redirect URI was provided." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var clientId = $"dyn-{Guid.NewGuid():N}";
        _clients[clientId] = new ClientRegistration
        {
            ClientId = clientId,
            RedirectUris = acceptedRedirectUris.ToHashSet(StringComparer.Ordinal),
        };

        return Results.Json(new
        {
            client_id = clientId,
            client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            redirect_uris = acceptedRedirectUris,
            token_endpoint_auth_method = "none",
        });
    }

    private string BuildLoginHtml(string sessionId)
    {
        var options = string.Join('\n', DurationLabels.Select(kv =>
            $"<option value=\"{kv.Key}\">{kv.Value}</option>"));

        var encodedSession = System.Net.WebUtility.HtmlEncode(sessionId);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>Trello MCP — sign in</title><style>" +
            "body{font-family:Segoe UI,Arial,sans-serif;background:#f3f4f6;margin:0;padding:30px}" +
            ".card{max-width:560px;margin:0 auto;background:#fff;border-radius:12px;padding:24px;box-shadow:0 4px 20px rgba(0,0,0,.08)}" +
            "label{display:block;margin:12px 0 6px;font-weight:600}" +
            "input,select,button{width:100%;font-size:14px;padding:10px;border-radius:8px;border:1px solid #d1d5db;box-sizing:border-box}" +
            "button{margin-top:16px;background:#2563eb;color:#fff;border:0;font-weight:600;cursor:pointer}" +
            ".muted{color:#6b7280;font-size:13px;margin-top:12px}</style></head><body><main class=\"card\">" +
            "<h1>Connect Trello to MCP</h1>" +
            "<p>Choose how long access should remain valid, then approve on Trello. You will return to Claude automatically.</p>" +
            $"<form method=\"post\" action=\"{Issuer}/trello/start\">" +
            $"<input type=\"hidden\" name=\"sid\" value=\"{encodedSession}\" />" +
            "<label for=\"duration\">Authentication validity</label>" +
            $"<select id=\"duration\" name=\"duration\">{options}</select>" +
            "<button type=\"submit\">Continue to Trello</button></form>" +
            "</main></body></html>";
    }

    private bool TryGetTrelloAppCredentials(out TrelloAppCredentials creds, out string? errorHtml)
    {
        var apiKey = _settings.DefaultTrelloApiKey?.Trim() ?? string.Empty;
        var secret = _settings.TrelloOAuthSecret?.Trim() ?? string.Empty;

        if (!IsPlausibleTrelloApiKey(apiKey) || string.IsNullOrWhiteSpace(secret))
        {
            creds = default;
            errorHtml =
                "<!doctype html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;padding:24px\">" +
                "<h1>Trello MCP is not configured</h1>" +
                "<p>Set <code>McpOAuth__DefaultTrelloApiKey</code> and <code>McpOAuth__TrelloOAuthSecret</code> on the server " +
                "(Power-Up API key + OAuth secret from <a href=\"https://trello.com/power-ups/admin\">trello.com/power-ups/admin</a>).</p>" +
                "<p>Also add this allowed origin: <code>" + System.Net.WebUtility.HtmlEncode(_settings.GetOAuthIssuerUrl().Replace("/oauth", "", StringComparison.Ordinal)) + "</code></p>" +
                "</body></html>";
            return false;
        }

        creds = new TrelloAppCredentials(apiKey, secret);
        errorHtml = null;
        return true;
    }

    private readonly record struct TrelloAppCredentials(string ApiKey, string OAuthSecret);

    private (IResult? Error, string? RedirectUri) ValidateAuthorizeRequest(
        string? clientId,
        string? redirectUri,
        string? responseType,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? resource,
        string? state)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (OAuthError("invalid_request", "client_id is required."), null);
        }

        if (!IsAllowedRedirectUri(redirectUri))
        {
            return (OAuthError("invalid_request", "redirect_uri is not allowed."), null);
        }

        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            return (RedirectOAuthError(redirectUri!, "unsupported_response_type", "Only code is supported.", state), null);
        }

        if (!string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            return (RedirectOAuthError(redirectUri!, "invalid_request", "Only S256 PKCE is supported.", state), null);
        }

        if (string.IsNullOrWhiteSpace(codeChallenge))
        {
            return (RedirectOAuthError(redirectUri!, "invalid_request", "code_challenge is required.", state), null);
        }

        if (!string.IsNullOrWhiteSpace(resource) && !string.Equals(resource.Trim(), ResourceUrl, StringComparison.Ordinal))
        {
            return (RedirectOAuthError(redirectUri!, "invalid_target", "Invalid resource parameter.", state), null);
        }

        return (null, redirectUri);
    }

    private IResult HandleAuthorizationCodeGrant(IFormCollection form, string clientId)
    {
        var code = form["code"].ToString();
        if (string.IsNullOrWhiteSpace(code) || !_authCodes.TryRemove(code, out var entry))
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return Results.Json(new { error = "invalid_grant", error_description = "Code expired." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var redirectUri = form["redirect_uri"].ToString();
        if (!string.IsNullOrWhiteSpace(redirectUri) && !string.Equals(redirectUri, entry.RedirectUri, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant", error_description = "redirect_uri mismatch." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var verifier = form["code_verifier"].ToString();
        if (!VerifyPkce(verifier, entry.CodeChallenge))
        {
            return Results.Json(new { error = "invalid_grant", error_description = "PKCE verification failed." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (_credentials.GetByClientId(clientId) is null)
        {
            return Results.Json(new { error = "invalid_grant", error_description = "Trello not linked for this client." }, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(CreateTokenResponse(clientId, entry.Scopes));
    }

    private IResult HandleRefreshGrant(IFormCollection form, string clientId)
    {
        var refresh = form["refresh_token"].ToString();
        if (string.IsNullOrWhiteSpace(refresh) || !_refreshTokens.TryTake(refresh, out var entry) || entry is null)
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(CreateTokenResponse(clientId, entry.Scopes));
    }

    private object CreateTokenResponse(string clientId, IReadOnlyList<string> scopes)
    {
        var accessToken = CreateJwt(clientId, scopes);
        var refreshToken = GenerateToken();
        _refreshTokens.Save(refreshToken, clientId, scopes, DateTimeOffset.UtcNow.AddDays(90));

        return new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = 3600,
            refresh_token = refreshToken,
            scope = string.Join(' ', scopes),
        };
    }

    private string CreateJwt(string clientId, IReadOnlyList<string> scopes)
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ResourceUrl,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            Expires = now.AddHours(1).UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        };

        if (scopes.Count > 0)
        {
            descriptor.Subject.AddClaim(new Claim("scope", string.Join(' ', scopes)));
        }

        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private static string? GetClientId(HttpContext context, IFormCollection form)
    {
        var fromForm = form["client_id"].ToString();
        if (!string.IsNullOrWhiteSpace(fromForm))
        {
            return fromForm;
        }

        if (context.Request.Headers.Authorization.Count > 0
            && AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var auth)
            && string.Equals(auth.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter ?? string.Empty));
                var colon = decoded.IndexOf(':');
                if (colon > 0)
                {
                    return decoded[..colon];
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private void EnsureClientRegistered(string clientId, string redirectUri)
    {
        _clients.AddOrUpdate(
            clientId,
            _ => new ClientRegistration
            {
                ClientId = clientId,
                RedirectUris = new HashSet<string>(StringComparer.Ordinal) { redirectUri },
            },
            (_, existing) =>
            {
                existing.RedirectUris.Add(redirectUri);
                return existing;
            });
    }

    private static bool IsPlausibleTrelloApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        var key = apiKey.Trim();
        if (key.Length is < 20 or > 128)
        {
            return false;
        }

        if (key.Contains(' ') || key.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        if (key.Contains("Specify", StringComparison.OrdinalIgnoreCase)
            || key.Contains("available options", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsAllowedRedirectUri(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, "cursor", StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("anysphere.cursor-mcp", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/oauth/callback", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (uri.Scheme != "https" && uri.Scheme != "http")
        {
            return false;
        }

        var isClaudeHost = ClaudeRedirectHosts.Contains(uri.Host)
            || uri.Host.EndsWith(".claude.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".claude.com", StringComparison.OrdinalIgnoreCase);
        if (isClaudeHost && uri.AbsolutePath.StartsWith("/api/mcp/auth_callback", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1")
        {
            return uri.AbsolutePath.Contains("callback", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static List<string> ParseScopes(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Scopes.ToList();
        }

        var parsed = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return parsed.Count == 0 ? Scopes.ToList() : parsed;
    }

    private static bool VerifyPkce(string verifier, string challenge)
    {
        if (string.IsNullOrWhiteSpace(verifier))
        {
            return false;
        }

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var computed = Base64UrlEncoder.Encode(hash);
        return string.Equals(computed, challenge, StringComparison.Ordinal);
    }

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult RedirectOAuthError(string redirectUri, string error, string description, string? state)
    {
        var location = QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
        {
            ["error"] = error,
            ["error_description"] = description,
            ["state"] = state,
        });
        return Results.Redirect(location);
    }

    private sealed class AuthorizationCodeEntry
    {
        public required string ClientId { get; init; }
        public required string RedirectUri { get; init; }
        public required string CodeChallenge { get; init; }
        public required IReadOnlyList<string> Scopes { get; init; }
        public required string Resource { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class ClientRegistration
    {
        public required string ClientId { get; init; }
        public HashSet<string> RedirectUris { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class ClientRegistrationRequest
    {
        [JsonPropertyName("redirect_uris")]
        public List<string> RedirectUris { get; init; } = [];
    }

    private sealed class PendingOAuthSession
    {
        public required string ClientId { get; init; }
        public required string RedirectUri { get; init; }
        public required string CodeChallenge { get; init; }
        public required IReadOnlyList<string> Scopes { get; init; }
        public string? State { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class PendingTrelloLogin
    {
        public required string OAuthSessionId { get; init; }
        public required string ApiKey { get; init; }
        public required string Duration { get; init; }
        public required string RequestToken { get; init; }
        public required string RequestTokenSecret { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
    }
}
