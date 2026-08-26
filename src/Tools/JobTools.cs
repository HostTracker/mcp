using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// Jobs - the asynchronous half of the API: bulk monitor and contact operations and report generation answer with a
/// job id. The scope a job needs is the one its creating operation needed. Lifecycle states are queued, running,
/// succeeded, partial, failed, cancelled and interrupted; only the first six of those are terminal apart from
/// <c>interrupted</c>, which means the server running it died and it can be resumed. A <c>partial</c> job is a
/// success with some failed items, not an error.
/// </summary>
[McpServerToolType]
public sealed class JobTools : V2ToolBase {
    private const int MaxWaitMs = 30000;      // wall-clock budget of one wait_for_job call
    private const int MinPollMs = 2000;
    private const int MaxPollMs = 10000;

    private static readonly string[] Terminal = ["succeeded", "partial", "failed", "cancelled"];

    public JobTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "get_job", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Poll one asynchronous operation: its state, progress and per-item results. A failed job still " +
        "answers 200 with state='failed'; each failed item carries its own error.")]
    public Task<CallToolResult> GetJob(
        [Description("The job id.")] string id,
        [Description("How many per-item results to include, 1-50 (default 20).")] int? limit = null,
        [Description("Opaque cursor to continue the item list.")] string? cursor = null,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/job/{Uri.EscapeDataString(id)}",
            new Q().Set("limit", CapLimit(limit)).Set("cursor", cursor), heading: "Job:", ct: ct);

    [McpServerTool(Name = "wait_for_job", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Poll a job until it reaches a terminal state or ~30 seconds elapse, then return it. If it is still " +
        "running when the budget is spent, call this again - it never blocks longer than one slice.")]
    public async Task<CallToolResult> WaitForJob(
        [Description("The job id.")] string id,
        CancellationToken ct = default) {

        if (!TryGetToken(out var token, out var noToken)) return Err(noToken);

        var stopwatch = Stopwatch.StartNew();
        var delayMs = MinPollMs;
        V2Response? last = null;
        while (!ct.IsCancellationRequested) {
            last = await Api.SendAsync(token, HttpMethod.Get, $"/job/{Uri.EscapeDataString(id)}", ct: ct).ConfigureAwait(false);
            if (!last.Ok) return Err(ToolText.ErrorText(last.Error!));

            var state = last.Body.ValueKind == JsonValueKind.Object && last.Body.TryGetProperty("state", out var s)
                        && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (state != null && Terminal.Contains(state, StringComparer.OrdinalIgnoreCase))
                return Ok("Job finished (" + state + "):\n" + ToolText.Render(last.Body));

            delayMs = Math.Clamp((last.RetryAfterSeconds ?? 3) * 1000, MinPollMs, MaxPollMs);
            if (stopwatch.ElapsedMilliseconds + delayMs >= MaxWaitMs) break;
            try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { break; }
        }

        var rendered = last is { Ok: true } ? ToolText.Render(last.Body) : "";
        return Ok("Job still running after ~30s - call wait_for_job again with the same id.\n" + rendered);
    }

    [McpServerTool(Name = "cancel_job", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Cancel a queued or running asynchronous operation. Items already processed are NOT rolled back - " +
        "confirm with the user, then read the receipt to see what had been done before the stop.")]
    public Task<CallToolResult> CancelJob(
        [Description("The job id.")] string id,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/job/{Uri.EscapeDataString(id)}/cancel", heading: "Cancellation requested:", ct: ct);

    [McpServerTool(Name = "resume_job", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Continue a job whose state is 'interrupted' (the server running it died). Items already concluded " +
        "are skipped. A job that is not interrupted, or whose kind cannot be resumed, is refused.")]
    public Task<CallToolResult> ResumeJob(
        [Description("The job id.")] string id,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/job/{Uri.EscapeDataString(id)}/resume", heading: "Resumed:", ct: ct);
}
