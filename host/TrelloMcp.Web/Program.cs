using Microsoft.AspNetCore.Authentication.JwtBearer;
using TrelloMcp.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTrelloMcpOAuth(builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddTrelloMcpAuthentication(builder.Configuration);
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly(typeof(TrelloMcpTools).Assembly);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text("Trello MCP — connect via Claude using the /mcp URL and OAuth."));
app.MapTrelloMcpOAuth();
app.MapMcp("/mcp").RequireAuthorization("McpAccess");

app.Run();
