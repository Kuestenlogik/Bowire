---
title: Migrating from Bruno
summary: 'Import a Bruno collection, .bru files or OpenCollection YAML, into Bowire, and what changes on the way.'
---

# Migrating from Bruno

Bruno keeps a collection as a folder in your repository. Bowire reads that folder directly, in either of Bruno's formats:

- **Bru**: `bruno.json`, `collection.bru`, one `.bru` file per request, `folder.bru` per folder, `environments/*.bru`. This is the format before Bruno 3.
- **OpenCollection YAML**: `opencollection.yml`, one `.yml` file per request, `folder.yml` per folder, `environments/*.yml`. This is the default since Bruno 3.1. A *bundled* OpenCollection file holds the whole collection in one `.yml`.

## Import into Bowire

Workbench: right-click **Collections → Import folder…** and pick the collection folder; a bundled file goes through **Import files…**. CLI: `bowire import path/to/collection`. Picking the folder above the collection works too.

## What maps one-to-one

| Bruno | Bowire |
|-------|--------|
| Collection | Collection |
| Folder | A path in front of the item's name, using the folder's display name; `seq` order is kept |
| HTTP request: method, URL, headers, query, body (JSON, text, XML, SPARQL) | Collection item (REST) |
| `{{variable}}` | `{{variable}}` |
| `{{$randomUUID}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt}}` | `{{uuid}}`, `{{now}}`, `{{timestamp}}`, `{{random}}` |
| Environment | Environment; OpenCollection `extends` is resolved |
| Collection and folder headers | Headers on every request below them |
| `auth: inherit` | The folder's or the collection's auth |
| Bearer, API key auth | `Authorization` header / the key's header or query parameter |

## What changes shape

- **Path parameters** (`/pets/:id` with `params:path`) are filled in from their values: `/pets/42`.
- **Collection and folder variables** (`vars:pre-request`) are merged into every environment. Bowire has no per-folder variables.
- **Secret variables** keep their names, but their values never leave Bruno's local storage. They come across empty and are listed in the report. Fill them in, or move them into Bowire secrets and reference them as `{{secret.name}}`.
- **Basic auth** becomes an `Authorization: Basic …` header. With variables in it, set Basic auth on the Bowire environment instead.
- **Form bodies** are joined into one urlencoded body.
- **GraphQL requests** become a REST `POST` with `{"query": …, "variables": …}`. For schema-aware calls, add the endpoint as a GraphQL source.
- **Body variants** (OpenCollection): the selected variant is imported.

## What has no equivalent

Each of these is named in the import report:

- Pre-request and post-response scripts, tests and shared script files: Bowire scripts use their own `ctx.*` API.
- Assertions (`assert` blocks): Bowire's assertions use their own operators; re-create them on the item.
- Request variables and post-response variables: use the environment and `{{response.path}}` [response chaining](../features/collections.md#response-chaining-between-items).
- AWS SigV4, Digest, NTLM, OAuth 1/2 and WSSE auth: set them on the Bowire environment where Bowire supports them.
- gRPC and WebSocket requests: point Bowire at the service; it discovers them.
- Multipart and file bodies.
- `.env` files are never read: their values are secrets.
