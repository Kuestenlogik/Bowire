# Kuestenlogik.Bowire.Sample.Grpc

The canonical gRPC **Greeter**, grown to cover **all four call types** and
the bits that make gRPC gRPC — wrapped so it demonstrates **both** ways
Bowire meets a gRPC service, from one project:

- **Embedded** — the full workbench is mounted at `/bowire` in this very
  process, and the bundled `grpc-catalogue.json` seeds the Sources rail
  with this host over *both* transports, so `Greeter` is discovered (via
  server reflection) the moment you open the page.
- **Separate** — because it is a real gRPC server, it doubles as a
  standalone target: point an external workbench or the CLI at it.

## The service

| RPC | Call type | What it shows |
|-----|-----------|---------------|
| `SayHello` | unary | request header, response trailers, non-OK status |
| `SayHelloStream` | server streaming | five replies, half a second apart |
| `SayHelloBatch` | client streaming | N queued requests → one `HelloSummary` |
| `Converse` | duplex | one reply per request, plus a closing frame |

`greeter.proto` is deliberately more than two one-field messages, so the
workbench's form builder has something real to render: a `Language`
**enum** (rendered as a select) and a nested `HelloRequest.Caller`
**message** (rendered as a sub-object). Both have visible effects —
`LANGUAGE_GERMAN` returns `Hallo`, `caller.priority = 1` shouts, and
`caller.team` is appended to the greeting.

`SayHello` in particular is the metadata/status demo:

- it reads the **`x-caller` request header** off `ServerCallContext.RequestHeaders`
  and echoes it in the reply's `caller` field — set it in the Headers panel;
- it writes **response trailers** (`x-greeter-version`, `x-greeter-caller`)
  via `context.ResponseTrailers`;
- an **empty `name` fails with `InvalidArgument`**, carrying an
  `x-greeter-hint` trailer. Bowire surfaces trailers as `_trailer:*`
  entries on the invoke result, so this is where they show up.

## Two ports, on purpose

Kestrel only picks HTTP/2 on a shared port through the TLS ALPN
handshake — a *plaintext* `Http1AndHttp2` endpoint answers HTTP/1.1 only
and rejects the h2c preface with `GOAWAY(HTTP_1_1_REQUIRED)`. So the
sample binds two cleartext listeners:

| Port | Protocol | Serves |
|------|----------|--------|
| `5182` | HTTP/1.1 | the workbench UI, and **gRPC-Web** |
| `5183` | h2c | **native gRPC** — the transport all four call types need |

gRPC-Web is enabled with `app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true })`.
`DefaultEnabled` rather than per-endpoint `.EnableGrpcWeb()` because the
reflection endpoint is mapped by `MapBowire()` (the gRPC plugin's
embedded-mode hook maps it for you — mapping it a second time makes the
route ambiguous), and `grpcweb@` discovery needs reflection on the web
transport too.

Unary and server streaming work over either transport; client streaming
and duplex need native HTTP/2, because HTTP/1.1 carries no trailers. See
[docs/protocols/grpc.md](../../docs/protocols/grpc.md).

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Grpc
```

- Embedded workbench: <http://localhost:5182/bowire> — both transports are
  already in the Sources rail, each with all four `Greeter` methods.
- As a separate target for another Bowire instance / the CLI:

  ```pwsh
  bowire --url grpc@http://localhost:5183      # native, all four call types
  bowire --url grpcweb@http://localhost:5182   # gRPC-Web, unary + server streaming
  ```

Server reflection is on, so no path needs a `.proto` upload.

## From the shell

```pwsh
bowire list --url http://localhost:5183 --plaintext
bowire describe bowire.samples.greeter.Greeter --url http://localhost:5183 --plaintext

# unary: enum + nested message + request header
bowire call bowire.samples.greeter.Greeter/SayHello --url http://localhost:5183 --plaintext `
  -d '{"name":"Thomas","language":"LANGUAGE_GERMAN","caller":{"team":"platform","priority":1}}' `
  -H 'x-caller: shell'

# the InvalidArgument path, with trailers
bowire call bowire.samples.greeter.Greeter/SayHello --url http://localhost:5183 --plaintext `
  -d '{"name":""}' --verbose

# client streaming: three messages in, one summary out
bowire call bowire.samples.greeter.Greeter/SayHelloBatch --url http://localhost:5183 --plaintext `
  -d '{"name":"Ada"}' -d '{"name":"Linus","language":"LANGUAGE_FRENCH"}' -d '{"name":"Grace"}'

# duplex: a reply per request, then the closing frame
bowire call bowire.samples.greeter.Greeter/Converse --url http://localhost:5183 --plaintext `
  -d '{"name":"Ada"}' -d '{"name":"Linus","language":"LANGUAGE_GERMAN"}'
```
