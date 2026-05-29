using System.Collections.Concurrent;
using System.Text.Json;

namespace TrelloMcp.Web.Mcp;

public sealed record TrelloCredentials(string ApiKey, string Token);

public sealed class TrelloCredentialStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, StoredCredentials> _byClientId = new(StringComparer.Ordinal);

    public TrelloCredentialStore(IWebHostEnvironment env)
    {
        _file = Path.Combine(McpDataPaths.GetRoot(env), "trello-credentials.json");
        Load();
    }

    public void Set(string clientId, string apiKey, string token, DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id is required.", nameof(clientId));
        }

        _byClientId[clientId] = new StoredCredentials(apiKey, token, expiresAtUtc);
        Persist();
    }

    public TrelloCredentials? GetByClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)
            || !_byClientId.TryGetValue(clientId, out var stored))
        {
            return null;
        }

        if (stored.ExpiresAtUtc is { } expiry && expiry < DateTimeOffset.UtcNow)
        {
            _byClientId.TryRemove(clientId, out _);
            Persist();
            return null;
        }

        return new TrelloCredentials(stored.ApiKey, stored.Token);
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
            var loaded = JsonSerializer.Deserialize<Dictionary<string, StoredCredentials>>(json);
            if (loaded is null)
            {
                return;
            }

            foreach (var (key, value) in loaded)
            {
                if (value.ExpiresAtUtc is null || value.ExpiresAtUtc >= DateTimeOffset.UtcNow)
                {
                    _byClientId[key] = value;
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
            var snapshot = _byClientId.ToDictionary(static p => p.Key, static p => p.Value);
            File.WriteAllText(_file, JsonSerializer.Serialize(snapshot));
        }
    }

    private sealed record StoredCredentials(string ApiKey, string Token, DateTimeOffset? ExpiresAtUtc);
}
