---
summary: "Bowire ships with first-class container support via the .NET 10 SDK's"
---

# Containers / OCI

Bowire is published as a multi-arch (`linux/amd64` + `linux/arm64`) OCI
image on every tagged release. Pull it from one of two registries:

| Registry | Image | Notes |
|----------|-------|-------|
| **GHCR** | `ghcr.io/kuestenlogik/bowire` | Primary — no login required for pulls, no rate limits, signed by the release workflow's `GITHUB_TOKEN`. |
| **Docker Hub** | `docker.io/kuestenlogik/bowire` (or just `kuestenlogik/bowire`) | Mirror, identical content. Use this when your tooling defaults to the Docker Hub namespace or your local registry mirror only proxies `docker.io`. |

Tags: `latest` (newest stable) and `<version>` (e.g. `1.0.12`) for pinning. The
multi-arch manifest list lets `docker` pick the right architecture
automatically — no `--platform` flag needed.

## Quick start

Pull and run from either registry:

```bash
# GHCR (recommended)
docker pull ghcr.io/kuestenlogik/bowire:latest
docker run --rm -p 5080:5080 \
  ghcr.io/kuestenlogik/bowire:latest \
  --url https://my-grpc-server:443

# Or via Docker Hub
docker pull kuestenlogik/bowire:latest
docker run --rm -p 5080:5080 \
  kuestenlogik/bowire:latest \
  --url https://my-grpc-server:443
```

Then open `http://localhost:5080/bowire` in your browser. The image runs
the standalone Bowire workbench on port `5080`.

`--no-browser` is auto-detected when the container has no controlling
TTY (which is always the case for `docker run`), so you don't need to
pass it explicitly.

## Plugin and recording persistence

Bowire stores plugins under `~/.bowire/plugins/` and recordings + environments
under `~/.bowire/`. Inside the container, that maps to
`/home/app/.bowire/...` (the chiseled base image runs as the non-root
`app` user). Mount a host directory there so state survives restarts:

```bash
mkdir -p ~/.bowire
docker run --rm -p 5080:5080 \
  -v ~/.bowire:/home/app/.bowire \
  ghcr.io/kuestenlogik/bowire:latest
```

Install plugins into the volume with a one-shot install container:

```bash
docker run --rm \
  -v ~/.bowire:/home/app/.bowire \
  ghcr.io/kuestenlogik/bowire:latest \
  plugin install Kuestenlogik.Bowire.Protocol.Storm
```

## docker-compose

```yaml
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

## Building locally

If you don't want to pull from a registry — for an air-gapped install,
a security-scanning pre-step, or because you've patched the source
locally — build the image yourself with the .NET SDK's container
builder. **No Dockerfile, no docker daemon, no multi-stage build is
required**:

```bash
scripts/publish-container.sh 1.0.12 linux-x64
docker load < artifacts/containers/bowire-1.0.12-linux-x64.tar.gz
docker run --rm -p 5080:5080 kuestenlogik/bowire:1.0.12 \
    --url https://my-grpc-server:443
```

The PowerShell variant works the same way:

```powershell
scripts\publish-container.ps1 -Version 1.0.12 -Arch linux-x64
```

## How it works

The build uses [`dotnet publish /t:PublishContainer`](https://learn.microsoft.com/dotnet/core/containers/sdk-publish),
which is the .NET 10 SDK's native way to produce OCI container images
without writing a `Dockerfile`. Configuration lives directly in
[`src/Kuestenlogik.Bowire.Tool/Kuestenlogik.Bowire.Tool.csproj`](https://github.com/Kuestenlogik/Bowire/blob/main/src/Kuestenlogik.Bowire.Tool/Kuestenlogik.Bowire.Tool.csproj):

| Property | Value | Effect |
|----------|-------|--------|
| `ContainerRepository` | `kuestenlogik/bowire` | Image name |
| `ContainerFamily` | `noble-chiseled-extra` | Picks the Ubuntu Noble (24.04) chiseled distroless base image with ICU + tzdata |
| `ContainerImageFormat` | `OCI` | Open Container Initiative format (broader runtime compatibility than the legacy Docker format) |
| `ContainerWorkingDirectory` | `/app` | `WORKDIR /app` inside the container |
| `ContainerPort` | `5080/tcp` | `EXPOSE 5080` |
| `ContainerEnvironmentVariable ASPNETCORE_URLS` | `http://+:5080` | Bowire binds to all interfaces inside the container |
| `ContainerLabel org.opencontainers.image.*` | title / description / vendor / source / licenses | Standard OCI annotations for image-scanning and registry UIs |

Image tags are picked at publish time by the script (`<version>`,`latest`).

### Why `noble-chiseled-extra`?

