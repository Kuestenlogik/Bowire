---
title: "Many protocols out of the box — and if yours is missing, build it in"
description: "Swagger UI and Scalar show REST. Postman, Insomnia and Bruno live outside your service. Bowire is the API workbench that embeds in your own service — and speaks gRPC, SignalR, MQTT, MCP and more, plus any protocol you teach it. With a worked example: a situational-awareness gRPC API on a live map."
lede: Most API tools speak HTTP and treat everything else as an afterthought. Bowire speaks gRPC, REST, GraphQL, SignalR, MQTT, MCP and a dozen more from one workbench — and when the protocol you need is not among them, you plug it in yourself.
---

A .NET backend in 2026 rarely speaks one protocol. There is REST for the public API, gRPC between services, SignalR pushing to the UI, maybe MQTT from devices, and more and more an MCP endpoint for AI agents. Each of them has a tool — and each tool has its own UI, its own idea of a "collection", its own place outside your service.

[Bowire](https://bowire.io) is the API workbench that embeds in your own service. One line mounts it into your ASP.NET app; it discovers what that app exposes and lets you call all of it from one browser UI. Or run it standalone against any URL. This article is about the part that sets it apart most, in two halves: **many protocols out of the box — and if yours is missing, you build it in.**

## Many protocols out of the box

Swagger UI and Scalar start from a document: an OpenAPI spec is the source of truth, and they render it. That is the right model for a public REST API designed contract-first. Bowire starts from the **running service** and asks it what it speaks:

- **gRPC** — server reflection, or a `.proto` you upload
- **REST** — the OpenAPI document the service emits
- **GraphQL** — introspection
- **SignalR** — the hubs registered in the app
- **MCP** — `server/discover`: tools, resources, prompts
- **MQTT** — topics, live
- …and WebSocket, SSE, Socket.IO, JSON-RPC, OData and SOAP.

That is what the standalone tool ships with. NATS, Pulsar, Kafka, AMQP and Akka.NET are one `bowire plugin install` away.

Each protocol is a plugin behind one interface. Whatever it discovered shows up in the same sidebar, with the same request form, the same streaming view, the same recorder and the same mock — a gRPC server stream and an MQTT subscription look and behave alike.

Embedded, it is three lines:

```csharp
builder.Services.AddBowire();
// ...
app.MapBowire();   // the workbench at /bowire
```

Standalone, it is a .NET tool:

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool
bowire --url grpc@https://localhost:5001
```

## More than call-and-response

Because every protocol goes through the same seams, the features on top work for all of them:

- **Streams** — server streaming, client streaming, duplex channels, subscriptions; frames arrive live.
- **Recordings** — capture a session across protocols and replay it.
- **Mocks** — serve a recording as a stand-in backend: `bowire mock`.
- **Flows and tests** — chain calls, assert on responses, run them headless with `bowire test` in CI.
- **An MCP surface** — AI agents can drive the workbench themselves.

## And if yours is missing: build it in

This is what no other workbench offers. Suppose your systems speak a protocol that is not on anyone's list — a domain API, an industrial bus, an in-house transport over TCP. In Bowire, that is a plugin, and writing one is an afternoon, not a project.

**1. Scaffold it.** A project template sets up the plugin, a test project and a CI workflow:

```bash
dotnet new install Kuestenlogik.Bowire.Templates
dotnet new bowire-plugin -n Contoso.Bowire.Protocol.Foo --DisplayName "Foo" --ProtocolId foo
cd Contoso.Bowire.Protocol.Foo
dotnet test
```

The package name has to contain `Bowire` — that is how the workbench finds plugins when it scans. `--Preset rest|mqtt|websocket|grpc|signalr` seeds the code with a realistic starting point for a transport close to yours.

**2. Implement the contract.** Everything a protocol does goes through one interface, `IBowireProtocol`:

```csharp
public sealed class FooProtocol : IBowireProtocol
{
    public string Name => "Foo";
    public string Id => "foo";
    public string IconSvg => """<svg viewBox="0 0 16 16">…</svg>""";

    // What does the server at this URL offer?
    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    { /* ask the server — or read a bundled schema — and describe its services and methods */ }

    // One request, one response.
    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method, List<string> jsonMessages,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    { /* send it, return status, body and headers */ }

    // A subscription or a server stream: one JSON frame per message.
    public IAsyncEnumerable<string> InvokeStreamAsync(/* … */)
    { /* yield frames as they arrive */ }

    // Fully interactive duplex traffic — or null if the protocol has none.
    public Task<IBowireChannel?> OpenChannelAsync(/* … */)
        => Task.FromResult<IBowireChannel?>(null);
}
```

That is the whole contract: **discover, invoke, stream**, optionally a duplex channel. Discovery is where the work is: the services and methods you return — with the fields of each request — become the sidebar and the request form. A protocol without a discovery mechanism can read a bundled schema or a config file instead; "discovery" only has to mean "say what there is".

**3. Add what your protocol needs, and nothing else.** Settings for the plugin's panel, a richer report when discovery half-succeeds, a mock emitter so recordings replay through your protocol (`dotnet new bowire-mock-emit`), a `bowire foo` CLI verb (`dotnet new bowire-cli-cmd`). All optional.

**4. Ship it.** `dotnet pack` produces a NuGet package. Standalone, `bowire plugin install` puts it next to the built-in protocols; embedded, it is a package reference in the host app. The [plugin guide](/docs/extending/protocol.html) has the details.

**What you get for free.** Your protocol gets its own tab, its own discovery, the request form, the live streaming view, recording and replay, flows and headless tests, and the MCP surface that lets AI agents call it — without a line of UI code. And everything the workbench learns later, your protocol inherits.

### How far it can go: situational awareness on a map

[TacticalAPI](https://github.com/Rheinmetall/tacticalapi) is an open-source gRPC interface for situational-awareness systems, published by Rheinmetall: tracks, the reporting platform's own position, blue-force tracking — streams of moving objects with military symbol codes. It is a good test for a workbench, because a JSON tree of coordinates tells you very little.

The [TacticalAPI plugin](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi) is built on exactly the interface above, and shows what a plugin can add once the basics work:

- **A bundled schema.** The plugin compiles the upstream `.proto` files at build time, so the service tree appears even when the server has no reflection enabled — no proto upload, no hand-editing.
- **Streams as streams.** `SubscribeSituationObjectEvents` and its siblings are server-streaming pumps; the plugin surfaces them as such.
- **The protocol's own idea of failure.** TacticalAPI refuses a request with a gRPC `OK` whose header says `success = false`. The plugin reads that header on every reply and frame and reports a refusal as a refusal — something a generic gRPC client cannot know.
- **Both wires, and mTLS** — native gRPC or gRPC-Web, client certificates through the same auth profile every other protocol uses.

And then the part nobody wrote a line of plugin code for: **the map**. Bowire's semantics layer looks at every response and recognises what the fields *mean*. A coordinate detector spots `{latitudeCoordinate, longitudeCoordinate}` pairs; the map widget mounts next to the JSON and puts every object on it. Where an object carries a MIL-STD-2525 symbol code, the pin is drawn as that symbol. Polygons, phase lines, corridors and range fans are drawn as tactical graphics. Subscribe to the event stream, and the tracks move as the server pushes new snapshots; scrub back through the frames, and the map follows.

![Bowire streaming TacticalAPI situation objects: the JSON frames in the middle, the same objects as MIL-STD-2525 symbols and tactical graphics on a satellite map on the right]({{ '/assets/images/screenshots/tacticalapi-map-dark.png' | absolute_url }})

The same detector fires for any protocol whose payload carries coordinates — an MQTT feed of vehicle positions, an AIS stream, a GPS trace over SignalR. The map is not a TacticalAPI feature; it is a workbench feature that a new protocol inherits. That is the point of building multi-protocol from the ground up: **a new protocol gets everything the workbench already does**, and a new viewer works for every protocol at once.

To try it, the plugin repository ships a sample server with thirteen tracks around the Baltic coast and a set of control measures:

```bash
bowire plugin install Kuestenlogik.Bowire.Protocol.TacticalApi
dotnet run --project samples/Kuestenlogik.Bowire.Protocol.TacticalApi.Sample   # in the plugin repo
bowire --url tacticalapi@http://localhost:5192
```

Invoke `Situation.GetSituationObjects`, or subscribe to `SubscribeSituationObjectEvents` and watch them move.

## When something else is the better fit

Bowire is not trying to be everything:

- **Scalar or Swagger UI** remain the better choice for a polished public docs site generated from a spec you design first. Bowire reads the same OpenAPI document; many teams use both. ([Bowire vs. Scalar](/alternatives/scalar.html))
- **Postman** fits a team that curates REST collections together in the cloud. Bowire is local-first, with no account. ([Bowire vs. Postman](/alternatives/postman.html))
- **Bruno** is great when Git-native REST collections are the whole story. ([Bowire vs. Bruno](/alternatives/bruno.html))

Bowire is for the service that speaks more than one protocol — or one nobody else supports.

## Try it

```bash
dotnet tool install -g Kuestenlogik.Bowire.Tool
bowire --url https://your-service
```

Or add `Kuestenlogik.Bowire` to your ASP.NET app and call `app.MapBowire()`. The [quickstart](/quickstart.html) takes five minutes; the [Bootcamp](/bootcamp/) goes from a first call to writing your own protocol plugin. Bowire is open source under Apache-2.0 — [on GitHub](https://github.com/Kuestenlogik/Bowire).
