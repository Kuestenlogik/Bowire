# SignalR — Chat sample

ASP.NET Core SignalR hub at `/chathub` with `SendMessage` (broadcast)
and `Echo` (round-trip to caller).

## Run

```pwsh
dotnet run --project examples/SignalR/Chat
```

Listens on `http://localhost:5184`.

## Connect from Bowire

Server URL: `http://localhost:5184/chathub`. The SignalR plugin
performs the standard negotiate handshake and surfaces both hub
methods for invocation.
