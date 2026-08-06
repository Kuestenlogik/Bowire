---
title: Mock configuration (schema-mock refinement)
summary: 'A persisted, versioned sidecar that refines a schema-generated mock — per-field response overrides, per-method conditional rules, and an enforced auth requirement.'
---

# Mock configuration

A schema mock ([`bowire mock --schema` / `--grpc-schema` / `--graphql-schema`](../recordings/bwr-format.md)) synthesises plausible-but-generic responses straight from the declared types — strings become `"sample"`, numbers `1`, and so on. **Declared examples in the schema are honoured in preference to those type-defaults** ([#559](https://github.com/Kuestenlogik/Bowire/issues/559)): OpenAPI `example` / `examples` (schema-level, the 3.1 `examples` array, and media-type-level under `content.<type>`), proto2 field defaults (`[default = …]`), and a GraphQL `@example(value: …)` field directive. Where a schema declares nothing, the values are placeholders — and the **mock configuration** is the persisted sidecar that refines them without re-discovering ([#558](https://github.com/Kuestenlogik/Bowire/issues/558)).

The config format defines the versioned envelope, the workspace store, the `--mock-config` flag, and applies **per-field response overrides** at startup ([#558](https://github.com/Kuestenlogik/Bowire/issues/558)). **Per-method conditional rules** are applied live by the workbench editors ([#560](https://github.com/Kuestenlogik/Bowire/issues/560)/[#561](https://github.com/Kuestenlogik/Bowire/issues/561)), and the **auth requirement** is enforced by the mock's auth gate ([#562](https://github.com/Kuestenlogik/Bowire/issues/562)).

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
- **`conditionalRules`** — per-method `request-predicate → response-variant` rules, evaluated by the conditional-rules editor.
- **`auth`** — the auth-requirement block: `required` (bool), `scheme` (`bearer` / `basic` / `apikey`, default bearer), `credential` (the exact accepted value; empty = presence-only), `header` (the header to read; default `Authorization`), and `authRecordingId` (resolves to a captured credential from the auth-recording store, [#563](https://github.com/Kuestenlogik/Bowire/issues/563)). Enforced by the auth gate: when `required`, an HTTP/WebSocket request that presents no credential — or the wrong one — gets a **401 before replay**.

## Using it

**CLI** — pass a config file to a schema mock:

```console
$ bowire mock --schema orders.openapi.yaml --mock-config overrides.json --port 6000
$ curl -s localhost:6000/orders | jq .status
"shipped"          # the override wins over the schema-typed default
```

The override reaches the schema-only modes (`--schema` / `--grpc-schema` / `--graphql-schema`). A recording-file mock (`--recording`) mounts its middleware straight from disk for hot-reload, so overrides don't apply there in this slice.

**From the workbench** ([#560](https://github.com/Kuestenlogik/Bowire/issues/560)) — the Mocks rail has a **Start a schema mock** card: pick a kind (OpenAPI / GraphQL), paste the schema, and Start. It POSTs the `{ schemaKind, schemaInline }` shape to `POST /api/mocks` (the same `MockServer` schema-load path the CLI uses), and on success seeds the mock's configuration artifact so the refinement editors have a target. The started mock shows up in the rail alongside recording-derived mocks. The DI wiring gives the workbench the plugin-contributed schema sources **and** hosting extensions, so a workbench-started mock is reachable at CLI parity (gRPC reflection, REST schema-discovery endpoints).

**The editors** ([#561](https://github.com/Kuestenlogik/Bowire/issues/561)) — a REST schema mock's detail pane carries two cards:

- **Response overrides** — override individual response field values by `(service, method)` and JSON path.
- **Conditional rules** — when a request to `(service, method)` matches a body predicate (`equals` / `contains` / `matches`), serve a response variant instead of the default; distinct from fault injection.

Apply both persists the config (`PUT /config`) **and** applies it live to the running mock (`POST /api/mocks/{id}/config/apply`) — no restart. A conditional rule compiles into a higher-priority match stub, so the existing mock matcher chooses the variant when the predicate matches and falls back to the default (override-applied) response otherwise; re-applying recomputes from the baseline, so edits never compound. The editors apply to **REST (OpenAPI)** schema mocks — a GraphQL/gRPC schema mock serves via a live handler that bypasses the stub middleware, so its detail pane shows a REST-only notice instead.

**Require authentication** ([#562](https://github.com/Kuestenlogik/Bowire/issues/562)) — a schema mock's detail pane also carries a **Require authentication** card (toggle + scheme picker + header + credential), applied through the same persist-and-apply flow. Because the gate is pipeline-level it also covers GraphQL/gRPC schema mocks, not just the stub-based REST path. From the CLI, `--require-auth <token>` starts a mock that demands a matching bearer token:

```console
$ bowire mock --schema orders.openapi.yaml --require-auth s3cret --port 6000
$ curl -s -o /dev/null -w '%{http_code}' localhost:6000/orders            # 401
$ curl -s -o /dev/null -w '%{http_code}' -H 'Authorization: Bearer s3cret' localhost:6000/orders   # 200
```

The gate answers **401 before replay** and exempts the mock's own control surface (`/__bowire/mock/*`). It covers HTTP and WebSocket replay; plugin transports (MQTT, DIS, …) run on their own sockets and are **not** gated.

**Auth recordings** ([#563](https://github.com/Kuestenlogik/Bowire/issues/563)) — instead of pasting a token, an `auth` block can reference a captured credential by id via `authRecordingId`. The credential is resolved **at apply-time** from the per-workspace auth-recording store (`workspaces/<wsId>/auth-recordings/<id>.json`, a `{ id, name, scheme, header, credential }` document) and populated into the gate — so the token itself never has to live in the mock-config sidecar (only the id does). Resolution is scoped to the mock's own workspace, and a referenced id that can't be resolved (no recording, or an empty credential) fails the apply rather than silently weakening the gate. The Require-authentication card has a **Credential source** picker that lists the workspace's recordings (`GET /api/auth-recordings`, credential-free summaries) and binds the choice to `authRecordingId`; picking one replaces the inline-credential field. Creating a recording is still manual for now — write the JSON file (interactive capture is a follow-up, see below).

**Workspace artifact** — the workbench persists a mock's configuration per (workspace, mock) at `workspaces/<wsId>/mocks/<mockId>.json` and reads/writes it over `GET` / `PUT /api/mocks/{mockId}/config`. Persisting to disk (not browser storage) lets the config survive a browser reset, ride the workspace export, and sync via git.

## Not yet

- The auth gate covers HTTP/WebSocket replay only; the plugin transports (MQTT, DIS, …) are not gated.
- `authRecordingId` resolves a **statically captured** credential from the store, and the card can pick one. **Capturing** a recording is still manual (write the JSON file); a first-class capture flow — re-running a `#sec-04` auth flow on resolve, which would be an opt-in outbound call — is a follow-up.
- The workbench schema-mock picker offers OpenAPI + GraphQL (paste); protobuf needs a binary `FileDescriptorSet`, reachable via `POST /api/mocks` with `schemaPath`. Conditional rules + overrides apply to the REST (OpenAPI) schema-mock path only.
- Overriding a field *to* JSON `null` (or removing it) is not expressed — a `null` value is treated as "no override".
