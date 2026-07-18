# Kuestenlogik.Bowire.Sample.Grpc

The canonical gRPC **Greeter** — unary (`SayHello`) and server-streaming
(`SayHelloStream`) — wrapped so it demonstrates **both** ways Bowire meets
a gRPC service, from one project:

- **Embedded** — the full workbench is mounted at `/bowire` in this very
  process, and the bundled `grpc-catalogue.json` seeds the Sources rail
  with this host, so `Greeter` is discovered (via server reflection) the
  moment you open the page.
- **Separate** — because it is a real gRPC server, it doubles as a
  standalone target: point an external workbench or the CLI at it.

A single cleartext Kestrel port serves both — the default
`Http1AndHttp2` negotiates HTTP/2 (gRPC) and HTTP/1.1 (the UI) on the
same socket.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Grpc
```

- Embedded workbench: <http://localhost:5182/bowire> — `Greeter` is
  already in the Sources rail with `SayHello` (Unary) and
  `SayHelloStream` (Server streaming).
- As a separate target for another Bowire instance / the CLI:

  ```pwsh
  bowire --url grpc@http://localhost:5182
  ```

Server reflection is on, so neither path needs a `.proto` upload.
