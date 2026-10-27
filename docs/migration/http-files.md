---
title: Migrating from .http files
summary: 'Import .http files from Rider, IntelliJ, Visual Studio or VS Code into Bowire, and write Bowire collections back out as .http.'
---

# Migrating from .http files

`.http` files are the request format of the JetBrains HTTP Client (Rider, IntelliJ), Visual Studio 2022 and VS Code's REST Client. Bowire reads them and writes them, so a collection can move in either direction.

## Import into Bowire

Workbench: right-click **Collections → Import files…** and pick the `.http` / `.rest` files, with `http-client.env.json` and `http-client.private.env.json` if you have them; **Import folder…** takes a folder of them. CLI: `bowire import requests.http` or `bowire import path/to/folder`.

## What maps one-to-one

| .http | Bowire |
|-------|--------|
| A file | A collection |
| `### Title` | Item name |
| Request line, headers, body | Collection item (REST) |
| `{{variable}}` | `{{variable}}` |
| `{{$uuid}}`, `{{$random.uuid}}`, `{{$guid}}` | `{{uuid}}` |
| `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt}}` | `{{now}}`, `{{timestamp}}`, `{{random}}` |
| `http-client.env.json` environments | Environments, with `$shared` merged into each |

## What changes shape

- **`@name = value` lines** of a file become an environment named `<file> (file variables)`.
- **`Authorization: Basic user password`** (the JetBrains shorthand) is encoded into a standard Basic header. With variables in it, set Basic auth on the Bowire environment instead.
- **`GRAPHQL` requests** become a REST `POST` with a `{"query": …}` body.
- **`http-client.private.env.json`** comes across, and each of its values is listed as a clear-text credential.

## What has no equivalent

Each of these is named in the import report:

- Response handlers (`> {% %}`) and output redirects (`>>`): write the checks as Bowire assertions.
- File bodies (`< ./body.json`): the file is not part of the import.
- `WEBSOCKET` and `GRPC` requests: point Bowire at the service; it discovers them.
- Directives such as `# @no-redirect`, and structured environment values (SSL or auth blocks).

## Back out as .http

**Export .http** in a collection's toolbar, `bowire export http workspace.bww` or the MCP tool `bowire.export.http` write the collection as `<name>.http` plus `http-client.env.json`. Secrets are never written: `{{secret.x}}` references become names for `http-client.private.env.json`. See [Collections: .http files](../features/collections.md#http-files-import-and-export).
