# MCP — Tools sample

Model Context Protocol server exposing two tools (`Echo`, `Add`) over
the official C# SDK's streamable-HTTP transport.

## Run

```pwsh
dotnet run --project examples/Mcp/Tools
```

Listens on `http://localhost:5190`. MCP endpoint at `/mcp`.

## Connect from Bowire

Server URL: `http://localhost:5190/mcp`. The MCP plugin completes the
handshake, lists both tools, and lets you invoke them with arguments.
