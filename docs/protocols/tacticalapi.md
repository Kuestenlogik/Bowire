---
summary: 'TacticalAPI is a Bowire sibling plugin that wraps Rheinmetall''s situational-awareness gRPC interface with a bundled schema so the Situation service tree renders even without Server Reflection. Preview release (v0.1.0): descriptor discovery only; typed CRUD + server-streaming pump land in v0.2.0.'
---

# TacticalAPI

> **Status: preview (v0.1.0)** &mdash; descriptor discovery and plugin registration only. Typed CRUD invoke for `GetSituationObjects` / `AddOrUpdateSituationObjects` / `DeleteSituationObjects` and the server-streaming pump for `SubscribeSituationObjectEvents` land in **v0.2.0**.

The TacticalAPI plugin connects Bowire to [Rheinmetall's **TacticalAPI**](https://github.com/Rheinmetall/tacticalapi) &mdash; a gRPC interface for situational-awareness systems. The plugin ships the upstream service schema bundled with the package, so Bowire can render the `Situation` service tree against any TacticalAPI server **even when the server does not expose gRPC Server Reflection**.

**Package:** `Kuestenlogik.Bowire.Protocol.TacticalApi` (sibling repo, not bundled with the CLI)

## Install

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.TacticalApi
```

Bowire discovers the plugin automatically via assembly scanning &mdash; no extra registration code.

## Use

```bash
bowire --url tacticalapi@my-situation-server:50051
```

Open the workbench, pick the **TacticalAPI** tab, and the four methods of the `Situation` service (`SubscribeSituationObjectEvents`, `GetSituationObjects`, `AddOrUpdateSituationObjects`, `DeleteSituationObjects`) appear in the sidebar. In v0.1.0 the methods are visible and the descriptors are projected into the standard Bowire `BowireServiceInfo` / `BowireMethodInfo` shape; clicking **Execute** is a no-op until v0.2.0 wires up the generated client stubs.

### Pairing with the gRPC plugin's gRPC-Web transport

TacticalAPI servers commonly expose **two ports**: a native HTTP/2 gRPC endpoint (typically `:4267`) and a gRPC-Web endpoint (typically `:4268`) for browser-fronted clients. Bowire's [gRPC plugin](grpc.md#grpc-web-transport) speaks both &mdash; once v0.2.0 lands, point at the native port for full bidirectional access, or at the gRPC-Web port behind an L7 proxy. Discovery (this release) works against either transport because the proto schema is bundled, not fetched at connect time.

## Licensing &mdash; please read

The plugin code, generated bindings package, and documentation are **Apache-2.0**. The upstream TacticalAPI `.proto` files at <https://github.com/Rheinmetall/tacticalapi> are **EPL-2.0 OR BSD-3-Clause** (Rheinmetall Electronics GmbH). Vendoring those files into an Apache-2.0 repository would be redistribution under a different license, which EPL-2.0 does not permit. To honour both:

- The `.proto` files are **downloaded at build time** from the upstream repository (pinned commit `e68546809d981cd649325dba4a9702c1a77a1a0b`) into `obj/tacticalapi-protos/` &mdash; gitignored, never committed.
- `Grpc.Tools` compiles them into the assembly; **only the generated C# bindings** ship in the NuGet package.
- The `.proto` source itself never enters the plugin's source tree or its published package.

The pin will move to a released tag once Rheinmetall cuts one. A scheduled GitHub Action in the sibling repo (`.github/workflows/check-upstream-protos.yml`) runs weekly, compares the pinned commit against `Rheinmetall/tacticalapi@main`, and opens a tracking issue when upstream drifts &mdash; so the bump cadence stays visible without manual polling.

## Build requirements

Building the plugin **requires outbound internet access** to `raw.githubusercontent.com` so the proto-fetch target can reach the upstream repo. GitHub Actions runners have this by default; air-gapped CI does not.

### Air-gapped builds

Pre-populate the proto cache before invoking `dotnet build`:

```text
<repo-root>/artifacts/obj/Kuestenlogik.Bowire.Protocol.TacticalApi/<Configuration>/tacticalapi-protos/rheinmetall/tactical_api/v0/
```

Drop the six upstream `.proto` files (same filenames as on `Rheinmetall/tacticalapi`) into that directory and the build's `DownloadFile` target short-circuits because the files already exist. Consumers of the **published NuGet package** don't need network access &mdash; only contributors and CI building from source do.

## Try it — upstream test client as data populator

Rheinmetall ships an [official C# test client](https://github.com/Rheinmetall/tacticalapi/tree/main/testclient/csharp) alongside the proto set. It's a small CLI that exercises every operation in `Situation` &mdash; `--observesituation` (server-streaming), `--printsituation` (unary polling), `--sendsymbol` (create), `--changesymbolname` (update), `--deletesymbol` (delete). For Bowire users the test client is the fastest way to **populate a server with realistic data** so the workbench has something interesting to render.

End-to-end demo, against either a live TacticalAPI server or the upstream `TacNet` instance:

```bash
# 1. Build the upstream test client (one-time)
git clone https://github.com/Rheinmetall/tacticalapi
cd tacticalapi/testclient/csharp
dotnet build TacticalApi.TestClient.csproj

# 2. Place a handful of symbols at WGS84 coordinates near Hamburg
#    (the test client takes lat / lon as positional args)
dotnet TacticalApi.TestClient.dll --sendsymbol 53.5 9.9
dotnet TacticalApi.TestClient.dll --sendsymbol 53.55 10.0
dotnet TacticalApi.TestClient.dll --sendsymbol 53.6 10.05

# 3. Add the optional map-widget extension so SituationObjectLocation
#    pins land on a MapLibre canvas next to the streaming-frames pane.
#    (Skip this step if you only want the raw JSON view — Bowire still
#    auto-detects the lat/lon fields, it just falls back to a
#    "Install …Extension.MapLibre" placeholder card instead of a map.)
dotnet add package Kuestenlogik.Bowire.Extension.MapLibre

# 4. Point Bowire at the same server — native HTTP/2 transport
bowire --url grpc@https://localhost:4267
#    …or gRPC-Web over HTTP/1.1 if the server exposes :4268 too
bowire --url grpcweb@https://localhost:4268

# 5. In the workbench, pick Situation → SubscribeSituationObjectEvents.
#    Click Execute. With the Frame-Semantics Framework live in
#    Bowire 1.3.0+, the workbench auto-detects the lat/lon fields on
#    every SituationObjectLocation and mounts a Map tab next to the
#    streaming-frames pane — every symbol the test client created
#    appears as a pin, every new --sendsymbol from a parallel
#    terminal lights up live.
```

Why this demo carries weight: **no Bowire-side configuration was involved**. No `bowire.schema-hints.json`, no `IBowireSchemaHints` implementation, no manual right-click on a field. The TacticalAPI plugin ships transport-only; the framework recognises `coordinate.latitude` / `coordinate.longitude` from the field names + WGS84 ranges and routes the data into the map widget on its own. The pgAdmin pattern: shape-of-data drives viewer choice, not protocol-author opt-in. The map widget itself rides its own NuGet package (`Kuestenlogik.Bowire.Extension.MapLibre`) so Bowire core stays ~870 KB lighter for users who never need geographic rendering &mdash; same plugin model as the protocol packages.

A companion walkthrough in **[the mock-server docs](../features/mock-server.md#external-client-validation)** uses the same test client to validate `bowire mock` &mdash; a useful inverse comparison if you ever want to verify Bowire reproduces a real server's behaviour faithfully.

## Roadmap

- **v0.1.0 (this release)** &mdash; bundled-schema discovery, plugin registration, identity API, generated client stubs available to consumers.
- **v0.2.0** &mdash; typed unary invoke for `GetSituationObjects` / `AddOrUpdateSituationObjects` / `DeleteSituationObjects` and the server-streaming pump for `SubscribeSituationObjectEvents` via the generated client, with JSON request/response envelopes matching the Bowire schema.
- **v0.3.0** &mdash; sample server + walkthrough, position-extractor adapter for the upcoming Bowire map widget so `SituationObjectLocation` updates land on the map automatically, authentication helpers (TLS + bearer-token metadata).

## Links

- Sibling repository: <https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi> &mdash; full README with the licensing rationale, the proto-fetch target, and the air-gapped instructions in source form.
- Upstream specification: <https://github.com/Rheinmetall/tacticalapi> &mdash; the canonical TacticalAPI proto set, by Rheinmetall Electronics GmbH.
- Related plugin docs: [gRPC](grpc.md) (parent protocol, including the gRPC-Web transport TacticalAPI's `:4268` port uses), [DIS](dis.md) (sibling simulation-environment plugin).

## Acknowledgements

The TacticalAPI specification, including every `.proto` file this plugin compiles against, is the work of **Rheinmetall Electronics GmbH**. Used in accordance with the upstream EPL-2.0 / BSD-3-Clause licensing.
