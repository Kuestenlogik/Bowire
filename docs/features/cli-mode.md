---
title: CLI mode
summary: 'A command-line interface for scripting, automation, and quick exploration without opening the browser UI.'
---

# CLI Mode

A command-line interface for scripting, automation, and quick exploration without opening the browser UI. Commands follow the shape `bowire <verb> --url <server> [args]`; `bowire send http [METHOD] <url> [items…]` sends one request with no discovery.

## Commands

### Discover Services (all protocols)

```bash
bowire discover --url https://api.example.com
bowire discover --url https://api.example.com -v      # verbose: list methods too
bowire discover --url rest@https://api.example.com    # pin one plugin, skip the rest
bowire discover --schema ./openapi.yaml               # from a file, no server needed
bowire discover --schema ./routeguide.proto --schema ./openapi.yaml
```

Probes the URL with **every** loaded protocol plugin in parallel and prints
what each one found — or why it didn't:

```text
petstore.Pets  (7 methods, via rest)

12 plugins probed · 2 failed
  gRPC     error      2011 ms  connection refused
  MQTT     timeout    8003 ms  probe exceeded the 8 s ceiling
  GraphQL  empty       311 ms  returned no services
  REST     ok          142 ms  1 service
  …
```

The attempt table always prints — the whole point of running the command is
the diagnosis, so there is no collapsed / expanded tradeoff the way there is
in the UI. Exit code is `0` when at least one service was found and `1`
otherwise, so CI can gate on it.

