# Builds and runs the HostTracker MCP server from the source in src/.
#
#   docker build -t hosttracker-mcp https://github.com/HostTracker/mcp.git
#   docker run --rm -p 8080:8080 hosttracker-mcp
#
# The server then answers MCP streamable-HTTP at http://localhost:8080/mcp. It needs no configuration: every
# request carries the caller's own HostTracker API token (Authorization: Bearer ...), and the tools call the
# public HostTracker API v2. Override the API root with -e mcp__api2BaseUrl=... if you ever need to.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/ ./
RUN dotnet publish HostTracker.Mcp.csproj -c Release -o /app --self-contained false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "HostTracker.Mcp.dll"]
