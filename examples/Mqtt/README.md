# MQTT — broker sample

Eclipse Mosquitto v2 with anonymous access on `tcp/1883` plus
WebSockets on `ws://localhost:9001`.

## Run

```pwsh
docker compose -f examples/Mqtt/docker-compose.yml up
```

## Generate traffic

```pwsh
# mosquitto_pub ships with the official client package on most distros:
mosquitto_pub -h localhost -p 1883 -t "sensors/temp" -m '{"c":21.4}'
mosquitto_pub -h localhost -p 1883 -t "sensors/humidity" -m '{"pct":42}'
```

## Connect from Bowire

Server URL: `tcp://localhost:1883` — the MQTT plugin subscribes to the
`#` wildcard for the discovery scan window, groups topics by prefix
and surfaces each as a Bowire service with publish/subscribe.

For WebSocket transport use `ws://localhost:9001/mqtt`.

## Teardown

```pwsh
docker compose -f examples/Mqtt/docker-compose.yml down -v
```
