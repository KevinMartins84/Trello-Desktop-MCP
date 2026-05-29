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

    [McpServerTool(Name = "trello_get_card_checklists"), Description("Get checklists on a card.")]
    public Task<string> TrelloGetCardChecklists(
        [Description("Card id")] string cardId,
        CancellationToken cancellationToken = default) =>
        GetRawAsync($"/cards/{cardId}/checklists", new Dictionary<string, string?> { ["checkItems"] = "all" }, cancellationToken);

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

            **Tools:** same set as the original Trello Desktop MCP (search, boards, cards, lists, comments including add/update/delete, members, labels, checklists, attachments).
            """;
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
}
