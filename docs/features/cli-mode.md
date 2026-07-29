---
title: CLI mode
summary: 'A command-line interface for scripting, automation, and quick exploration without opening the browser UI.'
---

# CLI Mode

A command-line interface for scripting, automation, and quick exploration without opening the browser UI. Commands follow the shape `bowire <verb> --url <server> [args]`.

## Commands

### Discover Services (all protocols)

```bash
bowire discover --url https://api.example.com
bowire discover --url https://api.example.com -v      # verbose: list methods too
bowire discover --url rest@https://api.example.com    # pin one plugin, skip the rest
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
| `-d, --data <json>` | `call` | Request body (JSON string or `@filename`). Repeatable — one frame per repeat for client-streaming |
| `-H <key:value>` | `call` | Add metadata header (repeatable) |
| `--var, --env <K=V>` | `call` | Variable for the `{{name}}` / `${name}` resolver (repeatable) |
| `--env-file <path>` | `call` | dotenv-style KEY=VALUE file for the resolver (repeatable; `--var` wins) |
| `--compact` | `call` | One-line JSON output for piping |
| `-plaintext` | all | Use plaintext (no TLS) |
| `-v, --verbose` | `list`, `discover` | Verbose output |

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | OK -- call succeeded (for `discover`: at least one service found) |
| `1` | Connection or runtime error (for `discover`: no service found) |
| `2` | Protocol-level error (a gRPC status, a 4xx/5xx HTTP response, an unknown `--protocol`, a URL no plugin recognised) or invalid usage |

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
