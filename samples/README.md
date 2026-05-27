# Bowire Samples

Minimal, runnable servers for every Bowire protocol plugin. Each sample
boots a target Bowire can connect to so you can see the plugin in
action without standing up a real backend.

| Sample | Plugin | How to run | Connect from Bowire |
|--------|--------|------------|---------------------|
| [Soap/CalculatorService](Soap/CalculatorService) | `Bowire.Protocol.Soap` | `dotnet run` | `http://localhost:5180/Calculator.asmx` |
| [Pulsar/](Pulsar) | `Bowire.Protocol.Pulsar` | `docker compose up` + `dotnet run --project Producer` | `pulsar://localhost:6650` |
| [Rest/PetStore](Rest/PetStore) | `Bowire.Protocol.Rest` | `dotnet run` | `http://localhost:5181` |
| [Grpc/Greeter](Grpc/Greeter) | `Bowire.Protocol.Grpc` | `dotnet run` | `http://localhost:5182` |
| [GraphQL/Books](GraphQL/Books) | `Bowire.Protocol.GraphQL` | `dotnet run` | `http://localhost:5183/graphql` |
| [SignalR/Chat](SignalR/Chat) | `Bowire.Protocol.SignalR` | `dotnet run` | `http://localhost:5184/chathub` |
| [WebSocket/Echo](WebSocket/Echo) | `Bowire.Protocol.WebSocket` | `dotnet run` | `ws://localhost:5185/ws` |
| [Sse/Ticker](Sse/Ticker) | `Bowire.Protocol.Sse` | `dotnet run` | `http://localhost:5186/events` |
| [JsonRpc/Math](JsonRpc/Math) | `Bowire.Protocol.JsonRpc` | `dotnet run` | `http://localhost:5187/rpc` |
| [OData/Northwind](OData/Northwind) | `Bowire.Protocol.OData` | `dotnet run` | `http://localhost:5188/odata` |
| [SocketIo/Chat](SocketIo/Chat) | `Bowire.Protocol.SocketIo` | `npm start` | `http://localhost:5189` |
| [Nats/](Nats) | `Bowire.Protocol.Nats` | `docker compose up` | `nats://localhost:4222` |
| [Mqtt/](Mqtt) | `Bowire.Protocol.Mqtt` | `docker compose up` | `tcp://localhost:1883` |
| [Mcp/Tools](Mcp/Tools) | `Bowire.Protocol.Mcp` | `dotnet run` | `http://localhost:5190/mcp` |

## Conventions

- Every sample listens on a port between 5180 and 5199 so they don't
  collide with Bowire itself (default 5080) or the usual ASP.NET dev
  ports.
- Broker-backed samples (Pulsar, NATS, MQTT) ship a `docker-compose.yml`
  for the broker plus a small .NET CLI producer + consumer so you can
  generate traffic from one side and inspect it from Bowire on the
  other.
- Samples target the same .NET 10 / SDK Bowire uses; no per-sample
  Directory.Build.props overrides.
