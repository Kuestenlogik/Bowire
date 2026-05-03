---
summary: 'Bowire discovers MQTT topics by connecting to a broker, subscribing to # for a short window (~3 seconds), and collecting every topic that publishes at least one message.'
---

# MQTT Protocol

Bowire discovers MQTT topics by connecting to a broker, subscribing to `#` for a short window (~3 seconds), and collecting every topic that publishes at least one message.

## Setup

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.Mqtt
```

### Standalone

```bash
bowire --url mqtt://localhost:1883
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("mqtt://localhost:1883");
});
```

## Discovery

Topics are grouped by their first path segment:

- `sensors/temperature` and `sensors/humidity` become the `sensors` service
- `home/lights/kitchen` becomes the `home` service
- Single-segment topics go into `(root)`

Each topic surfaces as two methods:
- **Subscribe** (ServerStreaming) -- listen for messages on the topic
- **Publish** (Unary) -- send a message to the topic

## Payload Handling

MQTT payloads are binary. Bowire tries three strategies:

1. **JSON** -- parsed and pretty-printed
2. **UTF-8 text** -- displayed as plain text
3. **Binary** -- hex dump with byte count

## Input Fields

- **payload** -- message content (JSON or text)
- **qos** -- Quality of Service (0 = AtMostOnce, 1 = AtLeastOnce, 2 = ExactlyOnce)
- **retain** -- whether the broker should retain the message

## Broker URL Formats

- `mqtt://host:1883` (standard)
- `mqtts://host:8883` (TLS)
- `tcp://host:1883`
- `host:1883` (scheme optional)
