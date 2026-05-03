---
summary: 'The Akka.NET plugin streams every message landing in a tap-mailboxed actor''s inbox into the Bowire workbench — observe a live actor system the same way you watch gRPC streams or MQTT topics.'
---

# Akka.NET

The Akka.NET plugin streams every message landing in a tap-mailboxed actor's inbox into the Bowire workbench. You watch a running actor system the same way you watch a gRPC server-stream or an MQTT topic — except the events are inter-actor `Tell`s instead of network frames.

**Package:** `Kuestenlogik.Bowire.Protocol.Akka` (sibling repo, not bundled with the CLI)

## Setup

```bash
bowire plugin install Kuestenlogik.Bowire.Protocol.Akka
```

Akka.NET runs in-process inside the host application — there is no remote endpoint to point Bowire at. The plugin therefore works in **embedded mode only**: the host registers its `ActorSystem` in DI, the plugin resolves it through `Initialize(IServiceProvider)`, and the workbench observes the actor traffic from within the same process. Standalone-CLI mode is not supported (yet — `Akka.Cluster.Tools.ClusterClient` transport is on the 0.2.0 roadmap).

## How it works

1. **Mailbox tap** &mdash; a custom Akka.NET `MailboxType` (`BowireTapMailbox`) wraps the standard unbounded queue and forwards every `Enqueue` to a per-actor-system extension. Opt-in either globally (replace the default mailbox) or per-actor (`Props.WithMailbox("akka.actor.bowire-tap")`).
2. **`IExtension` integration** &mdash; `BowireAkkaExtension` owns the broadcast channel + the active-subscriber list. Steady-state cost when nobody's watching: one volatile read per message, no allocation.
3. **Bowire streaming pane** &mdash; `BowireAkkaProtocol` exposes one server-streaming method, `Tap/MonitorMessages`, that yields `TappedMessage` envelopes (recipient path, sender path, CLR type, payload, timestamp) as JSON.

## Wire it up

### 1. Register the `ActorSystem` in DI

```csharp
using Akka.Actor;
using Microsoft.Extensions.DependencyInjection;

var system = ActorSystem.Create("MyApp", hocon);
builder.Services.AddSingleton(system);
builder.Services.AddBowire(); // picks up Kuestenlogik.Bowire.Protocol.Akka via plugin discovery
```

### 2. Opt actors into the tap mailbox

**Globally** — every actor created after this gets tapped:

```hocon
akka.actor.default-mailbox.mailbox-type = "Kuestenlogik.Bowire.Protocol.Akka.BowireTapMailbox, Kuestenlogik.Bowire.Protocol.Akka"
```

**Per-actor** — surgical, leaves the rest at their default mailbox:

```hocon
akka.actor.bowire-tap = {
  mailbox-type = "Kuestenlogik.Bowire.Protocol.Akka.BowireTapMailbox, Kuestenlogik.Bowire.Protocol.Akka"
}
```

```csharp
var orders = system.ActorOf(
    Props.Create<OrdersActor>().WithMailbox("akka.actor.bowire-tap"),
    "orders");
```

### 3. Watch in Bowire

Open the workbench (`/bowire` in embedded mode), pick the **Akka.NET** tab in the protocol filter, and start streaming `Tap/MonitorMessages`. Every `Tell` that lands in a tapped mailbox lands in the stream pane in real time.

## Sample

A runnable end-to-end sample lives under [`samples/Kuestenlogik.Bowire.Protocol.Akka.Sample`](https://github.com/Kuestenlogik/Bowire.Protocol.Akka/tree/main/samples/Kuestenlogik.Bowire.Protocol.Akka.Sample) — three actors arranged in a small harbour workflow plus a 2-second port-call ticker, so the live message stream is never quiet.

```bash
git clone https://github.com/Kuestenlogik/Bowire.Protocol.Akka.git
cd Bowire.Protocol.Akka
dotnet run --project samples/Kuestenlogik.Bowire.Protocol.Akka.Sample
```

## What it doesn't do (yet)

- **No remote / cluster mode** &mdash; the plugin needs to run in the host process. Cluster-side observation via `Akka.Cluster.Tools.ClusterClient` is parked on the 0.2.0 roadmap.
- **No mailbox-snapshot inspection** &mdash; the tap is a live event stream; there is currently no "show the queue depth + the next N messages" view. Also 0.2.0.
- **No `Tell`-from-Bowire** &mdash; the workbench observes; it does not yet inject messages back into actors. Coming in 0.3.0 alongside typed-payload roundtripping via the configured Akka serializer.
