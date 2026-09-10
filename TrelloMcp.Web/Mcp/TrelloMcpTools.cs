using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace TrelloMcp.Web.Mcp;

[McpServerToolType]
public sealed class TrelloMcpTools(
    IHttpContextAccessor httpContextAccessor,
    TrelloCredentialStore credentialStore,
    IHttpClientFactory httpClientFactory,
    IOptions<McpOAuthSettings> oauthSettings)
{
    private TrelloApiClient Client() =>
        new(httpClientFactory.CreateClient(), RequireCredentials().ApiKey, RequireCredentials().Token);

    // --- Essential (original names) ---

    [McpServerTool(Name = "trello_search"), Description("Search Trello boards, cards, and members.")]
    public Task<string> TrelloSearch(
        [Description("Search query")] string query,
        [Description("Optional: boards,cards,members")] string? modelTypes = null,
        CancellationToken cancellationToken = default)
    {
        var q = new Dictionary<string, string?> { ["query"] = query, ["cards_limit"] = "20", ["boards_limit"] = "10" };
        if (!string.IsNullOrWhiteSpace(modelTypes))
        {
            q["modelTypes"] = modelTypes;
        }

        return GetRawAsync("/search", q, cancellationToken);
    }

    [McpServerTool(Name = "trello_get_user_boards"), Description("List boards for the authenticated user.")]
    public Task<string> TrelloGetUserBoards(
        [Description("open, closed, or all")] string filter = "open",
        CancellationToken cancellationToken = default) =>
        GetRawAsync("/members/me/boards", new Dictionary<string, string?> { ["filter"] = filter, ["fields"] = "id,name,url,closed" }, cancellationToken);

    [McpServerTool(Name = "get_board_details"), Description("Get board details with lists and cards.")]
    public Task<string> GetBoardDetails(
        [Description("24-character board id")] string boardId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/boards/{boardId}", new Dictionary<string, string?>
        {
            ["lists"] = "open",
            ["cards"] = "open",
            ["card_members"] = "true",
            ["card_labels"] = "true",
        }, cancellationToken);

    [McpServerTool(Name = "get_card"), Description("Get card details.")]
    public Task<string> GetCard(
        [Description("24-character card id")] string cardId,
        [Description("Include members, labels, checklists")] bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        if (!includeDetails)
        {
            return GetRawAsync($"/cards/{cardId}", null, cancellationToken);
        }

        return GetRawAsync($"/cards/{cardId}", new Dictionary<string, string?>
        {
            ["members"] = "true",
            ["labels"] = "true",
            ["checklists"] = "all",
            ["badges"] = "true",
        }, cancellationToken);
    }

    [McpServerTool(Name = "create_card"), Description("Create a new card in a list.")]
    public Task<string> CreateCard(
        [Description("Card title")] string name,
        [Description("Destination list id")] string idList,
        [Description("Description")] string? desc = null,
        [Description("top, bottom, or number")] string? pos = null,
        [Description("ISO due date")] string? due = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["idList"] = idList };
        if (desc is not null) body["desc"] = desc;
        if (pos is not null) body["pos"] = pos;
        if (due is not null) body["due"] = due;
        return PostRawAsync("/cards", body, cancellationToken);
    }

    [McpServerTool(Name = "update_card"), Description("Update an existing card.")]
    public Task<string> UpdateCard(
        [Description("Card id")] string cardId,
        [Description("New name")] string? name = null,
        [Description("New description")] string? desc = null,
        [Description("Archive card")] bool? closed = null,
        [Description("ISO due or null to clear")] string? due = null,
        [Description("Move to list id")] string? idList = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (desc is not null) body["desc"] = desc;
        if (closed is not null) body["closed"] = closed;
        if (due is not null) body["due"] = due;
        if (idList is not null) body["idList"] = idList;
        return PutRawAsync($"/cards/{cardId}", body, cancellationToken);
    }

    [McpServerTool(Name = "move_card"), Description("Move a card to another list.")]
    public Task<string> MoveCard(
        [Description("Card id")] string cardId,
        [Description("Destination list id")] string idList,
        [Description("top, bottom, or number")] string? pos = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["idList"] = idList };
        if (pos is not null) body["pos"] = pos;
        return PutRawAsync($"/cards/{cardId}", body, cancellationToken);
    }

    // --- Board / list ---

    [McpServerTool(Name = "list_boards"), Description("List boards (alias of trello_get_user_boards).")]
    public Task<string> ListBoards(
        [Description("open, closed, or all")] string filter = "open",
        CancellationToken cancellationToken = default) =>
        TrelloGetUserBoards(filter, cancellationToken);

    [McpServerTool(Name = "get_lists"), Description("Get lists on a board.")]
    public Task<string> GetLists(
        [Description("Board id")] string boardId,
        [Description("open, closed, or all")] string filter = "open",
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/boards/{boardId}/lists", new Dictionary<string, string?> { ["filter"] = filter }, cancellationToken);

    [McpServerTool(Name = "trello_get_list_cards"), Description("Get cards in a list.")]
    public Task<string> TrelloGetListCards(
        [Description("List id")] string listId,
        [Description("all, open, or closed")] string filter = "open",
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/lists/{listId}/cards", new Dictionary<string, string?> { ["filter"] = filter }, cancellationToken);

    [McpServerTool(Name = "trello_create_list"), Description("Create a list on a board.")]
    public Task<string> TrelloCreateList(
        [Description("List name")] string name,
        [Description("Board id")] string idBoard,
        [Description("top, bottom, or number")] string? pos = "bottom",
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["idBoard"] = idBoard };
        if (pos is not null) body["pos"] = pos;
        return PostRawAsync("/lists", body, cancellationToken);
    }

    // --- Collaboration ---

    [McpServerTool(Name = "trello_add_comment"), Description("Add a comment to a card.")]
    public Task<string> TrelloAddComment(
        [Description("Card id")] string cardId,
        [Description("Comment text")] string text,
        CancellationToken cancellationToken = default) =>
        PostRawAsync($"/cards/{cardId}/actions/comments", new { text }, cancellationToken);

    [McpServerTool(Name = "trello_update_comment"), Description("Update an existing comment on a card. Use the comment action id from trello_get_card_actions (type commentCard). Only the original author can edit.")]
    public Task<string> TrelloUpdateComment(
        [Description("Card id")] string cardId,
        [Description("Comment action id from trello_get_card_actions")] string commentActionId,
        [Description("New comment text")] string text,
        CancellationToken cancellationToken = default) =>
        PutRawAsync(
            $"/cards/{cardId}/actions/{commentActionId}/comments",
            new Dictionary<string, string?> { ["text"] = text },
            cancellationToken);

    [McpServerTool(Name = "trello_delete_comment"), Description("Delete a comment on a card. Use the comment action id from trello_get_card_actions (type commentCard). The author or a board admin can delete.")]
    public Task<string> TrelloDeleteComment(
        [Description("Card id")] string cardId,
        [Description("Comment action id from trello_get_card_actions")] string commentActionId,
        CancellationToken cancellationToken = default) =>
        DeleteRawAsync($"/cards/{cardId}/actions/{commentActionId}/comments", cancellationToken);

    [McpServerTool(Name = "trello_get_member"), Description("Get member profile (use member id or @username).")]
    public Task<string> TrelloGetMember(
        [Description("Member id or username")] string memberId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/members/{memberId}", new Dictionary<string, string?> { ["boards"] = "open", ["organizations"] = "all" }, cancellationToken);

    // --- Advanced ---

    [McpServerTool(Name = "trello_get_board_cards"), Description("Get all cards on a board.")]
    public Task<string> TrelloGetBoardCards(
        [Description("Board id")] string boardId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/boards/{boardId}/cards", new Dictionary<string, string?> { ["filter"] = "open" }, cancellationToken);

    [McpServerTool(Name = "trello_get_card_actions"), Description("Get activity/actions on a card.")]
    public Task<string> TrelloGetCardActions(
        [Description("Card id")] string cardId,
        [Description("Max actions")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/cards/{cardId}/actions", new Dictionary<string, string?> { ["limit"] = limit.ToString() }, cancellationToken);

    [McpServerTool(Name = "trello_get_card_attachments"), Description("Get attachments on a card.")]
    public Task<string> TrelloGetCardAttachments(
        [Description("Card id")] string cardId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/cards/{cardId}/attachments", null, cancellationToken);

    [McpServerTool(Name = "trello_add_card_attachment"), Description(
        "Attach an image or file to a Trello card. Provide either url (http/https link) OR fileBase64 (raw or data-URL base64) with fileName. Use this when the user wants to upload a screenshot/image to a card.")]
    public async Task<string> TrelloAddCardAttachment(
        [Description("Card id")] string cardId,
        [Description("Public http(s) URL to attach (alternative to fileBase64)")] string? url = null,
        [Description("Base64 file bytes, optionally as a data: URL (alternative to url)")] string? fileBase64 = null,
        [Description("Filename for the upload, e.g. screenshot.png (required with fileBase64)")] string? fileName = null,
        [Description("MIME type, e.g. image/png (optional; inferred from fileName when possible)")] string? mimeType = null,
        [Description("Attachment display name (defaults to fileName)")] string? name = null,
        [Description("Set this attachment as the card cover")] bool setCover = false,
        CancellationToken cancellationToken = default)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        var hasFile = !string.IsNullOrWhiteSpace(fileBase64);
        if (hasUrl == hasFile)
        {
            throw new InvalidOperationException("Provide exactly one of: url, or fileBase64 (+ fileName).");
        }

        if (hasUrl)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("url must be an absolute http or https URL.");
            }

            var fields = new Dictionary<string, string?>
            {
                ["url"] = url,
                ["setCover"] = setCover ? "true" : "false",
            };
            if (!string.IsNullOrWhiteSpace(name))
            {
                fields["name"] = name;
            }

            var result = await Client()
                .PostMultipartAsync($"/cards/{cardId}/attachments", null, fields, file: null, cancellationToken)
                .ConfigureAwait(false);
            return result.GetRawText();
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("fileName is required when uploading with fileBase64.");
        }

        var bytes = DecodeBase64File(fileBase64!, out var dataUrlMime);
        const int maxBytes = 10 * 1024 * 1024;
        if (bytes.Length > maxBytes)
        {
            throw new InvalidOperationException($"File is {bytes.Length} bytes; max supported upload is {maxBytes} bytes (10 MB).");
        }

        var resolvedMime = FirstNonEmpty(mimeType, dataUrlMime, GuessMimeType(fileName));
        var fieldsFile = new Dictionary<string, string?>
        {
            ["setCover"] = setCover ? "true" : "false",
        };
        if (!string.IsNullOrWhiteSpace(name))
        {
            fieldsFile["name"] = name;
        }
        else
        {
            fieldsFile["name"] = fileName;
        }

        if (!string.IsNullOrWhiteSpace(resolvedMime))
        {
            fieldsFile["mimeType"] = resolvedMime;
        }

        var uploaded = await Client()
            .PostMultipartAsync(
                $"/cards/{cardId}/attachments",
                null,
                fieldsFile,
                ("file", fileName!, resolvedMime, bytes),
                cancellationToken)
            .ConfigureAwait(false);
        return uploaded.GetRawText();
    }

    [McpServerTool(Name = "trello_download_card_attachment"), Description(
        "Download a Trello card attachment with authenticated access (works for private uploads that need a logged-in session). Rehosts the file to a public URL so agents can open PDFs/images. Provide attachmentId and/or fileName from trello_get_card_attachments.")]
    public async Task<string> TrelloDownloadCardAttachment(
        [Description("Card id")] string cardId,
        [Description("Attachment id from trello_get_card_attachments")] string? attachmentId = null,
        [Description("Attachment file name (used to resolve the attachment when id is omitted, or as download path segment)")] string? fileName = null,
        [Description("Include base64 content when the file is small enough (default true, max ~1.5 MB)")] bool includeBase64 = true,
        [Description("Rehost to a public artifact URL (default true)")] bool publishPublicUrl = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) && string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Provide attachmentId and/or fileName.");
        }

        var meta = await ResolveAttachmentAsync(cardId, attachmentId, fileName, cancellationToken)
            .ConfigureAwait(false);
        if (!meta.IsUpload)
        {
            return JsonSerializer.Serialize(new
            {
                id = meta.Id,
                name = meta.Name,
                fileName = meta.FileName,
                mimeType = meta.MimeType,
                bytes = meta.Bytes,
                isUpload = false,
                url = meta.Url,
                note = "This attachment is a linked URL (not an uploaded file). Open url directly.",
            });
        }

        var downloadName = FirstNonEmpty(meta.FileName, meta.Name, fileName)
            ?? throw new InvalidOperationException("Attachment has no fileName.");
        var download = await Client()
            .DownloadAttachmentAsync(cardId, meta.Id, downloadName, cancellationToken)
            .ConfigureAwait(false);

        const int maxDownloadBytes = 50 * 1024 * 1024;
        if (download.Bytes.Length > maxDownloadBytes)
        {
            throw new InvalidOperationException(
                $"Attachment is {download.Bytes.Length} bytes; max download is {maxDownloadBytes} bytes (50 MB).");
        }

        string? publicUrl = null;
        string? publishError = null;
        if (publishPublicUrl)
        {
            try
            {
                publicUrl = await PublishArtifactAsync(downloadName, download.Bytes, download.ContentType ?? meta.MimeType, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                publishError = ex.Message;
            }
        }

        const int maxInlineBase64Bytes = 1_500_000;
        string? contentBase64 = null;
        if (includeBase64 && download.Bytes.Length <= maxInlineBase64Bytes)
        {
            contentBase64 = Convert.ToBase64String(download.Bytes);
        }

        return JsonSerializer.Serialize(new
        {
            id = meta.Id,
            name = meta.Name,
            fileName = downloadName,
            mimeType = download.ContentType ?? meta.MimeType,
            bytes = download.Bytes.Length,
            isUpload = true,
            publicUrl,
            temporaryDownloadUrl = download.TemporaryUrl,
            contentBase64,
            publishError,
            note = publicUrl is not null
                ? "Open publicUrl to view/download the file."
                : contentBase64 is not null
                    ? "File bytes are in contentBase64 (base64)."
                    : "Download succeeded but no publicUrl/contentBase64; retry with publishPublicUrl=true or use a smaller file.",
        });
    }

    [McpServerTool(Name = "trello_get_card_checklists"), Description("Get checklists on a card.")]
    public Task<string> TrelloGetCardChecklists(
        [Description("Card id")] string cardId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/cards/{cardId}/checklists", new Dictionary<string, string?> { ["checkItems"] = "all" }, cancellationToken);

    [McpServerTool(Name = "trello_create_checklist"), Description("Create a checklist on a card, optionally with items.")]
    public async Task<string> TrelloCreateChecklist(
        [Description("Card id")] string cardId,
        [Description("Checklist name")] string name,
        [Description("Optional checklist item names")] string[]? items = null,
        [Description("top, bottom, or number")] string? pos = "bottom",
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["idCard"] = cardId };
        if (pos is not null) body["pos"] = pos;
        var created = await PostRawAsync("/checklists", body, cancellationToken).ConfigureAwait(false);
        if (items is null or { Length: 0 })
        {
            return created;
        }

        using var doc = JsonDocument.Parse(created);
        var checklistId = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Trello did not return checklist id.");

        var results = new List<object> { JsonSerializer.Deserialize<object>(created)! };
        foreach (var item in items)
        {
            var itemJson = await PostRawAsync(
                $"/checklists/{checklistId}/checkItems",
                new Dictionary<string, object?> { ["name"] = item, ["pos"] = "bottom" },
                cancellationToken).ConfigureAwait(false);
            results.Add(JsonSerializer.Deserialize<object>(itemJson)!);
        }

        return JsonSerializer.Serialize(new { checklist = JsonSerializer.Deserialize<object>(created), checkItems = results.Skip(1) });
    }

    [McpServerTool(Name = "trello_add_checklist_item"), Description("Add an item to an existing checklist.")]
    public Task<string> TrelloAddChecklistItem(
        [Description("Checklist id")] string checklistId,
        [Description("Item name")] string name,
        [Description("top, bottom, or number")] string? pos = "bottom",
        [Description("Mark complete")] bool? @checked = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (pos is not null) body["pos"] = pos;
        if (@checked is not null) body["checked"] = @checked;
        return PostRawAsync($"/checklists/{checklistId}/checkItems", body, cancellationToken);
    }

    [McpServerTool(Name = "trello_get_board_members"), Description("Get members on a board.")]
    public Task<string> TrelloGetBoardMembers(
        [Description("Board id")] string boardId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/boards/{boardId}/members", null, cancellationToken);

    [McpServerTool(Name = "trello_get_board_labels"), Description("Get labels on a board.")]
    public Task<string> TrelloGetBoardLabels(
        [Description("Board id")] string boardId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/boards/{boardId}/labels", null, cancellationToken);

    [McpServerTool(Name = "get_trello_mcp_connection_guide"), Description("How to connect this Trello MCP from Claude.")]
    public string GetTrelloMcpConnectionGuide()
    {
        var s = oauthSettings.Value;
        return $"""
            ## Trello MCP (remote)

            **MCP URL:** `{s.GetMcpResourceUrl()}`

            Connect in Claude with URL only (no OAuth client secret). Approve on Trello when prompted.

            **Tools:** search, boards, cards, lists, comments (add/update/delete), members, labels, checklists, attachments (list, upload, and authenticated download with public rehost URL).
            """;
    }

    private async Task<AttachmentMeta> ResolveAttachmentAsync(
        string cardId,
        string? attachmentId,
        string? fileName,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(attachmentId))
        {
            var one = await Client()
                .GetAsync($"/cards/{cardId}/attachments/{attachmentId}", null, ct)
                .ConfigureAwait(false);
            return AttachmentMeta.FromJson(one);
        }

        var list = await Client()
            .GetAsync($"/cards/{cardId}/attachments", null, ct)
            .ConfigureAwait(false);
        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Unexpected attachments payload from Trello.");
        }

        AttachmentMeta? match = null;
        foreach (var item in list.EnumerateArray())
        {
            var meta = AttachmentMeta.FromJson(item);
            if (string.Equals(meta.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(meta.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                match = meta;
                break;
            }
        }

        if (match is null)
        {
            throw new InvalidOperationException($"No attachment named '{fileName}' on card {cardId}.");
        }

        return match.Value;
    }

    private async Task<string> PublishArtifactAsync(
        string fileName,
        byte[] bytes,
        string? contentType,
        CancellationToken ct)
    {
        var uploadUrl = oauthSettings.Value.ArtifactUploadUrl;
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            throw new InvalidOperationException("Artifact upload URL is not configured.");
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        content.Add(fileContent, "file", fileName);

        var http = httpClientFactory.CreateClient();
        using var response = await http.PostAsync(uploadUrl, content, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Artifact upload {(int)response.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("url", out var urlProp)
            && urlProp.GetString() is { Length: > 0 } url)
        {
            return url;
        }

        throw new InvalidOperationException($"Artifact upload response missing url: {text}");
    }

    private static byte[] DecodeBase64File(string input, out string? dataUrlMime)
    {
        dataUrlMime = null;
        var payload = input.Trim();
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = payload.IndexOf(',');
            if (comma < 0)
            {
                throw new InvalidOperationException("Invalid data URL for fileBase64.");
            }

            var meta = payload[..comma];
            payload = payload[(comma + 1)..];
            // data:image/png;base64
            var slash = meta.IndexOf('/');
            var semi = meta.IndexOf(';');
            if (slash > 0 && semi > slash)
            {
                dataUrlMime = meta[(meta.IndexOf(':') + 1)..semi];
            }
        }

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("fileBase64 is not valid base64.", ex);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return null;
    }

    private static string? GuessMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".zip" => "application/zip",
            _ => null,
        };
    }

    private async Task<string> GetRawAsync(string path, IReadOnlyDictionary<string, string?>? query, CancellationToken ct)
    {
        var result = await Client().GetAsync(path, query, ct).ConfigureAwait(false);
        return result.GetRawText();
    }

    private async Task<string> PostRawAsync(string path, object body, CancellationToken ct)
    {
        var result = await Client().PostAsync(path, body, ct).ConfigureAwait(false);
        return result.GetRawText();
    }

    private async Task<string> PutRawAsync(string path, object body, CancellationToken ct)
    {
        var result = await Client().PutAsync(path, body, ct).ConfigureAwait(false);
        return result.GetRawText();
    }

    private async Task<string> PutRawAsync(string path, IReadOnlyDictionary<string, string?> query, CancellationToken ct)
    {
        var result = await Client().PutAsync(path, query, body: null, ct).ConfigureAwait(false);
        return result.GetRawText();
    }

    private async Task<string> DeleteRawAsync(string path, CancellationToken ct)
    {
        var result = await Client().DeleteAsync(path, ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText();
    }

    private TrelloCredentials RequireCredentials()
    {
        var sub = httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContextAccessor.HttpContext?.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var creds = credentialStore.GetByClientId(sub);
        if (creds is null)
        {
            throw new InvalidOperationException("Trello credentials not found. Reconnect the MCP connector and complete Trello sign-in.");
        }

        return creds;
    }

    private readonly record struct AttachmentMeta(
        string Id,
        string? Name,
        string? FileName,
        string? MimeType,
        long? Bytes,
        bool IsUpload,
        string? Url)
    {
        public static AttachmentMeta FromJson(JsonElement el)
        {
            var id = el.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Attachment missing id.");
            string? name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? fileName = el.TryGetProperty("fileName", out var f) ? f.GetString() : null;
            string? mime = el.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
            long? bytes = el.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number
                ? b.GetInt64()
                : null;
            var isUpload = !el.TryGetProperty("isUpload", out var u) || u.ValueKind != JsonValueKind.False;
            string? url = el.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            return new AttachmentMeta(id, name, fileName, mime, bytes, isUpload, url);
        }
    }
}
