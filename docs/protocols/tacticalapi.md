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
