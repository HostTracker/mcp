using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Subscriptions - which contact hears about which monitor. Two independent legs on the same
/// (monitor, contact) pair: ALERTS (up / down / repeatedlyDown) and REPORTS (daily ... yearly). Listing needs
/// <c>subs:read</c>; writing a leg is a monitor write (<c>monitor:write</c>, plus <c>contact:read</c> to resolve
/// the contact). Setting a leg REPLACES its whole value set for that pair.
/// </summary>
[McpServerToolType]
public sealed class SubscriptionTools : V2ToolBase {
    public SubscriptionTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "list_subscriptions", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List who is notified about what. Scope 'subs:read'. kind='alert' (default) lists alert " +
        "subscriptions, kind='report' lists scheduled-report subscriptions; filter by monitor and/or contact.")]
    public Task<CallToolResult> ListSubscriptions(
        [Description("Which leg to list: 'alert' or 'report'.")] string? kind = "alert",
        [Description("Comma-separated monitor ids.")] string? monitorId = null,
        [Description("Comma-separated contact ids.")] string? contactId = null,
        [Description("Free-text search over the monitors.")] string? monitorQuery = null,
        [Description("Free-text search over the contacts.")] string? contactQuery = null,
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) {

        var report = string.Equals(kind, "report", StringComparison.OrdinalIgnoreCase);
        var path = report ? "/report" : "/alert";
        var query = new Q().List("monitor.id", monitorId).List("contact.id", contactId)
                           .Set("monitor.q", monitorQuery).Set("contact.q", contactQuery)
                           .Set("limit", CapLimit(limit)).Set("cursor", cursor);
        return ListAsync(path, query, "list_subscriptions", report ? "Report subscriptions:" : "Alert subscriptions:", ct);
    }

    [McpServerTool(Name = "subscribe_contact", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Subscribe a contact to a monitor. Scope 'monitor:write'. Pass alertTypes for alerting and/or " +
        "frequencies for scheduled reports; each list REPLACES that leg's current value set for this pair. The " +
        "contact must be confirmed before anything is actually delivered.")]
    public async Task<CallToolResult> SubscribeContact(
        [Description("The monitor id.")] string monitorId,
        [Description("The contact id.")] string contactId,
        [Description("Comma-separated alert types: up, down, repeatedlyDown.")] string? alertTypes = null,
        [Description("Comma-separated report frequencies: daily, weekly, monthly, quarterly, yearly.")] string? frequencies = null,
        CancellationToken ct = default) {

        var alerts = Split(alertTypes);
        var reports = Split(frequencies);
        if (alerts is null && reports is null)
            return Err("Pass 'alertTypes' (e.g. 'down,up') and/or 'frequencies' (e.g. 'weekly') - one of them is required.");

        var monitor = Uri.EscapeDataString(monitorId);
        var contact = Uri.EscapeDataString(contactId);
        var results = new List<string>();

        if (alerts is not null) {
            var response = await CallAsync(HttpMethod.Put, $"/monitor/{monitor}/alert/{contact}", null,
                Body(("alertTypes", alerts)), heading: "Alert subscription:", ct: ct);
            if (response.IsError == true) return response;
            results.Add(Text(response));
        }
        if (reports is not null) {
            var response = await CallAsync(HttpMethod.Put, $"/monitor/{monitor}/report/{contact}", null,
                Body(("frequencies", reports)), heading: "Report subscription:", ct: ct);
            if (response.IsError == true) return response;
            results.Add(Text(response));
        }
        return Ok(string.Join("\n", results));
    }

    [McpServerTool(Name = "unsubscribe_contact", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Remove a contact's subscription to a monitor. Scope 'monitor:write'. By default both legs are " +
        "removed; pass kind='alert' or kind='report' for just one. Confirm with the user - they stop being notified.")]
    public async Task<CallToolResult> UnsubscribeContact(
        [Description("The monitor id.")] string monitorId,
        [Description("The contact id.")] string contactId,
        [Description("Which leg to remove: 'alert', 'report' or 'both' (default).")] string? kind = "both",
        CancellationToken ct = default) {

        var monitor = Uri.EscapeDataString(monitorId);
        var contact = Uri.EscapeDataString(contactId);
        var both = string.IsNullOrWhiteSpace(kind) || kind.Equals("both", StringComparison.OrdinalIgnoreCase);
        var results = new List<string>();

        if (both || kind!.Equals("alert", StringComparison.OrdinalIgnoreCase)) {
            var response = await CallAsync(HttpMethod.Delete, $"/monitor/{monitor}/alert/{contact}", heading: "Alert subscription removed:", ct: ct);
            if (response.IsError == true && !both) return response;
            results.Add(Text(response));
        }
        if (both || kind!.Equals("report", StringComparison.OrdinalIgnoreCase)) {
            var response = await CallAsync(HttpMethod.Delete, $"/monitor/{monitor}/report/{contact}", heading: "Report subscription removed:", ct: ct);
            if (response.IsError == true && !both) return response;
            results.Add(Text(response));
        }
        if (results.Count == 0) return Err("'kind' must be 'alert', 'report' or 'both'.");
        return Ok(string.Join("\n", results));
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
