using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// The guarded generic door onto the rest of the v2 surface: the curated tools cover the hot workflows, these two
/// cover the long tail without shipping a tool per endpoint.
///
/// <c>api_request</c> is NOT a proxy for arbitrary URLs. Three gates run before anything is dialed: the method must
/// be a real HTTP method, the (method, path) pair must exist in the OPERATION CATALOGUE (live OpenAPI document, or
/// the compiled-in list from the committed spec), and it must pass <see cref="SensitivePolicy"/>. The caller's own
/// token still bounds everything it can reach; these gates only narrow that further.
/// </summary>
[McpServerToolType]
public sealed class ApiTools : V2ToolBase {
    private static readonly string[] AllowedMethods = ["GET", "POST", "PATCH", "PUT", "DELETE"];

    private readonly OperationCatalog catalog;

    public ApiTools(V2ApiClient api, IHttpContextAccessor httpContext, OperationCatalog catalog)
        : base(api, httpContext) => this.catalog = catalog;

    [McpServerTool(Name = "describe_api", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Describe the HostTracker v2 REST operations available through api_request: their paths, what each " +
        "one does, its query parameters and its request-body members. Call it with a search term (e.g. 'contact', " +
        "'/webhook', 'statuspage') before using api_request, so the call is built from the real contract rather than " +
        "guessed. All timestamps in this API are Unix seconds and all ids are opaque strings.")]
    public async Task<CallToolResult> DescribeApi(
        [Description("Path fragment or operation-id fragment to search for, e.g. '/monitor', 'incident', 'createWebhook'. Omit to list every path.")] string? search = null,
        CancellationToken ct = default) {

        var operations = await catalog.GetAsync(ct).ConfigureAwait(false);
        var matches = OperationCatalog.Find(operations, search).ToList();
        if (matches.Count == 0)
            return Err($"No v2 operation matches '{ToolText.Sanitize(search, 80)}'. Call describe_api with no argument to see every path.");

        var sb = new StringBuilder();
        sb.Append("HostTracker API v2 - ").Append(matches.Count).Append(" operation(s)");
        if (!string.IsNullOrWhiteSpace(search)) sb.Append(" matching '").Append(ToolText.Sanitize(search, 80)).Append('\'');
        sb.Append(" (catalogue source: ").Append(catalog.Source).Append(").\n");

        // A broad search would otherwise dump the whole surface; list paths only past a readable size.
        var terse = matches.Count > 40;
        foreach (var operation in matches.Take(200)) {
            sb.Append(operation.Method).Append(' ').Append(operation.Path);
            if (!terse && operation.Summary.Length > 0) sb.Append(" - ").Append(ToolText.Sanitize(operation.Summary, 200));
            sb.Append('\n');
            if (terse) continue;
            if (operation.Parameters.Count > 0) sb.Append("    params: ").Append(string.Join(", ", operation.Parameters)).Append('\n');
            if (operation.BodyMembers.Count > 0) sb.Append("    body: ").Append(string.Join(", ", operation.BodyMembers)).Append('\n');
        }
        if (matches.Count > 200) sb.Append("... narrow the search to see the rest.\n");
        if (terse) sb.Append("Search more narrowly to see summaries, parameters and body members.\n");
        sb.Append("'*' marks a required parameter, '@path' a path segment. Send a call with api_request.\n");
        return Ok(sb.ToString());
    }

