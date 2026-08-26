using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

// The instant-check tools, on the HostTracker API v2 (`POST /check`, `GET /check/{dbId}/{id}`, anonymous `GET /check/type` and
// `GET /check/device`). An instant check is addressed by the PAIR (dbId:int, id:guid). All timestamps are Unix
// seconds. Per-location error/metrics come from the CHECKED TARGET and are fenced + capped before an agent sees them.

/// <summary>One location's answer. <see cref="Error"/>/<see cref="Metrics"/> are raw, type-specific JSON passed
/// through verbatim by the API AND carry target-controlled content - the tool layer treats them as untrusted.</summary>
public sealed class CheckEvent {
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("provider")] public JsonElement? Provider { get; set; }
    [JsonPropertyName("doneAt")] public long? DoneAt { get; set; }
    [JsonPropertyName("error")] public JsonElement? Error { get; set; }
    [JsonPropertyName("metrics")] public JsonElement? Metrics { get; set; }
}

/// <summary>GET /check/{dbId}/{id}. <c>state == "done"</c> is the finished signal (a terminal answer also carries
/// <c>doneAt</c> and drops <c>retryAfter</c>).</summary>
public sealed class CheckResult {
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("dbId")] public int DbId { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("doneAt")] public long? DoneAt { get; set; }
    [JsonPropertyName("retryAfter")] public int? RetryAfter { get; set; }
    [JsonPropertyName("events")] public List<CheckEvent> Events { get; set; } = new();

    [JsonIgnore] public bool IsFinal => string.Equals(State, "done", StringComparison.OrdinalIgnoreCase) || DoneAt.HasValue;
}

/// <summary>
/// Instant checks: start one, poll it, and list what types the fleet offers. The caller's token is read from the
/// transport header (never a tool argument) and forwarded per request; the API owns authentication, the
/// <c>check</c> scope, quotas and the url blacklist.
/// </summary>
[McpServerToolType]
public sealed class InstantCheckTools : V2ToolBase {
    private const int MaxPollMs = 30000;        // WALL-CLOCK budget for the whole poll, incl. API call time
    private const int MinPollMs = 2000;
    private const int MaxPollIntervalMs = 10000;

    // Per-process cap on the long-running check tool: the per-IP rate limit bounds arrival rate, not true
    // concurrency, and each held check pins a thread + a pooled connection for up to 30s.
    private static readonly SemaphoreSlim InFlight = new(64, 64);

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly CheckCatalog catalog;

    public InstantCheckTools(V2ApiClient api, IHttpContextAccessor httpContext, CheckCatalog catalog)
        : base(api, httpContext) => this.catalog = catalog;

    [McpServerTool(Name = "list_check_types", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the instant-check types HostTracker supports, plus the device profiles a page-loading " +
        "(waterfall) check can emulate. Read live from the API catalogue and cached briefly. No authentication required.")]
    public async Task<CallToolResult> ListCheckTypes(CancellationToken ct = default) {
        TryGetToken(out var token, out _);                       // optional: an authenticated read gets its own quota bucket
        return Ok(await catalog.GetAsync(token, ct).ConfigureAwait(false));
    }

