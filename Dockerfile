# Bowire as an MCP server over stdio: `bowire mcp serve`, the workbench's own
# tools (bowire.discover, bowire.invoke, bowire.mock.start, ...) for an AI agent.
#
# This image exists for MCP directories that start a server to check it answers
# introspection (Glama, for awesome-mcp-servers - #670), and for agent hosts that
# run MCP servers as containers:
#
#   docker build -t bowire-mcp .
#   docker run -i --rm bowire-mcp
#
# It installs the released dotnet tool from NuGet rather than building the
# source, so it is small and builds in seconds. The workbench image with the
# browser UI is a different one, built by the .NET SDK from
# src/Kuestenlogik.Bowire.Tool (see the Downloads page).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS install
# Empty installs the latest release. CI pins it right after a release, when
# nuget.org already lists the new version but still resolves the previous one.
ARG BOWIRE_VERSION=""
RUN dotnet tool install --tool-path /opt/bowire Kuestenlogik.Bowire.Tool ${BOWIRE_VERSION:+--version "$BOWIRE_VERSION"}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=install /opt/bowire /opt/bowire
ENV PATH="/opt/bowire:${PATH}" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
USER $APP_UID
ENTRYPOINT ["bowire", "mcp", "serve"]
