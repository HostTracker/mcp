#!/bin/sh
# Starts the mcp-remote stdio bridge to the hosted HostTracker MCP server.
# HT_TOKEN (optional): your HostTracker API token, minted at https://www.host-tracker.com/integrations/api
set -e
URL="https://mcp.host-tracker.com/mcp"
if [ -n "$HT_TOKEN" ]; then
  exec mcp-remote "$URL" --transport http-only --header "Authorization:Bearer $HT_TOKEN"
fi
exec mcp-remote "$URL" --transport http-only
