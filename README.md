# HostTracker MCP server

[![validate](https://github.com/HostTracker/mcp/actions/workflows/validate.yml/badge.svg)](https://github.com/HostTracker/mcp/actions/workflows/validate.yml)
[![Smithery](https://img.shields.io/badge/Smithery-hosttracker%2Fhosttracker-ff6a1f)](https://smithery.ai/server/hosttracker/hosttracker)
[![Glama score](https://glama.ai/mcp/servers/HostTracker/mcp/badges/score.svg)](https://glama.ai/mcp/servers/HostTracker/mcp)
[![MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Connect an AI assistant to [HostTracker](https://www.host-tracker.com) over the
[Model Context Protocol](https://modelcontextprotocol.io) and let it *operate* your monitoring account in
conversation: run a live check from 300+ global locations, see what is down, create or pause a monitor, schedule a
maintenance window, review incidents, manage who gets alerted, wire a webhook, publish a status page update.

```
Endpoint   https://mcp.host-tracker.com/mcp
Transport  streamable HTTP
Auth       OAuth 2.1 (sign in when your client asks) - or Authorization: Bearer <HostTracker API token>
```

This repository is the public face of that hosted server: the connection metadata
([`server.json`](server.json)), the per-client setup guide ([`CLIENT.md`](CLIENT.md)), and the security policy
([`SECURITY.md`](SECURITY.md)) - and, since 2.0.0, the server's own source code in [`src/`](src/), so you can read
exactly what runs behind the endpoint or run a copy yourself (see "Run it yourself" below).

## Connect in two minutes

**With OAuth (recommended - Claude.ai, Claude Desktop, Claude Code, ChatGPT and any client with an OAuth-capable
connector dialog):**

1. **Add the endpoint** `https://mcp.host-tracker.com/mcp` as a connector in your client.
2. **Sign in and approve.** The client opens HostTracker's sign-in page, then a consent card listing the
   permissions it asks for (by default: run checks + read monitors). Press Approve. No token ever appears.
3. **Ask in plain language:** *"is example.com up right now,
   checked from Europe and Asia?"*, *"which of my monitors are down?"*, *"pause the staging monitor until
   tomorrow"*.

### Claude Code

With OAuth (no token needed):

```sh
claude mcp add --transport http hosttracker https://mcp.host-tracker.com/mcp
# then, inside Claude Code: /mcp -> hosttracker -> Authenticate (opens the sign-in + consent page)
```

Or with a bearer token in the header:

`.mcp.json` in your project (or `~/.claude.json` for a user-wide connector):

```json
{
  "mcpServers": {
    "hosttracker": {
      "type": "http",
      "url": "https://mcp.host-tracker.com/mcp",
      "headers": { "Authorization": "Bearer YOUR_HOSTTRACKER_API_TOKEN" }
    }
  }
}
```

Or from the command line:

```sh
claude mcp add --transport http hosttracker https://mcp.host-tracker.com/mcp \
  --header "Authorization: Bearer YOUR_HOSTTRACKER_API_TOKEN"
```

### Claude.ai and Claude Desktop

Settings -> Connectors -> **Add custom connector** -> paste `https://mcp.host-tracker.com/mcp` -> Connect. The
browser opens HostTracker's sign-in + consent page; press Approve and the connector is live. That is the whole
setup.

To use a **bearer token** instead (for example a long-lived token with a hand-picked scope set), Desktop can also
connect through the `mcp-remote` bridge. Edit `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "hosttracker": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote", "https://mcp.host-tracker.com/mcp",
        "--header", "Authorization:${HT_AUTH}"
      ],
      "env": { "HT_AUTH": "Bearer YOUR_HOSTTRACKER_API_TOKEN" }
    }
  }
}
```

The `${HT_AUTH}` indirection is deliberate: some `mcp-remote` builds split an argument on its first space, which
breaks a literal `Authorization: Bearer ...`. Node.js 18 or newer is required.

### Cursor

`~/.cursor/mcp.json` for every project, or `.cursor/mcp.json` for one:

```json
{
  "mcpServers": {
    "hosttracker": {
      "url": "https://mcp.host-tracker.com/mcp",
      "headers": { "Authorization": "Bearer YOUR_HOSTTRACKER_API_TOKEN" }
    }
  }
}
```

### VS Code (GitHub Copilot agent mode)

`.vscode/mcp.json` in the workspace. The `inputs` block keeps the token out of the file, prompting for it once and
storing it in the editor's secret storage:

```json
{
  "inputs": [
    {
      "type": "promptString",
      "id": "ht-token",
      "description": "HostTracker API token",
      "password": true
    }
  ],
  "servers": {
    "hosttracker": {
      "type": "http",
      "url": "https://mcp.host-tracker.com/mcp",
      "headers": { "Authorization": "Bearer ${input:ht-token}" }
    }
  }
}
```

### Windsurf

`~/.codeium/windsurf/mcp_config.json`:

```json
{
  "mcpServers": {
    "hosttracker": {
      "serverUrl": "https://mcp.host-tracker.com/mcp",
      "headers": { "Authorization": "Bearer YOUR_HOSTTRACKER_API_TOKEN" }
    }
  }
}
```

### Run it yourself (Docker)

The hosted endpoint is the normal way to use the server. If you would rather run your own copy - to read the
code, audit it, or keep the MCP hop inside your network - the repository builds it from source:

```bash
docker build -t hosttracker-mcp https://github.com/HostTracker/mcp.git
docker run --rm -p 8080:8080 hosttracker-mcp
```

Your copy then answers at `http://localhost:8080/mcp` and takes exactly the same `Authorization: Bearer` header:
it is a stateless bridge, so it stores nothing and still talks to the public HostTracker API v2 under your token.
Without Docker, `dotnet run --project src` does the same on any machine with the .NET 10 SDK.

The same binary also speaks **stdio**, for clients that launch the server as a child process instead of
connecting to a URL. Add `--stdio` and pass your token as the `HT_TOKEN` environment variable (there is no request
header on stdio):

```bash
HT_TOKEN=YOUR_HOSTTRACKER_API_TOKEN dotnet run --project src -- --stdio
docker run -i --rm -e HT_TOKEN=YOUR_HOSTTRACKER_API_TOKEN hosttracker-mcp --stdio
```

Logs go to stderr in that mode, so stdout stays a clean protocol stream.

### ChatGPT and other clients

**ChatGPT** (developer mode -> connectors): add `https://mcp.host-tracker.com/mcp`; ChatGPT offers its "link
account" step, which opens HostTracker's sign-in + consent page. Any other client with an OAuth-capable connector
dialog works the same way - just the URL.

Any client that can send a static header also works with a **bearer token**: the endpoint
`https://mcp.host-tracker.com/mcp` and the header `Authorization: Bearer YOUR_HOSTTRACKER_API_TOKEN`. Where a
client's connector form offers an API-key or custom-header authentication mode, the token goes there.

A generic, dependency-free bridge for anything that can only launch a command:

```sh
npx -y mcp-remote https://mcp.host-tracker.com/mcp --header "Authorization:${HT_AUTH}"
```

Longer walkthroughs, verification commands and troubleshooting live in [`CLIENT.md`](CLIENT.md).

## What the assistant can do

The server exposes the HostTracker v2 REST API as MCP tools. Every list tool takes `limit` (maximum 50) and
`cursor` and returns the next cursor; every timestamp is Unix seconds in both directions; ids are opaque strings.

| Family | What it covers |
|---|---|
| **Checks** | Run an instant check on any URL from 300+ locations (HTTP/S, ping, TCP port, traceroute, DNS, blacklist, WHOIS, Web Risk, crawl, page speed), fetch its result, list the available check types and devices. |
| **Monitors** | List, read, create, edit, copy, pause, resume and delete monitors, in single or bulk form, plus the catalogue of monitor types. |
| **Results and incidents** | Uptime summaries, raw check results, the incident list, one incident in detail, and comments on an incident. |
| **Maintenance** | List, create, edit and delete maintenance windows so planned work does not raise alerts. |
| **Contacts** | Manage contacts and contact groups, send and confirm a contact confirmation, and send a test alert to one. |
| **Subscriptions** | See who is notified for which monitor, and subscribe or unsubscribe a contact. |
| **Webhooks** | Manage webhook endpoints, send a test delivery, review the delivery log and redeliver a failed one. |
| **Status pages** | Manage public status pages, publish an incident on one and post follow-up updates. |
| **Reports** | Generate a report and list the report types available on your plan. |
| **Jobs** | Poll, wait on, cancel or resume the asynchronous jobs that bulk operations and reports return. |
| **Account** | Read-only: the account profile, its quota and its current usage. Useful for diagnosing a refused call. |
| **Locations** | List the checkpoint pools and individual monitoring locations you can target. |
| **Generic door** | `describe_api` searches the real v2 operations and `api_request` calls one, for anything without a dedicated tool. It is not a URL proxy: the operation must exist in the published API description, and the safety policy below still applies. |

<details>
<summary>The full tool list</summary>

| Family | Tools |
|---|---|
| Checks | `run_instant_check`, `get_check_result`, `list_check_types` |
| Monitors | `list_monitors`, `get_monitor`, `create_monitor`, `update_monitor`, `delete_monitor`, `pause_monitor`, `resume_monitor`, `copy_monitor`, `bulk_create_monitors`, `bulk_update_monitors`, `bulk_delete_monitors`, `list_monitor_types` |
| Results and incidents | `get_uptime_summary`, `list_monitor_results`, `list_incidents`, `get_incident`, `comment_incident` |
| Maintenance | `list_maintenance`, `create_maintenance`, `update_maintenance`, `delete_maintenance` |
| Contacts | `list_contacts`, `get_contact`, `create_contact`, `update_contact`, `delete_contact`, `send_contact_confirmation`, `confirm_contact`, `test_contact`, `list_contact_groups`, `create_contact_group`, `update_contact_group`, `delete_contact_group` |
| Subscriptions | `list_subscriptions`, `subscribe_contact`, `unsubscribe_contact` |
| Webhooks | `list_webhooks`, `create_webhook`, `update_webhook`, `delete_webhook`, `test_webhook`, `list_webhook_deliveries`, `redeliver_webhook` |
| Status pages | `list_status_pages`, `get_status_page`, `create_status_page`, `update_status_page`, `delete_status_page`, `create_status_page_incident`, `add_status_page_incident_update` |
| Reports | `generate_report`, `list_report_types` |
| Jobs | `get_job`, `wait_for_job`, `cancel_job`, `resume_job` |
| Account | `get_account`, `get_account_quota`, `get_account_usage` |
| Locations | `list_locations` |
| Generic door | `describe_api`, `api_request` |

</details>

Three behaviours worth knowing before the first call:

- **Bulk operations validate first.** A bulk tool returns a validation report; the write needs an explicit second
  call with `submit=true`. Bulk deletion additionally needs `confirmed=true` and the count the validation pass
  reported, so a selection that drifted in between is refused.
- **Bulk operations and reports are asynchronous.** They answer with a job id; poll it with `wait_for_job`.
- **Deleting anything is not undoable, so it always takes two calls.** A delete tool's first call removes
  nothing - it returns the resource so the assistant can show you what is about to go - and only a repeat call
  with `confirmed=true` deletes. The API returns a receipt listing what was removed.

## Authentication and scopes

Two ways in, same permission model:

- **OAuth (connected apps).** The client registers itself, you sign in once and approve a scope set on the
  consent page, and the server mints short-lived access tokens (1 hour) with rotating refresh tokens (90 days)
  behind the scenes. If the client requests no scopes it gets `check` + `monitor:read`. The `account` family is
  never grantable to a connected app. Every connection is listed under
  [Integrations -> API -> Connected apps](https://www.host-tracker.com/integrations/api), where one click revokes it
  (the app then has to ask you again). A tool that needs a scope the connection lacks answers `missing_scope`
  naming what is required - reconnect and approve the wider set.
- **Bearer tokens.** Mint them at [Integrations -> API](https://www.host-tracker.com/integrations/api) with a
  hand-picked scope set, expiration and optional IP allow-list; pass them in the `Authorization` header.

Scopes are per family with `:read` and `:write` leaves that do **not** imply each other; a bare family name
satisfies every leaf under it.

| You want the assistant to | Scopes |
|---|---|
| Run instant checks | `check` |
| See monitors, uptime and incidents | `monitor:read` |
| Create, edit, pause or delete monitors and maintenance | `monitor:write` |
| See who is notified | `contact:read`, `subs:read` |
| Manage contacts and subscriptions | `contact:write`, `subs:write` |
| Manage webhooks | `webhook:read`, `webhook:write` |
| Manage status pages and publish incidents | `statuspage:read`, `statuspage:write` |
| Read quota, usage and limits | `account:read` (bearer tokens only - not offered to OAuth connections) |

Grant the narrowest set that covers the work. There is never a reason to grant `account:write`: the server refuses
every write under `/account` regardless of what the token allows. If a call comes back refused, ask the assistant
to run `get_account_quota`, which reports the scopes the token actually carries.

## Limits and quota

- The endpoint rate-limits each client IP on `/mcp`. A limited response carries `Retry-After`, which the server
  passes through as a value; it never sleeps or retries on your behalf, so the assistant decides what to do.
- Your API plan quota is enforced against your own token, exactly as it is for direct REST calls. `get_account_usage`
  and `get_account_quota` report where you stand. Details in the
  [errors and limits guide](https://www.host-tracker.com/apidocs/v2/errors).
- Application failures (no token, wrong scope, quota exhausted, invalid input) come back as ordinary tool results
  with an actionable message rather than a protocol error.

## Safety

The server is stateless and stores nothing: every call is forwarded to the API under your own token, and all
ownership, quota and rate enforcement happens there. On top of that it refuses, at the server, regardless of the
token: any write under `/account`, and anything touching payments, plans, passwords, login or the minting of API
tokens. Content that a checked target controls is wrapped in a fenced, length-capped block before it reaches the
model, so a hostile target cannot inject instructions into your assistant. Full policy in
[`SECURITY.md`](SECURITY.md).

## Links

- [MCP server overview](https://www.host-tracker.com/mcp-server)
- [Mint an API token](https://www.host-tracker.com/integrations/api)
- [API v2 reference](https://www.host-tracker.com/apidocs/v2) and [guides](https://www.host-tracker.com/apidocs/v2/guide)
- [All integrations](https://www.host-tracker.com/integrations)
- Official clients: [JavaScript](https://github.com/HostTracker/hosttracker-sdk-js) ·
  [Python](https://github.com/HostTracker/hosttracker-sdk-python) ·
  [Go](https://github.com/HostTracker/hosttracker-sdk-go) ·
  [.NET](https://github.com/HostTracker/hosttracker-sdk-dotnet) ·
  [CLI](https://github.com/HostTracker/cli) ·
  [OpenAPI description](https://github.com/HostTracker/openapi) ·
  [GitHub Action](https://github.com/HostTracker/check-action)
- Support: [ht2support@host-tracker.com](mailto:ht2support@host-tracker.com)

## License

The contents of this repository (documentation and metadata) are released under the [MIT license](LICENSE). The
hosted MCP server and the HostTracker service itself are proprietary.
