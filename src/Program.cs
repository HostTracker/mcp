using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using HostTracker.Mcp;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

// HostTracker MCP server: a thin, stateless bridge that exposes the HostTracker API v2 as MCP (Model Context
// Protocol) tools. It speaks MCP streamable-HTTP to AI clients and plain HTTPS to the API, forwarding the
// CALLER's own HostTracker API token per request. It holds no secrets, no database and no privileged access:
// every authentication, scope and quota decision is made by the API from that token.

var builder = WebApplication.CreateBuilder(args);

// Real client IP behind Cloudflare: the per-IP rate limit below must key off the caller, not Cloudflare's edge
// IP (otherwise every client behind one PoP shares a single bucket), so CF-Connecting-IP is honoured.
// Trusting that header is safe only when the origin is reachable exclusively through Cloudflare. If you run
// this server elsewhere, remove this block or set KnownProxies to your own reverse proxy.
builder.Services.Configure<ForwardedHeadersOptions>(o => {
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.ForwardedForHeaderName = "CF-Connecting-IP";
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();   // trust is established by the network layer in front of the server, not here
    o.KnownProxies.Clear();
});

// Tools read the caller's bearer token off the incoming request (transport header) - needs the accessor.
builder.Services.AddHttpContextAccessor();

// Typed HttpClient to the HostTracker API v2. Base address + timeout from config; NO default Authorization
// header - the token is set per request inside V2ApiClient, because a shared default header would cross user
// tokens under concurrency. Outbound connections and response size are bounded so a slow or huge upstream
// cannot exhaust the pool or memory.
var api2Base = builder.Configuration.GetValue<string>("mcp:api2BaseUrl") ?? "https://api2.host-tracker.com";
var api2TimeoutMs = builder.Configuration.GetValue("mcp:api2TimeoutMs", 20000);
builder.Services.AddHttpClient<V2ApiClient>(c => {
        c.BaseAddress = new Uri(api2Base.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMilliseconds(api2TimeoutMs);
        c.MaxResponseContentBufferSize = 8 * 1024 * 1024;   // 8 MiB - v2 pages are small; cap the trusted-peer read anyway
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler {
        MaxConnectionsPerServer = 64,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

// The operation catalogue behind api_request/describe_api: the live OpenAPI document (anonymous, ~3 MiB) with the
// compiled-in list from the committed spec as fallback. Its own client - a bigger buffer and a longer timeout than
// the per-call one, and no reason to share a connection budget with tool traffic.
builder.Services.AddHttpClient<OperationCatalog>(c => {
    c.BaseAddress = new Uri(api2Base.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 32 * 1024 * 1024;
});
builder.Services.AddHostedService<OperationCatalogWarmup>();

// The live check-type/device catalogue, cached in-process so the "what can be checked" answer can't drift from
// what the API accepts.
builder.Services.AddSingleton<CheckCatalog>();

// The MCP server + HTTP (streamable) transport + the tool types. ServerInfo set explicitly so serverInfo.version
// reads a clean "2.0.0" (the SDK otherwise derives the 4-part assembly version).
builder.Services.AddMcpServer(o => o.ServerInfo = new ModelContextProtocol.Protocol.Implementation {
        Name = "HostTracker.Mcp",
        Version = "2.0.0",
    })
    .WithHttpTransport()
    .WithTools<InstantCheckTools>()
    .WithTools<MonitorTools>()
    .WithTools<ResultTools>()
    .WithTools<MaintenanceTools>()
    .WithTools<ContactTools>()
    .WithTools<SubscriptionTools>()
    .WithTools<WebhookTools>()
    .WithTools<StatusPageTools>()
    .WithTools<ReportTools>()
    .WithTools<JobTools>()
    .WithTools<AccountTools>()
    .WithTools<LocationTools>()
    .WithTools<ApiTools>();

// Controllers only for the anonymous /stats health surface. MCP is mapped separately below.
builder.Services.AddControllers();

// Request-flood controls: body cap, connection ceiling and a per-IP rate limit keyed off the real client IP
// (forwarded headers above). A bot challenge in front of the server cannot do this job - MCP clients are
// programs, not browsers - so these in-app limits are the defense.
builder.WebHost.ConfigureKestrel(k => {
    k.Limits.MaxRequestBodySize = 256 * 1024;               // 256 KiB - JSON-RPC is small
    k.Limits.MaxConcurrentConnections = 500;                // ceiling so SSE/poll holds can't accumulate unbounded
    k.Limits.MaxConcurrentUpgradedConnections = 500;
});
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var permit = builder.Configuration.GetValue("mcp:rateLimitPerMinute", 60);
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => {
        // /stats (health) is exempt - an uptime monitor must not be starved by MCP traffic sharing its partition.
        // /.well-known (directory ownership proofs, static) likewise - a directory's verifier must never see a 429.
        if (ctx.Request.Path.StartsWithSegments("/stats") || ctx.Request.Path.StartsWithSegments("/.well-known"))
            return RateLimitPartition.GetNoLimiter("stats");
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",   // real client IP after ForwardedHeaders
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

var app = builder.Build();

// Apply forwarded headers FIRST so RemoteIpAddress is the real client for the rate limiter + logging.
app.UseForwardedHeaders();

// JSON-RPC-shaped error envelope for anything that falls through unhandled (malformed or empty JSON body,
// JSON-RPC batch arrays). The framework's default developer error page would echo request headers - including
// the bearer token - into the response body; this handler never renders request content or headers, so a
// token cannot leak. It returns a clean -32700 parse error (the common cause) with nothing beyond the
// envelope. Registered before MapMcp so it wraps the transport handler.
app.UseExceptionHandler(errApp => errApp.Run(async ctx => {
    ctx.Response.StatusCode = StatusCodes.Status200OK;   // JSON-RPC transports errors in the body, not the HTTP status
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new {
        jsonrpc = "2.0",
        id = (object?)null,
        error = new { code = -32700, message = "Parse error or malformed request." },
    }));
}));

app.UseRateLimiter();

// Anonymous /stats health surface (counts-only). MapMcp hosts the JSON-RPC endpoint at /mcp.
app.MapControllers();

// Glama connector ownership proof (glama.ai/mcp/connectors): a static maintainer record at the well-known
// path. Nothing here is secret or request-dependent.
app.MapGet("/.well-known/glama.json", () => Results.Content(
    "{\"$schema\":\"https://glama.ai/mcp/schemas/connector.json\",\"maintainers\":[{\"email\":\"danyloshashenko@gmail.com\"},{\"email\":\"danylo.shashenko@gmail.com\"}]}",
    "application/json"));
app.MapMcp("/mcp");

app.Run();
