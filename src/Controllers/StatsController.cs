using Microsoft.AspNetCore.Mvc;

namespace HostTracker.Mcp.Controllers;

/// <summary>Anonymous liveness endpoint, counts only. It deliberately does not disclose the upstream API URL
/// or any per-request data.</summary>
[ApiController]
[Route("stats")]
public sealed class StatsController : ControllerBase {
    [HttpGet]
    public IActionResult Get() => Ok(new {
        service = "HostTracker.Mcp",
        status = "ok",
        mcpEndpoint = "/mcp",
    });
}
