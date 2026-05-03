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
