---
title: Pulsar
summary: 'In-tree Apache Pulsar plugin on the Apache-maintained DotPulsar 3.5.x client. Topic discovery via HTTP admin, produce + subscribe over the binary protocol.'
---

# Apache Pulsar Protocol

Bowire's Pulsar plugin enumerates topics via Pulsar's HTTP admin API and produces / subscribes over the binary protocol using the Apache-maintained **DotPulsar 3.5.x** client.

## Setup

In-tree plugin — no separate install needed.

### Standalone

```bash
bowire --url pulsar://localhost:6650
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("pulsar://localhost:6650");
});
```

## URL shapes

The plugin derives both the broker URL (binary protocol) and the admin URL (HTTP) from any of these:

```
pulsar://host:6650         # binary broker; admin guessed at http://host:8080
pulsar+ssl://host:6651     # TLS broker; admin guessed at https://host:8080
http://host:8080           # admin URL; broker guessed at pulsar://host:6650
https://host:8443          # TLS admin; broker guessed at pulsar+ssl://host:6651
host[:port]                # bare; assumed binary on port 6650
```

## Discovery

`DiscoverAsync` hits `/admin/v2/persistent/<tenant>/<namespace>` for every namespace in the `namespaces` setting (default `public/default`). Each topic surfaces as a Bowire service with two methods:

- **`produce`** (Unary) — publish one string message
- **`subscribe`** (ServerStreaming) — open an exclusive subscription and tail the topic, ack each message so the cursor advances

## Topic-name normalisation

Short names get expanded into Pulsar's fully-qualified form:

- `my-topic` → `persistent://public/default/my-topic`
- `tenant/ns/foo` → `persistent://tenant/ns/foo`
- `persistent://...` / `non-persistent://...` — pass through

## Metadata overrides

| Key | Purpose | Default |
|-----|---------|---------|
| `topic` | Override the discovery-time topic on invoke | discovered topic |
| `subscription_name` | Subscription name (subscribe op) | `bowire-<8-hex>` |
| `from_latest` | `"false"` replays the backlog; `"true"` tails new messages | from `subscribeFromLatest` setting |

## Settings

- `namespaces` (string, default `public/default`) — comma-separated `tenant/namespace` pairs to scan
- `subscribeFromLatest` (bool, default `true`) — initial subscription position when not overridden via metadata

## Sample

A combined sample lives at [`samples/Kuestenlogik.Bowire.Sample.Pulsar`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Kuestenlogik.Bowire.Sample.Pulsar) — it mounts the embedded workbench at `/bowire` and runs a resilient producer publishing one message per second to `persistent://public/default/bowire-sample`, pointed at an external Pulsar broker (its own `docker-compose.yml`, broker `:6650` + admin `:8080`). `docker compose up`, then `dotnet run`, and open <http://localhost:5194/bowire> — or point a separate workbench at `pulsar://localhost:6650`. (The host starts even before the broker is up; the topic lights up once it appears.)