    [McpServerTool(Name = "api_request", Destructive = true, ReadOnly = false, OpenWorld = false)]
    [Description("Call any HostTracker v2 REST operation that no curated tool covers. Look the operation up with " +
        "describe_api first - the method and path must match a real operation or the call is refused. The caller's " +
        "token supplies authorisation and its scopes still apply. Writes under /account are refused outright by " +
        "this server's safety policy. A DELETE, and any bulk write that is not a /validate dry-run, is refused " +
        "unless confirmed=true - confirm with the user first, then retry with confirmed=true.")]
    public async Task<CallToolResult> ApiRequest(
        [Description("HTTP method: GET, POST, PATCH, PUT or DELETE.")] string method,
        [Description("The v2 path with its ids filled in, e.g. '/monitor/9f2.../incident'. No host, no version prefix.")] string path,
        [Description("Query string, e.g. 'limit=10&state=down'. May also be a JSON object of parameters.")] string? query = null,
        [Description("Request body as a JSON object, for POST/PATCH/PUT.")] string? bodyJson = null,
        [Description("Idempotency key; required by the bulk and status-page-incident doors, optional elsewhere.")] string? idempotencyKey = null,
        [Description("Required true for a DELETE or a non-validate bulk write, after the user has confirmed.")] bool confirmed = false,
        CancellationToken ct = default) {

        if (!TryGetToken(out var token, out var noToken)) return Err(noToken);

        var normalizedMethod = (method ?? "").Trim().ToUpperInvariant();
        if (!AllowedMethods.Contains(normalizedMethod))
            return Err($"'{ToolText.Sanitize(method, 20)}' is not an allowed method. Use one of: {string.Join(", ", AllowedMethods)}.");

        // Case is PRESERVED on the wire (ids in path segments are case-sensitive); the policy and the catalogue
        // both compare case-insensitively themselves.
        var normalizedPath = SensitivePolicy.Trim(path);
        if (normalizedPath.Length == 0) return Err("Provide a v2 path, e.g. '/monitor'. Use describe_api to find one.");

        // Gate 1: the safety policy (deny rules win over everything, including a token that would be allowed to).
        if (SensitivePolicy.IsBlocked(normalizedMethod, normalizedPath, out var blocked)) return Err(blocked);

        // Gate 1b: destructive calls need the explicit confirmed flag - the generic door must not be a way
        // around the curated tools' two-step delete/bulk guards. /validate dry-runs stay free.
        if (!confirmed && RequiresConfirmation(normalizedMethod, normalizedPath))
            return Err($"{normalizedMethod} {normalizedPath} is destructive and was NOT executed. Show the user exactly "
                     + "what it will do and ask; retry with confirmed=true only after they agree.");

        // Gate 2: the operation must exist. This is what stops a fabricated URL from being forwarded.
        var operations = await catalog.GetAsync(ct).ConfigureAwait(false);
        var operation = OperationCatalog.Match(operations, normalizedMethod, normalizedPath);
        if (operation is null) {
            var sameShape = operations.Where(candidate =>
                OperationCatalog.Match([candidate], candidate.Method, normalizedPath) is not null).Select(o => o.Method).ToList();
            return Err(sameShape.Count > 0
                ? $"{normalizedMethod} {normalizedPath} is not a HostTracker v2 operation, but that path answers: {string.Join(", ", sameShape)}."
                : $"{normalizedMethod} {normalizedPath} is not a HostTracker v2 operation. Call describe_api to find the right path.");
        }

        if (!TryParseQuery(query, out var parsedQuery, out var badQuery)) return Err(badQuery);
        if (!TryJson(bodyJson, nameof(bodyJson), out var body, out var badBody)) return Err(badBody);
        if (body.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
            return Err("'bodyJson' must be a JSON object (or array where the endpoint takes one).");

        var response = await Api.SendAsync(token, new HttpMethod(normalizedMethod), normalizedPath,
            parsedQuery, body.ValueKind == JsonValueKind.Undefined ? null : body, idempotencyKey, ct).ConfigureAwait(false);
        if (!response.Ok) return Err(ToolText.ErrorText(response.Error!));

        var text = $"{normalizedMethod} {normalizedPath} -> {response.StatusCode} ({operation.OperationId})\n"
                 + (response.Body.ValueKind == JsonValueKind.Undefined ? "(no body)\n" : ToolText.Render(response.Body));
        if (response.RetryAfterSeconds is { } seconds) text += $"Retry-After: {seconds} seconds.\n";
        return Ok(text);
    }

    /// <summary>DELETE always; POST/PATCH/PUT onto a bulk door too - except its /validate dry-run twin.</summary>
    internal static bool RequiresConfirmation(string method, string path) {
        if (method == "DELETE") return true;
        if (method is not ("POST" or "PATCH" or "PUT")) return false;
        var p = path.TrimEnd('/');
        return p.Contains("/bulk", StringComparison.OrdinalIgnoreCase)
            && !p.EndsWith("/validate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Accept either a query string ('a=b&amp;c=d') or a flat JSON object of parameters.</summary>
    internal static bool TryParseQuery(string? raw, out Q query, out string error) {
        query = new Q(); error = "";
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('{')) {
            try {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Object) { error = "'query' JSON must be an object."; return false; }
                foreach (var member in document.RootElement.EnumerateObject()) {
                    if (member.Value.ValueKind == JsonValueKind.Array) {
                        foreach (var item in member.Value.EnumerateArray()) query.Set(member.Name, Scalar(item));
                    } else {
                        query.Set(member.Name, Scalar(member.Value));
                    }
                }
                return true;
            } catch (JsonException e) {
                error = "The 'query' argument is not valid JSON: " + ToolText.Sanitize(e.Message, 200);
                return false;
            }
        }

        foreach (var pair in trimmed.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var split = pair.Split('=', 2);
            // Added directly, not via Set: a PRESENT-but-empty value is meaningful in v2 ('expand=' = leanest row).
            query.Add(new(Uri.UnescapeDataString(split[0]), split.Length > 1 ? Uri.UnescapeDataString(split[1]) : ""));
        }
        return true;
    }

    private static string Scalar(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
}
