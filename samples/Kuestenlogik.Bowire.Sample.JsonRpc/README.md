# Kuestenlogik.Bowire.Sample.JsonRpc

A JSON-RPC 2.0 **Math** server (`add` / `subtract` / `divide`, plus
`rpc.discover` for OpenRPC) that demonstrates **both** ways Bowire meets a
JSON-RPC service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `jsonrpc-catalogue.json` seeds the Sources rail with this host's `/rpc`
  endpoint. `rpc.discover` lets the plugin auto-list the methods.
- **Separate** — it is a real JSON-RPC server, so point an external
  workbench or the CLI at it.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.JsonRpc
```

- Embedded workbench: <http://localhost:5187/bowire> — `Math` is already
  in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url jsonrpc@http://localhost:5187/rpc
  ```
