---
title: Updating
summary: 'How to update Bowire and its plugins — the tool side (dotnet tool update / winget / choco / MSI / standalone) and the sibling-plugin side (CLI or workbench UI, with --prerelease opt-in for RCs).'
---

# Updating Bowire and its plugins

Bowire ships in two halves that update through different channels:

- The **Bowire tool itself** (`bowire` CLI + browser-UI host + every bundled protocol plugin) updates as a single unit through whichever package manager you installed it with.
- **Sibling-repo protocol plugins** (Akka, AMQP, DIS, Kafka, Surgewave, TacticalAPI, UDP) live under `~/.bowire/plugins/` and update independently — one at a time or in a batch — either through the workbench's Settings → Plugins panel or through the `bowire plugin` CLI verbs.

Knowing which side a given plugin sits on saves a search-and-replace later: the bundled-plugin list is fixed at `Kuestenlogik.Bowire.Tool` build time (everything `Kuestenlogik.Bowire.Protocol.*` inside the in-tree set), and every plugin name not on that list is a sibling that needs its own update. The Settings → Plugins panel labels bundled rows with a `bundled` badge so you don't have to remember.

## Update the Bowire tool

Pick the channel that matches your install:

| Installed via | Update command |
|---|---|
| `dotnet tool install -g Kuestenlogik.Bowire.Tool` | `dotnet tool update -g Kuestenlogik.Bowire.Tool` |
| Winget | `winget upgrade Kuestenlogik.Bowire` |
| Chocolatey | `choco upgrade bowire` |
| Homebrew (planned) | `brew upgrade bowire` |
| MSI / DEB / RPM | Re-run the installer with the new release asset from [GitHub Releases](https://github.com/Kuestenlogik/Bowire/releases) |
| Standalone ZIP / tarball | Extract the new release archive over the install directory |
| Docker | `docker pull ghcr.io/kuestenlogik/bowire:latest` (or pin a specific tag) |

All bundled protocol plugins (gRPC, REST, GraphQL, SignalR, WebSocket, SSE, MQTT, Socket.IO, MCP, OData) move in lockstep with the tool — there is no separate update for them. Sibling-repo plugins under `~/.bowire/plugins/` are **not touched** by a tool update; they keep their installed version and continue working under the new tool as long as the SemVer contract holds (see [Plugin Compatibility](../architecture/compatibility.md)).

After the tool update, verify the new version landed:

```bash
bowire --version
```

## Update sibling-repo plugins

### Via the workbench UI (1.6.0+)

The cleanest path for ad-hoc updates: open **Settings → Plugins**. Every installed plugin appears in the list with its current version. When the configured NuGet feed has a newer release, the row shows an `→ X.Y.Z available` hint and the Update button picks up an accent colour. Click **Update** and the workbench shells out to the same `bowire plugin update <id>` path the CLI uses; the result banner above the list reports outcome + CLI output for debugging.

Toggle **Include pre-release versions when checking for updates** at the top of the panel to bring RCs (`1.0.0-rc.2`, …) into the latest-lookup — that's the only way Amqp v1.0.0-rc.2 / TacticalApi v1.0.0-rc.1 surface as available updates without typing a version string.

Bundled plugins (badge `bundled` next to the id) show their current version too but the Update / Uninstall buttons are disabled with a tooltip pointing back to `dotnet tool update`.

### Via the CLI

```bash
# What's installed and at which version?
bowire plugin list
bowire plugin list -v          # also prints DLL count + plugin.json contents

# Update one sibling plugin (latest stable)
bowire plugin update Kuestenlogik.Bowire.Protocol.Kafka

# Update every installed sibling plugin (stops on the first non-zero exit,
# but continues processing the rest so partial updates land)
bowire plugin update

# Allow pre-release versions (1.6.0+) — pulls 1.0.0-rc.x cuts
bowire plugin update Kuestenlogik.Bowire.Protocol.Amqp --prerelease

# Pin to an exact version (works even when --prerelease isn't set)
bowire plugin update Kuestenlogik.Bowire.Protocol.Amqp --version 1.0.0-rc.2

# Refresh against a custom feed (internal mirror, offline cache, ...)
bowire plugin update --source https://nuget.internal.example/v3/index.json
```

The `--prerelease` flag mirrors `dotnet add package --prerelease` — without it, `bowire plugin update` resolves the latest **stable** version and ignores pre-release tags. The flag only affects version *resolution*; an explicit `--version 1.0.0-rc.2` works either way because the version is pinned.

### Sanity-check after an update

```bash
bowire plugin list -v
bowire plugin inspect Kuestenlogik.Bowire.Protocol.Kafka
```

`plugin list -v` reports each plugin's `resolvedVersion` from its `plugin.json`. `plugin inspect` loads the plugin into a fresh `BowirePluginLoadContext`, walks it for `IBowireProtocol` types, and prints the load result — useful when a plugin install / update completed but Bowire doesn't see the protocol on startup (a `BindingFailureException` here usually means the plugin was built against a Bowire major you don't have; see [Plugin Compatibility](../architecture/compatibility.md)).

## Pre-release versions

Plugin releases on the 1.0-rc line (Amqp, TacticalApi) sit on nuget.org as pre-release versions. Stable consumers ignore them by default — both the workbench panel and `bowire plugin install/update` need explicit opt-in:

```bash
# CLI: --prerelease flag
bowire plugin install Kuestenlogik.Bowire.Protocol.Amqp --prerelease

# CLI: explicit version pin (works without --prerelease)
bowire plugin install Kuestenlogik.Bowire.Protocol.Amqp --version 1.0.0-rc.2
```

The UI toggle ("Include pre-release versions when checking for updates") is the workbench equivalent — it's off by default so a routine "any updates?" check doesn't propose an RC to a user who didn't ask for one.

## Why bundled vs. sibling matters when updating

Bundled plugins ride along with `dotnet tool update`. Sibling plugins don't. So a workflow like "update everything" looks like two commands, not one:

```bash
dotnet tool update -g Kuestenlogik.Bowire.Tool   # bowire + 10 bundled plugins
bowire plugin update                              # every sibling plugin under ~/.bowire/plugins/
```

The split exists because the bundled-plugin set is the part the Bowire repo can guarantee API-compatible at every commit (they're all tested together in CI). Sibling plugins ship from their own repos on their own cadence; pinning every plugin to the tool's version would mean either lying about API maturity (e.g. AMQP 0.2-era was still settling) or forcing a release on every Bowire patch even when the plugin had nothing to ship. The compatibility matrix in [Plugin Compatibility](../architecture/compatibility.md) documents how the SemVer contract between Bowire and each sibling plugin holds.

## Automatic update check (opt-in)

By default Bowire makes no outbound calls on startup — air-gapped and privacy-sensitive installs see no traffic to nuget.org. Operators who *do* want a daily nudge when a newer plugin version ships can opt in:

```bash
# Standalone tool — one-shot opt-in for this run
bowire --update-check
```

```jsonc
// appsettings.json — persistent opt-in for embedded hosts
{
  "Bowire": {
    "PluginUpdateCheck": {
      "Enabled": true,
      "IntervalHours": 24,        // optional, default 24
      "IncludePrerelease": false  // optional, default false
    }
  }
}
```

When enabled, a hosted service runs once on startup and then every `IntervalHours` (default 24) — for every installed *sibling* plugin under `~/.bowire/plugins/`, it asks nuget.org for the latest stable version and writes the result to `~/.bowire/state/update-check.json`. The Bowire UI reads that file and surfaces a count badge over the Settings gear when one or more plugins have an upgrade waiting. Bundled plugins (gRPC, REST, MQTT, …) are not checked — they move with `dotnet tool update`.

A **manual** check is always available regardless of the opt-in: open Settings → Plugins and click "Check now". The opt-in flag only gates the *background* sweep — a direct user click is always a direct user action.

To check from a script without enabling the daily sweep:

```bash
curl http://localhost:5080/bowire/api/plugins/check-updates
```

Returns the same shape that lands in `update-check.json` — `{ checkedAt, includePrerelease, results: [{ packageId, installed, latest, updateAvailable, error? }] }`.

## Air-gapped / offline updates

For installs without outbound NuGet access:

```bash
# On a connected host — pull the plugin + all its transitive deps as .nupkg files
bowire plugin download Kuestenlogik.Bowire.Protocol.Kafka -o ./offline-pkgs

# Move ./offline-pkgs to the air-gapped host, then install from the directory
bowire plugin install Kuestenlogik.Bowire.Protocol.Kafka \
    --source ./offline-pkgs
```

`plugin download` writes the resolved root package + every transitive dep into the output directory; `plugin install --source <dir>` then resolves against that directory instead of nuget.org.

## See also

- [Plugin Architecture](../architecture/plugin-architecture.md) — the four extension points + the registries that discover them.
- [Plugin Compatibility](../architecture/compatibility.md) — which sibling-plugin version works with which Bowire host.
- [Plugin System](../features/plugin-system.md) — the runtime view (what the workbench panel shows, what the REST endpoints do).
