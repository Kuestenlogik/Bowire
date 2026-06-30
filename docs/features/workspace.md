---
title: Workspace
summary: "A workspace is your project folder — URLs, environments, collections, recordings, scripts. It can live in localStorage (browser-only mode), in a git-friendly directory (Workspace.Git), or as a portable single-file `.bww` export bundle you commit / share / round-trip between machines."
---

# Workspaces (.bww)

A **workspace** is Bowire's project-folder abstraction: every URL you discover, every environment + variable + secret, every collection / recording / benchmark / flow lives inside one. The workbench always has exactly one active workspace; switching workspaces switches every list at once.

Workspaces have three storage faces:

1. **Browser-backed** — every workspace's data sits in `localStorage` under a per-workspace key prefix (`bowire_ws_<id>_*`). Default for the Tool. Survives reloads, scoped per-browser-profile.
2. **Disk-backed (per-entity files)** — the `Kuestenlogik.Bowire.Workspace.Git` package materialises a workspace as a directory of per-entity files (`workspace.json`, `environments/*.json`, `collections/*/`, `recordings/*.json`, `scripts/*.js`, `secrets/*`). Designed for git review (see [Git-backed workspace](#git-backed-workspace) below).
3. **`.bww` export bundle** — a single JSON file carrying the full state. Produced via the Workspace-detail header's **Save as… → Export to .bww file** action; imported via the create-workspace dialog (or via the App-drawer **Import workspace…**). Both the workbench and the `bowire workspace export` CLI produce the SAME canonical format — full schema in [docs/format/bowire-workspace.md](../format/bowire-workspace.md).

This doc focuses on `.bww` — the portable export format — and the Git-backed directory layout. For the workbench-side UX (create, switch, manage, save-as-template), see the Workspaces rail in the running app.

## What a `.bww` file contains

A `.bww` is a single JSON document with a versioned envelope. The current canonical version is **v2** (see [#282](https://github.com/Kuestenlogik/Bowire/issues/282)); pre-unification v1 shapes from either the UI or CLI export path are auto-migrated on read. Migrated payloads carry a `_migratedFrom: 'ui-v1' | 'cli-v1'` diagnostic field. The shim retires in [v3.0.0 (#283)](https://github.com/Kuestenlogik/Bowire/issues/283).

A minimal v2 export looks like:

```json
{
  "format": "bowire-workspace",
  "version": 2,
  "exportedAt": "2026-06-24T08:42:13.421Z",
  "workspace": {
    "id": "ws_abc1234567",
    "name": "Petstore staging",
    "color": "#22c55e",
    "description": "Smoke + happy-path against staging.petstore.example",
    "pluginPins": { "rest": ">=2.0", "grpc": "*" }
  },
  "data": {
    "urls": ["https://petstore.swagger.io/v2"],
    "urlMeta": { /* per-URL metadata */ },
    "environments": [ /* … */ ],
    "activeEnvironmentId": "env_dev",
    "globals": { "apiVersion": "v2" },
    "collections": [ /* … */ ],
    "recordings": [], "scripts": [], "flows": [],
    "favorites": [], "benchmarks": [],
    "presets": { "discover": [ /* per-method saved configs */ ] }
  }
}
```

| Envelope field | Type | Purpose |
|---|---|---|
| `format` | string | Always `"bowire-workspace"`. Importers reject any other value before reading. |
| `version` | number | Schema version. v2 = current canonical; v1 is auto-migrated. |
| `exportedAt` | ISO timestamp | When the bundle was produced. Diagnostic. |
| `workspace` | object | Workspace identity (id, name, color, description, plugin pins). |
| `data` | object | Workspace content — every persistable bucket. Sparse exports still include every bucket as `[]` / `{}` / `null` so readers can iterate without null checks. |

**See [docs/format/bowire-workspace.md](../format/bowire-workspace.md)** for the full per-bucket schema, browser-vs-disk content differences, legacy v1 shape descriptions, and migration shim behaviour.

> **Note:** the on-disk shape produced by `bowire workspace init` (per-entity files, see below) is NOT the same as the `.bww` single-file bundle. The two are interoperable — `bowire workspace export <dir>` produces a `.bww`; `bowire workspace import <file.bww>` materialises a per-entity directory.

## Filename

Workspace exports are saved as `<workspace-name>.bww`. Names with characters not safe for filesystems (slashes, colons, &c) get sanitised to `_`. The `.bww` extension is the Bowire-workspace canonical extension; importers also accept `.json` for compatibility with hand-edited files.

The pre-v2.0.1 convention (`bowire-workspace-<name>.bowire.json`) was renamed — the type is identified by the file's `format` header, not the filename.

## Templates on create

When you create a new workspace from the workbench (topbar workspace dropdown → **New workspace…**, or **+ New workspace** in the Workspaces rail / Home tile), Bowire offers a template picker so the workspace starts with realistic seed data instead of an empty page.

The dialog separates **Start from scratch** (no template, blank workspace) from the template list (filterable). Templates ship built-in; user-saved templates appear in the same list with a trailing delete icon.

| Built-in template | What it seeds |
|---|---|
| **REST API testing** | `https://petstore.swagger.io/v2` as the discovery URL — Bowire auto-discovers Pet / Store / User services on first connect. `baseUrl` + `apiToken` globals + a starter collection with two ready-to-invoke calls (`GET /pet/findByStatus`, `POST /pet`). |
| **gRPC services** | `grpcs@grpcb.in:443` (server-reflection enabled) plus `service` + `method` placeholder globals and an empty starter collection. Discovery populates a Pet / Empty / Helloer tree on connect. |
| **Mock server build** | Petstore as the seed URL (discovery-enabled) plus an empty `Mock targets` collection ready to capture recordings as mock fixtures. |
| **Multi-protocol smoke test** | Petstore REST + `wss://ws.postman-echo.com/raw` + `grpcs@grpcb.in:443` — three different wire formats in one workspace for cross-protocol coverage runs. |

The picked template is remembered as the default for the next workspace you create.

### Save as template

The active workspace can be saved as a user template via the Workspaces rail's per-row **Save as template** action (bookmark icon), the workspace-detail header's **Save as template…** button, or the same action on any non-active workspace in the overview list. User templates show up under **Or pick a template** in the create dialog with a delete affordance for cleanup.

User templates snapshot the workspace's URLs, env vars, collections, global vars, plugin pins, and presets. They live in `localStorage` under `bowire_user_workspace_templates` (per-user, cross-workspace) — they're per-machine, not synced across machines.

## Git-backed workspace (per-entity files)

The `Kuestenlogik.Bowire.Workspace.Git` runtime materialises a workspace as a directory of per-entity files designed for review under version control. `bowire workspace init <path>` creates the layout at any directory:

```
my-workspace/
  workspace.json                   manifest (id / name / color / schema version / plugin pins)
  .gitignore                       excludes secrets + cache files
  environments/
    staging.json                   non-secret vars (committed)
    staging.secrets.json           secret overlay — gitignored
    staging.example.json           template for the team (committed)
    production.json
  globals.json                     workspace globals (single file)
  collections/
    payments/
      collection.json              collection metadata
      Login.req.json               one file per request
      RefundFlow.req.json
      pre-request.js               collection-scope pre-script
      post-response.js
  recordings/
    login-flow.json                recording manifest
    bodies/                        large captured payloads (gitignored)
  scripts/
    Auth.Login.pre.js              service.method pre-script
    Pets.GetById.post.js
    Pets.GetById.assert.js
  flows/
    smoke.json
  secrets/                         workspace-wide secret values
    GH_TOKEN                       one file per named secret (gitignored body)
    DB_PASSWORD
```

The directory layout decouples the workspace state into reviewable units — renaming an environment touches one file, editing a script's JavaScript is a normal `.js` diff, adding a request creates a new file rather than mutating a bundle.

### Secret separation (#151)

Two complementary surfaces keep secrets out of git:

- `environments/<env>.secrets.json` — per-environment overlay. Merged with `<env>.json` at read time so `{{API_KEY}}` resolves whether the value lives in either file. The non-secret file is committed; the secret overlay is gitignored.
- `secrets/<NAME>` — workspace-wide named secret. Resolved through the reserved `secret.<NAME>` source prefix. One file per secret name; the directory structure is committed (so the team sees which secret names are expected), the file bodies are gitignored.

### `bowire workspace init`

```bash
$ bowire workspace init ./payments-team
Initialised workspace at /Users/me/work/payments-team
  → workspace.json (manifest, schema v1)
  → .gitignore (secrets + cache excluded)
  → environments/ collections/ recordings/ scripts/ flows/ secrets/ (empty)
  → git init done — first commit pending

Next: cd payments-team && git add . && git commit -m "Initial workspace"
```

Flags:
- `--name <display name>` — workspace name written into `workspace.json`. Defaults to the directory's basename.
- `--color <hex>` — accent color (e.g. `#22c55e`). Defaults to `#6366f1`.
- `--no-git` — skip the trailing `git init`. Useful when initialising inside an existing repository.

### `bowire workspace export / import`

| Command | Effect |
|---|---|
| `bowire workspace export <dir> <out.bww>` | Bundles a per-entity directory into a single `.bww` file (suitable for sharing). |
| `bowire workspace import <file.bww> <dir>` | Materialises a `.bww` bundle into the per-entity directory shape. |

The two formats are equivalent round-trip targets; the workbench's create-workspace dialog accepts both `.bww` files and per-entity directories (when running the disk-backed runtime).

### Storage root resolution

A workspace's `storageRoot` field (when set) points at a directory like the one above. The workbench and CLI route per-workspace reads + writes through that path instead of the per-user `~/.bowire/workspaces/<id>/` default. The two storage modes compose orthogonally with the storage-mode setting (`both` / `browser-only` / `disk-only`).

When `storageRoot` is unset, every per-workspace file lands under `~/.bowire/workspaces/<id>/` — the per-user storage layout.

### Package boundary

The Workspace.Git runtime (per-entity reader/writer, `FileSystemWatcher`, secret-overlay merge, workspace lockfile, SSE producer) ships as a **separate optional NuGet package** — `Kuestenlogik.Bowire.Workspace.Git` — and is NOT in core `Kuestenlogik.Bowire`. The `Workspace.<backend>` namespace leaves room for future storage backends (`Workspace.S3`, `Workspace.Sql`, …) under the same shape. This keeps embedded ASP.NET hosts free of file-IO machinery they didn't ask for: a stock `app.MapBowire(...)` call without the extension package referenced sees the legacy per-user storage path and runs no background watcher.

Standalone `Kuestenlogik.Bowire.Tool` carries the package transitively so `bowire` from the command line gets the full git-workspace surface out of the box. Embedded hosts opt in with an explicit package reference when they want it.

`BowireUserContext.GetWorkspacePath` ships in core as a path-resolver seam with zero new dependencies — the extension package plugs into it without touching the host's dependency graph.

## Export workflow

From the workbench:

1. **Workspaces rail** (or workspace-detail header) → **Save now** → flushes every in-flight autosave bucket to durable storage.
2. **Export…** in the same header (or per-row tool) → produces a `<workspace-name>.bww` download.

The `.bww` is a snapshot of the workspace at the moment of export. Edits made afterward stay in the live workspace; re-export to update the snapshot.

## Import workflow

From the workbench:

1. **Topbar workspace chip** → **New workspace…** → in the create dialog, switch to the **Import .bww** tab (or drop the `.bww` onto the dialog).
2. The importer validates the `format` header, migrates the schema version if older, and materialises the workspace into the operator's storage (localStorage in browser-only mode; per-entity directory if Workspace.Git is active).

A successful import lands as a NEW workspace (separate ID) — the importer doesn't overwrite the active workspace's state. To replace a workspace, delete it first then import.

## Version control patterns

The `.bww` single-file bundle is git-friendly:

- **Human-readable JSON** — written with `WriteIndented = true`, so diffs are clean and reviewable.
- **No secrets by default** — URLs and environment names are safe to commit. Tokens and API keys should live in `environments/<env>.secrets.json` (per-entity layout) or be referenced via `{{secret.NAME}}` (workspace-wide secrets).
- **Deterministic structure** — fields are always serialised in the same key order, minimising noise in diffs.

For team workflows, prefer the **per-entity directory** layout (Workspace.Git) over the single-file `.bww`. Per-entity gives clean file-level diffs (rename one env, touch one file) instead of a single ever-changing bundle.

### Recommended `.gitignore` entry

If your project tracks `.bww` exports of personal workspaces (not shared team workspaces), exclude them:

```
# Bowire workspace exports — per-user state, not for the team
*.bww
```

For team-shared per-entity workspaces, the layout's own `.gitignore` (produced by `bowire workspace init`) already excludes secrets.

## Combining with environments

The workspace's `environments` and `globals` follow the same schema regardless of storage mode (single `.bww` or per-entity files). When a workspace is loaded, its environments are available alongside any user-level environments.

This means you can have:

- **Project-level** environments in the workspace — shared across the team
- **Personal** environments scoped to the user — private tokens, local overrides

## Combining with collections

Collections stored in the workspace are loaded alongside any user-level collections. This lets you ship a set of "starter" collections with the project while team members add their own.

## Tips

- Use workspaces to **standardise your team's API testing setup** — everyone gets the same URLs, environments, and starter collections.
- For team-shared workspaces, prefer the per-entity directory layout (`bowire workspace init`) so secret separation, file-level diffs, and per-script editing all work out of the box.
- For one-off sharing (sending a workspace to a teammate, archiving a snapshot before a big change), use `.bww` export — single file, easy to attach.
- For open-source projects, include a `workspace.example.json` (or a `.bww` with placeholder tokens) so contributors can get started quickly.

See also: [Workspaces (rail + env vars)](workspaces.md), [Collections](collections.md)
