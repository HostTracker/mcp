using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HostTracker.Mcp;

/// <summary>
/// On-demand uptime reports. Generation is asynchronous: the door answers with a job id, the job produces a report
/// resource, and the report carries a content url. Scope <c>monitor:read</c>; the catalogue is anonymous.
/// </summary>
[McpServerToolType]
public sealed class ReportTools : V2ToolBase {
    public ReportTools(V2ApiClient api, IHttpContextAccessor httpContext) : base(api, httpContext) { }

    [McpServerTool(Name = "generate_report", Destructive = false, ReadOnly = false, OpenWorld = false)]
    [Description("Request an uptime report over a set of monitors and a time range. Scope 'monitor:read'. Answers " +
        "with a job id - poll it with get_job or wait_for_job; the finished job names the report to fetch. Times are " +
        "Unix seconds.")]
    public Task<CallToolResult> GenerateReport(
        [Description("Comma-separated monitor ids the report covers.")] string monitorIds,
        [Description("Range start, Unix seconds.")] long? from = null,
        [Description("Range end, Unix seconds.")] long? to = null,
        [Description("Output format: pdf, csv, xml or html.")] string? format = null,
        [Description("Comma-separated sections: state, stats, outages, incidents, log.")] string? sections = null,
        [Description("IANA timezone the report is rendered in.")] string? timezone = null,
        [Description("Language code for the report text, e.g. 'en'.")] string? language = null,
        [Description("Reuse the same key to make a retry replay instead of generating twice.")] string? idempotencyKey = null,
        CancellationToken ct = default) {

        var monitors = Split(monitorIds);
        if (monitors is null) return Task.FromResult(Err("Provide at least one monitor id in 'monitorIds'."));

        var body = Body(("monitorIds", monitors), ("from", from), ("to", to), ("format", format),
                        ("sections", Split(sections)), ("timezone", timezone), ("language", language));
        return CallAsync(HttpMethod.Post, "/monitor/report", null, body,
            heading: "Report generation accepted - poll the job with get_job:", idempotencyKey: IdempotencyKey(idempotencyKey), ct: ct);
    }

    [McpServerTool(Name = "list_report_types", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the report types, output formats, sections and schedules available. Anonymous.")]
    public Task<CallToolResult> ListReportTypes(CancellationToken ct = default) =>
        ListAsync("/report/type", new Q().Set("limit", MaxLimit), "list_report_types", "Report types:", ct);
}
