---
title: Migrating from Yaak
summary: 'Import a Yaak export into Bowire, and what maps one-to-one, what changes shape and what has no equivalent.'
---

# Migrating from Yaak

## Export from Yaak

In the app, use **Export Data** and pick the workspaces. With the CLI: `yaak export yaak.json <workspace-id>`. Either way you get one JSON file holding the workspaces with their folders, requests and environments.

Private environments are left out of an export unless you include them (**Include private environments** in the app, `--include-private-environments` on the CLI).

## Import into Bowire

Workbench: right-click **Collections → Import files…** and pick the file. CLI: `bowire import yaak.json`.

An export with several workspaces becomes one collection per workspace.

## What maps one-to-one

| Yaak | Bowire |
|------|--------|
| Workspace | Collection |
| Folder | A path in front of the item's name (`Ships / Berths / Book berth`) |
| HTTP request: method, URL, headers, query parameters, body | Collection item (REST) |
| Headers of the workspace and of every folder above a request | Headers of the item, as Yaak sends them; the request's own win |
| `${[ base_url ]}` | `{{base_url}}` |
| `${[ uuid.v4() ]}` | `{{uuid}}` |
| `${[ timestamp.iso8601() ]}` / `unix()` / `unixMillis()` | `{{timestamp}}` / `{{now}}` / `{{nowMs}}` |
| Path parameter (`:id` with a value) | Filled into the URL |
| Sub-environment | Environment, with the base environment's values merged in |
| Bearer, API key auth (also from a folder or the workspace) | `Authorization` header / the key's header or query parameter |

## What changes shape

- **Basic auth** becomes an `Authorization: Basic …` header. With variables in the user name or password it cannot be encoded ahead of time. The report asks you to set Basic auth on the Bowire environment instead.
- **Form bodies** (`application/x-www-form-urlencoded`) are joined into one urlencoded body.
- **GraphQL requests** become a REST `POST` with their `{"query": …, "variables": …}` body. For schema-aware calls, add the endpoint as a GraphQL source.
- **Private environments** come across, and each of their values is listed as a clear-text credential.
- **Disabled headers, query parameters and variables** are left out and named in the report.

## What has no equivalent

Each of these is named in the import report:

- `${[ response.body.path(…) ]}` and the other response functions stay as written. Rewrite them as `{{response.path}}` [response chaining](../features/collections.md#response-chaining-between-items).
- Other template functions (prompts, encryption, files, hashes, …) stay as written.
- OAuth 2, AWS, Digest, NTLM and JWT auth: set them on the Bowire environment.
- gRPC and WebSocket requests: point Bowire at the service; it discovers them. An export that holds nothing else is refused, with the number of requests it found.
- Multipart and file bodies, and the request settings (timeouts, redirects, certificate validation, cookies).
