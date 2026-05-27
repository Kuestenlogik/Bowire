# WebSocket — Echo sample

Plain ASP.NET Core WebSocket endpoint at `/ws`. Every text frame is
echoed back with an `echo: ` prefix.

## Run

```pwsh
dotnet run --project samples/WebSocket/Echo
```

Listens on `http://localhost:5185`.

## Connect from Bowire

Server URL: `ws://localhost:5185/ws`. Open the channel from the
WebSocket plugin, send a text frame, watch the echo come back.
