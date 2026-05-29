using System.Collections.Concurrent;
using System.Text.Json;

namespace TrelloMcp.Web.Mcp;

public sealed class McpRefreshTokenStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, RefreshTokenRecord> _tokens = new(StringComparer.Ordinal);

    public McpRefreshTokenStore(IWebHostEnvironment env)
    {
        _file = Path.Combine(McpDataPaths.GetRoot(env), "oauth-refresh-tokens.json");
        Load();
    }

    public void Save(string refreshToken, string clientId, IReadOnlyList<string> scopes, DateTimeOffset expiresAtUtc)
    {
        _tokens[refreshToken] = new RefreshTokenRecord
        {
            ClientId = clientId,
            Scopes = scopes.ToList(),
            ExpiresAtUtc = expiresAtUtc,
        };
        Persist();
    }

    public bool TryTake(string refreshToken, out RefreshTokenRecord? record)
    {
        if (_tokens.TryRemove(refreshToken, out var entry))
        {
            if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                record = null;
                Persist();
                return false;
            }

            record = entry;
            Persist();
            return true;
        }

        record = null;
        return false;
    }

    private void Load()
    {
        if (!File.Exists(_file))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_file);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, RefreshTokenRecord>>(json);
            if (loaded is null)
            {
                return;
            }

            foreach (var (key, value) in loaded)
            {
                if (value.ExpiresAtUtc >= DateTimeOffset.UtcNow)
                {
                    _tokens[key] = value;
                }
            }
        }
        catch
        {
            // Corrupt file — start fresh.
        }
    }

    private void Persist()
    {
        lock (_lock)
        {
            var snapshot = _tokens.ToDictionary(static p => p.Key, static p => p.Value);
            File.WriteAllText(_file, JsonSerializer.Serialize(snapshot));
        }
    }
}

public sealed class RefreshTokenRecord
{
    public required string ClientId { get; init; }
    public required List<string> Scopes { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
