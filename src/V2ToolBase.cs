using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace HostTracker.Mcp;

/// <summary>
/// Shared plumbing for every curated tool class: the caller's token (read from the TRANSPORT header, never from a
/// tool argument - arguments land in agent transcripts), the one-call helpers that turn a v2 answer into a tool
/// result, and the paging cap. Tool classes stay one line per tool on top of this.
/// </summary>
public abstract class V2ToolBase {
    /// <summary>Hard cap on a list tool's page size - an agent must not pull a whole account in one answer.</summary>
    public const int MaxLimit = 50;
    protected const int DefaultLimit = 20;

    protected readonly V2ApiClient Api;
    private readonly IHttpContextAccessor httpContext;

    protected V2ToolBase(V2ApiClient api, IHttpContextAccessor httpContext) {
        Api = api;
        this.httpContext = httpContext;
    }

    protected static CallToolResult Ok(string text) => new() { Content = [new TextContentBlock { Text = text }] };
    protected static CallToolResult Err(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };

    /// <summary>The token for the stdio transport, where no request header exists: set once at startup from the
    /// HT_TOKEN environment variable. Never consulted while serving HTTP, where every request carries its own token
    /// - a process-wide token must not leak across callers.</summary>
    internal static string? EnvironmentToken { get; set; }

    /// <summary>The caller's HostTracker API token: the incoming request's Authorization header on the HTTP
    /// transport, <see cref="EnvironmentToken"/> on stdio (no request in flight).</summary>
    protected bool TryGetToken(out string token, out string message) {
        token = ""; message = "";
        var request = httpContext.HttpContext?.Request;
        if (request is null) {
            token = EnvironmentToken?.Trim() ?? "";
            if (token.Length > 0) return true;
            message = "No HostTracker API token found. Set the HT_TOKEN environment variable for this MCP server. " +
                      ToolText.MintHint;
            return false;
        }
        var auth = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
            token = auth["Bearer ".Length..].Trim();
            if (token.Length > 0) return true;
        }
        message = "No HostTracker API token found. Configure this MCP server with an 'Authorization: Bearer <token>' " +
                  "header. " + ToolText.MintHint;
        return false;
    }

    protected static int CapLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    /// <summary>Issue one v2 call and render the answer. <paramref name="untrusted"/> fences the rendered body for
    /// endpoints whose payload carries third-party-controlled text.</summary>
    protected async Task<CallToolResult> CallAsync(
        HttpMethod method, string path, Q? query = null, object? body = null,
        string? heading = null, bool untrusted = false, string? idempotencyKey = null,
        CancellationToken ct = default) {

        if (!TryGetToken(out var token, out var noToken)) return Err(noToken);
        if (SensitivePolicy.IsBlocked(method.Method, path, out var blocked)) return Err(blocked);

        var response = await Api.SendAsync(token, method, path, query, body, idempotencyKey, ct).ConfigureAwait(false);
        if (!response.Ok) return Err(ToolText.ErrorText(response.Error!));

        var rendered = response.Body.ValueKind == JsonValueKind.Undefined ? "done.\n" : ToolText.Render(response.Body);
        if (untrusted) rendered = ToolText.Fence(rendered);
        var text = (heading is null ? "" : heading + "\n") + rendered;
        if (response.RetryAfterSeconds is { } seconds) text += $"Retry-After: {seconds} seconds (poll no sooner than that).\n";
        return Ok(text);
    }

    /// <summary>Issue one paged GET and render it, including the continuation cursor.</summary>
    protected async Task<CallToolResult> ListAsync(string path, Q query, string toolName, string heading, CancellationToken ct = default) {
        if (!TryGetToken(out var token, out var noToken)) return Err(noToken);

        var page = await Api.GetPageAsync(token, path, query, ct).ConfigureAwait(false);
        return page.Ok ? Ok(ToolText.RenderPage(heading, page, toolName)) : Err(ToolText.ErrorText(page.Error!));
    }

    /// <summary>Parse a caller-supplied JSON argument. Tools take JSON strings for the members v2 leaves open
    /// (per-type monitor settings, bulk item lists, filters) rather than mirroring every schema as arguments.</summary>
    protected static bool TryJson(string? raw, string argumentName, out JsonElement value, out string error) {
        value = default; error = "";
        if (string.IsNullOrWhiteSpace(raw)) return true;
        try {
            value = JsonDocument.Parse(raw).RootElement.Clone();
            return true;
        } catch (JsonException e) {
            error = $"The '{argumentName}' argument is not valid JSON: {ToolText.Sanitize(e.Message, 200)}";
            return false;
        }
    }

    /// <summary>Build a request body from named members, dropping the ones the caller left unset.</summary>
    /// <summary>
    /// The two-step guard every SINGLE-resource delete tool routes through (the bulk tools have their own
    /// validate-then-submit gate). Without <paramref name="confirmed"/> nothing is deleted: the tool answers with
    /// the live resource so the agent can show the user exactly what would be removed, and only a second call
    /// with <c>confirmed=true</c> performs the delete. Server-side on purpose - a model cannot skip it.
    /// </summary>
    protected async Task<CallToolResult> DeleteWithPreviewAsync(
        string what, string getPath, string deletePath, bool confirmed,
        string heading = "Deleted:", CancellationToken ct = default) {
        if (confirmed) return await CallAsync(HttpMethod.Delete, deletePath, heading: heading, ct: ct).ConfigureAwait(false);
        return await CallAsync(HttpMethod.Get, getPath,
            heading: $"NOT deleted. This is the {what} that confirmed=true would permanently remove - show it to "
                   + "the user and ask; call again with confirmed=true only after they agree:", ct: ct).ConfigureAwait(false);
    }

    protected static Dictionary<string, object?> Body(params (string Name, object? Value)[] members) {
        var body = new Dictionary<string, object?>();
        foreach (var (name, value) in members) {
            if (value is null) continue;
            if (value is JsonElement { ValueKind: JsonValueKind.Undefined }) continue;
            body[name] = value;
        }
        return body;
    }

    /// <summary>Comma-separated argument -> string list (the shape v2 array members take in JSON bodies).</summary>
    protected static List<string>? Split(string? commaSeparated) =>
        string.IsNullOrWhiteSpace(commaSeparated)
            ? null
            : commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>A fresh idempotency key for the doors that require one; the caller may pin their own so a retry
    /// replays instead of duplicating.</summary>
    protected static string IdempotencyKey(string? supplied) =>
        string.IsNullOrWhiteSpace(supplied) ? "mcp-" + Guid.NewGuid().ToString("N") : supplied.Trim();
}
