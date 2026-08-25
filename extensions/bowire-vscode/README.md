# Bowire for VS Code

The [Bowire](https://bowire.io/) multi-protocol API workbench, hosted in VS Code.

gRPC, REST, GraphQL, SignalR, MQTT, NATS, WebSocket, SSE, SOAP, OData, MCP — the same workbench you get from the standalone tool, in a side panel, with collections and environments stored in your workspace so they travel with the repo.

## Requirements

Bowire 2.0 or newer — and you do not have to install it first.

The extension drives a Bowire you already have: one you configured, one your repository pins in a tool manifest, or one on your `PATH`. That is the better case, because it is the same CLI your terminal and your CI run.

If it finds none, it offers to download one and manages that copy itself. Nothing is fetched without you saying yes, the archive is verified against its digest before anything is unpacked, and uninstalling the extension deletes that copy again — a Bowire you installed yourself is never touched.

To install one yourself:

```bash
winget install Kuestenlogik.Bowire     # Windows
choco install bowire                   # Windows (Chocolatey)
dotnet tool install -g Kuestenlogik.Bowire.Tool
```

The extension does **not** bundle Bowire — a self-contained build is roughly 120 MB per platform, which would mean one marketplace package per platform to keep in step and a new extension release for every Bowire release.

📖 **[How the extension finds Bowire →](https://bowire.io/docs/integrations/vscode.html)** — the four resolution steps, what each one is for, how to pin a specific build, and what the managed download does.

## Use

Run **Bowire: Open workbench** from the command palette. The extension starts Bowire with your workspace folder as its working directory and opens the workbench beside your editor. Closing the panel stops the process.

**Bowire: Show resolved CLI** answers the other question — *which* Bowire this would run, and which of the four resolution steps produced it — without starting anything. The **Bowire** output channel records the same after each start.

## Where your work is stored

Collections, environments, recordings and presets, as plain JSON. Nothing lives in IDE-proprietary storage and nothing is locked to VS Code — the same data is what the standalone tool and the CLI use, so a collection you build in the editor replays unchanged in CI.

By default that is `~/.bowire/`, shared by every workspace on the machine. To keep a project's data beside its code, add one line to that repo's `.bowire/project.json`:

```jsonc
{ "version": 1, "storage": "project" }
```

The collections then commit, diff and review like any other file, and two repos open in two windows keep separate sets. It is opt-in: a manifest that says nothing keeps the machine-wide store, so nobody's existing data moves under them.

📖 **[Storage in detail →](https://bowire.io/docs/integrations/vscode.html)** — moving an existing store into a repo, and why the repository decides rather than the editor.

## Settings

| | |
|---|---|
| `bowire.cliPath` | Path to a specific `bowire` executable. Supports `${workspaceFolder}`. Checked when you change it: a path that does not exist, names a folder rather than the executable, or points at a build too old for this extension is reported straight away rather than at the next start. |
| `bowire.autoDownload` | `prompt` (default), `always` or `never` — what to do when no CLI is found. |

## Ports

The extension does not pick a port. It starts Bowire with `--port 0`, so the OS assigns a free one, and `--port-file`, so Bowire writes back the address it actually bound. That file appears only once the server is listening, which makes it both the address and the signal that the panel is safe to open.

A Bowire you started yourself is left alone — its port, its process, its lifetime. The extension runs its own alongside it, which is what you want: yours has a different working directory, so it reads a different `.bowire/project.json`, and closing the panel stops only the process the panel started.

## Related

- [Bowire documentation](https://bowire.io/docs/) · [VS Code integration](https://bowire.io/docs/integrations/vscode.html)
- [JetBrains plugin](https://github.com/Kuestenlogik/Bowire/issues/588) — the same shape for the IntelliJ platform family, tracked separately.
