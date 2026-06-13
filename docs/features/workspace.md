---
summary: "A workspace file bundles Bowire's connection and configuration state into a single JSON file that you can commit to version control, share with teammates, or load on a different m"
---

# Workspace Files (.blw)

A workspace file bundles Bowire's connection and configuration state into a single JSON file that you can commit to version control, share with teammates, or load on a different machine to reproduce the same setup.

## What a .blw file contains

| Field | Type | Description |
|-------|------|-------------|
| `urls` | `string[]` | Server URLs to connect to |
| `environments` | `object[]` | Environment definitions (same shape as `environments.json`) |
| `globals` | `{ key: value }` | Global variables |
| `collections` | `object[]` | Collection definitions with their items |

A minimal workspace:

```json
{
  "urls": ["https://api.example.com:443"],
  "environments": [
    {
      "id": "env_dev",
      "name": "Dev",
      "vars": {
        "baseUrl": "localhost:5001",
        "token": "dev-token"
      }
    },
    {
      "id": "env_prod",
      "name": "Prod",
      "vars": {
        "baseUrl": "api.example.com",
        "token": "prod-token"
      }
    }
  ],
  "globals": {
    "apiVersion": "v2",
    "userAgent": "bowire/1.0"
  },
  "collections": []
}
```

## Templates on create

When you create a new workspace from the workbench (topbar workspace dropdown → **New workspace…** or the **+** button in the Workspaces rail), Bowire offers a template picker so the workspace starts with realistic seed data instead of an empty page.

| Template | What it seeds |
|----------|---------------|
| **Empty** | No URLs, no env vars, no collections. The default. |
| **REST API testing** | A sample URL (httpbin.org), `baseUrl` + `apiToken` global variables, and a starter collection with GET + POST stubs. |
| **gRPC services** | A gRPC URL prefix (`grpc@…`) ready for a `.proto` upload, plus `service` + `method` placeholder globals and an empty starter collection. |
| **Mock server build** | A starter URL pointed at postman-echo, plus an empty collection ready to capture recordings as mock fixtures. |
| **Multi-protocol smoke test** | REST + WebSocket + gRPC URLs in one workspace, ready for cross-protocol coverage runs. |

The picked template is remembered as the default for the next workspace you create — convenient when you spin up several workspaces of the same shape (e.g. five staging environments that all start as REST).

Templates write directly to the new workspace's per-workspace localStorage bucket. The workbench reloads once after applying a non-empty template so the in-memory state hydrates from the freshly seeded buckets.

## Git-backed workspace (per-entity files)

Beyond the single-file `.blw` bundle, Bowire supports a per-entity directory layout designed for review under version control. `bowire workspace init <path>` materialises this shape at any directory:

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

### Storage root resolution

A workspace's `storageRoot` field (when set) points at a directory like the one above. The workbench and CLI route per-workspace reads + writes through that path instead of the per-user `~/.bowire/workspaces/<id>/` default. The two storage modes compose orthogonally with the storage-mode setting (`both` / `browser-only` / `disk-only`) introduced for chunked recording storage.

When `storageRoot` is unset, every per-workspace file lands under `~/.bowire/workspaces/<id>/` — the legacy single-user layout, preserved exactly.

### Phase 2

The runtime workbench still routes existing workspaces through the legacy `~/.bowire/` per-user store; full read/write at `storageRoot` (including the filesystem watcher that reflects external edits back into the live workbench) lands in v2.1. The CLI's `init` produces the directory shape today so teams can stage their workspace under git and migrate when the workbench-side wiring ships.

### Package boundary

The Phase 2 runtime (per-entity reader/writer, `FileSystemWatcher`, secret-overlay merge, workspace lockfile, SSE producer) ships as a **separate optional NuGet package** — `Kuestenlogik.Bowire.Workspace.Git` — and is NOT in core `Kuestenlogik.Bowire`. The `Workspace.<backend>` namespace leaves room for future storage backends (`Workspace.S3`, `Workspace.Sql`, …) under the same shape. This keeps embedded ASP.NET hosts free of file-IO machinery they didn't ask for: a stock `app.MapBowire(...)` call without the extension package referenced sees the legacy per-user storage path and runs no background watcher.

