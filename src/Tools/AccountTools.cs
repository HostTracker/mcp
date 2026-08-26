using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Account - READ ONLY, by design. Scope <c>account:read</c>. There is deliberately no tool that writes to the
/// account: profile, email, country and default pools are identity data a person changes themselves, and
/// <see cref="SensitivePolicy"/> refuses any write under <c>/account</c> even through the generic door. Payments,
/// packages, passwords and token minting are not on this API surface at all.
/// </summary>
[McpServerToolType]
public sealed class AccountTools : V2ToolBase {
    public AccountTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "get_account", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the account: identity, package, resource usage, limits and status flags. Scope " +
        "'account:read'. Read-only - this server cannot change account settings.")]
    public Task<CallToolResult> GetAccount(CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, "/account", heading: "Account:", ct: ct);

    [McpServerTool(Name = "get_account_quota", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the API quota headroom and the scopes the current token actually carries. Scope " +
        "'account:read'. Call this first when another tool returns a 403 - it shows whether the token is simply " +
        "missing a scope.")]
    public Task<CallToolResult> GetAccountQuota(CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, "/account/quota", heading: "API quota and token scopes:", ct: ct);

    [McpServerTool(Name = "get_account_usage", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read how many monitors, contacts, reports and maintenance windows the account uses out of what " +
        "its package allows. Scope 'account:read'.")]
    public Task<CallToolResult> GetAccountUsage(CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, "/account/usage", heading: "Resource usage:", ct: ct);
}

/// <summary>
/// Monitoring locations: the pools a monitor or an instant check can be pinned to, and the individual agents behind
/// them. Anonymous reference data.
/// </summary>
[McpServerToolType]
public sealed class LocationTools : V2ToolBase {
    public LocationTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "list_locations", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the location pools checks can run from (and, with agents=true, the individual monitoring " +
        "locations). Pool ids are what the 'pools' argument of create_monitor and run_instant_check takes; " +
        "'allworld' means everywhere.")]
    public Task<CallToolResult> ListLocations(
        [Description("Set true to list individual agents instead of pools.")] bool agents = false,
        [Description("Comma-separated ISO country codes to filter agents by.")] string? country = null,
        [Description("Comma-separated pool ids to filter agents by.")] string? pool = null,
        [Description("Rows per page, 1-50 (default 50 for pools).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) =>
        agents
            ? ListAsync("/agent",
                new Q().List("country", country).List("pool", pool).Set("limit", CapLimit(limit ?? MaxLimit)).Set("cursor", cursor),
                "list_locations", "Monitoring locations:", ct)
            : ListAsync("/agent/pool",
                new Q().Set("limit", CapLimit(limit ?? MaxLimit)).Set("cursor", cursor),
                "list_locations", "Location pools:", ct);
}
