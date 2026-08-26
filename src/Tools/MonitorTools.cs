using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Monitors - the account's continuously-running checks. Reads need the <c>monitor:read</c> scope, writes
/// <c>monitor:write</c>. Ids are opaque strings; every timestamp is Unix seconds. The bulk doors run their
/// validate step first so the agent sees what a submission would touch before it touches it.
/// </summary>
[McpServerToolType]
public sealed class MonitorTools : V2ToolBase {
    public MonitorTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "list_monitors", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the account's monitors, newest page first. Scope 'monitor:read'. Filters combine with AND; " +
        "omit them all to list the whole account. Returns at most 50 rows plus a cursor for the next page.")]
    public Task<CallToolResult> ListMonitors(
        [Description("Free-text search over name and url.")] string? q = null,
        [Description("Comma-separated states to keep: up, down, paused, maintenance.")] string? state = null,
        [Description("Comma-separated monitor types, e.g. 'http,ping'. See list_monitor_types.")] string? type = null,
        [Description("Comma-separated tags.")] string? tag = null,
        [Description("Comma-separated monitor ids.")] string? id = null,
        [Description("Sort column, e.g. 'name', 'state', 'lastChange:desc'.")] string? sort = null,
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call's continuation line.")] string? cursor = null,
        CancellationToken ct = default) =>
        ListAsync("/monitor",
            new Q().Set("q", q).List("state", state).List("type", type).List("tag", tag).List("id", id)
                   .Set("sort", sort).Set("limit", CapLimit(limit)).Set("cursor", cursor),
            "list_monitors", "Monitors:", ct);

    [McpServerTool(Name = "get_monitor", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read one monitor with its full configuration. Scope 'monitor:read'. Add expand tokens for more " +
        "detail (settings, uptime, lastResult, lastIncident, subscription, maintenance, attached, spans).")]
    public Task<CallToolResult> GetMonitor(
        [Description("The monitor id.")] string id,
        [Description("Comma-separated expand tokens; defaults to 'settings,uptime'.")] string? expand = "settings,uptime",
        [Description("Window start for uptime/spans, Unix seconds.")] long? from = null,
        [Description("Window end for uptime/spans, Unix seconds.")] long? to = null,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/monitor/{Uri.EscapeDataString(id)}",
            new Q().Csv("expand", expand).Set("from", (int?)from).Set("to", (int?)to),
            heading: "Monitor:", ct: ct);

    [McpServerTool(Name = "create_monitor", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Create a monitor. Scope 'monitor:write'. Confirm the target and interval with the user first - a " +
        "monitor consumes an account slot and starts alerting. Attach contacts afterwards with subscribe_contact.")]
    public async Task<CallToolResult> CreateMonitor(
        [Description("Monitor type, e.g. http, ping, port, waterfall, sslExp, domainExp. See list_monitor_types.")] string type,
        [Description("The address to monitor.")] string? url = null,
        [Description("Display name; defaults to the url.")] string? name = null,
        [Description("Check interval in minutes; must be one of the account's allowed intervals.")] int? interval = null,
        [Description("Comma-separated tags.")] string? tags = null,
        [Description("Comma-separated location pools, e.g. 'allworld' for everywhere. At least one is required when the type needs locations.")] string? pools = null,
        [Description("Whether the monitor starts enabled (default true).")] bool? enabled = null,
        [Description("Type-specific settings as a JSON object (see the monitor type's schema).")] string? settingsJson = null,
        [Description("Set true to validate only, creating nothing.")] bool? dryRun = null,
        CancellationToken ct = default) {

        if (!TryJson(settingsJson, nameof(settingsJson), out var settings, out var badJson)) return Err(badJson);
        var body = Body(("type", type), ("url", url), ("name", name), ("interval", interval), ("tags", Split(tags)),
                        ("enabled", enabled), ("settings", settings),
                        ("locations", Split(pools) is { } list ? Body(("pools", list)) : null));
        return await CallAsync(HttpMethod.Post, "/monitor", new Q().Set("dryRun", dryRun), body,
            heading: dryRun == true ? "Validation only, nothing created:" : "Monitor created:", ct: ct);
    }

    [McpServerTool(Name = "update_monitor", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Partially update a monitor. Scope 'monitor:write'. Only the arguments you pass are changed; " +
        "everything else stays as it is.")]
    public async Task<CallToolResult> UpdateMonitor(
        [Description("The monitor id.")] string id,
        [Description("New display name.")] string? name = null,
        [Description("New address.")] string? url = null,
        [Description("New check interval in minutes.")] int? interval = null,
        [Description("Comma-separated tags that REPLACE the current set.")] string? tags = null,
        [Description("Comma-separated tags to add.")] string? addTags = null,
        [Description("Comma-separated tags to remove.")] string? removeTags = null,
        [Description("Comma-separated location pools that replace the current pinning.")] string? pools = null,
        [Description("Type-specific settings as a JSON object.")] string? settingsJson = null,
        CancellationToken ct = default) {

        if (!TryJson(settingsJson, nameof(settingsJson), out var settings, out var badJson)) return Err(badJson);
        var body = Body(("name", name), ("url", url), ("interval", interval), ("tags", Split(tags)),
                        ("addTags", Split(addTags)), ("removeTags", Split(removeTags)),
                        ("settings", settings),
                        ("locations", Split(pools) is { } list ? Body(("pools", list)) : null));
        if (body.Count == 0) return Err("Nothing to update - pass at least one field to change.");
        return await CallAsync(HttpMethod.Patch, $"/monitor/{Uri.EscapeDataString(id)}", null, body, heading: "Monitor updated:", ct: ct);
    }

    [McpServerTool(Name = "delete_monitor", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Delete one monitor and its subscriptions. Scope 'monitor:write'. DESTRUCTIVE and not undoable - " +
        "confirm with the user first, then report the deletion receipt this returns.")]
    public Task<CallToolResult> DeleteMonitor(
        [Description("The monitor id.")] string id,
        [Description("Must be true to actually delete. Call WITHOUT it first: the tool answers with the resource so you can confirm with the user.")] bool confirmed = false,
        CancellationToken ct = default) =>
        DeleteWithPreviewAsync("monitor", $"/monitor/{Uri.EscapeDataString(id)}", $"/monitor/{Uri.EscapeDataString(id)}", confirmed, ct: ct);

    [McpServerTool(Name = "pause_monitor", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Pause a monitor: it stops checking and stops alerting until resumed. Scope 'monitor:write'.")]
    public Task<CallToolResult> PauseMonitor(
        [Description("The monitor id.")] string id,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Patch, $"/monitor/{Uri.EscapeDataString(id)}", null, Body(("enabled", false)), heading: "Monitor paused:", ct: ct);

    [McpServerTool(Name = "resume_monitor", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Resume a paused monitor. Scope 'monitor:write'.")]
    public Task<CallToolResult> ResumeMonitor(
        [Description("The monitor id.")] string id,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Patch, $"/monitor/{Uri.EscapeDataString(id)}", null, Body(("enabled", true)), heading: "Monitor resumed:", ct: ct);

    [McpServerTool(Name = "copy_monitor", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Copy a monitor to one or more new addresses, keeping its configuration. Scope 'monitor:write'. " +
        "Copying many addresses answers with a job id - poll it with get_job.")]
    public Task<CallToolResult> CopyMonitor(
        [Description("The monitor id to copy from.")] string id,
        [Description("Comma-separated addresses to create copies for.")] string urls,
        [Description("Copy the alert subscriptions too (default true).")] bool? includeAlerts = null,
        [Description("Copy the report subscriptions too.")] bool? includeReports = null,
        [Description("Copy the maintenance windows too.")] bool? includeMaintenance = null,
        [Description("Name for the copies; the address is used when omitted.")] string? name = null,
        CancellationToken ct = default) {

        var addresses = Split(urls);
        if (addresses is null) return Task.FromResult(Err("Provide at least one address in 'urls'."));
        var body = Body(("urls", addresses), ("includeAlerts", includeAlerts), ("includeReports", includeReports),
                        ("includeMaintenance", includeMaintenance), ("name", name));
        return CallAsync(HttpMethod.Post, $"/monitor/{Uri.EscapeDataString(id)}/copy", null, body,
            heading: "Copies:", idempotencyKey: IdempotencyKey(null), ct: ct);
    }

    [McpServerTool(Name = "bulk_create_monitors", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Create many monitors in one asynchronous job. Scope 'monitor:write'. The batch is validated " +
        "first and, unless submit is true, only the validation report comes back - show it to the user, then call " +
        "again with submit=true. The submitted job is polled with get_job.")]
    public async Task<CallToolResult> BulkCreateMonitors(
        [Description("JSON array of monitor definitions, e.g. [{\"type\":\"http\",\"url\":\"a.com\"},{\"type\":\"ping\",\"url\":\"b.com\"}].")] string itemsJson,
        [Description("JSON object of defaults applied to every item, e.g. {\"interval\":5,\"tags\":[\"prod\"]}.")] string? defaultsJson = null,
        [Description("Set true to actually create them after reviewing the validation report.")] bool submit = false,
        [Description("Reuse the same key to make a retry replay instead of creating twice.")] string? idempotencyKey = null,
        CancellationToken ct = default) {

        if (!TryJson(itemsJson, nameof(itemsJson), out var items, out var badItems)) return Err(badItems);
        if (items.ValueKind != JsonValueKind.Array) return Err("'itemsJson' must be a JSON array of monitor definitions.");
        if (!TryJson(defaultsJson, nameof(defaultsJson), out var defaults, out var badDefaults)) return Err(badDefaults);

        var body = Body(("items", items), ("defaults", defaults));
        if (!submit)
            return await CallAsync(HttpMethod.Post, "/monitor/bulk-validate", null, body,
                heading: "Validation only - nothing was created. Review, then call again with submit=true:", ct: ct);
        return await CallAsync(HttpMethod.Post, "/monitor/bulk", null, body,
            heading: "Bulk create accepted - poll the job with get_job:", idempotencyKey: IdempotencyKey(idempotencyKey), ct: ct);
    }

    [McpServerTool(Name = "bulk_update_monitors", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Apply one patch to every monitor a filter selects, as an asynchronous job. Scope 'monitor:write'. " +
        "Without submit=true only the count and a sample of what would be touched come back - show that to the user first.")]
    public async Task<CallToolResult> BulkUpdateMonitors(
        [Description("JSON selection filter, e.g. {\"tags\":[\"prod\"]} or {\"monitorIds\":[\"...\"]}.")] string filterJson,
        [Description("JSON patch applied to each selected monitor, e.g. {\"interval\":5}.")] string? patchJson = null,
        [Description("Set to 'resetStats' to clear statistics instead of patching.")] string? operation = null,
        [Description("Set true to actually apply the change.")] bool submit = false,
        [Description("Reuse the same key to make a retry replay instead of applying twice.")] string? idempotencyKey = null,
        CancellationToken ct = default) {

        if (!TryJson(filterJson, nameof(filterJson), out var filter, out var badFilter)) return Err(badFilter);
        if (filter.ValueKind != JsonValueKind.Object) return Err("'filterJson' must be a JSON object, e.g. {\"tags\":[\"prod\"]}.");
        if (!TryJson(patchJson, nameof(patchJson), out var patch, out var badPatch)) return Err(badPatch);
        if (patch.ValueKind == JsonValueKind.Undefined && string.IsNullOrWhiteSpace(operation))
            return Err("Pass either 'patchJson' (what to change) or operation='resetStats'.");

        var body = Body(("filter", filter), ("patch", patch), ("operation", operation));
        if (!submit)
            return await CallAsync(HttpMethod.Post, "/monitor/bulk-update-validate", null, body,
                heading: "Selection only - nothing changed. Review, then call again with submit=true:", ct: ct);
        return await CallAsync(HttpMethod.Post, "/monitor/bulk-update", null, body,
            heading: "Bulk update accepted - poll the job with get_job:", idempotencyKey: IdempotencyKey(idempotencyKey), ct: ct);
    }

    [McpServerTool(Name = "bulk_delete_monitors", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Delete every monitor a filter selects, as an asynchronous job. HIGHLY DESTRUCTIVE - always show " +
        "the user the validation count first and get an explicit go-ahead. Called without expectedCount it only " +
        "validates; the submission needs BOTH confirmed=true and the expectedCount the validation reported, and the " +
        "API refuses it if the selection drifted meanwhile. Scope 'monitor:write'.")]
    public async Task<CallToolResult> BulkDeleteMonitors(
        [Description("JSON selection filter, e.g. {\"tags\":[\"staging\"]}.")] string filterJson,
        [Description("The 'matched' number the validation step reported. Required to submit.")] int? expectedCount = null,
        [Description("Must be true, together with expectedCount, to actually delete.")] bool confirmed = false,
        [Description("Reuse the same key to make a retry replay instead of deleting twice.")] string? idempotencyKey = null,
        CancellationToken ct = default) {

        if (!TryJson(filterJson, nameof(filterJson), out var filter, out var badFilter)) return Err(badFilter);
        if (filter.ValueKind != JsonValueKind.Object) return Err("'filterJson' must be a JSON object, e.g. {\"tags\":[\"staging\"]}.");

        if (!confirmed || expectedCount is null)
            return await CallAsync(HttpMethod.Post, "/monitor/bulk-delete-validate", null, Body(("filter", filter)),
                heading: "Validation only - NOTHING was deleted. Show the matched count to the user, get an explicit " +
                         "confirmation, then call again with confirmed=true and expectedCount set to that number:", ct: ct);

        return await CallAsync(HttpMethod.Post, "/monitor/bulk-delete", null,
            Body(("filter", filter), ("expectedCount", expectedCount)),
            heading: "Bulk delete accepted - poll the job with get_job:", idempotencyKey: IdempotencyKey(idempotencyKey), ct: ct);
    }

    [McpServerTool(Name = "list_monitor_types", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List every monitor type with its label, minimum interval and whether the account's package can " +
        "create it. Anonymous, but a token adds the per-account limits.")]
    public Task<CallToolResult> ListMonitorTypes(CancellationToken ct = default) =>
        ListAsync("/monitor/type", new Q().Set("limit", MaxLimit), "list_monitor_types", "Monitor types:", ct);
}