This is the same `BowireDiscoveryProbe` fan-out the `/bowire/api/services`
endpoint and the `bowire.discover` MCP tool use, so the terminal and the
workbench can never disagree about what happened. See
[Auto-discovery → When discovery finds nothing](auto-discovery.md#when-discovery-finds-nothing)
for the outcome vocabulary.

#### Discovering from a file

`--schema` points discovery at a document on disk — a `.proto`, or an OpenAPI /
Swagger `.json` / `.yaml` / `.yml`. Repeatable, and it works with or without a
`--url`: a schema and a live server merge, with the server winning a name
clash because it describes itself more currently than a saved file does.

The workbench's equivalent is dropping the file on the sidebar, which
**stores** it under your identity — you come back to it tomorrow. The CLI
names the file instead, and that is deliberate: a pipeline that read an
uploaded schema would depend on state another process wrote, on a machine it
does not control, to find something the job could simply have said. In CI the
schema is in the repository already.

An uploaded schema is still reachable from the terminal when no `--schema` is
given — `bowire discover` reads what the calling identity has uploaded
without a workspace selected. A schema uploaded *into a workspace* is not
read; name the file.

For gRPC, `list`, `describe` and `call` take **`--descriptor-set <file.pb>`**
instead — the compiled `FileDescriptorSet` carries more than a `.proto` does.
Passing `--schema x.proto` to one of those is refused with that pointer rather
than accepted and quietly ignored.

### List Services

```bash
bowire list --url https://server:443
bowire list --url https://server:443 -v   # verbose: show methods
```

Lists all discovered services via **gRPC server reflection only** — it talks
straight to `GrpcReflectionClient` and never touches the multi-protocol
fan-out. That is deliberate: scripts depend on its output shape. Reach for
`bowire discover` when you want every plugin's verdict. With `-v`, shows each
method with its call type.

### Describe a Service or Method

```bash
bowire describe --url https://server:443 weather.WeatherService
bowire describe --url https://server:443 weather.WeatherService/GetCurrentWeather
```

Shows method signatures and input/output schemas. For gRPC, this includes protobuf field numbers and types.

### Invoke a Method

```bash
# Unary call
bowire call --url https://server:443 \
  weather.WeatherService/GetCurrentWeather -d '{"city":"Berlin"}'

# Server streaming (prints each message as it arrives)
bowire call --url https://server:443 \
  weather.WeatherService/SubscribeWeather -d '{"city":"Tokyo"}'

# Compact JSON output (one line per message, pipe-friendly)
bowire call --url https://server:443 \
  weather.WeatherService/SubscribeWeather -d '{"city":"Tokyo"}' --compact

# Read request body from file
bowire call --url https://server:443 \
  weather.WeatherService/GetCurrentWeather -d @request.json

# With metadata headers
bowire call --url https://server:443 \
  weather.WeatherService/GetCurrentWeather -d '{}' -H "authorization: Bearer token123"

# Plaintext (no TLS)
bowire call --url http://server:5000 -plaintext \
  weather.WeatherService/GetCurrentWeather -d '{}'
```

#### Any protocol, not just gRPC

`call` invokes through whichever protocol plugin owns the URL, so every
request the workbench can send has a terminal equivalent. Pin the plugin
either with the `protocol@url` hint form (the same one `discover` and the
sidebar accept) or with an explicit `--protocol`:

```bash
# REST — the hint form
bowire call --url rest@https://petstore3.swagger.io/api/v3 \
  pet/getPetById -d '{"petId":1}'

# GraphQL
bowire call --url graphql@https://countries.trevorblades.com/graphql \
  Query/country -d '{"query":"query($c:ID!){country(code:$c){name}}","variables":{"c":"DE"}}'

# MQTT — the broker address carries no scheme, so name the plugin instead
bowire call --url broker.example.com:1883 --protocol mqtt \
  sensors/sensors/temperature -d '{"celsius":21.5}'
```

Without a hint or a `--protocol`, `call` assumes gRPC and takes a fast path
that skips loading the plugin registry — existing gRPC scripts pay nothing
for the widening. With one, the URL is probed by that plugin (the same
`BowireDiscoveryProbe` fan-out `discover` uses) before the invocation, so a
wrong URL reports which plugin said what rather than a bare transport error.

All first-party protocol plugins ship inside the `bowire` tool, so nothing
needs installing for the protocols listed above. A plugin that is not
loaded produces an error naming the ones that are, plus the
`bowire plugin install Kuestenlogik.Bowire.Protocol.<Name>` line that would
add it.

#### Following a stream

```bash
# SSE — one JSON document per event until Ctrl+C
bowire call --url sse@https://stream.example.com 'SSE Endpoints//events' --stream

# WebSocket — send one frame, then print what comes back
bowire call --url websocket@https://echo.example.com 'WebSocket endpoints//chat' \
  -d '{"text":"hello"}' --stream
```

`--stream` routes the call through the plugin's streaming entry point.
gRPC server-streaming methods are detected automatically and don't need
it; every other protocol does, because only the caller knows whether an
SSE / WebSocket / broker target should be read once or followed. A plugin
with no streaming support answers with a one-line explanation rather than
a stack trace.

Ctrl+C is the normal way to stop a subscription and exits `0`. A stream
that ends *without ever delivering a frame* exits `1` with an explanation
instead — in a pipeline, "printed nothing, exited 0" is indistinguishable
from success.

#### Variables

`-d`, `-H` and `--url` all run through the same `{{name}}` / `${name}`
resolver `bowire test` uses, so one recorded request works against several
environments:

```bash
bowire call --url rest@https://{{host}}/api/v3 pet/getPetById \
  -d '{"petId":{{petId}}}' \
  --var host=petstore3.swagger.io --var petId=1

# Or from a dotenv-style file; --var repeats win over file entries
bowire call --url rest@https://{{host}}/api/v3 pet/getPetById \
  -d '{"petId":1}' --env-file staging.env
```

The built-in `{{uuid}}` / `{{now}}` / `{{timestamp}}` / `{{random}}`
variables resolve without being declared. Unknown names are left intact so
a typo shows up in the request rather than as an empty value.

### Send a request directly (`bowire send http`)

`call` invokes a method it has discovered first. `send` sends a request you
spell out yourself, with no discovery and no schema: the one-off request curl
and HTTPie are used for. The protocol comes second and decides the arguments;
`http` is the first.

```bash
bowire send http https://api.example.com/pets/1              # GET, body to stdout
bowire send http post api.example.com/pets name=ada age:=3   # POST {"name":"ada","age":3}
bowire send http :5000/pets q==ada limit==5                  # http://localhost:5000/pets?q=ada&limit=5
bowire send http put :5000/pets/1 -d @pet.json               # a raw body from a file
bowire send http :5000/upload file@./logo.png note=hi        # multipart form upload
bowire send http propfind https://dav.example.com/files      # any method token
bowire send http post api/x name=ada --offline               # print the request, send nothing
```

`bowire send http [METHOD] <url> [items…]`:
- **METHOD** is optional and case-insensitive. It defaults to `GET`, or to
  `POST` when body items or `-d` are given. Any token works, `PROPFIND`
  included. A bare word before a URL is the method; a URL with a scheme, a
  dotted host or `host:port` settles any doubt.
- **The URL** may leave out the scheme: it then gets
  `Bowire:Cli:Send:DefaultScheme` (`http` unless configured), as HTTPie does.
  `:3000/x` is `http://localhost:3000/x`.
- **Items**, in HTTPie's notation, so a request needs no JSON quoting:

  | Item | Meaning |
  |------|---------|
  | `Header:value` | A request header. A later one of the same name replaces an earlier one. |
  | `param==value` | A query parameter, URL-encoded and added to the URL's own query. |
  | `field=value` | A JSON field holding a string; with a file item, a form field. |
  | `field:=json` | A JSON field holding raw JSON: `age:=36`, `tags:='["a","b"]'`, `ok:=true`. |
  | `field@file` | A file, uploaded as a `multipart/form-data` part. |

  The first separator in an item decides its kind, the longest where two
  start at the same place, so `Authorization:Bearer a=b` is a header and
  `q==a:b` a query parameter. A backslash keeps a separator in the name:
  `a\=b=c` is the field `a=b`. Body items become a JSON object sent as
  `application/json`, with `Accept: application/json, */*;q=0.5`; a
  `Content-Type:…` item names another type. `-d` and body items do not mix,
  and a form cannot carry `:=` fields.

`{{name}}` / `${name}` resolve in the URL, the items and `-d`, from `--var`
and `--env-file` as for `call`. A variable is resolved after the item is
split, so a value holding `=` or `:` stays a value. `--negotiate`, `--ntlm`
and `--digest` authenticate as on `call`. The proxy and the CA bundle come
from `--proxy-url` / `--no-proxy` / `--ca-bundle` and the network settings,
as for every command.

The response body goes to stdout: JSON indented (one line with `--compact`),
other text as it came, a binary body only as its size on stderr. The status
line and the time go to stderr. Exit `0` for 2xx and 3xx, `2` for 4xx and
5xx or a usage error, `1` when the request could not be sent.

`--offline` builds the request, prints it as an HTTP/1.1 message with every
variable resolved, and opens no connection. A file part shows its name and
size instead of its bytes.

```text
$ bowire send http post api/x name={{who}} age:=36 --var who=ada --offline
POST /x HTTP/1.1
Host: api
Accept: application/json, */*;q=0.5
User-Agent: Bowire/<version>
Content-Type: application/json

{"name":"ada","age":36}
```

**Startup.** `send` loads no protocol plugin and skips the scan for
plugin-contributed commands, which is most of what any other command pays
before its first byte. Measured on a Windows 11 developer machine (Release
build, 15 runs each, median) against one local endpoint, the same pet with
`send http` and with `call` through the REST plugin's discovery:

| Command | Median |
|---------|--------|
| `bowire send http http://127.0.0.1:5391/pets/1` | 270 ms |
| `bowire send http … --offline` | 202 ms |
| `bowire call --url rest@http://127.0.0.1:5391 pet/getPetById -d '{"petId":1}'` | 656 ms |

`BowirePluginLoadGuardTests` holds that `send` skips the plugin load.

### Copy a request out of the workbench

The workbench's **Code** tab (and the response pane's **Copy ▾** dropdown)
offers **Bowire CLI** alongside curl / grpcurl / wscat / fetch: it renders
the request you are looking at as a runnable `bowire call …` line, with a
shell-flavour toggle (bash/zsh or PowerShell) and a **Keep {{variables}}**
pill that leaves the refs in place and pairs them with `--var`.

