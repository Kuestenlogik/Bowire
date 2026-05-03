---
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

See also: [Setup -- Standalone](../setup/standalone.md)