    [McpServerTool(Name = "run_instant_check", Destructive = false, ReadOnly = false, OpenWorld = true)]
    [Description("Run a free instant website/host check from HostTracker's global monitoring locations and return " +
        "per-location results. Requires a token with the 'check' scope (mint at Integrations -> API, " +
        "https://www.host-tracker.com/integrations/api). Starts the check, polls up to ~30s, and returns per-location " +
        "status plus the public result-page URL; a check that is still running comes back partial with the ids to poll.")]
    public async Task<CallToolResult> RunInstantCheck(
        [Description("The site or host to check, e.g. example.com or https://example.com")] string url,
        [Description("Check type; one of the tokens from list_check_types (default http). 'pageSpeed' is accepted as an alias for 'waterfall'.")] string? type = null,
        [Description("Comma-separated location pools to run from, e.g. 'europe,northamerica'. Unknown pool names are refused by the API, which names the offender.")] string? pools = null,
        [Description("Device-emulation profile for a waterfall/pageSpeed check; one of the device tokens from list_check_types.")] string? device = null,
        [Description("http checks only: validate the TLS handshake strictly. An untrusted root, an incomplete chain, a hostname mismatch or a self-signed certificate fails the handshake and is recorded on the result's TLS details - what a certificate check wants. Default false keeps the relaxed handshake an uptime check wants.")] bool strictTls = false,
        CancellationToken ct = default) {

        if (!TryGetToken(out var token, out var noToken)) return Err(noToken);
        if (string.IsNullOrWhiteSpace(url)) return Err("Provide a 'url' to check (e.g. example.com).");

        if (!await InFlight.WaitAsync(0, ct)) return Err("HostTracker is handling too many checks right now. Please retry in a few seconds.");
        try {
            var body = Body(("url", url.Trim()), ("type", string.IsNullOrWhiteSpace(type) ? null : type.Trim()),
                            ("pools", Split(pools)), ("deviceEmulation", string.IsNullOrWhiteSpace(device) ? null : device.Trim()),
                            ("strictTls", strictTls ? (object?)true : null));

            var created = await Api.SendAsync(token, HttpMethod.Post, "/check", null, body, ct: ct).ConfigureAwait(false);
            if (!created.Ok) return Err(ToolText.ErrorText(created.Error!));

            var dbId = created.Body.TryGetProperty("dbId", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
            var id = created.Body.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            var resultUrl = created.Body.TryGetProperty("resultUrl", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (id is null || dbId == 0) return Err("HostTracker accepted the check but returned no id to poll. Please retry.");

            var firstDelay = created.Body.TryGetProperty("retryAfter", out var ra) && ra.ValueKind == JsonValueKind.Number
                ? ra.GetInt32() : (created.RetryAfterSeconds ?? 5);

            // Poll until done or the WALL-CLOCK budget elapses, pacing off the retryAfter each answer carries.
            // The budget is measured with a Stopwatch (not a naive countdown) so a slow-but-responsive API cannot
            // hold the MCP connection far past MaxPollMs.
            var stopwatch = Stopwatch.StartNew();
            var delayMs = Clamp(firstDelay);
            CheckResult? last = null;
            while (!ct.IsCancellationRequested) {
                if (stopwatch.ElapsedMilliseconds + delayMs >= MaxPollMs) break;
                try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { break; }

                var polled = await Api.SendAsync(token, HttpMethod.Get, $"/check/{dbId}/{id}", ct: ct).ConfigureAwait(false);
                if (!polled.Ok) return Err(ToolText.ErrorText(polled.Error!));
                last = Deserialize(polled.Body);
                if (last is { IsFinal: true }) break;
                delayMs = Clamp(last?.RetryAfter ?? polled.RetryAfterSeconds ?? 5);
            }

            return Ok(FormatResult(url, type, dbId, id, last, complete: last is { IsFinal: true }, resultUrl));
        } finally {
            InFlight.Release();
        }
    }

    [McpServerTool(Name = "get_check_result", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Fetch the current results of a previously started instant check by its dbId and id (as returned by " +
        "run_instant_check). Requires a token with the 'check' scope.")]
    public async Task<CallToolResult> GetCheckResult(
        [Description("The dbId from run_instant_check.")] int dbId,
        [Description("The check id (a GUID) from run_instant_check.")] string id,
        CancellationToken ct = default) {

        if (!TryGetToken(out var token, out var noToken)) return Err(noToken);
        if (!Guid.TryParse(id, out var guid)) return Err("The 'id' must be the GUID returned by run_instant_check.");

        var polled = await Api.SendAsync(token, HttpMethod.Get, $"/check/{dbId}/{guid}", ct: ct).ConfigureAwait(false);
        if (!polled.Ok) return Err(ToolText.ErrorText(polled.Error!));

        var result = Deserialize(polled.Body);
        return Ok(FormatResult(result?.Url, result?.Type, dbId, guid.ToString(), result, complete: result is { IsFinal: true }, null));
    }

    private static int Clamp(int seconds) => Math.Clamp(seconds * 1000, MinPollMs, MaxPollIntervalMs);

    private static CheckResult? Deserialize(JsonElement body) {
        try { return body.ValueKind == JsonValueKind.Object ? body.Deserialize<CheckResult>(ReadOpts) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Render one instant check. Per-location output is target-controlled: it goes inside a nonce'd
    /// untrusted fence and every field is length-capped, so a malicious checked site cannot inject instructions the
    /// calling agent acts on. Do NOT remove the nonce or the caps.</summary>
    internal static string FormatResult(string? url, string? checkType, int dbId, string id, CheckResult? result, bool complete, string? resultUrl) {
        var sb = new StringBuilder();
        sb.Append(complete ? "Instant check complete" : "Instant check still running (partial results)");
        if (!string.IsNullOrEmpty(url)) sb.Append(" for ").Append(ToolText.Sanitize(url, 200));
        if (!string.IsNullOrEmpty(checkType)) sb.Append(" [").Append(ToolText.Sanitize(checkType, 40)).Append(']');
        sb.Append(".\n");
        sb.Append("Full result page: ")
          .Append(string.IsNullOrEmpty(resultUrl) ? $"https://www.host-tracker.com/en/ic/{dbId}/{id}" : ToolText.Sanitize(resultUrl, 300))
          .Append('\n');

        var events = result?.Events ?? new List<CheckEvent>();
        sb.Append("Locations reporting: ").Append(events.Count).Append('\n');
        if (events.Count == 0) {
            sb.Append(complete ? "No locations returned data.\n" : "No locations have reported yet.\n");
        } else {
            var inner = new StringBuilder();
            foreach (var location in events) {
                inner.Append("• ").Append(ToolText.Sanitize(location.Location, 80) is { Length: > 0 } name ? name : "unknown location");
                if (location.Provider is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } provider)
                    inner.Append(" (").Append(ToolText.Sanitize(provider.ToString(), 60)).Append(')');
                inner.Append(": ");
                if (location.Error is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } error)
                    inner.Append("ERROR ").Append(ToolText.Sanitize(error.GetRawText(), ToolText.MaxUntrustedChars));
                else if (location.Metrics is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } metrics)
                    inner.Append(ToolText.Sanitize(metrics.GetRawText(), ToolText.MaxUntrustedChars));
                else
                    inner.Append("ok");
                inner.Append('\n');
            }
            sb.Append(ToolText.Fence(inner.ToString()));
        }
        if (!complete)
            sb.Append("Call get_check_result with dbId=").Append(dbId).Append(" and id=").Append(id).Append(" to poll for the rest.\n");
        return sb.ToString();
    }
}

/// <summary>
/// In-process cache of the live check-type + device catalogues (anonymous <c>GET /check/type</c> and
/// <c>GET /check/device</c>), so the tool description of what can be checked can no longer drift from what the API
/// accepts. A static line is served when the catalogue is unreachable.
/// </summary>
public sealed class CheckCatalog {
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private const string AliasNote = "'pageSpeed' is accepted as an alias for 'waterfall' on both the instant-check " +
                                     "and the monitor doors; the API always reports back the canonical 'waterfall'.";

    internal const string Fallback =
        "HostTracker instant-check types (live catalogue unavailable; default http): http (HTTP/S availability), " +
        "ping (ICMP), port (TCP port), trace (traceroute), dns (DNS query), dnsbl (DNS blacklist), whois (domain " +
        "WHOIS), webRisk (Google Web Risk), crawl (site crawl), waterfall (page-load waterfall). " + AliasNote;

    private readonly V2ApiClient api;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;

    public CheckCatalog(V2ApiClient api) => this.api = api;

    public async Task<string> GetAsync(string? token, CancellationToken ct = default) {
        if (cached != null && DateTimeOffset.UtcNow - cachedAt < Ttl) return cached;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (cached != null && DateTimeOffset.UtcNow - cachedAt < Ttl) return cached;
            var text = await BuildAsync(token ?? "", ct).ConfigureAwait(false);
            if (text != null) { cached = text; cachedAt = DateTimeOffset.UtcNow; }
            return text ?? cached ?? Fallback;
        } finally {
            gate.Release();
        }
    }

    private async Task<string?> BuildAsync(string token, CancellationToken ct) {
        var types = await api.GetPageAsync(token, "/check/type", new Q().Set("limit", 100), ct).ConfigureAwait(false);
        if (!types.Ok || types.Data.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder("HostTracker instant-check types (the 'type' argument of run_instant_check; default http):\n");
        foreach (var row in types.Data.EnumerateArray()) {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var type = Text(row, "type");
            if (type.Length == 0) continue;
            sb.Append("• ").Append(type);
            var label = Text(row, "label");
            if (label.Length > 0) sb.Append(" - ").Append(label);
            var description = Text(row, "description");
            if (description.Length > 0) sb.Append(": ").Append(description);
            if (row.TryGetProperty("experimental", out var experimental) && experimental.ValueKind == JsonValueKind.True)
                sb.Append(" [experimental]");
            if (string.Equals(type, "waterfall", StringComparison.OrdinalIgnoreCase)) sb.Append(" - ").Append(AliasNote);
            sb.Append('\n');
        }

        var devices = await api.GetPageAsync(token, "/check/device", new Q().Set("limit", 100), ct).ConfigureAwait(false);
        if (devices.Ok && devices.Data.ValueKind == JsonValueKind.Array) {
            var names = devices.Data.EnumerateArray()
                .Where(row => row.ValueKind == JsonValueKind.Object)
                .Select(row => Text(row, "device"))
                .Where(name => name.Length > 0)
                .ToList();
            if (names.Count > 0)
                sb.Append("Device-emulation profiles for waterfall checks (the 'device' argument): ")
                  .Append(string.Join(", ", names)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? ToolText.Sanitize(value.GetString(), 300) : "";
}
