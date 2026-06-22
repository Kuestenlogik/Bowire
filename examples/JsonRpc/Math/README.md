# JSON-RPC — Math sample

JSON-RPC 2.0 endpoint at `/rpc` that publishes its surface via the
OpenRPC `rpc.discover` convention. Three methods: `add`, `subtract`,
`divide`.

## Run

```pwsh
dotnet run --project examples/JsonRpc/Math
```

Listens on `http://localhost:5187`.

## Connect from Bowire

Server URL: `http://localhost:5187/rpc`. The JSON-RPC plugin calls
`rpc.discover` and lists all three methods with their parameter
shapes pulled from the OpenRPC document.
