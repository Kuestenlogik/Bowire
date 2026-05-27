# SSE — Ticker sample

Server-Sent Events endpoint at `/events`. Streams one `tick` event per
second with a sequence number and UTC timestamp.

## Run

```pwsh
dotnet run --project samples/Sse/Ticker
```

Listens on `http://localhost:5186`.

## Connect from Bowire

Server URL: `http://localhost:5186/events`. The SSE plugin tails the
stream and surfaces each event as it arrives.
