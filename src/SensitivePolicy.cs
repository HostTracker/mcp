namespace HostTracker.Mcp;

/// <summary>
/// The MCP-side safety belt: operations an AI agent must never perform on a user's account, refused here
/// regardless of what the caller's token is allowed to do. Enforced in BOTH layers - the curated tool set simply
/// has no such tool, and <c>api_request</c> checks every call against these rules before dialing the API.
///
/// DEFAULT-CLOSED FOR THE FUTURE: <c>api_request</c> additionally requires the (method, path) pair to exist in the
/// operation catalogue, so a sensitive v2 endpoint added later is reachable only if it passes BOTH gates - add a
/// rule here when one lands. Payments, packages, passwords and token minting are not on the v2 surface at all
/// (first-party doors only), so this door physically cannot reach them.
/// </summary>
public static class SensitivePolicy {
    /// <summary>One deny rule. <see cref="Methods"/> empty = every method.</summary>
    private sealed record Rule(string PathPrefix, string[] Methods, string Reason);

    private static readonly Rule[] Rules = [
        new("/account", ["POST", "PUT", "PATCH", "DELETE"],
            "writing to the account profile (name, email, country, default pools) is an identity change a person " +
            "must make themselves - do it on the HostTracker profile page. Reading the account (GET /account, " +
            "/account/quota, /account/usage) is allowed."),
    ];

    private static readonly string[] KnownMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    /// <summary>True when the call is refused. <paramref name="reason"/> then names why, in agent-readable prose.</summary>
    public static bool IsBlocked(string? method, string? path, out string reason) {
        reason = "";
        var m = (method ?? "").Trim().ToUpperInvariant();
        var p = Normalize(path);

        if (m.Length == 0 || !KnownMethods.Contains(m)) {
            reason = $"'{Sanitize(method)}' is not an HTTP method this server accepts (GET, POST, PUT, PATCH, DELETE).";
            return true;
        }
        if (p.Length == 0) {
            reason = "A v2 path is required, e.g. /monitor.";
            return true;
        }

        foreach (var rule in Rules) {
            if (!IsUnder(p, rule.PathPrefix)) continue;
            if (rule.Methods.Length > 0 && !rule.Methods.Contains(m)) continue;
            reason = $"Blocked by the HostTracker MCP safety policy: {m} {p} is not permitted - {rule.Reason}";
            return true;
        }
        return false;
    }

    /// <summary>Query-stripped, single-leading-slash path with its CASE PRESERVED - this is what actually gets
    /// sent, because path segments carry ids (incident/result ids are case-sensitive opaque strings).</summary>
    internal static string Trim(string? path) {
        var p = (path ?? "").Trim();
        var cut = p.IndexOfAny(['?', '#']);
        if (cut >= 0) p = p[..cut];
        p = p.Replace('\\', '/').TrimEnd('/');
        if (p.Length == 0) return "";
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        return p;
    }

    /// <summary>Lower-cased form used for RULE MATCHING only, so a differently-cased '/Account' cannot slip past a
    /// rule. Never send this - use <see cref="Trim"/> for the wire.</summary>
    internal static string Normalize(string? path) => Trim(path).ToLowerInvariant();

    // "/account" matches "/account" and "/account/anything", never "/accountsomething".
    private static bool IsUnder(string path, string prefix) =>
        path.Equals(prefix, StringComparison.Ordinal) || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static string Sanitize(string? s) {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = new string(s.Where(c => !char.IsControl(c)).Take(20).ToArray());
        return clean;
    }
}
