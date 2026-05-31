---
summary: 'Bowire supports exporting requests as grpcurl commands and importing/downloading response data.'
---

# Export & Import

Bowire supports exporting requests as grpcurl commands and importing/downloading response data.

## Export as grpcurl

From the request editor, click the **Export** button to generate a grpcurl-compatible command for the current method and request body. The generated command includes:

- The fully qualified service and method name
- The request body as a `-d` JSON argument
- Any metadata headers as `-H` flags
- The server URL

Example output:

```bash
grpcurl -d '{"city":"Berlin"}' \
  -H "authorization: Bearer token123" \
  localhost:5001 \
  weather.WeatherService/GetCurrentWeather
```

This is useful for sharing exact invocations with team members or including them in documentation.

## JSON Response Download

Click the **Download** button in the response viewer to save the response body as a JSON file. For streaming responses, the download includes all received messages as a JSON array.

## Copy to Clipboard

Click the **Copy** button to copy the response body to your clipboard. For streaming responses, this copies all messages received so far.

## File-Based Input (CLI)

In CLI mode, use `@filename` to read the request body from a file:

```bash
bowire call --url https://server:443 \
  weather.WeatherService/GetCurrentWeather -d @request.json
```

This is useful for large or complex request bodies that are cumbersome to type inline.

## Schema export — `bowire export`

`bowire export` turns a live discovery result back into a portable schema artefact. Pair it with `bowire mock --schema` (which goes the other direction &mdash; schema → live mock endpoint) to round-trip a captured surface between teams without the original service being reachable.

Two subcommands, picked by output format:

```bash
# REST → OpenAPI 3.0
bowire export openapi http://api.example.com --output api.yaml

# Messaging → AsyncAPI 3.0. URL scheme picks the wire plugin
# (mqtt / nats / kafka / ws / amqp / amqp1 / pulsar / http).
bowire export asyncapi mqtt://broker:1883 --output sensors.yaml
bowire export asyncapi nats://broker:4222 --format json
```

Both commands accept an optional `--recording <file>` &mdash; when supplied, every operation in the emitted document gets an `x-bowire-coverage` extension reporting whether the recording carries replay steps for it and how many:

```bash
bowire export openapi http://api.example.com \
  --recording session.bwr \
  --output api-with-coverage.yaml
```

```yaml
# api-with-coverage.yaml
paths:
  /users/{id}:
    get:
      operationId: getUser
      x-bowire-coverage:
        recorded: true
        stepCount: 3
    post:
      operationId: createUser
      x-bowire-coverage:
        recorded: false
        stepCount: 0
```

This is the consumer-side view of the [mock-as-stand-in](mock-server.md#mock-as-stand-in-recording-carries-the-original-contract) story: the recording carries the original contract, the exporter re-emits it with coverage so a team can tell which slice the recorded mock can replay deterministically vs. which slice would have to fall back to schema-generated samples.

Default output is YAML to stdout (omit `--output` to pipe somewhere). Override with `--format json`, `--title <s>`, `--version-info <s>` if needed. Exit codes: `0` ok, `1` plugin-not-loaded / discovery failure, `2` usage error (empty URL, unrecognised scheme).

See also: [CLI Mode](cli-mode.md), [UI Guide -- Response Pane](../ui-guide/response-pane.md), [Mock Server](mock-server.md)
