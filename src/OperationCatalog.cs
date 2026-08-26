using System.Text.Json;

namespace HostTracker.Mcp;

/// <summary>One v2 operation as the generic door knows it: enough to validate a call and to describe it.</summary>
public sealed record V2Operation(
    string Method,
    string Path,
    string OperationId,
    string Summary,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> BodyMembers) {

    private readonly string[] segments = Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Does a concrete request path fill this template? Template segments (<c>{id}</c>) accept any single
    /// non-empty segment; everything else must match case-insensitively.</summary>
    public bool Matches(string[] requestSegments) {
        if (requestSegments.Length != segments.Length) return false;
        for (var i = 0; i < segments.Length; i++) {
            var template = segments[i];
            if (template.StartsWith('{') && template.EndsWith('}')) {
                if (requestSegments[i].Length == 0) return false;
                continue;
            }
            if (!template.Equals(requestSegments[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}

/// <summary>
/// The operation list <c>api_request</c> validates against, and <c>describe_api</c> reads from. Fetched from the
/// live <c>GET {api2}/openapi/v2.json</c> at startup and refreshed daily; when that is unreachable the compiled-in
/// list generated from the committed spec is used, so the generic door never degrades into "forward anything".
/// The list is an ALLOW-list of shapes, not an authorization decision - scopes stay the API's job and the
/// <see cref="SensitivePolicy"/> deny rules run on top.
/// </summary>
public sealed class OperationCatalog {
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    private readonly HttpClient http;
    private readonly ILogger<OperationCatalog> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    private IReadOnlyList<V2Operation> operations = Fallback();
    private string source = "compiled-in fallback (committed spec)";
    private DateTimeOffset fetchedAt = DateTimeOffset.MinValue;

    public OperationCatalog(HttpClient http, ILogger<OperationCatalog> logger) {
        this.http = http;
        this.logger = logger;
    }

    public string Source => source;
    public int Count => operations.Count;

    /// <summary>Current operation list, refreshing from the live document when the cache is stale. A failed refresh
    /// keeps whatever is cached - never an empty list.</summary>
    public async Task<IReadOnlyList<V2Operation>> GetAsync(CancellationToken ct = default) {
        if (DateTimeOffset.UtcNow - fetchedAt < RefreshAfter) return operations;
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return operations;   // a concurrent refresh is enough
        try {
            if (DateTimeOffset.UtcNow - fetchedAt < RefreshAfter) return operations;
            var fetched = await FetchAsync(ct).ConfigureAwait(false);
            if (fetched is { Count: > 0 }) {
                operations = fetched;
                source = "live GET /openapi/v2.json";
                fetchedAt = DateTimeOffset.UtcNow;
                logger.LogInformation("[OperationCatalog] loaded {Count} v2 operations from the live document", fetched.Count);
            } else {
                // Back off for the same window so a down API is not polled on every tool call.
                fetchedAt = DateTimeOffset.UtcNow;
            }
            return operations;
        } finally {
            gate.Release();
        }
    }

    /// <summary>Find the operation a concrete (method, path) resolves to, if any.</summary>
    public static V2Operation? Match(IReadOnlyList<V2Operation> ops, string method, string path) {
        var normalized = SensitivePolicy.Normalize(path);
        if (normalized.Length == 0) return null;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return ops.FirstOrDefault(op => op.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && op.Matches(segments));
    }

    /// <summary>Operations whose path or operationId contains the search text (for <c>describe_api</c>).</summary>
    public static IEnumerable<V2Operation> Find(IReadOnlyList<V2Operation> ops, string? search) {
        if (string.IsNullOrWhiteSpace(search)) return ops;
        var needle = search.Trim().TrimStart('/');
        return ops.Where(op =>
            op.Path.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            op.OperationId.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<V2Operation>?> FetchAsync(CancellationToken ct) {
        try {
            using var response = await http.GetAsync("openapi/v2.json", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                logger.LogWarning("[OperationCatalog] GET /openapi/v2.json -> {Status}; keeping {Source}", (int)response.StatusCode, source);
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Parse(document.RootElement);
        } catch (Exception e) {
            logger.LogWarning(e, "[OperationCatalog] could not load the live OpenAPI document; keeping {Source}", source);
            return null;
        }
    }

    internal static List<V2Operation> Parse(JsonElement root) {
        var result = new List<V2Operation>();
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object) return result;

        foreach (var path in paths.EnumerateObject()) {
            if (path.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var operation in path.Value.EnumerateObject()) {
                var method = operation.Name.ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE")) continue;

                var summary = operation.Value.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? "" : "";
                var operationId = operation.Value.TryGetProperty("operationId", out var o) && o.ValueKind == JsonValueKind.String
                    ? o.GetString() ?? "" : "";

                var parameters = new List<string>();
                if (operation.Value.TryGetProperty("parameters", out var pars) && pars.ValueKind == JsonValueKind.Array) {
                    foreach (var parameter in pars.EnumerateArray()) {
                        // $ref'd parameters carry no inline name; the fallback list has the full set, so skipping
                        // one here only makes describe_api terser, never the validation wronger (names are not
                        // what api_request validates on).
                        if (parameter.ValueKind != JsonValueKind.Object) continue;
                        if (!parameter.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                        var location = parameter.TryGetProperty("in", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                        if (location == "header") continue;
                        var required = parameter.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
                        parameters.Add(n.GetString() + (required ? "*" : "") + (location == "path" ? "@path" : ""));
                    }
                }

                var bodyMembers = new List<string>();
                if (operation.Value.TryGetProperty("requestBody", out var requestBody)
                    && requestBody.ValueKind == JsonValueKind.Object
                    && requestBody.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Object) {
                    foreach (var media in content.EnumerateObject()) {
                        if (!media.Value.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object) continue;
                        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) continue;
                        foreach (var member in properties.EnumerateObject())
                            if (!bodyMembers.Contains(member.Name)) bodyMembers.Add(member.Name);
                    }
                }

                result.Add(new V2Operation(method, path.Name, operationId, summary, parameters, bodyMembers));
            }
        }
        return result;
    }

    private static List<V2Operation> Fallback() =>
        V2OperationsFallback.All.Select(row => new V2Operation(
            row.Method, row.Path, row.OperationId, row.Summary,
            row.Parameters.Length == 0 ? [] : row.Parameters.Split(','),
            row.BodyMembers.Length == 0 ? [] : row.BodyMembers.Split(','))).ToList();
}

/// <summary>Warms the catalogue once at boot so the first <c>api_request</c> call does not pay the fetch. Failure
/// is non-fatal - the compiled-in list already answers.</summary>
public sealed class OperationCatalogWarmup : IHostedService {
    private readonly OperationCatalog catalog;
    public OperationCatalogWarmup(OperationCatalog catalog) => this.catalog = catalog;

    public Task StartAsync(CancellationToken ct) {
        _ = Task.Run(async () => { try { await catalog.GetAsync(CancellationToken.None); } catch { /* fallback stands */ } }, CancellationToken.None);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
