# Public Sample APIs

Bowire's integration tests and quickstart docs use the public APIs below. Pick one to discover or invoke without standing anything up yourself.

> Endpoints belong to third parties — they go up and down without notice. If a sample stops responding, swap it for any equivalent service.

## REST / OpenAPI

| Service | URL | Notes |
| ------- | --- | ----- |
| Swagger Petstore | `https://petstore3.swagger.io/api/v3/openapi.json` | Classic CRUD playground; full OpenAPI 3 |
| RESTful API | `https://api.restful-api.dev/objects` | GET / POST / PUT / DELETE on generic objects |
| JSONPlaceholder | `https://jsonplaceholder.typicode.com` | Posts, comments, users — read-only |
| Postman Echo | `https://postman-echo.com` | Echoes requests back, useful for headers / auth |

## gRPC

| Service | URL | Notes |
| ------- | --- | ----- |
| Postman gRPC Echo | `grpc.postman-echo.com:443` | Unary + streaming, TLS |
| Local `.proto` | upload via Workspace → Sources | Bowire's `samples/proto/echo.proto` works offline |

## GraphQL

| Service | URL | Notes |
| ------- | --- | ----- |
| Countries | `https://countries.trevorblades.com/graphql` | Queries about countries / continents |
| Rick and Morty | `https://rickandmortyapi.com/graphql` | Schema with relations + pagination |

## WebSocket

| Service | URL | Notes |
| ------- | --- | ----- |
| websocket.events | `wss://echo.websocket.events` | Echo server |
| Postman echo | `wss://ws.postman-echo.com/raw` | Echo server with `/raw` and `/socketio` endpoints |

## Server-Sent Events (SSE)

| Service | URL | Notes |
| ------- | --- | ----- |
| sse.dev | `https://sse.dev/test` | Continuous tick stream |

## MQTT

| Service | URL | Notes |
| ------- | --- | ----- |
| HiveMQ public broker (TCP) | `mqtt://broker.hivemq.com:1883` | No auth |
| HiveMQ public broker (WSS) | `wss://broker.hivemq.com:8884/mqtt` | TLS + WebSocket |
| EMQX public broker | `mqtt://broker.emqx.io:1883` | Alternative when HiveMQ throttles |

## MCP

MCP is a local-only protocol. Use Bowire's own MCP adapter for a self-contained test target:

```bash
bowire mcp serve
```

Then point Discover at the printed URL (default `http://localhost:5000/mcp`).

## Auth playgrounds

| Service | URL | Notes |
| ------- | --- | ----- |
| httpbin Basic Auth | `https://httpbin.org/basic-auth/user/passwd` | Returns 401 unless `user:passwd` is present |
| httpbin Bearer | `https://httpbin.org/bearer` | Expects `Authorization: Bearer ...` |
| httpbin Digest | `https://httpbin.org/digest-auth/auth/user/passwd` | Digest auth demo |
