namespace TrelloMcp.Web.Mcp;

/// <summary>
/// Durable paths that survive Azure zip deploy (--clean replaces wwwroot only).
/// </summary>
public static class McpDataPaths
{
    public static string GetRoot(IWebHostEnvironment env)
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var siteData = Path.Combine(home, "site", "data", "trello-mcp");
            Directory.CreateDirectory(siteData);
            return siteData;
        }

        var local = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(local);
        return local;
    }
}
