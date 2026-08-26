using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HostTracker.Mcp;

// Typed thin client over the HostTracker API v2 REST surface. Hand-rolled and dependency-free (it references no
// other HostTracker code). Responses are surfaced as raw JsonElement instead of ~180 generated DTOs: the tool layer
// renders them generically, so an additive v2 change (new response member) needs no code change here.
// TIMESTAMPS ARE UNIX SECONDS in both directions - v2's contract, no DateTime anywhere in this client.

/// <summary>One entry of an RFC 9457 problem document's <c>errors[]</c>. Members beyond the fixed four are
/// per-code remediation fields (reason/allowed/min/max/didYouMean/...); they are kept as raw JSON text so no
/// per-code schema has to be mirrored here.</summary>
public sealed record V2ErrorItem(string? Pointer, string? Parameter, string? Detail);

/// <summary>An API v2 failure, parsed from <c>application/problem+json</c>. <see cref="Code"/> is the machine
/// branch field (RFC 9457 extension); <see cref="RetryAfterSeconds"/> carries the <c>Retry-After</c> header
/// VALUE ONLY - this client never sleeps or retries on its own (the calling agent decides).</summary>
public sealed record V2Error(
    int Status,
    string Code,
    string? Title,
    string? Detail,
    string? RequestId,
    int? RetryAfterSeconds,
    IReadOnlyList<V2ErrorItem> Errors) {

    public static V2Error Transport(string detail) =>
        new(0, "transport_error", "Could not reach HostTracker", detail, null, null, []);
}

/// <summary>Outcome of one v2 call. <see cref="Body"/> is <c>Undefined</c> when the answer had no JSON body
/// (204, or a failure).</summary>
public sealed record V2Response(bool Ok, int StatusCode, JsonElement Body, V2Error? Error, int? RetryAfterSeconds);

/// <summary>The v2 list envelope, flattened. <c>hasMore</c> always equals <c>nextCursor != null</c>; cursors are
/// opaque and are never constructed or parsed here.</summary>
public sealed record V2Page(bool Ok, JsonElement Data, string? NextCursor, bool HasMore, JsonElement Summary, V2Error? Error);

/// <summary>Query-string accumulator. Array params bind as REPEATED names (OpenAPI <c>style:form,
/// explode:true</c>); the two comma-list exceptions (<c>expand</c>, <c>fields</c>, <c>metrics</c>) are written with
/// <see cref="Csv"/>, which sends the value verbatim.</summary>
public sealed class Q : List<KeyValuePair<string, string>> {
    public Q Set(string name, string? value) {
        if (!string.IsNullOrWhiteSpace(value)) Add(new(name, value.Trim()));
        return this;
    }
    public Q Set(string name, int? value) {
        if (value.HasValue) Add(new(name, value.Value.ToString(CultureInfo.InvariantCulture)));
        return this;
    }
    public Q Set(string name, double? value) {
        if (value.HasValue) Add(new(name, value.Value.ToString(CultureInfo.InvariantCulture)));
        return this;
    }
    public Q Set(string name, bool? value) {
        if (value.HasValue) Add(new(name, value.Value ? "true" : "false"));
        return this;
    }
    /// <summary>Comma-separated caller input -> one repeated param per token (the surface-wide array style).</summary>
    public Q List(string name, string? commaSeparated) {
        if (string.IsNullOrWhiteSpace(commaSeparated)) return this;
        foreach (var token in commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Add(new(name, token));
        return this;
    }
    /// <summary>Verbatim comma-list (expand/fields/metrics - <c>explode:false</c>).</summary>
    public Q Csv(string name, string? value) => Set(name, value);
}

/// <summary>
/// HTTP client for the HostTracker API v2. STATELESS w.r.t. auth: the caller's bearer token is a per-request argument set on each
/// <see cref="HttpRequestMessage"/>, NEVER on <c>DefaultRequestHeaders</c> - a shared default header races under
/// concurrency and would send one user's token with another user's request. All authentication, scope, quota and
/// ownership enforcement lives in the API, keyed off that token; this client adds no authorization logic of its own
/// (the one MCP-side gate is <see cref="SensitivePolicy"/>, which refuses calls before they are made).
/// </summary>
public sealed class V2ApiClient {
    private readonly HttpClient http;
    private readonly ILogger<V2ApiClient> logger;

