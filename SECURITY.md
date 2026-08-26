# Security policy

This repository holds documentation and connection metadata for the hosted HostTracker MCP server at
`https://mcp.host-tracker.com/mcp`. The server itself is operated by HostTracker Ltd.

## Reporting a vulnerability

Email **[ht2support@host-tracker.com](mailto:ht2support@host-tracker.com)** with "MCP" in the subject. Useful
detail: what you did, what happened, the approximate time in UTC, and the affected tool or endpoint. Please give us
a chance to fix the issue before disclosing it publicly, and never include a working API token in the report.

## Your API token

The token you put in a client configuration is a bearer credential for your HostTracker account. Anyone holding it
can do whatever its scopes allow.

- **Mint the narrowest scope set that covers the work.** Scopes are per family with `:read` and `:write` leaves
  that do not imply each other. An assistant that only needs to report status wants `check` and `monitor:read`,
  nothing more.
- **There is no reason to grant `account:write`.** The server refuses every write under `/account` regardless of
  the token, so the scope buys nothing here and widens what a leaked token could do elsewhere.
- **Prefer a short expiration.** Tokens cannot be revoked before they expire. A leaked long-lived token stays valid
  until its expiry, so a short lifetime is the real containment control.
- **Use the mint-time IP allow-list** when the client runs from a fixed address.
- **Keep it out of source control.** Use your client's secret storage or an environment variable rather than a
  literal in a committed file. The VS Code configuration in [`CLIENT.md`](CLIENT.md) shows the prompted-input
  pattern; the Claude Desktop one shows the environment-variable pattern.
- **Rotate on suspicion.** Remove the token from every client configuration and mint a replacement at
  [Integrations -> API](https://www.host-tracker.com/integrations/api).

The server does not store tokens. Each request carries the caller's own token, which is forwarded to the
HostTracker API for that one call and never persisted or shared between callers.

## What the server can and cannot do

Enforcement happens in two places: your token's scopes decide what the API will accept, and the MCP server refuses
a further set of operations on top of that, regardless of the token.

| Refused at the server, whatever the token allows | Why |
|---|---|
| Any write under `/account`: profile, email, country, default locations | Identity data a person changes themselves. Reads stay available so an assistant can diagnose a refused call. |
| Payments, packages and plan changes | Not on the public API surface at all. The server physically cannot reach them. |
| Passwords, sign-in and account recovery | Same: not on the public API surface. |
| Minting or revoking API tokens | Minting lives on your own account pages. A token cannot mint another one. |

The generic door (`api_request`) is not a URL proxy. The method and path must match an operation in the published
API description, and the refusal list above runs before anything is dialed, so an operation added to the API later
cannot be written to through this door until it has been reviewed.

## Protections against prompt injection

An assistant reads whatever a checked target returns, and a hostile target can try to plant instructions in it.

- Content controlled by a third party (check errors and metrics, monitor result errors, webhook response excerpts,
  contact-test exchanges, WHOIS and DNS records) is wrapped in a per-response fenced block marked as untrusted and
  is length-capped before it reaches the model. Every string leaving a tool is stripped of control characters.
- Destructive tools state in their description that the assistant should confirm with you first, which is the text
  a model reads before choosing a tool, and every tool carries the standard MCP annotations (read-only,
  destructive) so a client can interpose its own confirmation prompt.
- Deleting a single resource is a two-step operation enforced by the server, not by the model: the first call
  deletes nothing and returns the live resource to show you; only a second call with `confirmed=true` deletes.
  The generic `api_request` door applies the same rule to any `DELETE` and to bulk writes that are not a
  `/validate` dry-run.
- Bulk writes run a validation pass first and need an explicit second call to submit. Bulk deletion additionally
  needs an explicit confirmation flag and the count the validation pass reported, so a selection that changed in
  between is refused rather than applied.

None of that replaces your own judgement. Review what an assistant proposes to delete, and keep the token's scopes
aligned with the work you actually want it to do.

## Reporting abuse

If you believe someone is using the MCP server to check or attack a property you own, write to
[ht2support@host-tracker.com](mailto:ht2support@host-tracker.com) with the target, the timestamps and, if you have
it, the source address seen at your end.
