---
summary: "Bowire ships with first-class container support via the .NET 10 SDK's"
---

# Containers / OCI

Bowire ships with first-class container support via the .NET 10 SDK's
built-in container builder. **No Dockerfile, no docker daemon, no
multi-stage build is required** to produce a runnable image.

## Quick start

```bash
scripts/publish-container.sh 0.9.4 linux-x64
docker load < artifacts/containers/bowire-0.9.4-linux-x64.tar.gz
docker run --rm -p 5080:5080 kuestenlogik/bowire:0.9.4 \
    --url https://my-grpc-server:443 --no-browser
```

Then open `http://localhost:5080/bowire` in your browser.

The PowerShell variant works the same way:

```powershell
scripts\publish-container.ps1 -Version 0.9.4 -Arch linux-x64
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
scripts/publish-container.sh 0.9.4 linux-x64
scripts/publish-container.sh 0.9.4 linux-arm64
```

You'll get `bowire-0.9.4-linux-x64.tar.gz` and `bowire-0.9.4-linux-arm64.tar.gz`.
Push both with the same repository name to a registry to assemble a
multi-arch manifest list (e.g. via `docker buildx imagetools create`).

## Pushing to a registry

Two options:

**Option A — load and push manually:**

```bash
docker load < artifacts/containers/bowire-0.9.4-linux-x64.tar.gz
docker tag kuestenlogik/bowire:0.9.4 ghcr.io/kuestenlogik/bowire:0.9.4
docker push ghcr.io/kuestenlogik/bowire:0.9.4
```

**Option B — let the SDK push directly** (skip the tarball, push to a
registry your local credential helper has access to):

```bash
dotnet publish src/Kuestenlogik.Bowire.Tool \
  -c Release -r linux-x64 \
  -p:Version=0.9.4 \
  -p:ContainerImageTags='"0.9.4;latest"' \
  -p:ContainerRegistry=ghcr.io/kuestenlogik \
  -t:PublishContainer
```

This requires `docker login ghcr.io` (or equivalent) ahead of time.

## Running the container

```bash
docker run --rm -p 5080:5080 kuestenlogik/bowire:0.9.4 \
    --url https://my-grpc-server:443 --no-browser
```

The CLI mode also works:

```bash
docker run --rm kuestenlogik/bowire:0.9.4 \
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
