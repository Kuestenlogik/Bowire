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
| REST, OData, GraphQL, JSON-RPC, MCP | curl, JS fetch, JS axios, Python (requests), Python (httpx), C# (HttpClient), C# (RestSharp), Go (net/http), Java (HttpClient), Java (OkHttp), Kotlin (OkHttp), PHP (cURL), Ruby (net/http), Rust (reqwest), Swift (URLSession), PowerShell, HTTPie, wget, .http (not MCP), Bowire CLI |
| SSE | curl, JS EventSource, Python (httpx), C# (HttpClient), Bowire CLI |
| gRPC | grpcurl, C# (Grpc.Net.Client), Go (grpc-go), Python (grpcio), Node (@grpc/grpc-js), Java (grpc-java), Bowire CLI |
| WebSocket | wscat, JS WebSocket, Python (websockets), C# (ClientWebSocket), Bowire CLI |
| SignalR | C# (HubConnection), JS (@microsoft/signalr), Java (signalr), Bowire CLI |
| MQTT | mosquitto_pub / _sub, Python (paho-mqtt), Node (mqtt), C# (MQTTnet), Bowire CLI |
| NATS | nats CLI, Python (nats-py), Node (nats), Go (nats.go), C# (NATS.Net), Bowire CLI |
| Socket.IO | Node (socket.io-client), Python (python-socketio), C# (SocketIOClient), Bowire CLI |
| Pulsar | pulsar-client CLI, Python (pulsar-client), Java (pulsar-client), Bowire CLI |
| any other protocol | Bowire CLI |

Every snippet keeps the same rules:

- **The request the workbench sends.** JSON-RPC and MCP snippets carry the envelope their plugin builds (`{"jsonrpc":"2.0","method":…}`, `tools/call` / `resources/read` / `prompts/get`); a broker snippet publishes to the topic or subject the method names, with its QoS, retain flag or JetStream publish; a gRPC snippet uses the call shape (unary, server-, client- or bidirectional streaming) of the method.
- **Secrets stay references.** `{{secret.*}}` and `{{keyring.*}}` are left as they are, and a comment above the snippet says so; a credential never lands in copied text.
- **What it cannot carry, it says.** A token the environment fetches at request time (OAuth, session, custom token, JWT), AWS SigV4 and client certificates are a comment, never a made-up header; so are the parts a library cannot express (a browser `EventSource` or `WebSocket` cannot send headers) and what a snippet expects from you (the client generated from your `.proto`, MCP's `initialize` handshake).
- **Only what fits the protocol.** A broker is never offered a curl command. SOAP (whose envelope is built from the WSDL) and OTLP (where Bowire receives, it does not send) offer the Bowire CLI.

A protocol plugin adds snippets of its own from its UI extension bundle, without a change to Bowire:

```js
window.bowireCodeExport.register({
  id: 'kafka-kcat', label: 'kcat', protocols: ['kafka'], comment: '#', rank: 10,
  generate: (ctx) => `kcat -b ${ctx.serverUrl} -t ${ctx.method} -P`,
});
```

`ctx` carries the server URL, service, method and its full name, the body (`bodyJson`) and headers (`metadata`) with variables resolved and secrets held back, the streaming flags and the input type; anything the generator cannot express goes into `ctx.notes`, which the workbench prints as comments above the snippet. A golden file holds every snippet of every built-in target, and the JavaScript, Python and Go snippets are parsed on each test run.

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
- **Secrets.** `{{secret.*}}` and `{{keyring.*}}` references are deliberately *not* resolved into the copied text — they stay as references, so a command pasted into a ticket or a shell history carries no credential. The same holds for every other snippet.

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