Two things it will not do. It never resolves `{{secret.*}}` or
`{{keyring.*}}` into the copied text — those stay as refs, with a note
saying so. And for the auth types whose token is fetched at request time
(session, OAuth client-credentials / auth-code, custom token, signed JWT)
it emits a `#` note instead of an `Authorization` header, because the
exchange happens in the browser and no static header can stand in for it.

A golden fixture parses every command shape that generator can emit
through this command's real grammar on each build, so the copied line
cannot quietly stop being runnable.

## Options

| Option | Applies to | Description |
|--------|------------|-------------|
| `--url <url>` | all | Target server URL (required). `discover` and `call` also accept the `protocol@url` hint form |
| `--protocol <id>` | `call` | Protocol plugin to invoke through (`grpc` / `rest` / `graphql` / `mqtt` / …). Overrides a `protocol@url` prefix |
| `--stream` | `call` | Consume the method as a stream: one JSON document per frame until the stream ends or Ctrl+C |
| `-d, --data <json>` | `call`, `send http` | Request body (JSON string or `@filename`). Repeatable — one frame per repeat for client-streaming |
| `-H <key:value>` | `call` | Add metadata header (repeatable) |
| `--var, --env <K=V>` | `call`, `send http` | Variable for the `{{name}}` / `${name}` resolver (repeatable) |
| `--env-file <path>` | `call`, `send http` | dotenv-style KEY=VALUE file for the resolver (repeatable; `--var` wins) |
| `--compact` | `call`, `send http` | One-line JSON output for piping |
| `--offline` | `send http` | Print the request, variables resolved, and send nothing |
| `--negotiate`, `--ntlm`, `--digest` | `call`, `send http` | Challenge-response authentication |
| `-plaintext` | all | Use plaintext (no TLS) |
| `--schema <path>` | `discover` | Discover from a schema file: `.proto`, or OpenAPI `.json` / `.yaml` / `.yml`. Repeatable; works with or without `--url`. Accepted on `list` / `describe` / `call` for OpenAPI only — use `--descriptor-set` for protobuf there |
| `--descriptor-set <path>` | `list`, `describe`, `call` | Compiled protobuf `FileDescriptorSet` (`.pb`) for gRPC without server reflection |
| `-v, --verbose` | `list`, `discover` | Verbose output |

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | OK -- call succeeded (for `discover`: at least one service found) |
| `1` | Connection or runtime error (for `discover`: no service found) |
| `2` | Protocol-level error (a gRPC status, a 4xx/5xx HTTP response, an unknown `--protocol`, a URL no plugin recognised) or invalid usage |

