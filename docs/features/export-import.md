---
title: Export / Import
summary: 'Bowire turns the request you are looking at into a runnable command or snippet, and takes response data back out.'
---

# Export & Import

Bowire turns the request you are looking at into a runnable command or snippet, and takes response data back out.

## Code export

The request pane's **Code** tab renders the current request — body, metadata headers, resolved `{{variables}}`, server URL — in whichever language fits the protocol. The same list is behind the request-pane header button and the response pane's **Copy ▾** dropdown, so the offer is identical wherever you reach for it:

| Protocol | Offered |
|----------|---------|
| REST | curl, JS fetch, Python (requests), C# (HttpClient), Bowire CLI |
| GraphQL | curl, JS fetch, Python (requests), Bowire CLI |
| MCP | curl, Python (requests), JS fetch, Bowire CLI |
| SSE | curl, JS EventSource, Bowire CLI |
| gRPC | grpcurl, C# (Grpc.Net.Client), Bowire CLI |
| WebSocket | wscat, JS WebSocket, Bowire CLI |
| SignalR | C# (HubConnection), JS (@microsoft/signalr), Bowire CLI |
| MQTT / NATS / Socket.IO | Bowire CLI |

The three broker protocols at the bottom used to fall through to the REST list and be offered a curl command that could never work against a broker; they now offer the one export that can.

Example — the gRPC entry:

```bash
grpcurl \
  -H 'authorization: Bearer token123' \
  -d '{"city":"Berlin"}' \
  localhost:5001 \
  weather.WeatherService/GetCurrentWeather
```

## Export as a Bowire CLI command

**Bowire CLI** is the one entry that is not a translation into someone else's tool: it renders the request as a runnable [`bowire call`](cli-mode.md#invoke-a-method) line, so the thing you just did in the workbench has a terminal equivalent you can paste into a script or a CI job.

```bash
bowire call \
  --url grpc@http://localhost:5001 \
  weather.WeatherService/GetCurrentWeather \
  -d '{"city":"Berlin"}' \
  -H 'authorization: Bearer token123'
```

Two pills appear next to the language strip while it is selected:

- **bash/zsh** ⇄ **PowerShell** — quoting and line continuations for the shell you are in. Defaults to PowerShell on a Windows client.
- **Keep {{variables}}** — leaves the variable references in the command and pairs them with `--var NAME=value` instead of baking the resolved values in, so the line stays readable and re-targetable.

Above the command, a `#` comment block names anything the CLI cannot reproduce:

- **Runtime-fetched tokens.** Session login, OAuth client-credentials / authorization-code, custom-token and signed-JWT auth all exchange a token in the browser at request time. No static header can stand in for that, so the note tells you to export the token and add `-H 'Authorization: Bearer $TOKEN'` yourself.
- **API keys sent as a query parameter**, which belong on `--url` rather than on `-H`.
- **Client-streaming and duplex methods**, where `bowire call` sends each `-d` as one frame and then closes the send side; an interactive duplex session needs the workbench.
- **Secrets.** `{{secret.*}}` and `{{keyring.*}}` references are deliberately *not* resolved into the copied text — they stay as references, so a command pasted into a ticket or a shell history carries no credential. (The curl / fetch / Python generators still substitute everything, including secrets. Treat those as clipboard-only.)

A golden fixture parses every command shape this generator can emit through the real `bowire call` grammar on each build, so a renamed or removed flag fails CI rather than quietly producing a line that no longer runs.

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
