---
title: Migrating to Bowire
summary: 'Bring a collection over from Insomnia, Bruno or .http files, and what changes on the way.'
---

# Migrating to Bowire

Bowire reads the collections of other API clients and turns them into Bowire collections and environments. All of them go through one entry point, which recognises the format by its content:

- **Workbench:** right-click **Collections** in the Workspaces tree, or use **Import collection** in a collection's toolbar. Choose **Import files…** for a single export, **Import folder…** for a collection that lives in a folder (Bruno, or a folder of `.http` files).
- **CLI:** `bowire import <file-or-folder>` writes a `.bww` workspace file next to the input. Open it in the workbench, or load it with `bowire workspace import`.
- **MCP:** the `bowire.import` tool takes the same path.

Every import ends with a report in three parts:

- **Left out**: what Bowire could not import, and why.
- **Changed on the way**: what came across in a different shape.
- **Credentials in clear text**: values that look like secrets. Move them into [secrets](../features/environments.md) before you share the workspace.

Nothing is dropped without an entry in the report.

| Source | Guide |
|--------|-------|
| Insomnia (v4 export, v5 collection) | [From Insomnia](insomnia.md) |
| Bruno (`.bru` and OpenCollection YAML) | [From Bruno](bruno.md) |
| `.http` files (Rider, IntelliJ, Visual Studio, VS Code REST Client) | [From .http files](http-files.md) |
| Postman Collection v2.1 | [Collections: Postman import](../features/collections.md#postman-collection-v21-import) |

## Before you import

For gRPC, GraphQL and OpenAPI services you may not need a collection at all: point Bowire at the running service and [discovery](../features/auto-discovery.md) lists every method with its schema. An import is most useful for what discovery cannot know: the requests you saved, their bodies, headers and environments.