### Starting Bowire when Bowire is already running

`bowire` with no subcommand starts the workbench. If the port it wants is
already serving **another Bowire**, it does not start a second one: it prints
the running instance's URL, opens a browser at it (unless `--no-browser`), and
exits `0`. Nothing went wrong -- you asked for a workbench and there is one, so
a script that starts Bowire idempotently keeps working.

Flags that shape the *server* cannot be applied to an instance that is already
up. If the second invocation carries any of `--url`, `--enable-mcp-adapter`,
`--title`, `--disable-plugin`, `--allowed-server-url`, `--allowed-auth-url`, `--plugin-dir`, `--map-basemap` or
`--auto-create-initial-workspace`, they are named on stderr rather than
silently dropped. Stop the running instance first if you need them.

If the port is held by something that is **not** Bowire, you get one line
naming the port and the two ways out (`--port <number>`, or `--port 0` together
with `--port-file`), and exit `1`. Either way there is no stack trace.

## Response files

Arguments can come from a file: `bowire @ci.rsp` reads `ci.rsp` and puts its
arguments where the token stood, so a long command line lives in the
repository or stays out of shell history.

```text
# ci.rsp: one argument per line, as it stands
call
--url
rest@https://{{host}}/api/v3
pet/getPetById
-d
{"petId": 1}
--env-file
staging.env
```

```bash
bowire @ci.rsp --compact
```

- Only a token `@<path>.rsp` is a response file (the extension in any case).
  Every other `@` token reaches the command as written, so `-d @request.json`
  still reads that file as the body.
- One argument per line, taken as it stands: a JSON value with spaces is one
  argument, quotes included. Surrounding whitespace is trimmed; blank lines
  and lines starting with `#` are skipped.
- The file is expanded before anything else reads the command line, so
  `--proxy-url`, `--ca-bundle` and `--plugin-dir` in it work as on the
  command line.
- A response file does not expand further response files, and nothing after
  `--` is expanded. A missing file exits `2` naming it.

## CI/CD Usage

CLI mode is designed for automated pipelines:

```bash
# Health check in CI
bowire call --url https://staging:443 \
  health.HealthService/Check -d '{}' --compact \
  || exit 1
```

The `--compact` flag produces one-line JSON output suitable for piping to `jq` or other tools.

## Argument validation

Common mistakes are caught at parse time -- before any server binds a
socket -- and reported on **stderr** with a one-line pointer at the
relevant `--help`:

- `--port` (and `--api-port`) must be in `1..65535`.
- `--recording <path>` (and the positional `bowire mock <file>` form) must
  point at an existing file.
- `--chaos` is parsed eagerly, so a malformed spec such as
  `--chaos bogus` fails immediately instead of mid-boot.

```console
$ bowire mock --port 70000
✗ --port: port must be between 1 and 65535 (got 70000).

Run 'bowire mock --help' for usage.
```

Error output is colourised on an interactive terminal and plain when
redirected (pipes, CI logs), so captured output stays ANSI-free.

## Tab completion

Bowire answers the standard [`dotnet-suggest`](https://github.com/dotnet/command-line-api/blob/main/docs/dotnet-suggest.md)
completion protocol, so bash / zsh / PowerShell users get completion for
sub-commands, options, and enumerated values (e.g. `fuzz --payloads`
offers `sqli / xss / pathtrav / cmdinj`; `--map-basemap` offers
`osm / satellite / demotiles / none`).

One-time setup:

```bash
# 1. Install the completion broker (once per machine)
dotnet tool install -g dotnet-suggest

# 2. Add the shell shim to your profile, then reload:
#    bash/zsh  -> https://github.com/dotnet/command-line-api/blob/main/src/System.CommandLine.Suggest/dotnet-suggest-shim.bash
#    PowerShell:
#      Add-Content $PROFILE (dotnet-suggest script powershell)

# 3. Register the bowire executable with the broker
dotnet-suggest register --command-path "$(command -v bowire)"
```

After reloading the shell, `bowire mo<Tab>` completes to `mock`, and
`bowire fuzz --payloads <Tab>` lists the payload categories.

See also: [Setup -- Standalone](../setup/standalone.md)
