using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Webhooks - signed HTTPS deliveries of account events. Reads need <c>webhook:read</c>, writes
/// <c>webhook:write</c>. A webhook is its own resource (not a contact): the url must be https, deliveries are
/// HMAC-signed, and 20 consecutive failures auto-disable it. The signing secret is returned ONCE at creation and on
/// rotation - relay it to the user and do not repeat it afterwards.
/// </summary>
[McpServerToolType]
public sealed class WebhookTools : V2ToolBase {
    public WebhookTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "list_webhooks", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the account's registered webhooks, including whether each is enabled and its recent failure " +
        "count. Scope 'webhook:read'.")]
    public Task<CallToolResult> ListWebhooks(
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) =>
        ListAsync("/webhook", new Q().Set("limit", CapLimit(limit)).Set("cursor", cursor), "list_webhooks", "Webhooks:", ct);

    [McpServerTool(Name = "create_webhook", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Register a webhook. Scope 'webhook:write'. The url must be https and publicly reachable. Events " +
        "are chosen from: monitor.down, monitor.up, monitor.repeatedlyDown, incident.opened, incident.closed, " +
        "monitor.created, monitor.updated, monitor.deleted, maintenance.ended, certificate.expiring, " +
        "domain.expiring, contact.confirmed, contact.updated. The response carries the signing secret once.")]
    public async Task<CallToolResult> CreateWebhook(
        [Description("The https endpoint deliveries are POSTed to.")] string url,
        [Description("Comma-separated event names, e.g. 'monitor.down,monitor.up'.")] string events,
        [Description("Display name.")] string? name = null,
        [Description("Comma-separated monitor ids to scope deliveries to; omit for the whole account.")] string? monitorIds = null,
        [Description("Comma-separated tags to scope deliveries to.")] string? tags = null,
        CancellationToken ct = default) {

        var eventList = Split(events);
        if (eventList is null) return Err("Provide at least one event name in 'events'.");

        var monitors = Split(monitorIds);
        var tagList = Split(tags);
        var scope = monitors is null && tagList is null
            ? Body(("all", true))
            : Body(("monitorIds", monitors), ("tags", tagList));

        var body = Body(("url", url), ("events", eventList), ("name", name), ("scope", scope));
        return await CallAsync(HttpMethod.Post, "/webhook", null, body,
            heading: "Webhook created - the signing secret below is shown only once:", ct: ct);
    }

    [McpServerTool(Name = "update_webhook", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Change a webhook's url, events, scope, name, or enabled state. Scope 'webhook:write'. Re-enabling " +
        "an auto-disabled webhook also clears its failure counter.")]
    public async Task<CallToolResult> UpdateWebhook(
        [Description("The webhook id.")] string id,
        [Description("New https endpoint.")] string? url = null,
        [Description("Comma-separated event names that replace the current set.")] string? events = null,
        [Description("New display name.")] string? name = null,
        [Description("Enable or disable deliveries.")] bool? enabled = null,
        [Description("Comma-separated monitor ids that replace the current scope.")] string? monitorIds = null,
        CancellationToken ct = default) {

        var monitors = Split(monitorIds);
        var body = Body(("url", url), ("events", Split(events)), ("name", name), ("enabled", enabled),
                        ("scope", monitors is null ? null : Body(("monitorIds", monitors))));
        if (body.Count == 0) return Err("Nothing to update - pass at least one field to change.");
        return await CallAsync(HttpMethod.Patch, $"/webhook/{Uri.EscapeDataString(id)}", null, body, heading: "Webhook updated:", ct: ct);
    }

    [McpServerTool(Name = "delete_webhook", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Unregister a webhook and stop its deliveries. Scope 'webhook:write'. DESTRUCTIVE - confirm with " +
        "the user first; pending deliveries are dropped and the signing secret cannot be recovered.")]
    public Task<CallToolResult> DeleteWebhook(
        [Description("The webhook id.")] string id,
        [Description("Must be true to actually delete. Call WITHOUT it first: the tool answers with the resource so you can confirm with the user.")] bool confirmed = false,
        CancellationToken ct = default) =>
        DeleteWithPreviewAsync("webhook", $"/webhook/{Uri.EscapeDataString(id)}", $"/webhook/{Uri.EscapeDataString(id)}", confirmed, ct: ct);

    [McpServerTool(Name = "test_webhook", Destructive = false, ReadOnly = false, OpenWorld = true)]
    [Description("Send a synthetic test delivery and report the endpoint's answer. Scope 'webhook:write'. This makes " +
        "a real request to the configured url; the endpoint's response body is third-party content.")]
    public Task<CallToolResult> TestWebhook(
        [Description("The webhook id.")] string id,
        [Description("Event name to simulate, e.g. 'monitor.down'.")] string? eventName = null,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/webhook/{Uri.EscapeDataString(id)}/test", null,
            Body(("event", eventName)), heading: "Test delivery:", untrusted: true, ct: ct);

    [McpServerTool(Name = "list_webhook_deliveries", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List recent deliveries for one webhook, with their outcome and attempts. Scope 'webhook:read'. " +
        "Use it to diagnose why an endpoint stopped receiving events.")]
    public async Task<CallToolResult> ListWebhookDeliveries(
        [Description("The webhook id.")] string id,
        [Description("Comma-separated outcomes: pending, delivered, failed, dropped.")] string? outcome = null,
        [Description("Comma-separated event names.")] string? eventName = null,
        [Description("Window start, Unix seconds.")] long? from = null,
        [Description("Window end, Unix seconds.")] long? to = null,
        [Description("Rows per page, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        CancellationToken ct = default) {

        if (!TryGetToken(out var token, out var noToken)) return Err(noToken);
        var page = await Api.GetPageAsync(token, $"/webhook/{Uri.EscapeDataString(id)}/delivery",
            new Q().List("outcome", outcome).List("event", eventName).Set("from", (int?)from).Set("to", (int?)to)
                   .Set("limit", CapLimit(limit)).Set("cursor", cursor), ct).ConfigureAwait(false);
        if (!page.Ok) return Err(ToolText.ErrorText(page.Error!));
        return Ok(ToolText.Fence(ToolText.RenderPage("Deliveries:", page, "list_webhook_deliveries")));
    }

    [McpServerTool(Name = "redeliver_webhook", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Resend a previously recorded delivery to the same endpoint. Scope 'webhook:write'. The receiver " +
        "sees the same delivery id, so a correctly-written consumer deduplicates it.")]
    public Task<CallToolResult> RedeliverWebhook(
        [Description("The webhook id.")] string id,
        [Description("The delivery id (d_... ) from list_webhook_deliveries.")] string deliveryId,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/webhook/{Uri.EscapeDataString(id)}/delivery/{Uri.EscapeDataString(deliveryId)}/redeliver",
            heading: "Redelivered:", untrusted: true, ct: ct);
}
