# Bowire for VS Code

The [Bowire](https://bowire.io/) multi-protocol API workbench, hosted in VS Code.

gRPC, REST, GraphQL, SignalR, MQTT, NATS, WebSocket, SSE, SOAP, OData, MCP — the same workbench you get from the standalone tool, in a side panel, with collections and environments stored in your workspace so they travel with the repo.

## Requirements

Bowire itself, on your `PATH`:

```bash
winget install Kuestenlogik.Bowire     # Windows
choco install bowire                   # Windows (Chocolatey)
dotnet tool install -g Kuestenlogik.Bowire.Tool
```

The extension does **not** bundle Bowire. Shipping a self-contained build per platform would mean roughly 100 MB per marketplace package, three builds to keep in step, and a new extension release for every Bowire release. Requiring the CLI keeps the extension small and lets it host whichever Bowire you already run.

## Use

Run **Bowire: Open workbench** from the command palette. The extension starts Bowire with your workspace folder as its working directory and opens the workbench beside your editor. Closing the panel stops the process.

## Where your work is stored

In `.bowire/` inside the workspace — collections, environments, recordings, contract results, benchmark schedules. It is plain JSON, so it is diff-able, reviewable and shared with everyone who clones the repo. Nothing lives in IDE-proprietary storage.

That falls out of how the extension works rather than being a feature bolted on: the Bowire process owns the workspace folder as its working directory, so it reads and writes those files itself. The webview only speaks HTTP to it, which is also why no file-system bridge is needed.

## Ports

The extension asks for a port derived from the workspace path (5099–6098), so reopening the panel reuses the same one and two windows do not collide. It stays clear of Bowire's own default 5080, so a workbench you started by hand keeps working. If the CLI binds elsewhere, the extension follows the port it reports rather than the one it asked for.

## Related

- [JetBrains plugin](https://github.com/Kuestenlogik/Bowire/issues/588) — the same shape for the IntelliJ platform family, tracked separately.
- [Bowire documentation](https://bowire.io/docs/)
