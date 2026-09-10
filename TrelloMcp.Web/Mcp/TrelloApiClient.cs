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

    /// <summary>
    /// Download an uploaded card attachment. Auth must be the OAuth Authorization header
    /// (query-string key/token is rejected on /download/ routes).
    /// </summary>
    public async Task<TrelloAttachmentDownload> DownloadAttachmentAsync(
        string cardId,
        string attachmentId,
        string fileName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cardId)
            || string.IsNullOrWhiteSpace(attachmentId)
            || string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("cardId, attachmentId, and fileName are required to download an attachment.");
        }

        var url =
            $"{Base}/cards/{Uri.EscapeDataString(cardId)}/attachments/{Uri.EscapeDataString(attachmentId)}/download/{EncodePathSegment(fileName)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"OAuth oauth_consumer_key=\"{apiKey}\", oauth_token=\"{token}\"");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Trello attachment download {(int)response.StatusCode}: {err}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var temporaryUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
        if (temporaryUrl is not null
            && temporaryUrl.StartsWith(Base, StringComparison.OrdinalIgnoreCase))
        {
            // Still on the API host — no useful public redirect URL.
            temporaryUrl = null;
        }

        return new TrelloAttachmentDownload(bytes, contentType, temporaryUrl);
    }

    /// <summary>
    /// POST multipart/form-data (required for Trello file attachments).
    /// </summary>
    public async Task<JsonElement> PostMultipartAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        IReadOnlyDictionary<string, string?> formFields,
        (string FieldName, string FileName, string? MimeType, byte[] Bytes)? file,
        CancellationToken ct = default)
    {
        var url = BuildUrl(path, query);
        using var content = new MultipartFormDataContent();
        foreach (var (key, value) in formFields)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            content.Add(new StringContent(value), key);
        }

        if (file is { } f)
        {
            var stream = new ByteArrayContent(f.Bytes);
            if (!string.IsNullOrWhiteSpace(f.MimeType))
            {
                stream.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(f.MimeType);
            }

            content.Add(stream, f.FieldName, f.FileName);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
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

    private static string EncodePathSegment(string value) =>
        Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.Ordinal);
}

public readonly record struct TrelloAttachmentDownload(byte[] Bytes, string? ContentType, string? TemporaryUrl);
