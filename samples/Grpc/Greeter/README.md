# gRPC — Greeter sample

The canonical gRPC Greeter with both unary (`SayHello`) and
server-streaming (`SayHelloStream`) RPCs. Server Reflection is on so
Bowire's gRPC plugin discovers the surface without a `.proto` upload.

## Run

```pwsh
dotnet run --project samples/Grpc/Greeter
```

Listens on `http://localhost:5182`.

## Connect from Bowire

Server URL: `http://localhost:5182`. `Greeter` shows up in the service
list with `SayHello` (Unary) and `SayHelloStream` (Server streaming).
