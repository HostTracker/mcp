using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Maintenance windows - planned periods during which selected monitors suppress alerts and/or statistics. Reads
/// need <c>monitor:read</c>, writes <c>monitor:write</c>. All instants are Unix seconds; a timezone is an IANA id
/// (a Windows spelling is accepted on write and normalised).
/// </summary>
[McpServerToolType]
public sealed class MaintenanceTools : V2ToolBase {
    public MaintenanceTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "list_maintenance", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List scheduled, active and finished maintenance windows. Scope 'monitor:read'.")]
    public Task<CallToolResult> ListMaintenance(
        [Description("Comma-separated states: scheduled, active, finished.")] string? state = null,
        [Description("Comma-separated monitor ids to filter by.")] string? monitor = null,
        [Description("Window start, Unix seconds.")] long? from = null,
        [Description("Window end, Unix seconds.")] long? to = null,
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) =>
        ListAsync("/maintenance",
            new Q().List("state", state).List("monitor", monitor).Set("from", (int?)from).Set("to", (int?)to)
                   .Set("limit", CapLimit(limit)).Set("cursor", cursor),
            "list_maintenance", "Maintenance windows:", ct);

    [McpServerTool(Name = "create_maintenance", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Schedule a maintenance window over an explicit set of monitors. Scope 'monitor:write'. While it " +
        "runs the covered monitors suppress alerts (and statistics, if asked). Times are Unix seconds.")]
    public async Task<CallToolResult> CreateMaintenance(
        [Description("Window name.")] string name,
        [Description("Start instant, Unix seconds.")] long from,
        [Description("Comma-separated monitor ids the window covers.")] string monitorIds,
        [Description("Length in seconds. Pass this or 'to'.")] int? durationSec = null,
        [Description("End instant, Unix seconds. Pass this or 'durationSec'.")] long? to = null,
        [Description("IANA timezone the schedule is expressed in, e.g. Europe/Berlin.")] string? timezone = null,
        [Description("Suppress alerts during the window (default true on the API side).")] bool? suppressAlerts = null,
        [Description("Suppress statistics during the window.")] bool? suppressStats = null,
        [Description("Comma-separated weekdays for a recurring window, e.g. 'Saturday,Sunday'.")] string? weekDays = null,
        CancellationToken ct = default) {

        var monitors = Split(monitorIds);
        if (monitors is null) return Err("Provide at least one monitor id in 'monitorIds'.");
        var suppress = suppressAlerts is null && suppressStats is null ? null : Body(("alerts", suppressAlerts), ("stats", suppressStats));
        var recurrence = Split(weekDays) is { } days ? Body(("weekDays", days)) : null;

        var body = Body(("name", name), ("from", from), ("to", to), ("durationSec", durationSec),
                        ("timezone", timezone), ("monitorIds", monitors), ("suppress", suppress), ("recurrence", recurrence));
        return await CallAsync(HttpMethod.Post, "/maintenance", null, body, heading: "Maintenance window created:", ct: ct);
    }

    [McpServerTool(Name = "update_maintenance", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Reschedule a maintenance window or change what it covers. Scope 'monitor:write'. Only the " +
        "arguments you pass are changed; a monitorIds list REPLACES the current coverage.")]
    public async Task<CallToolResult> UpdateMaintenance(
        [Description("The maintenance window id.")] string id,
        [Description("New name.")] string? name = null,
        [Description("New start instant, Unix seconds.")] long? from = null,
        [Description("New end instant, Unix seconds.")] long? to = null,
        [Description("New length in seconds.")] int? durationSec = null,
        [Description("New IANA timezone.")] string? timezone = null,
        [Description("Comma-separated monitor ids that replace the current coverage.")] string? monitorIds = null,
        [Description("Enable or disable the window without deleting it.")] bool? enabled = null,
        CancellationToken ct = default) {

        var body = Body(("name", name), ("from", from), ("to", to), ("durationSec", durationSec),
                        ("timezone", timezone), ("monitorIds", Split(monitorIds)), ("enabled", enabled));
        if (body.Count == 0) return Err("Nothing to update - pass at least one field to change.");
        return await CallAsync(HttpMethod.Patch, $"/maintenance/{Uri.EscapeDataString(id)}", null, body,
            heading: "Maintenance window updated:", ct: ct);
    }

    [McpServerTool(Name = "delete_maintenance", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Cancel a maintenance window. Scope 'monitor:write'. DESTRUCTIVE - confirm with the user first; " +
        "cancelling an ACTIVE window makes its monitors start alerting again immediately.")]
    public Task<CallToolResult> DeleteMaintenance(
        [Description("The maintenance window id.")] string id,
        [Description("Must be true to actually cancel. Call WITHOUT it first: the tool answers with the resource so you can confirm with the user.")] bool confirmed = false,
        CancellationToken ct = default) =>
        DeleteWithPreviewAsync("maintenance window", $"/maintenance/{Uri.EscapeDataString(id)}", $"/maintenance/{Uri.EscapeDataString(id)}", confirmed, heading: "Cancelled:", ct: ct);
}
