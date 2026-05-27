using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using ModelContextProtocol.AspNetCore.Authentication;

namespace TrelloMcp.Web.Mcp;

public static class McpOAuthEndpointExtensions
{
    public static IServiceCollection AddTrelloMcpOAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpOAuthSettings>(configuration.GetSection(McpOAuthSettings.SectionName));
        services.AddDataProtection();
        services.AddSingleton<McpOAuthSigningKeyStore>();
        services.AddSingleton<TrelloCredentialStore>();
        services.AddSingleton<TrelloMcpOAuthServer>();
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, TrelloMcpJwtBearerOptionsConfigurer>();
        services.AddHttpContextAccessor();
        services.AddHttpClient();

        services.AddAuthorization(options =>
        {
            options.AddPolicy("McpAccess", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        return services;
    }

    public static AuthenticationBuilder AddTrelloMcpAuthentication(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        return builder
            .AddJwtBearer()
            .AddMcp(options =>
            {
                var settings = configuration.GetSection(McpOAuthSettings.SectionName).Get<McpOAuthSettings>() ?? new McpOAuthSettings();
                var resource = settings.GetMcpResourceUrl();
                var issuer = settings.GetOAuthIssuerUrl();
                options.ResourceMetadata = new()
                {
                    Resource = resource,
                    AuthorizationServers = { issuer },
                    ScopesSupported = ["mcp:tools", "offline_access"],
                };
            });
    }

    public static WebApplication MapTrelloMcpOAuth(this WebApplication app)
    {
        var oauth = app.Services.GetRequiredService<TrelloMcpOAuthServer>();
        var settings = app.Services.GetRequiredService<IOptions<McpOAuthSettings>>().Value;
        var oauthPath = settings.OAuthPath.TrimEnd('/');
        var mcpPath = settings.McpPath.TrimEnd('/');

        app.MapGet($"{mcpPath}/.well-known/oauth-protected-resource", oauth.HandleProtectedResourceMetadata);
        app.MapGet($"{oauthPath}/.well-known/oauth-authorization-server", oauth.HandleAuthorizationServerMetadata);
        app.MapGet($"{oauthPath}/.well-known/openid-configuration", oauth.HandleAuthorizationServerMetadata);
        app.MapGet($"{oauthPath}/.well-known/jwks.json", oauth.HandleJwks);

        app.MapGet($"{oauthPath}/authorize", (
            string? client_id,
            string? redirect_uri,
            string? response_type,
            string? code_challenge,
            string? code_challenge_method,
            string? scope,
            string? state,
            string? resource) =>
            oauth.HandleAuthorize(client_id, redirect_uri, response_type, code_challenge, code_challenge_method, scope, state, resource));

        app.MapPost($"{oauthPath}/trello/start", async (HttpContext ctx, TrelloMcpOAuthServer server) =>
            await server.HandleTrelloStartAsync(ctx).ConfigureAwait(false));
        app.MapGet($"{oauthPath}/trello/callback", (string? sid, string? token) => oauth.HandleTrelloCallback(sid, token));

        app.MapPost($"{oauthPath}/token", async (HttpContext ctx, TrelloMcpOAuthServer server) =>
            await server.HandleTokenAsync(ctx).ConfigureAwait(false));
        app.MapPost($"{oauthPath}/register", async (HttpContext ctx, TrelloMcpOAuthServer server) =>
            await server.HandleRegisterAsync(ctx).ConfigureAwait(false));

        return app;
    }
}

internal sealed class TrelloMcpJwtBearerOptionsConfigurer(TrelloMcpOAuthServer oauthServer) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        var openId = new OpenIdConnectConfiguration { Issuer = oauthServer.Issuer };
        openId.SigningKeys.Add(oauthServer.SigningKey);

        options.Authority = null;
        options.MetadataAddress = null;
        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(openId);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = oauthServer.Issuer,
            ValidateAudience = true,
            ValidAudience = oauthServer.ResourceUrl,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = oauthServer.SigningKey,
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    }
}
