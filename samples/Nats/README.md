# NATS — broker sample

Single-node NATS broker with JetStream enabled (`-js`). Bowire's
NATS plugin can connect over `nats://localhost:4222` and exercise
all three discovery sources (subject scan, JetStream streams,
Services API).

## Run

```pwsh
docker compose -f samples/Nats/docker-compose.yml up
```

## Generate traffic

The plugin's discovery is passive — it observes whatever flows past
during the scan window. To see services appear, publish from a second
terminal:

```pwsh
# Using the standard NATS CLI (https://github.com/nats-io/natscli):
nats pub "orders.created" '{"id":42,"customer":"foo"}'
nats pub "orders.shipped" '{"id":42,"carrier":"bowire"}'

# Or create a JetStream stream so the plugin's JetStream discovery
# has something to surface:
nats stream add ORDERS --subjects "orders.>" --storage file --defaults
nats pub orders.created '{"id":43}'
```

## Connect from Bowire

Server URL: `nats://localhost:4222` — the plugin auto-detects core
subjects, JetStream streams, and any registered Services API
endpoints.

## Teardown

```pwsh
docker compose -f samples/Nats/docker-compose.yml down -v
```
