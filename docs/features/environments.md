---
summary: 'Named environments with ${var} placeholder substitution.'
---

# Environments & variables

Named environments with `${var}` placeholder substitution. Define variables once, reuse them across requests, and switch between Dev / Staging / Prod with a single dropdown.

## How it works

Anywhere a request needs a value -- request body JSON, metadata values, the server URL field -- you can use `${name}` placeholders. Before the request fires, Bowire replaces them with values from the **active environment**, falling back to **global variables** when an environment doesn't define the key.

```json
{
  "userId": "${userId}",
  "token": "${apiKey}",
  "host": "${baseUrl}"
}
```

If a placeholder has no matching variable it is left untouched (`${userId}` stays as-is) so typos are visible instead of silently producing empty strings.

To emit a literal `${name}` without substitution, escape the leading dollar: `$${name}`.

## The environment selector

The sidebar shows a globe icon followed by the environment dropdown. The badge next to it counts how many variables are currently available (globals + active environment). Click the gear icon to open the manager.

| Element | Purpose |
|---------|---------|
| Globe icon | Visual marker for the environment row |
| Dropdown | Switch active environment (or "No environment") |
| Count badge | Total variables resolvable right now |
| Gear button | Open the environments manager |

## The manager modal

The manager has two panes:

- **Left pane** -- list of environments with a `+` button to create new ones, plus a separate **Globals** entry at the bottom and Import / Export / Clear all buttons.
- **Right pane** -- variable editor for whatever is selected on the left.

For each environment you can:

- Rename it (the input at the top of the right pane)
- Add and remove key/value pairs
- Delete the entire environment with the trash icon

Variables are stored as plain strings. To pass numbers, booleans or JSON objects through a placeholder, just write the value -- the substitution is textual and the result is interpreted by the request body parser.

## Globals vs. environment variables

| Scope | Defined in | Overridden by |
|-------|-----------|---------------|
| **Globals** | The "Globals" entry in the manager | Active environment with the same key |
| **Environment** | Per-environment variable list | -- |

A typical setup:

- Globals: `apiVersion`, `userAgent`, things that never change
- Each environment: `baseUrl`, `apiKey`, `token`, things that differ per stage

## Where variables are substituted

Substitution happens client-side, just before the request is sent. It applies to:

| Location | Example |
|----------|---------|
| Request body JSON | `{ "user": "${userId}" }` |
| Metadata values | `Authorization: Bearer ${token}` |
| Server URL field | `https://${baseUrl}` |
| Channel send messages (duplex / client streaming) | Same as request body |

Metadata **keys** are not substituted -- only values.

## Persistence

Bowire stores environments in two places, kept in sync automatically:

1. **Browser localStorage** (`bowire_environments`, `bowire_global_vars`, `bowire_active_env`) -- instant updates, no server roundtrip.
2. **Disk** at `~/.bowire/environments.json` -- survives browser changes, profile switches, and CLI runs. Same folder used for plugins.

On startup, Bowire loads from disk first, so opening Bowire in a fresh browser still shows your environments. Every change in the manager is debounced (400 ms) and pushed back to disk.

The disk file is plain JSON and human-readable -- you can edit it in any text editor when Bowire is closed:

```json
{
  "globals": {
    "apiVersion": "v2"
  },
  "environments": [
    {
      "id": "env_abc123",
      "name": "Dev",
      "vars": {
        "baseUrl": "localhost:5001",
        "token": "dev-token-xyz"
      }
    },
    {
      "id": "env_def456",
      "name": "Prod",
      "vars": {
        "baseUrl": "api.example.com",
        "token": "prod-token-secret"
      }
    }
  ],
  "activeEnvId": "env_abc123"
}
```

## Import / Export / Clear all

The manager footer has three buttons:

- **Export** downloads `bowire-environments.json` containing all environments, globals and the active id.
- **Import** loads a previously exported file, replacing the current state.
- **Clear all** wipes everything -- both the localStorage cache and `~/.bowire/environments.json` on disk. This is irreversible and prompts for confirmation.

## REST API

The disk store is also exposed as a tiny REST API for tooling and CI scenarios:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/bowire/api/environments` | `GET` | Returns the current document |
| `/bowire/api/environments` | `PUT` | Replaces the document (validates JSON) |
| `/bowire/api/environments` | `DELETE` | Resets to an empty document |

The browser uses these endpoints internally; you can call them yourself to seed environments from a script.

## Tips

- Use globals for things that never change, environments for things that do.
- Name your environments after your deployment targets (`Dev`, `Staging`, `Prod`) or per-customer (`Acme`, `Globex`).
- Variables are great for **bearer tokens** -- store them once in an environment, reference as `Bearer ${token}` in metadata, and rotate them in one place.
- Combine with [Favorites](favorites-history.md) to quickly switch between environments while exercising the same set of starred methods.
