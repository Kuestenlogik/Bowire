---
title: Sidecar
summary: 'Run Bowire as a sidecar container next to your service — same pod, same network namespace, no schema files to keep in sync. Docker, docker-compose, and Kubernetes setups, plus the `--url-file` pattern for dynamic discovery.'
---

# Sidecar Deployment

Bowire ships as a single Docker image that drops into any container orchestrator as a **sidecar** — a co-located helper container that shares the network with your service and gives your team a workbench / MCP endpoint at a known URL.

## When sidecar fits

Three concrete shapes:

* **Internal-debug sidecar** — `bowire` runs next to your service in dev / staging, listening on `localhost:5080`. Developers port-forward to the pod and get the workbench. No production traffic touches it; production-tier deployments skip the sidecar entirely or gate it behind an auth provider.
* **CI fixture sidecar** — a job spec brings up `bowire mock` alongside the service under test. Integration tests hit the mock at `localhost:5099`; the real upstream stays untouched.
* **MCP agent sidecar** — `bowire mcp serve` runs as a sidecar to expose the service's discovered methods (or Bowire's own primitives) as MCP tools for an in-cluster AI agent.

In every shape the sidecar is **co-located**: same pod, same network namespace, `localhost` as the service-to-sidecar bridge. That keeps the latency negligible and removes the need to authenticate the loopback hop.

## Docker

The container ships from GHCR (recommended) and Docker Hub:

```bash
docker pull ghcr.io/kuestenlogik/bowire:latest
docker run --rm -p 5080:5080 \
  ghcr.io/kuestenlogik/bowire:latest \
  --url https://api.example.com
```

Persist plugins + recordings between runs with a named volume:

```bash
docker volume create bowire-home
docker run --rm -p 5080:5080 \
  -v bowire-home:/home/bowire/.bowire \
  ghcr.io/kuestenlogik/bowire:latest \
  --url https://api.example.com
```

## docker-compose

Sidecar pattern next to a sample backend:

```yaml
services:
  api:
    image: my-org/my-api:latest
    ports:
      - "5000:5000"

  bowire:
    image: ghcr.io/kuestenlogik/bowire:latest
    network_mode: "service:api"  # share api's network namespace
    command: ["--url", "http://localhost:5000"]
    volumes:
      - bowire-home:/home/bowire/.bowire

volumes:
  bowire-home:
```

`network_mode: "service:api"` is the trick — Bowire sees the API at `localhost:5000` exactly as the API sees itself, no service-discovery needed.

## Kubernetes

A `Deployment` with two containers in one pod:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: my-api
spec:
  replicas: 1
  selector:
    matchLabels: { app: my-api }
  template:
    metadata:
      labels: { app: my-api }
    spec:
      containers:
        - name: api
          image: my-org/my-api:latest
          ports: [{ containerPort: 5000 }]
        - name: bowire
          image: ghcr.io/kuestenlogik/bowire:latest
          args: ["--url", "http://localhost:5000"]
          ports: [{ containerPort: 5080 }]
          volumeMounts:
            - { name: bowire-home, mountPath: /home/bowire/.bowire }
      volumes:
        - { name: bowire-home, emptyDir: {} }
```

`kubectl port-forward pod/<name> 5080:5080` opens the workbench from the operator's laptop. For team-wide access, expose it through your service mesh / ingress with an auth layer in front (see the [auth-provider roadmap](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md#auth-provider-extension-spi-phase-a--core-seam)).

## Multi-URL discovery from a file

When the sidecar should discover several services at once, the `--url-file <path>` flag reads a newline-separated list from disk and re-reads it on a change:

```bash
docker run --rm -p 5080:5080 \
  -v /etc/bowire/urls.txt:/etc/bowire/urls.txt:ro \
  ghcr.io/kuestenlogik/bowire:latest \
  --url-file /etc/bowire/urls.txt
```

`urls.txt`:

```text
http://orders.local:5000
http://users.local:5000/openapi.json
mqtt://broker.local:1883
```

In Kubernetes that's a ConfigMap projected into the pod; in docker-compose it's a bind-mount; in plain Docker it's a host file. Hot-reload picks up changes within the discovery tick.

## Image base

The image is built on `mcr.microsoft.com/dotnet/nightly/aspnet:10.0-noble-chiseled-extra` — a chiseled Ubuntu Noble base with no shell, no package manager, no busybox: just glibc + .NET 10 runtime + ICU. That keeps the attack surface minimal and the image small (~ 90 MB).

For the full image-build details, see [Containers / OCI](docker.md).

## Related

* [Containers / OCI](docker.md) — base image, build details, GHCR / Docker Hub tags
* [Standalone CLI](standalone.md) — same binary, run directly on the host
* [Embedded mode](embedded.md) — alternative shape when you control the service's code
