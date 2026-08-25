# HostTracker MCP server, packaged as a stdio bridge.
#
# The server itself is hosted at https://mcp.host-tracker.com/mcp (streamable HTTP). This image runs the
# official mcp-remote bridge in front of it, so a client that only speaks stdio, or an automated
# directory check, can start it with `docker run -i`. Nothing of the service runs in the container.
#
#   docker build -t hosttracker-mcp .
#   docker run -i --rm -e HT_TOKEN=YOUR_HOSTTRACKER_API_TOKEN hosttracker-mcp
#
# Without HT_TOKEN the bridge still starts, and initialize / tools/list answer normally; every tool
# call then returns an authentication error until a token is supplied.
FROM node:22-alpine
RUN npm install -g mcp-remote@latest
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
