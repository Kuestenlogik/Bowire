---
summary: 'Reference for every file format Bowire reads, writes, and stores — Bowire-native exports, standard interop formats, and on-disk stores.'
---

# File Formats

Bowire reads, writes, and stores a small handful of file formats. They split into three groups: **Bowire-native exports** (`.bw*` family) that the workbench produces and consumes, **standard interop formats** that Bowire shares with the rest of the ecosystem (NuGet, HAR, OpenAPI, …), and **internal on-disk stores** under `~/.bowire/` that the workbench manages on the user's behalf.

## Bowire-native exports — the `.bw*` family

Every Bowire-native export uses a `.bw` prefix so a desktop file manager treats them as one coherent family.

| Extension | Purpose | Produced by | Consumed by |
|-----------|---------|-------------|-------------|
| `.bwf` | **Flow** — visual flow editor (5 node types, branching, loops, response chaining) | Workbench → Flows tab → Export | Workbench Import (same or another Bowire instance) |
| `.bwc` | **Collection** — Postman-style request bundle with environment-variable substitution | Workbench → Collections tab → Export | Workbench Import |
| `.bwr` | **Recording** — captured session across REST, gRPC (unary + streaming), WebSocket, SSE, SignalR, MQTT, Socket.IO, GraphQL subscriptions | Workbench → Recording manager → Export, or `bowire mock --capture-miss` | Workbench Replay; `bowire mock --recording <file>`; `UseBowireMock(<file>)` in embedded mode |
| `.bwt` | **Tests** — method-scoped test collection (`{ name, serverUrl, protocol, environment, tests: [...] }`) for CI execution | Workbench → Tests tab → Export collection | Workbench Import; CLI test-runner |
| `.bwe` | **Environments** — every environment plus globals and the active id, ready to share | Workbench → Environment manager → Export | Workbench Import |

All five are JSON documents — the `.bw*` extension signals "this is a Bowire artefact" without hiding its plain-text nature. Editors that recognise JSON by sniffing content keep working; file managers get a Bowire-specific affordance.

## Standard interop formats

Bowire deliberately uses standard formats for everything that crosses an ecosystem boundary, instead of inventing a Bowire-specific wrapper.

| Format | Purpose | Bowire's role |
|--------|---------|---------------|
| `.nupkg` | NuGet package | **Plugin distribution.** `bowire plugin install <PackageId>` pulls from nuget.org or any configured private feed; embedded hosts use `dotnet add package`. Bowire has no custom plugin-package format — every protocol plugin and template ships as a regular `.nupkg`. |
| `.har` | HTTP Archive (1.2) | Alternate recording export and import path. Interop with DevTools, Charles, Insomnia, Postman, and Playwright's `recordHar`. HAR import into a `.bwr` recording is on the roadmap. |
| `.html` (`*.report.html`) | Self-contained HTML report | Pass/fail summary from the test runner with collapsible step details. Single file — drop it into a CI artefact bucket and link to it. |
| `.proto` | Protobuf source | Schema upload for gRPC when the target doesn't support Server Reflection. Also produces the `FileDescriptorSet` for `bowire mock --grpc-schema`. |
| `.pb` | Protobuf descriptor set | Compiled `FileDescriptorSet` (`protoc --descriptor_set_out=path.pb --include_imports your.proto`). Direct input for `bowire mock --grpc-schema`. |
| `.json` (OpenAPI 3) | OpenAPI / Swagger | Schema upload for REST and input for `bowire mock --schema`. |
| `.yaml` / `.yml` (OpenAPI 3) | OpenAPI / Swagger | Same as above; SharpYaml reader is registered explicitly so YAML and JSON variants are interchangeable. |
| `.graphql` | GraphQL SDL | Schema input for `bowire mock --graphql-schema`. |

## Internal on-disk stores

These live under `~/.bowire/` and are managed by the workbench. They are **not** user-export formats — exporting the equivalent data goes through the `.bw*` family above.

| Path | Purpose |
|------|---------|
| `~/.bowire/recordings.json` | Master store of every recording captured in the workbench. The mock CLI also accepts this file directly via `--recording`; pair with `--select <name-or-id>` when the store has more than one recording. |
| `~/.bowire/environments.json` | Master store of every environment, globals, and the currently active id. Sourced by the MCP allowlist and the workbench's variable substitution. |
| `~/.bowire/plugins/<PackageId>/` | Unpacked NuGet plugins — runtime DLLs, the original `.nuspec`, and transitive deps that aren't already provided by the host. Each plugin loads into its own `BowirePluginLoadContext` so transitive-dep collisions don't cross plugin boundaries. |
| `~/.bowire/collections/` | Named collections (`.bwc` documents) synced to disk so they survive browser changes. Same disk-sync pattern as recordings and environments. |

## A note on plugin packaging

Some sister Küstenlogik products (notably [Surgewave](https://github.com/Kuestenlogik/Surgewave)) ship a custom package format (`.swpkg`) bundling DLL + manifest + transitive deps into a single archive. Bowire deliberately does **not** do that — every Bowire plugin is a regular NuGet package, every Bowire host is a regular .NET app, and every install path (CLI, embedded, container, air-gapped) reuses standard NuGet tooling. The advantage is interop with `dotnet add package`, `dotnet pack`, private feeds, GitHub Package Registry, and any other NuGet-compatible infrastructure your shop already runs. If a future Bowire feature needs richer per-plugin metadata than `.nuspec` carries, a `bowire-plugin.json` sidecar inside the existing `.nupkg` is the natural extension point — not a wrapper format.

## See also

- [Plugin Architecture](plugin-architecture.md) — how plugins load, where deps come from, ALC isolation
- [Packages](packages.md) — full NuGet package map
- [Mock server](../features/mock-server.md) — `.bwr` recording replay, `--schema` / `--grpc-schema` / `--graphql-schema` flags
- [Recording](../features/recording.md) — how `.bwr` files are produced
