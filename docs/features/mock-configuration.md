---
title: Mock configuration (schema-mock refinement)
summary: 'A persisted, versioned sidecar that refines a schema-generated mock — per-field response overrides today, with per-method conditional rules and an auth requirement modelled for the editors that build on it.'
---

# Mock configuration

A schema mock ([`bowire mock --schema` / `--grpc-schema` / `--graphql-schema`](../recordings/bwr-format.md)) synthesises plausible-but-generic responses straight from the declared types — strings become `"sample"`, numbers `1`, and so on. That is a great starting point, but the values are placeholders. The **mock configuration** is the persisted sidecar that refines them without re-discovering ([#558](https://github.com/Kuestenlogik/Bowire/issues/558)).

This is the foundation slice: it defines the versioned config format, the workspace store, the `--mock-config` flag, and applies **per-field response overrides** at startup. Conditional rules and the auth requirement are part of the format but are consumed by later slices (the workbench editors, [#560](https://github.com/Kuestenlogik/Bowire/issues/560)/[#561](https://github.com/Kuestenlogik/Bowire/issues/561), and the auth gate, [#562](https://github.com/Kuestenlogik/Bowire/issues/562)).

## The file

A mock configuration is a JSON document:

```json
{
  "configFormatVersion": 1,
  "source": { "kind": "openapi", "path": "./orders.openapi.yaml" },
  "fieldOverrides": [
    { "service": "Orders", "method": "listOrders", "jsonPath": "$.status", "value": "shipped" },
    { "jsonPath": "$.items[0].sku", "value": "SKU-OVERRIDE" }
  ],
  "conditionalRules": [],
  "auth": null
}
```

- **`configFormatVersion`** — the envelope version. Parsing is version-tolerant: an absent version defaults to the current one, and a *newer* version still loads (unknown fields are ignored).
- **`fieldOverrides`** — the arm applied today. Each entry sets the value at `jsonPath` in the response of the matching `(service, method)`.
  - `service` / `method` — the scope. Absent, empty, or `"*"` is a **wildcard** (applies to every method). Otherwise a case-insensitive match against the discovered service tag / operation id.
  - `jsonPath` — the path into the response body, using the same syntax as the mock body matchers: `$.status`, `user.role`, `items[0].sku`. Missing intermediate objects are created; an out-of-range array index is a no-op (arrays are never grown). A `null` / absent value is a no-op.
  - `value` — the override value, as arbitrary JSON (string, number, object, array).
- **`conditionalRules`** — per-method `request-predicate → response-variant` rules. Modelled here; evaluated by the conditional-rules editor.
- **`auth`** — the auth-requirement block (`required`, `scheme`, `authRecordingId`, `header`). Modelled here; enforced by the auth slice.

## Using it

**CLI** — pass a config file to a schema mock:

```console
$ bowire mock --schema orders.openapi.yaml --mock-config overrides.json --port 6000
$ curl -s localhost:6000/orders | jq .status
"shipped"          # the override wins over the schema-typed default
```

The override reaches the schema-only modes (`--schema` / `--grpc-schema` / `--graphql-schema`). A recording-file mock (`--recording`) mounts its middleware straight from disk for hot-reload, so overrides don't apply there in this slice.

**Workspace artifact** — the workbench persists a mock's configuration per (workspace, mock) at `workspaces/<wsId>/mocks/<mockId>.json` and reads/writes it over `GET` / `PUT /api/mocks/{mockId}/config`. Persisting to disk (not browser storage) lets the config survive a browser reset, ride the workspace export, and sync via git.

## Not yet

- The workbench UI to author overrides and rules (a picker to start a schema mock, per-field and conditional-rule cards) ships in [#560](https://github.com/Kuestenlogik/Bowire/issues/560) / [#561](https://github.com/Kuestenlogik/Bowire/issues/561).
- Serve-time evaluation of `conditionalRules` and enforcement of the `auth` requirement are deferred to those slices and [#562](https://github.com/Kuestenlogik/Bowire/issues/562).
- Overriding a field *to* JSON `null` (or removing it) is not expressed — a `null` value is treated as "no override".
