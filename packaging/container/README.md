# Container distribution

Bowire ships as an OCI container image to two registries:

| Registry | Image | Notes |
|----------|-------|-------|
| **GHCR** | `ghcr.io/kuestenlogik/bowire` | Always pushed; uses the workflow's `GITHUB_TOKEN`, no extra secrets needed. |
| **Docker Hub** | `docker.io/kuestenlogik/bowire` | Optional; pushes only when `DOCKERHUB_TOKEN` (+ `DOCKERHUB_USERNAME`) repo secrets are set. |

Both publishes are multi-arch (`linux/amd64` + `linux/arm64`) and
share the same tags per release: `<version>` + `latest`.

## End-user pull

```bash
# GitHub Container Registry (recommended — no rate limits, no account required)
docker pull ghcr.io/kuestenlogik/bowire:latest

# Docker Hub mirror
docker pull kuestenlogik/bowire:latest
```

Pin to a specific version for CI / production:

```bash
docker pull ghcr.io/kuestenlogik/bowire:0.9.4
```

## Run

The image runs the standalone Bowire workbench on port `5080`:

```bash
docker run --rm -p 5080:5080 \
    ghcr.io/kuestenlogik/bowire:latest \
    --url https://api.example.com/openapi.json
```

Open <http://localhost:5080/bowire> in a browser. Every protocol
plugin bundled with the CLI (gRPC, REST, GraphQL, SignalR, MCP, SSE,
WebSocket, MQTT, Socket.IO, OData) is loaded by default; sibling
plugins (Surgewave, Kafka, DIS, UDP) need to be installed via the
plugin volume mount described below.

### Plugin persistence

Bowire reads installed plugins from `~/.bowire/plugins/`. Inside
the container that maps to `/home/app/.bowire/plugins/` (the base
image runs as the non-root `app` user). Mount a host directory there
so plugins survive container restarts:

```bash
mkdir -p ~/.bowire/plugins
docker run --rm -p 5080:5080 \
    -v ~/.bowire/plugins:/home/app/.bowire/plugins \
    ghcr.io/kuestenlogik/bowire:latest
```

To install plugins into the volume, run a one-shot install
container:

```bash
docker run --rm \
    -v ~/.bowire/plugins:/home/app/.bowire/plugins \
    ghcr.io/kuestenlogik/bowire:latest \
    plugin install Kuestenlogik.Bowire.Protocol.Storm
```

### Recordings + environments

Same shape as plugins — Bowire stores them in `~/.bowire/`. Mount
the parent directory if you want everything persistent:

```bash
docker run --rm -p 5080:5080 \
    -v ~/.bowire:/home/app/.bowire \
    ghcr.io/kuestenlogik/bowire:latest
```

### MCP adapter

Expose Bowire as an MCP server for Claude / Cursor / Copilot:

```bash
docker run --rm -p 5080:5080 \
    ghcr.io/kuestenlogik/bowire:latest \
    --url https://my-internal-service:8443 \
    --enable-mcp-adapter
```

Then point your AI tool at `http://localhost:5080/mcp`.

## docker-compose

For a stable local setup with persistent plugins, environments, and
recordings:

```yaml
# docker-compose.yml
services:
  bowire:
    image: ghcr.io/kuestenlogik/bowire:latest
    ports:
      - "5080:5080"
    volumes:
      - ./.bowire:/home/app/.bowire
    command:
      - "--url"
      - "https://api.example.com/openapi.json"
    restart: unless-stopped
```

```bash
docker compose up -d
```

## Tags

Each release pushes:

| Tag | Stability | Use for |
|-----|-----------|---------|
| `<version>` (e.g. `0.9.4`) | immutable | production, CI pinning |
| `latest`              | mutable, points at the newest tagged release | local exploration |

There's no rolling `main` tag — only tagged releases produce images.

## Image internals

| Setting | Value |
|---------|-------|
| Base image | `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` |
| User | `app` (non-root, uid 1654) |
| Working directory | `/app` |
| Exposed port | `5080` |
| Entrypoint | `dotnet bowire.dll` |
| `ASPNETCORE_URLS` | `http://+:5080` |
| Image format | OCI |
| Annotations | `org.opencontainers.image.{title,description,vendor,source,licenses}` |

`noble-chiseled-extra` is Microsoft's smallest secure ASP.NET base
image (Ubuntu 24.04 chiseled, no shell, no package manager, ICU +
tzdata included). Total image size lands around 100 MB compressed.

## Local build

The container build uses `dotnet publish /t:PublishContainer` —
no `Dockerfile` because the .NET SDK 8+ knows how to compose an
OCI image directly from the project metadata.

```bash
# Single-arch (whatever the local machine is)
dotnet publish src/Kuestenlogik.Bowire.Tool \
    -c Release \
    -t:PublishContainer \
    -p:Version=0.9.4 \
    -p:ContainerRepository=kuestenlogik/bowire

# Multi-arch (both linux-x64 + linux-arm64 in one manifest)
dotnet publish src/Kuestenlogik.Bowire.Tool \
    -c Release \
    -t:PublishContainer \
    -p:Version=0.9.4 \
    -p:ContainerRuntimeIdentifiers='"linux-x64;linux-arm64"' \
    -p:ContainerImageTags='"0.9.4;latest"' \
    -p:ContainerRepository=kuestenlogik/bowire
```

Push to a registry by adding `-p:ContainerRegistry=ghcr.io` (or
`docker.io`); without it, the image stays in the local Docker
daemon's image store.

The container properties live under
[`src/Kuestenlogik.Bowire.Tool/Kuestenlogik.Bowire.Tool.csproj`](../../src/Kuestenlogik.Bowire.Tool/Kuestenlogik.Bowire.Tool.csproj#L34-L70)
— bumping the base image, adding a label, or changing the exposed
port all happens there, not in this README.

## One-time setup before the first release

The GHCR push works out of the box (the workflow uses the built-in
`GITHUB_TOKEN`).

For Docker Hub mirroring, two repo secrets need to exist:

1. **`DOCKERHUB_USERNAME`** — your Docker Hub username (the org
   that owns the `kuestenlogik/bowire` repo on Docker Hub).
2. **`DOCKERHUB_TOKEN`** — a Docker Hub access token, generated
   under [Docker Hub → Account Settings → Security](https://hub.docker.com/settings/security).
   Scope: **Read, Write, Delete** on the `kuestenlogik/bowire`
   repository only.

Without those secrets the workflow exits the Docker Hub steps
gracefully — GHCR continues to publish on every release.
