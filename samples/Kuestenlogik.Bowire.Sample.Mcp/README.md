# Kuestenlogik.Bowire.Sample.Mcp

A Model Context Protocol server (tools: `echo` + `add`) over the official
C# SDK's streamable-HTTP transport, demonstrating **both** ways Bowire
meets an MCP service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `mcp-catalogue.json` seeds the Sources rail with this host's `/mcp`
  endpoint.
- **Separate** — it is a real MCP server, so point an external workbench
  or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mcp
```

- Embedded workbench: <http://localhost:5190/bowire> — the sample tools
  are already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url mcp@http://localhost:5190/mcp
  ```
