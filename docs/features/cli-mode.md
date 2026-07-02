---
title: CLI mode
summary: 'A command-line interface for scripting, automation, and quick exploration without opening the browser UI.'
---

# CLI Mode

A command-line interface for scripting, automation, and quick exploration without opening the browser UI. Commands follow the shape `bowire <verb> --url <server> [args]`.

## Commands

### List Services

```bash
bowire list --url https://server:443
bowire list --url https://server:443 -v   # verbose: show methods
```

Lists all discovered services. With `-v`, shows each method with its call type.

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

## Options

| Option | Description |
|--------|-------------|
| `--url <url>` | Target server URL (required) |
| `-d, --data <json>` | Request body (JSON string or `@filename`) |
| `-H <key:value>` | Add metadata header (repeatable) |
| `--compact` | One-line JSON output for piping |
| `-plaintext` | Use plaintext (no TLS) |
| `-v, --verbose` | Verbose output (for `list`) |

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | OK -- call succeeded |
| `1` | Connection or runtime error |
| `2` | gRPC error or invalid usage |

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
