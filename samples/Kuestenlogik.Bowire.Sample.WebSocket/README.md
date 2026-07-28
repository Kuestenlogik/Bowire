# Kuestenlogik.Bowire.Sample.WebSocket

A WebSocket server demonstrating **both** ways Bowire meets a WebSocket
service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, the bundled
  `websocket-catalogue.json` seeds the Sources rail, and every route
  carries `[WebSocketEndpoint]` metadata so embedded discovery lists them
  by name instead of falling back to the ad-hoc URL entry.
- **Separate** — it is a real WebSocket server, so point an external
  workbench or the CLI at it.

## Endpoints

| Path | What it does |
| --- | --- |
| `/ws` | Text echo — every inbound text frame comes back prefixed with `echo: `. |
| `/ws/binary` | Binary echo — continuation frames are accumulated until `EndOfMessage`, then the whole payload is sent back as one binary frame. A text frame here closes the socket with **1003 InvalidMessageType**. |
| `/ws/json` | Strict JSON — text frames have to parse as JSON (the reply nests them under `received`); anything else closes with **1003** and a description. |

`/ws/binary` is the one that shows the `{ "type": "binary", "bytes": n,
"base64": ... }` envelope in the channel; `/ws/json` is the quickest way
to see a close envelope with a status that is not the usual `1000`.

## Sub-protocol negotiation

All three endpoints speak the `bowire-echo.v1` sub-protocol:

- a client that offers no sub-protocol at all gets a plain connection —
  that is what the Bowire channel does by default;
- a client that *does* offer a list has to include `bowire-echo.v1`,
  otherwise the upgrade is refused with `400` before it reaches `101`.

From the workbench, ask for it with the metadata header

```
X-Bowire-WebSocket-Subprotocol: bowire-echo.v1
```

`app.UseWebSockets` is configured with a `KeepAliveInterval` (and a
`KeepAliveTimeout`), so the ping/pong heartbeat is on and a peer that
stops answering is dropped instead of lingering half-open.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.WebSocket
```

- Embedded workbench: <http://localhost:5185/bowire> — all three
  endpoints are already in the sidebar and in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url websocket@ws://localhost:5185/ws
  ```
