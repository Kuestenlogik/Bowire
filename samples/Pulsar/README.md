# Pulsar — broker + producer sample

A Docker-Compose-managed Pulsar broker plus a small .NET producer that
keeps publishing one message per second to
`persistent://public/default/bowire-demo`. The Bowire Pulsar plugin can
discover the topic via the admin API and tail it via the `subscribe`
operation while the producer keeps feeding.

## Run

```pwsh
# 1. Start Pulsar standalone (binary + admin):
docker compose -f samples/Pulsar/docker-compose.yml up

# 2. In a second terminal, start the demo producer:
dotnet run --project samples/Pulsar/Producer
```

Wait until the broker logs `Pulsar Functions ... started` before the
producer connects. The healthcheck in the compose file gates the
broker as healthy once `pulsar-admin clusters list` succeeds.

## Connect from Bowire

1. Start Bowire and pick the **Pulsar** protocol tab.
2. Paste `pulsar://localhost:6650` into the server URL field. The
   plugin scans `public/default` via the HTTP admin on `:8080` and
   should surface a `bowire-demo` topic.
3. Click `subscribe` — Bowire opens a server-streaming view and you'll
   see the producer's messages flow in.
4. Click `produce` and send your own payload — the running producer
   keeps going alongside.

## Teardown

```pwsh
docker compose -f samples/Pulsar/docker-compose.yml down -v
```
