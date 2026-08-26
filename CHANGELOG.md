# Changelog

Notable changes to the HostTracker MCP server as a client sees it. The numbers match the `version` field of
[`server.json`](server.json) and the `serverInfo.version` the endpoint reports at handshake time, and follow
[semantic versioning](https://semver.org).

Changes behind the endpoint that do not alter how a client connects (new tools, clearer tool descriptions) are
listed here but do not require anyone to change their configuration: MCP clients read the tool list live.

## Unreleased

### Added

- The server's source code, under `src/`. It is the exact code behind `https://mcp.host-tracker.com/mcp`:
  a stateless ASP.NET Core bridge to the HostTracker API v2, with no configuration or secrets of its own.
- A `Dockerfile` that builds and runs that source (`docker build` + `docker run -p 8080:8080`), and a CI job
  that builds it on every push.
- A `--stdio` mode (also `MCP_TRANSPORT=stdio`): the same 65 tools over stdin/stdout for clients and sandboxes that
  start the server as a child process. The token comes from the `HT_TOKEN` environment variable; logs go to stderr.

### Changed

- The previous `Dockerfile`, which only wrapped the `mcp-remote` stdio bridge, is gone. Clients that need
  stdio keep using `npx -y mcp-remote ...` as shown in `CLIENT.md`; nothing about the hosted endpoint changes.

## 2.0.0 - 2026-08-25

Live at `https://mcp.host-tracker.com/mcp`: the handshake reports `2.0.0` and `tools/list` returns 65 tools.

### Added

- Server-enforced two-step deletion: a single-resource delete tool's first call previews what would be removed
  and deletes nothing; only `confirmed=true` deletes. The generic `api_request` door applies the same rule to
  `DELETE` and to non-dry-run bulk writes.
- Standard MCP tool annotations on every tool (read-only, destructive, open-world), so clients can gate
  destructive calls with their own confirmation UI.
- `run_instant_check` takes an optional `strictTls` (http checks only): `true` validates the TLS handshake
  strictly, so an untrusted root, an incomplete chain, a hostname mismatch or a self-signed certificate fails
  the handshake and is recorded on the result's TLS details. The default keeps the relaxed handshake an uptime
  check wants.

- The full HostTracker v2 API surface as tools: monitors (single and bulk), uptime summaries and raw results,
  incidents, maintenance windows, contacts and contact groups, alert subscriptions, webhooks and their delivery
  log, status pages and status-page incidents, reports, asynchronous jobs, read-only account information, and the
  catalogue of monitoring locations.
- A guarded generic door, `describe_api` and `api_request`, for operations without a dedicated tool. It validates
  the requested operation against the published API description rather than proxying arbitrary URLs.
- A server-side refusal list, independent of the token: no writes under `/account`, and no reach at all towards
  payments, plans, passwords or token minting.
- Untrusted-content fencing and length caps on everything a checked target controls, so a hostile target cannot
  inject instructions into the calling assistant.
- Validate-then-submit for bulk writes, with an extra confirmation flag and an expected-count check on bulk
  deletion.
- Cursor paging on every list tool, with a documented cap of 50 items per page.

### Changed

- Check types and device profiles are read live from the API catalogue instead of a hard-coded list, so they cannot
  go stale. `pageSpeed` is accepted as an alias for `waterfall`, and the API reports the canonical `waterfall` back.
- Application failures (missing token, insufficient scope, exhausted quota, invalid input) are returned as tool
  results with an actionable message rather than as protocol errors, and `Retry-After` is passed through as a value
  for the assistant to act on.

## 1.0.0

Initial release: instant checks over streamable HTTP with bearer-token authentication.

### Added

- `run_instant_check`, `get_check_result` and `list_check_types`, covering ad-hoc checks from 300+ global
  locations.
