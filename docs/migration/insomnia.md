---
title: Migrating from Insomnia
summary: 'Import an Insomnia export into Bowire, and what maps one-to-one, what changes shape and what has no equivalent.'
---

# Migrating from Insomnia

## Export from Insomnia

- **Insomnia 10 and later:** export the collection as a file. You get a v5 YAML file starting with `type: collection.insomnia.rest/5.0`.
- **Earlier versions:** use *Export Data → Insomnia v4*, as JSON or YAML.

Bowire reads both.

## Import into Bowire

Workbench: right-click **Collections → Import files…** and pick the file. CLI: `bowire import insomnia.yaml`.

## What maps one-to-one

| Insomnia | Bowire |
|----------|--------|
| Workspace / collection | Collection |
| Folder | A path in front of the item's name (`Orders / List orders`) |
| HTTP request: method, URL, headers, query parameters, body | Collection item (REST) |
| `{{ _.base_url }}`, `{{ base_url }}` | `{{base_url}}` |
| `{% uuid %}` | `{{uuid}}` |
| `{% now 'iso-8601' %}` / `'unix'` / `'millis'` | `{{timestamp}}` / `{{now}}` / `{{nowMs}}` |
| Sub-environment | Environment, with the base environment's values merged in |
| Bearer, API key auth (also from a folder) | `Authorization` header / the key's header, cookie or query parameter |

## What changes shape

- **Nested environment values** (`{"api": {"version": "v2"}}`, used as `{{ _.api.version }}`) become dotted names: `{{api.version}}`.
- **Folder variables** are merged into every environment. Bowire has no per-folder variables.
- **Basic auth** becomes an `Authorization: Basic …` header. With variables in the user name or password it cannot be encoded ahead of time. The report asks you to set Basic auth on the Bowire environment instead.
- **Form bodies** (`application/x-www-form-urlencoded`) are joined into one urlencoded body.
- **GraphQL requests** become a REST `POST` with their `{"query": …}` body. For schema-aware calls, add the endpoint as a GraphQL source.
- **Private environments** come across, and each of their values is listed as a clear-text credential.

## What has no equivalent

Each of these is named in the import report:

- `{% response %}` chaining tags stay as written. Rewrite them as `{{response.path}}` [response chaining](../features/collections.md#response-chaining-between-items).
- Faker, prompt and file tags stay as written.
- Pre-request and after-response scripts: Bowire scripts use their own `ctx.*` API. Port them by hand, or write the checks as assertions.
- OAuth 2, AWS IAM, Digest and NTLM auth: set them on the Bowire environment, which supports all four.
- gRPC and WebSocket requests: point Bowire at the service; it discovers them.
- Multipart and file bodies, cookie jars, API specs (add the spec as a schema source instead), unit tests.