    private static readonly JsonSerializerOptions WriteOpts = new() {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public V2ApiClient(HttpClient http, ILogger<V2ApiClient> logger) {
        this.http = http;
        this.logger = logger;
    }

    /// <summary>One v2 call. <paramref name="path"/> is a v2 path such as <c>/monitor/{id}</c> already resolved
    /// (v2 has NO version path prefix). The token is applied per-request.</summary>
    public async Task<V2Response> SendAsync(
        string bearerToken,
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string>>? query = null,
        object? jsonBody = null,
        string? idempotencyKey = null,
        CancellationToken ct = default) {

        try {
            using var request = new HttpRequestMessage(method, BuildUri(path, query));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);   // per-request, never shared
            if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            if (jsonBody != null) request.Content = JsonContent.Create(jsonBody, options: WriteOpts);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var retryAfter = ReadRetryAfter(response);
            var status = (int)response.StatusCode;

            var raw = response.Content.Headers.ContentLength == 0
                ? ""
                : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var body = ParseOrUndefined(raw);

            if (response.IsSuccessStatusCode) return new V2Response(true, status, body, null, retryAfter);
            return new V2Response(false, status, default, ReadProblem(status, body, raw, retryAfter), retryAfter);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception e) {
            // Log the exception OBJECT (type + stack) but never the request - HttpRequestMessage carries the token.
            logger.LogError(e, "[V2ApiClient] {Method} {Path} failed", method.Method, path);
            return new V2Response(false, 0, default, V2Error.Transport("HostTracker did not answer."), null);
        }
    }

    /// <summary>A paged GET, flattened to <see cref="V2Page"/> (data / nextCursor / hasMore / summary).</summary>
    public async Task<V2Page> GetPageAsync(
        string bearerToken, string path, IEnumerable<KeyValuePair<string, string>>? query = null, CancellationToken ct = default) {

        var response = await SendAsync(bearerToken, HttpMethod.Get, path, query, ct: ct).ConfigureAwait(false);
        if (!response.Ok) return new V2Page(false, default, null, false, default, response.Error);

        var data = response.Body.ValueKind == JsonValueKind.Object && response.Body.TryGetProperty("data", out var d) ? d : default;
        string? next = response.Body.ValueKind == JsonValueKind.Object
                       && response.Body.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;
        var summary = response.Body.ValueKind == JsonValueKind.Object && response.Body.TryGetProperty("summary", out var s) ? s : default;
        return new V2Page(true, data, next, next != null, summary, null);
    }

    private static string BuildUri(string path, IEnumerable<KeyValuePair<string, string>>? query) {
        var sb = new StringBuilder(path.TrimStart('/'));
        if (query != null) {
            var first = true;
            foreach (var (name, value) in query) {
                sb.Append(first ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
                first = false;
            }
        }
        return sb.ToString();
    }

    private static int? ReadRetryAfter(HttpResponseMessage response) {
        if (response.Headers.RetryAfter?.Delta is { } delta) return (int)delta.TotalSeconds;
        if (response.Headers.TryGetValues("Retry-After", out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return seconds;
        return null;
    }

    private static JsonElement ParseOrUndefined(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return default;
        try { return JsonDocument.Parse(raw).RootElement.Clone(); } catch (JsonException) { return default; }
    }

    /// <summary>Map an <c>application/problem+json</c> body to <see cref="V2Error"/>. A non-problem failure body
    /// (proxy HTML, empty 502) still yields a usable error keyed by status.</summary>
    internal static V2Error ReadProblem(int status, JsonElement body, string raw, int? retryAfter) {
        if (body.ValueKind != JsonValueKind.Object)
            return new V2Error(status, "http_" + status, null, Truncate(raw, 300), null, retryAfter, []);

        string? Str(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var items = new List<V2ErrorItem>();
        if (body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array) {
            foreach (var entry in errors.EnumerateArray()) {
                if (entry.ValueKind != JsonValueKind.Object) { items.Add(new V2ErrorItem(null, null, Truncate(entry.ToString(), 300))); continue; }
                string? pointer = entry.TryGetProperty("pointer", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                string? parameter = entry.TryGetProperty("parameter", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null;
                // Everything else in the entry is per-code remediation data (reason/allowed/required/granted/...) -
                // kept verbatim so refusals like 422 unknown_pool reach the agent with the offending token named.
                var rest = new StringBuilder();
                foreach (var member in entry.EnumerateObject()) {
                    if (member.NameEquals("pointer") || member.NameEquals("parameter")) continue;
                    if (rest.Length > 0) rest.Append(", ");
                    rest.Append(member.Name).Append('=').Append(Truncate(member.Value.ToString(), 200));
                }
                items.Add(new V2ErrorItem(pointer, parameter, rest.Length == 0 ? null : rest.ToString()));
            }
        }

        var code = Str("code") ?? "http_" + status;
        var declared = body.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : status;
        return new V2Error(declared, code, Str("title"), Str("detail"), Str("instance"), retryAfter, items);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
