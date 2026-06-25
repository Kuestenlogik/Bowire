---
summary: "Canonical wire schema for `.bww` Bowire-workspace export bundles. Version 2 is the current shape; legacy v1 shapes from the UI and CLI export paths are auto-migrated on read."
---

# `.bww` Workspace Export Schema

The Bowire workspace export bundle (`.bww`) is the portable, single-file representation of a workspace. It carries every persistable bucket regardless of which storage backend the source workspace lives on (browser-only `localStorage`, disk-backed `Workspace.Git`, or a per-user fallback path).

This document is the wire contract. The JS workbench exporter (`exportWorkspaceJson` in `prologue.js`) and the .NET CLI exporter (`RunExportAsync` in `WorkspaceCommand.cs`) both produce this shape; both importers consume it.

## Envelope

```json
{
  "format": "bowire-workspace",
  "version": 2,
  "exportedAt": "<ISO-8601 timestamp>",
  "workspace": { /* identity */ },
  "data":      { /* content   */ }
}
```

| Field | Type | Required | Purpose |
|---|---|---|---|
| `format` | string | yes | Always `"bowire-workspace"`. Importers reject any other value with `"Not a Bowire workspace file (missing format marker)"`. |
| `version` | integer | yes | Schema version. Current canonical: `2`. Legacy `1` (UI-shape) is auto-migrated to `2` on read. |
| `exportedAt` | string (ISO-8601) | yes | When the bundle was produced. Diagnostic. |
| `workspace` | object | yes | Workspace identity — see [workspace identity](#workspace-identity). |
| `data` | object | yes | Workspace content — see [data buckets](#data-buckets). |

## Workspace identity

```json
{
  "id":          "ws_<10-char-hex>",
  "name":        "Petstore staging",
  "color":       "#22c55e",
  "description": "Smoke + happy-path against staging.petstore.example",
  "storage":     "browser-only" | "disk",
  "recordingStorageMode": "both" | "browser-only" | "disk-only",
  "pluginPins":  { "rest": ">=2.0", "grpc": "*" }
}
```

| Field | Type | Required | Purpose |
|---|---|---|---|
| `id` | string | optional | Workspace id when present in the source. Importers allocate a fresh id on `mode: 'new'` regardless. |
| `name` | string | yes | Display name. |
| `color` | string (hex) | yes | Accent colour (e.g. `#22c55e`). Default `#6366f1` when source didn't carry one. |
| `description` | string | optional | Free-text. |
| `storage` | enum | optional | Workspace storage mode. `browser-only` keeps data in `localStorage`; `disk` (default) routes through the disk-backed path when available. |
| `recordingStorageMode` | enum | optional | Legacy field from pre-#212 imports; new importers prefer `storage` and fall back to this if absent. |
| `pluginPins` | object | optional | Required-protocol set (`Dictionary<protocolId, semverString>`) so a recipient gets the "install missing plugins" banner instead of cryptic "no such protocol" errors. |

## Data buckets

Every v2 export carries every bucket — empty array / empty object / `null` when the source workspace doesn't have data of that kind. Readers can iterate without defensive null checks.

```json
{
  "urls":                ["https://petstore.swagger.io/v2"],
  "urlMeta":             { "https://petstore.swagger.io/v2": { "alias": "Petstore" } },
  "urlAliases":          { "https://petstore.swagger.io/v2": "Petstore" },
  "environments":        [ /* … */ ],
  "activeEnvironmentId": "env_dev",
  "globals":             { "apiVersion": "v2" },
  "collections":         [ /* … */ ],
  "collectionsTrash":    [],
  "recordings":          [],
  "recordingsTrash":     [],
  "favorites":           [],
  "benchmarks":          [],
  "flows":               [],
  "scripts":             [],
  "requestBuilderHistory":      [],
  "presets":             { "discover": [ /* … */ ] }
}
```

| Bucket | Type | Browser-mode | Disk-mode | Description |
|---|---|---|---|---|
| `urls` | string[] | ✓ | ✗ (empty) | Server URLs the workspace targets. |
| `urlMeta` | object | ✓ | ✗ (empty) | Per-URL metadata (alias, last status, custom flags). |
| `urlAliases` | object | ✓ | ✗ (empty) | Short-name overlay per URL — separate bucket for legacy compat. |
| `environments` | object[] | ✓ | ✓ | Environment defs (per-env vars). |
| `activeEnvironmentId` | string \| null | ✓ | ✗ (`null`) | Which env was active at export time. |
| `globals` | object | ✓ | ✓ (`globals.json`) | Workspace-wide variables. |
| `collections` | object[] | ✓ | ✓ | Collection defs (each carrying its request items). |
| `collectionsTrash` | object[] | ✓ | ✗ (empty) | Soft-deleted collections (recoverable for N days). |
| `recordings` | object[] | ✓ | ✓ | Saved invocation recordings. |
| `recordingsTrash` | object[] | ✓ | ✗ (empty) | Soft-deleted recordings. |
| `favorites` | object[] | ✓ | ✗ (empty) | Pinned methods / requests. |
| `benchmarks` | object[] | ✓ | ✗ (empty) | Saved benchmark configurations + history. |
| `flows` | object[] | ✓ | ✓ | Saved multi-step flow definitions. |
| `scripts` | object[] | ✗ (empty) | ✓ | Pre/post/assert scripts attached to services + collections. |
| `requestBuilderHistory` | object[] | ✓ | ✗ (empty) | Recent Request-builder executions (last 50). Each entry: `{ id, ts, method, url, params, headers, body, bodyMode, authKind, authData, preScript, postScript, status, durationMs }`. Response body intentionally NOT carried — only the request shape + status/timing. |
| `presets` | object | ✓ | ✗ (empty) | Per-mode (discover / mocks / proxy / …) saved configs. Each value is an array of preset entries. |

### Bucket-level invariants

- **Order**: arrays preserve insertion order; readers should not depend on alphabetical ordering.
- **Identity**: every entry in object-arrays carries an `id` field. Items without `id` are silently dropped by the importer (the per-entity file layout keys on id).
- **References**: cross-bucket references use ids (e.g. `activeEnvironmentId` points at an `environments[].id`).
- **Secrets**: SHOULD NOT be exported in `.bww`. Secrets ride on per-environment `.secrets.json` overlays (disk-mode) or `secret.<NAME>` workspace-wide secrets (disk-mode), both of which are gitignored. The .bww export deliberately drops them.

## Versions

| Version | Where produced | Status |
|---|---|---|
| **v2** | both writers (current canonical) | **active** — emitted by every v2.0.x+ writer |
| **v1 (UI shape)** | `prologue.js exportWorkspaceJson` pre-#282 | legacy — auto-migrated on read; shim retires in [v3.0.0](https://github.com/Kuestenlogik/Bowire/issues/283) |
| **v1 (CLI shape)** | `WorkspaceCommand.cs RunExportAsync` pre-#282 | legacy — auto-migrated on read; shim retires in [v3.0.0](https://github.com/Kuestenlogik/Bowire/issues/283) |

### Legacy v1-UI shape

```json
{
  "format": "bowire-workspace",
  "version": 1,
  "exportedAt": "…",
  "workspace": { "name": "…", "color": "…", "description": "…", "storage": "…" },
  "data": {
    "bowire_server_urls":        [ /* … */ ],
    "bowire_url_meta":           { /* … */ },
    "bowire_environments":       [ /* … */ ],
    "bowire_active_env":         "…",
    "bowire_collections":        [ /* … */ ],
    "bowire_recordings":         [ /* … */ ],
    "bowire_flows":              [ /* … */ ],
    "bowire_plugin_pins":        { /* … */ },
    "presets":                   { /* … */ }
  }
}
```

Differences vs v2:
- `data` fields use raw `bowire_*` localStorage bucket names instead of canonical short names.
- `pluginPins` lived inside `data`, not on workspace identity.
- `recordings` / `scripts` / `flows` / `favorites` / `benchmarks` were optional and frequently absent.

### Legacy v1-CLI shape

```json
{
  "workspaceFormatVersion": 1,
  "exportedAt": "…",
  "environments": [ /* … */ ],
  "collections":  [ /* … */ ],
  "recordings":   [ /* … */ ],
  "scripts":      [ /* … */ ],
  "flows":        [ /* … */ ]
}
```

Differences vs v2:
- No `format` header.
- Version field renamed (`workspaceFormatVersion` → `version`).
- Per-kind arrays at top level instead of nested under `data`.
- No `workspace` identity wrapper.
- Browser-only buckets (urls / globals / favorites / benchmarks / presets) absent — auto-migrated to empty defaults.

### Migration shim

Both readers (`importWorkspaceJson` in `prologue.js` and `RunImportAsync` in `WorkspaceCommand.cs`) detect the legacy shape and rewrite the in-memory payload to v2 before the rest of the pipeline runs. The migrated payload carries a `_migratedFrom: 'ui-v1' | 'cli-v1'` diagnostic field — useful in support transcripts; readers ignore it.

The shim retires in [v3.0.0 (#283)](https://github.com/Kuestenlogik/Bowire/issues/283); v3+ readers reject anything that isn't v2 with a hint to re-export through any v2.x release first.

## Importer behaviour

### Conflict modes

Importers accept a mode flag controlling how the incoming workspace lands relative to existing state:

| Mode | Effect |
|---|---|
| `new` (default) | Allocate a fresh workspace id. Source identity (`workspace.id`) is ignored. |
| `replace` | Replace the bucket contents of the workspace named in `opts.target`. |
| `merge` | Concat arrays + assign-merge objects into `opts.target`. Incoming values win on conflict. |

### Rejection paths

| Condition | Action |
|---|---|
| Root is not a JSON object | Throw `"Not a Bowire workspace file (missing format marker)"`. |
| `format !== "bowire-workspace"` post-migration | Same as above. |
| `version > CanonicalFormatVersion` (= 2 today) post-migration | Throw `"Unsupported workspace format version: <n> (expected 2; legacy 1 shapes are auto-migrated, anything else is rejected)"`. CLI returns sysexit 65 (`EX_DATAERR`). |
| `version === 1` (legacy) | Pass through migration shim → re-read as v2. |
| Future writes carrying additional buckets the reader doesn't recognise | Ignored. Forward-compat — v2 readers tolerate v2.x extensions, only major-version bumps trigger rejection. |

## Round-trip guarantees

A workspace exported on v2.x and re-imported on v2.x preserves:

- Every bucket listed in [Data buckets](#data-buckets).
- Workspace identity except `id` (re-allocated on `mode: 'new'`).
- Environment ids + cross-bucket references.

Things that do NOT round-trip through `.bww`:
- **Secrets** (intentionally excluded — see [bucket-level invariants](#bucket-level-invariants)).
- **Per-method preset state for unknown modes** — exporters only consult the `_WORKSPACE_PRESET_MODES` allowlist; presets attached to a mode added in a later release won't survive a round-trip through an older Bowire.
- **Browser-session ephemera** — open tabs, scroll position, last-viewed-rail. Tracked under `_WORKSPACE_BROWSER_STATE_KEYS` and explicitly excluded.

## Related

- [Workspaces (workbench feature)](../features/workspace.md) — how workspaces work in the UI.
- Issue [#282](https://github.com/Kuestenlogik/Bowire/issues/282) — format unification.
- Issue [#283](https://github.com/Kuestenlogik/Bowire/issues/283) — v1 migration shim retirement (v3.0.0).
