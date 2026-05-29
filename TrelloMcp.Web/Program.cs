using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using TrelloMcp.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);

var dataRoot = McpDataPaths.GetRoot(builder.Environment);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "dataprotection-keys")))
    .SetApplicationName("TrelloMcp.Web");

builder.Services.AddTrelloMcpOAuth(builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddTrelloMcpAuthentication(builder.Configuration);
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly(typeof(TrelloMcpTools).Assembly);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    await next(context).ConfigureAwait(false);
    if (context.Response.StatusCode != StatusCodes.Status401Unauthorized)
    {
        return;
    }

    if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    if (context.Response.Headers.ContainsKey(HeaderNames.WWWAuthenticate))
    {
        return;
    }

    var oauthSettings = context.RequestServices.GetRequiredService<IOptions<McpOAuthSettings>>().Value;
    context.Response.Headers[HeaderNames.WWWAuthenticate] = McpOAuthDiscovery.BuildWwwAuthenticate(oauthSettings);
});

app.MapGet("/", () => Results.Text("Trello MCP — connect via Claude or Cursor using the /mcp URL and OAuth."));
app.MapTrelloMcpOAuth();
app.MapMcp("/mcp").RequireAuthorization("McpAccess");

app.Run();