The base image is auto-selected: because `Kuestenlogik.Bowire.Tool` is an
ASP.NET Core app, the SDK chooses `mcr.microsoft.com/dotnet/aspnet:10.0`,
then `ContainerFamily=noble-chiseled-extra` flips that to the
[Ubuntu Noble chiseled](https://devblogs.microsoft.com/dotnet/announcing-dotnet-chiseled-containers/)
distroless variant.

Compared to the regular `aspnet` image, chiseled is **~100 MB smaller**
and dramatically reduces the attack surface:

- **No shell** — `sh`, `bash`, none of them. A compromised process can't
  spawn a reverse shell.
- **No package manager** — no `apt`, no `apk`. No way for an attacker to
  install tools after a breach.
- **Non-root by default** — the image ships with a `app` user enabled,
  so the Bowire process runs unprivileged out of the box.
- **Smaller CVE surface** — fewer packages = fewer vulnerabilities to
  patch.

The `-extra` suffix adds ICU + tzdata back so culture-aware string
operations and time-zone conversions work normally — Bowire doesn't
require it across the board, but it's only ~10 MB extra in the
compressed tarball and avoids any risk of a runtime
`InvariantGlobalization` exception in code paths we haven't audited.

If you want the absolute smallest image and have verified your code
uses only `StringComparison.Ordinal` / `CultureInfo.InvariantCulture`,
drop `-extra` from `ContainerFamily`:

```xml
<ContainerFamily>noble-chiseled</ContainerFamily>
```

The compressed tarball is roughly the same size either way (~70 MB)
because the bulk of the image is the ASP.NET runtime and Bowire's own
assemblies, not the base image.

### Other base image families

If chiseled doesn't fit your environment, the SDK supports other
families via the same `ContainerFamily` property:

| Family | Base | Notes |
|--------|------|-------|
| `noble-chiseled-extra` (default) | Ubuntu Noble chiseled + ICU/tzdata | Distroless, secure, globalized |
| `noble-chiseled` | Ubuntu Noble chiseled | Distroless, no globalization |
| `alpine` | Alpine Linux (musl libc) | Has shell + apk, popular but not distroless |
| _(empty)_ | Ubuntu Noble (default) | Largest, has shell + apt |

Set in the csproj or override per build with `-p:ContainerFamily=alpine`.

## Why a tarball, not a daemon push?

The script sets `ContainerArchiveOutputPath`, which makes the SDK write
the image to a `tar.gz` instead of pushing it to a local daemon. That has
three advantages:

1. **No docker / podman daemon required at build time** — works in any
   CI runner that has a .NET 10 SDK installed
2. **Security-scanning friendly** — drop the tarball into Trivy, Grype,
   Snyk Container, or any other scanner before pushing it to a registry
3. **Both docker AND podman can load the same artifact**:
   ```bash
   docker load < bowire-<version>-<arch>.tar.gz
   podman load < bowire-<version>-<arch>.tar.gz
   ```

## Multi-arch builds

Build a separate tarball per architecture:

```bash
scripts/publish-container.sh 1.0.12 linux-x64
scripts/publish-container.sh 1.0.12 linux-arm64
```

You'll get `bowire-1.0.12-linux-x64.tar.gz` and `bowire-1.0.12-linux-arm64.tar.gz`.
Push both with the same repository name to a registry to assemble a
multi-arch manifest list (e.g. via `docker buildx imagetools create`).

## Pushing to a registry

Two options:

**Option A — load and push manually:**

```bash
docker load < artifacts/containers/bowire-1.0.12-linux-x64.tar.gz
docker tag kuestenlogik/bowire:1.0.12 ghcr.io/kuestenlogik/bowire:1.0.12
docker push ghcr.io/kuestenlogik/bowire:1.0.12
```

**Option B — let the SDK push directly** (skip the tarball, push to a
registry your local credential helper has access to):

```bash
dotnet publish src/Kuestenlogik.Bowire.Tool \
  -c Release -r linux-x64 \
  -p:Version=1.0.12 \
  -p:ContainerImageTags='"1.0.12;latest"' \
  -p:ContainerRegistry=ghcr.io/kuestenlogik \
  -t:PublishContainer
```

This requires `docker login ghcr.io` (or equivalent) ahead of time.

## Running the container

```bash
docker run --rm -p 5080:5080 kuestenlogik/bowire:1.0.12 \
    --url https://my-grpc-server:443 --no-browser
```

The CLI mode also works:

```bash
docker run --rm kuestenlogik/bowire:1.0.12 \
    call --url https://staging:443 health.HealthService/Check -d '{}' --compact
```

`--no-browser` is auto-detected when running headlessly (no controlling
TTY), so explicitly setting it is only needed for clarity in scripts.

## Embedded mode (with your own application)

If you want to embed Bowire into your own ASP.NET Core app's container
image, you don't need this script at all — just reference the
`Kuestenlogik.Bowire` NuGet package and use your existing `Dockerfile` /
`/t:PublishContainer` build:

```csharp
app.MapBowire(options =>
{
    options.Title = "My API";
    options.ServerUrl = "https://my-grpc-server:443";
});
```

The Bowire UI will be served at `/bowire` from your own container.

### Kestrel configuration for embedded gRPC

Configure Kestrel to handle both HTTP/1.1 (for the Bowire UI) and
HTTP/2 (for gRPC) on the same port:

```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
    });
});
```

## CI/CD usage

Use Bowire in CI pipelines for automated API validation:

```yaml
# GitHub Actions example
- name: Smoke test
  run: |
    dotnet tool install -g Kuestenlogik.Bowire.Tool
    bowire call --url https://staging:443 \
      health.HealthService/Check -d '{}' --compact
```

The `--compact` flag produces pipe-friendly one-line JSON output. Exit
code `0` means success.

See also: [Standalone Tool](standalone.md), [CLI Mode](../features/cli-mode.md)
