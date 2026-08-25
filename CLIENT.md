# Connecting a client to the HostTracker MCP server

The server speaks MCP over **streamable HTTP** at `https://mcp.host-tracker.com/mcp` and authenticates with an
`Authorization: Bearer <token>` header carrying a HostTracker API token. There is nothing to install and nothing to
run locally.

## 1. Mint a token

Open [Integrations → API](https://www.host-tracker.com/integrations/api) on your HostTracker account and create a
token. Two settings matter:

- **Scopes.** Check only what the assistant should be able to do. Scopes are per family with `:read` and `:write`
  leaves that do not imply each other, and a bare family name covers every leaf under it. `check` alone is enough
  for instant checks; add `monitor:read` to look at your monitors, `monitor:write` to change them, and so on.
  `account:write` is never useful here: the server refuses every write under `/account` whatever the token allows.
- **Expiration and IP allow-list.** Tokens cannot be revoked before they expire, so prefer a short expiration, and
  use the mint-time IP allow-list when the client runs from a fixed address.

Your plan must include API access. The token is a credential: treat it like a password, see
[`SECURITY.md`](SECURITY.md).

## 2. Configure your client

### Claude Code

Project-scoped, in `.mcp.json` at the repository root:

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

Or add it from the command line, which writes the same entry:

```sh
claude mcp add --transport http hosttracker https://mcp.host-tracker.com/mcp \
  --header "Authorization: Bearer YOUR_HOSTTRACKER_API_TOKEN"
```

Check it with `claude mcp list`, then `/mcp` inside a session to see the connection state. The tools appear
prefixed, for example `hosttracker_run_instant_check`.

### Claude Desktop

Desktop's "Add connector" dialog accepts OAuth connectors only. This server uses a bearer token, so connect
through the `mcp-remote` bridge. Settings → Developer → Edit config opens `claude_desktop_config.json`:

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

Restart Desktop completely (quit, not just close the window). Node.js 18 or newer must be on the PATH that the
desktop app sees; on macOS an app launched from Finder does not read your shell profile, so an absolute path such
as `/usr/local/bin/npx` is sometimes needed in `command`.

### Cursor

`~/.cursor/mcp.json` for all projects, or `.cursor/mcp.json` inside one:

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

Settings → MCP lists the server and its tools, with a refresh control if it was added while Cursor was running.

### VS Code, GitHub Copilot agent mode

`.vscode/mcp.json` in the workspace, or the user-level `mcp.json` reached through the "MCP: Open User
Configuration" command. The `inputs` block keeps the token out of the file:

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

VS Code prompts for the token on first use and keeps it in its secret storage. Open the Chat view, switch to Agent
mode, and the tool picker lists the HostTracker tools.

### Windsurf

`~/.codeium/windsurf/mcp_config.json`, reachable from Settings → Cascade → MCP servers:

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

Some Windsurf versions use `url` rather than `serverUrl` for remote servers. If the entry is ignored, try the other
key before assuming the token is at fault.

### ChatGPT and other MCP clients

Any client that can dial a streamable-HTTP MCP endpoint and send a static header works. Supply:

```
URL     https://mcp.host-tracker.com/mcp
Header  Authorization: Bearer YOUR_HOSTTRACKER_API_TOKEN
```

If the client's connector form offers an API-key or custom-header authentication mode, the token goes there. If it
offers OAuth only, or can launch a command but not send headers, use the bridge:

```sh
npx -y mcp-remote https://mcp.host-tracker.com/mcp --header "Authorization:${HT_AUTH}"
```

with `HT_AUTH` set to `Bearer YOUR_HOSTTRACKER_API_TOKEN` in the environment.

### The `.mcp.json` in this repository

The [`.mcp.json`](.mcp.json) at the root of this repository is the installable descriptor that plugin directories
read. It deliberately carries **no credentials**: the specification treats header values in a published manifest as
public package data. It names the endpoint and the transport, and you add your own `Authorization` header in your
own client configuration, as shown above.

## 3. Verify the connection

A plain handshake with `curl`, which needs no token and proves the endpoint is reachable from your machine:

```sh
curl -sS https://mcp.host-tracker.com/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize",
       "params":{"protocolVersion":"2025-06-18","capabilities":{},
                 "clientInfo":{"name":"curl","version":"1"}}}'
```

The reply is a `text/event-stream` frame whose `data` line carries `serverInfo` with the server name and version.
A bare `GET` of the same URL answers `400` with a JSON-RPC envelope asking for a session header, which is also a
sign the transport is healthy, not an error on your side.

Then, from your assistant: *"use hosttracker to run an http check on example.com"*.

## Troubleshooting

**The client connects but lists no tools, or only three.** The three-tool list is the previous version of the
endpoint; reconnect once the current rollout completes. No tools at all usually means the client dropped the
`Authorization` header: confirm the header reaches the server by testing the same token with `curl` as above plus a
`tools/list` call.

**Every call is refused for permissions.** Ask the assistant to run `get_account_quota`. It reports the scopes the
token actually carries, which is the fastest way to see that, for example, `monitor:write` was never checked at
mint time. Scopes cannot be added to an existing token; mint a new one.

**Calls fail with a rate-limit or quota message.** The endpoint limits requests per client IP, and your API plan
quota is enforced against the token itself. The message names which one bound and, when the server was told, when
it resets. The server passes `Retry-After` through as a value and never sleeps on your behalf, so it is the
assistant that should wait rather than retry immediately.

**`mcp-remote` starts and exits.** Three usual causes: Node.js older than 18; an argument split on its first space,
which is why the header is passed as `Authorization:${HT_AUTH}` with the value in the environment; or a stale
cached session under `~/.mcp-auth`, which can be deleted safely.

**A corporate proxy or TLS inspection breaks the stream.** Streamable HTTP holds a long-lived response; proxies
that buffer or truncate it cause silent disconnects. Allow `mcp.host-tracker.com` through without buffering.

**The token leaked, or you no longer trust it.** Tokens cannot be revoked before expiry. Remove it from every
client configuration, mint a replacement with a short expiration, and see [`SECURITY.md`](SECURITY.md).

**Anything else.** [ht2support@host-tracker.com](mailto:ht2support@host-tracker.com), with the tool name, the
message you saw, and the approximate time. Never include the token itself.
