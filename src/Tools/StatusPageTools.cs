using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Status pages - the PUBLIC pages that show an account's service health, and the incidents declared on them.
/// Reads need <c>statuspage:read</c>, writes <c>statuspage:write</c>. Everything written here is visible to the
/// page's audience and, for a declared incident, fans out to its subscribers: treat every write as publishing.
/// </summary>
[McpServerToolType]
public sealed class StatusPageTools : V2ToolBase {
    public StatusPageTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "list_status_pages", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the account's status pages. Scope 'statuspage:read'.")]
    public Task<CallToolResult> ListStatusPages(
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) =>
        ListAsync("/statuspage", new Q().Set("limit", CapLimit(limit)).Set("cursor", cursor), "list_status_pages", "Status pages:", ct);

    [McpServerTool(Name = "get_status_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read one status page with its settings and components. Scope 'statuspage:read'.")]
    public Task<CallToolResult> GetStatusPage(
        [Description("The status page id.")] string id,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/statuspage/{Uri.EscapeDataString(id)}", heading: "Status page:", ct: ct);

    [McpServerTool(Name = "create_status_page", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Create a status page. Scope 'statuspage:write'. The page becomes PUBLIC at its slug - agree the " +
        "slug, the title and which monitors appear with the user before creating it.")]
    public async Task<CallToolResult> CreateStatusPage(
        [Description("URL slug the page is served at; must be unique.")] string slug,
        [Description("Page title shown to visitors.")] string title,
        [Description("JSON array of components, e.g. [{\"monitorId\":\"...\",\"name\":\"API\",\"group\":\"Core\"}].")] string? componentsJson = null,
        [Description("JSON object of page settings, e.g. {\"theme\":\"light\",\"robotsIndex\":false}.")] string? settingsJson = null,
        CancellationToken ct = default) {

        if (!TryJson(componentsJson, nameof(componentsJson), out var components, out var badComponents)) return Err(badComponents);
        if (components.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array))
            return Err("'componentsJson' must be a JSON array.");
        if (!TryJson(settingsJson, nameof(settingsJson), out var settings, out var badSettings)) return Err(badSettings);

        var body = Body(("slug", slug), ("title", title), ("components", components), ("settings", settings));
        return await CallAsync(HttpMethod.Post, "/statuspage", null, body, heading: "Status page created:", ct: ct);
    }

    [McpServerTool(Name = "update_status_page", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Change a status page's title and/or settings. Scope 'statuspage:write'. The change is immediately " +
        "visible to the public.")]
    public async Task<CallToolResult> UpdateStatusPage(
        [Description("The status page id.")] string id,
        [Description("New page title.")] string? title = null,
        [Description("JSON object of settings to apply; it REPLACES the settings object, it does not merge.")] string? settingsJson = null,
        CancellationToken ct = default) {

        if (!TryJson(settingsJson, nameof(settingsJson), out var settings, out var badJson)) return Err(badJson);
        var body = Body(("title", title), ("settings", settings));
        if (body.Count == 0) return Err("Nothing to update - pass a new title or a settings object.");
        return await CallAsync(HttpMethod.Patch, $"/statuspage/{Uri.EscapeDataString(id)}", null, body, heading: "Status page updated:", ct: ct);
    }

    [McpServerTool(Name = "delete_status_page", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Delete a status page with its components, incidents and subscribers. Scope 'statuspage:write'. " +
        "DESTRUCTIVE and public-facing - confirm with the user first; the slug stops resolving immediately.")]
    public Task<CallToolResult> DeleteStatusPage(
        [Description("The status page id.")] string id,
        [Description("Must be true to actually delete. Call WITHOUT it first: the tool answers with the resource so you can confirm with the user.")] bool confirmed = false,
        CancellationToken ct = default) =>
        DeleteWithPreviewAsync("status page", $"/statuspage/{Uri.EscapeDataString(id)}", $"/statuspage/{Uri.EscapeDataString(id)}", confirmed, ct: ct);

    [McpServerTool(Name = "create_status_page_incident", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Declare an incident or a scheduled maintenance on a status page. Scope 'statuspage:write'. This " +
        "PUBLISHES the message to the page and notifies its subscribers - have the user approve the exact wording first.")]
    public Task<CallToolResult> CreateStatusPageIncident(
        [Description("The status page id.")] string id,
        [Description("Incident headline.")] string title,
        [Description("The first timeline message shown to visitors.")] string message,
        [Description("Lifecycle state: investigating, identified, monitoring or resolved.")] string state = "investigating",
        [Description("'incident' (default) or 'maintenance'.")] string? kind = null,
        [Description("Impact: minor or major.")] string? impact = null,
        [Description("Comma-separated component ids the incident affects.")] string? componentIds = null,
        [Description("Scheduled start for a maintenance, Unix seconds.")] long? scheduledStart = null,
        [Description("Scheduled end for a maintenance, Unix seconds.")] long? scheduledEnd = null,
        [Description("Reuse the same key to make a retry replay instead of publishing twice.")] string? idempotencyKey = null,
        CancellationToken ct = default) {

        var body = Body(("title", title), ("message", message), ("state", state), ("kind", kind), ("impact", impact),
                        ("componentIds", Split(componentIds)), ("scheduledStart", scheduledStart), ("scheduledEnd", scheduledEnd));
        return CallAsync(HttpMethod.Post, $"/statuspage/{Uri.EscapeDataString(id)}/incident", null, body,
            heading: "Incident published:", idempotencyKey: IdempotencyKey(idempotencyKey), ct: ct);
    }

    [McpServerTool(Name = "add_status_page_incident_update", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Append an update to a declared incident's timeline (and move its state, e.g. to 'resolved'). Scope " +
        "'statuspage:write'. This too is PUBLISHED and notifies subscribers - get the wording approved first.")]
    public Task<CallToolResult> AddStatusPageIncidentUpdate(
        [Description("The status page id.")] string id,
        [Description("The incident id.")] string incidentId,
        [Description("The update message shown to visitors.")] string message,
        [Description("New lifecycle state: investigating, identified, monitoring or resolved.")] string state,
        [Description("Reuse the same key to make a retry replay instead of publishing twice.")] string? idempotencyKey = null,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post,
            $"/statuspage/{Uri.EscapeDataString(id)}/incident/{Uri.EscapeDataString(incidentId)}/timeline", null,
            Body(("message", message), ("state", state)),
            heading: "Timeline updated:", idempotencyKey: IdempotencyKey(idempotencyKey), ct: ct);
}
