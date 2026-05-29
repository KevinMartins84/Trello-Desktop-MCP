using System.Text;
using System.Text.Json;

namespace TrelloMcp.Web.Mcp;

public sealed class TrelloApiClient(HttpClient http, string apiKey, string token)
{
    private const string Base = "https://api.trello.com/1";

    public Task<JsonElement> GetAsync(string path, IReadOnlyDictionary<string, string?>? query = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, path, query, null, ct);

    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, path, null, body, ct);

    public Task<JsonElement> PutAsync(string path, object body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, path, null, body, ct);

    public Task<JsonElement> PutAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        object? body = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, path, query, body, ct);

    public Task<JsonElement> DeleteAsync(string path, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, path, null, null, ct);

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        object? body,
        CancellationToken ct)
    {
        var url = BuildUrl(path, query);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Trello API {(int)response.StatusCode}: {text}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private string BuildUrl(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var relative = path.TrimStart('/');
        var sb = new StringBuilder($"{Base}/{relative}?key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(token)}");
        if (query is null)
        {
            return sb.ToString();
        }

        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            sb.Append('&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return sb.ToString();
    }
}
