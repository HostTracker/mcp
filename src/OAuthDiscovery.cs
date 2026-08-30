using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace HostTracker.Mcp;

/// <summary>
/// The OAuth discovery half of this server: the 401 challenge on `/mcp`, the two Protected Resource
/// Metadata (RFC 9728) documents, and the in-band re-auth hint attached when an upstream call answers 401.
///
/// <para><b>This server is NOT a token validator - every check here is presence-only.</b> It asks only "is
/// there an Authorization header at all" / "did the API answer 401 just now"; it never inspects, decodes or
/// verifies the token itself. The HostTracker API remains the sole validator of the caller's credential,
/// exactly as in plain bearer-token use.</para>
///
/// <para><b>The dark switch:</b> an empty/unset <c>mcp:oauthIssuer</c> disables every piece of this - no 401
/// challenge on <c>/mcp</c>, both well-known routes answer 404, and no `_meta` hint is attached - which is
/// byte-identical to plain bearer-token behaviour (anonymous requests reach the tools and get the existing
/// in-tool "mint a token" guidance). Enabling OAuth is that one config key, nothing else.</para>
/// </summary>
internal static class OAuthDiscovery {

    /// <summary>
/// The OAuth scope vocabulary offered to MCP clients: every mintable scope leaf + family umbrella of the
/// HostTracker API EXCEPT the <c>account</c> family - a connected assistant must never be able to change
/// account settings, so those scopes are simply not part of the OAuth vocabulary.
///
/// <para>Hardcoded: this server references no other HostTracker code by design, so it cannot read the API's
/// scope registry directly - a scope added, renamed or retired there must be mirrored here in the same
/// change, or this list silently drifts from what tokens the authorization server can actually grant.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> ScopesSupported = new[] {
        "monitor:read", "monitor:write", "monitor",
        "contact:read", "contact:write", "contact",
        "subs:read", "subs",
        "webhook:read", "webhook:write", "webhook",
        "statuspage:read", "statuspage:write", "statuspage",
        "check:read", "check:write", "check",
    };

    /// <summary>Reads + normalizes the two OAuth config keys once. Shared by Program.cs (the challenge
    /// middleware + well-known routes) and <see cref="V2ApiClient"/> (the upstream-401 `_meta` hint) so the
    /// dark-switch parsing lives in exactly one place.</summary>
    public static (string? Issuer, string PublicBase) FromConfig(IConfiguration configuration) {
        var issuer = configuration.GetValue<string>("mcp:oauthIssuer")?.Trim();
        var publicBase = (configuration.GetValue<string>("mcp:publicBase") ?? "https://mcp.host-tracker.com").TrimEnd('/');
        return (string.IsNullOrEmpty(issuer) ? null : issuer, publicBase);
    }

    /// <summary>True when the OAuth surface is turned on at all (a non-empty issuer configured).</summary>
    public static bool Enabled(string? oauthIssuer) => !string.IsNullOrEmpty(oauthIssuer);

    /// <summary>
    /// True when the incoming request must be turned back with a 401 challenge: OAuth is on, the request
    /// targets <c>/mcp</c>, and it carries NO Authorization header at all. Presence-only - a garbage or expired
    /// token still passes this check (the API answers its own 401 on the forwarded call).
    /// </summary>
    public static bool NeedsChallenge(string? oauthIssuer, PathString path, string? authorizationHeader) =>
        Enabled(oauthIssuer) && path.StartsWithSegments("/mcp") && string.IsNullOrEmpty(authorizationHeader);

    /// <summary>The <c>WWW-Authenticate</c> header value both the 401 challenge and the upstream-401 `_meta`
    /// hint carry - always the BARE well-known path (no <c>/mcp</c> suffix), per plan §9.5/§1 point 1.</summary>
    public static string ChallengeHeaderValue(string publicBase) =>
        $"Bearer resource_metadata=\"{publicBase.TrimEnd('/')}/.well-known/oauth-protected-resource\"";

    /// <summary>Writes the 401 response: the challenge header + a tiny JSON body. Short-circuits BEFORE
    /// `MapMcp` - the caller must not call `next()` afterwards.</summary>
    public static Task WriteChallengeAsync(HttpContext context, string publicBase) {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = ChallengeHeaderValue(publicBase);
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new {
            error = "unauthorized",
            error_description = "Authorization required. See the WWW-Authenticate header for the OAuth resource metadata.",
        }));
    }

    /// <summary>The RFC 9728 Protected Resource Metadata document, or <see langword="null"/> while
    /// <c>mcp:oauthIssuer</c> is unset (the well-known routes answer 404 - the dark switch, plan §9.2).</summary>
    public static object? ProtectedResourceMetadata(string? oauthIssuer, string publicBase) {
        if (!Enabled(oauthIssuer)) return null;
        return new {
            resource = publicBase.TrimEnd('/') + "/mcp",
            authorization_servers = new[] { oauthIssuer },
            scopes_supported = ScopesSupported,
            bearer_methods_supported = new[] { "header" },
        };
    }

    /// <summary>Serves one of the two PRM routes (bare + <c>/mcp</c>-suffixed - clients probe the suffixed
    /// variant first, plan §1 point 1): the same document, cache-headered ~1 h, 404 while OAuth is off. Kept
    /// as a plain <see cref="HttpContext"/> writer (not a minimal-API <c>Results.*</c> return) so it is
    /// trivially unit-testable against a bare <see cref="Microsoft.AspNetCore.Http.DefaultHttpContext"/>.</summary>
    public static Task WriteProtectedResourceMetadataAsync(HttpContext context, string? oauthIssuer, string publicBase) {
        var document = ProtectedResourceMetadata(oauthIssuer, publicBase);
        if (document is null) {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        context.Response.Headers.CacheControl = "public, max-age=3600";
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(document));
    }

    /// <summary>
    /// The in-band re-auth hint for a tool call that failed because the API answered 401 to the caller's forwarded
    /// token (expired/invalid). Attached to <c>CallToolResult.Meta["mcp/www_authenticate"]</c> - the same
    /// challenge string the initial discovery 401 carries, which is what makes ChatGPT's re-auth UI fire
    /// (plan §1 point 9, §3.6 point 3) and is standard practice under the 2025-11-25 MCP revision.
    ///
    /// <para>SDK investigation (ModelContextProtocol.AspNetCore/Core 1.4.1, pinned - not upgraded): reflection +
    /// a live round-trip against the package confirmed <c>CallToolResult</c> (and every other RPC result type)
    /// inherits <c>ModelContextProtocol.Protocol.Result.Meta</c>, a plain public get/set
    /// <see cref="System.Text.Json.Nodes.JsonObject"/> - no `McpException` equivalent exists (it carries only
    /// the JSON-RPC `code`/`message`, no metadata bag), which is irrelevant here anyway since every tool result
    /// in this codebase already returns a `CallToolResult` with `IsError=true` rather than throwing. So the SDK
    /// natively supports exactly what this feature needs; nothing had to be approximated.</para>
    /// </summary>
    public static JsonObject WwwAuthenticateMeta(string publicBase) =>
        new() { ["mcp/www_authenticate"] = ChallengeHeaderValue(publicBase) };
}