Standalone `Kuestenlogik.Bowire.Tool` carries the package transitively so `bowire` from the command line gets the full git-workspace surface out of the box. Embedded hosts opt in with an explicit package reference when they want it.

Phase 1's `BowireUserContext.GetWorkspacePath` seam ships in core because it's a pure path resolver with zero new dependencies — the extension package plugs into it via the seam without touching the host's dependency graph.

## File location

The workspace file is read from and written to the **working directory** where Bowire was launched. The file is always named `.blw` (no base name, just the extension).

```
my-project/
  .blw              <-- workspace file
  src/
  tests/
```

When no `.blw` file exists, the workspace endpoints return empty defaults and the UI operates normally without workspace-driven configuration.

## Loading and saving

Bowire exposes two endpoints for workspace operations:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/bowire/api/workspace` | `GET` | Returns the current workspace document (or empty defaults) |
| `/bowire/api/workspace` | `PUT` | Replaces the workspace file on disk |

The GET endpoint reads the file on every request, so external edits (e.g. from `git pull`) are picked up immediately.

The PUT endpoint validates the incoming JSON and writes it to disk with pretty-printed formatting.

## Version control

The `.blw` file is designed to be version-control friendly:

- **Human-readable JSON** -- the file is written with `WriteIndented = true`, so diffs are clean and reviewable.
- **No secrets by default** -- URLs and environment names are safe to commit. Tokens and API keys can be stored in environment variables, but consider using `.gitignore` for workspaces that contain sensitive values.
- **Deterministic structure** -- fields are always serialized in the same order, minimizing noise in diffs.

### Recommended .gitignore entry

If your workspace contains sensitive environment variables, add the file to `.gitignore`:

```
# Bowire workspace (contains tokens)
.blw
```

Alternatively, keep the workspace committed but move secrets into a separate file or system environment variables.

## Team sharing

A committed `.blw` file gives every team member the same starting configuration:

1. Clone the repository.
2. Run `bowire` from the project root.
3. Bowire loads the `.blw` file and pre-populates server URLs, environments, globals, and collections.

This eliminates the "how do I connect to the API?" onboarding question. New team members get a working setup immediately.

### Workflow

```bash
# Developer A sets up the workspace
bowire --url https://api.staging.example.com
# (configure environments, save collections, etc.)
# The UI writes the workspace via PUT /bowire/api/workspace

git add .blw
git commit -m "Add Bowire workspace with staging config"

# Developer B clones and runs
git clone repo
cd repo
bowire
# Bowire loads the workspace — same URLs, environments, collections
```

## Combining with environments

The workspace file's `environments` and `globals` fields follow the same schema as `~/.bowire/environments.json`. When a workspace is loaded, its environments are available alongside any user-level environments.

This means you can have:

- **Project-level** environments in `.blw` -- shared across the team
- **Personal** environments in `~/.bowire/environments.json` -- private tokens, local overrides

## Combining with collections

Collections stored in the workspace file are loaded alongside any user-level collections. This lets you ship a set of "starter" collections with the project while team members add their own.

## Empty workspace

When the `.blw` file does not exist or is empty/corrupt, the workspace endpoints return:

```json
{
  "urls": [],
  "environments": [],
  "globals": {},
  "collections": []
}
```

Bowire operates normally in this case -- all configuration comes from user-level storage (`~/.bowire/` and `localStorage`).

## Tips

- Use workspaces to **standardize your team's API testing setup** -- everyone gets the same URLs, environments, and starter collections.
- Keep the `.blw` file next to your project's source code so it travels with the repo.
- For open-source projects, include a `.blw` file with example URLs and placeholder tokens so contributors can get started quickly.
- The workspace is a snapshot -- it does not auto-sync with the UI. Save explicitly when you want to update the committed file.

See also: [Environments & Variables](environments.md), [Collections](collections.md)
