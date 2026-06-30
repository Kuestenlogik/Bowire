---
title: Build a protocol plugin
summary: 'Implement IBowireProtocol to teach Bowire a new wire protocol — discovery, unary invoke, server / client / duplex streaming, and an interactive channel for duplex traffic.'
---

# Build a protocol plugin

A **protocol plugin** is what Bowire calls a package that speaks a wire protocol — gRPC, REST, GraphQL, SignalR, MQTT, NATS, WebSocket, SSE, MCP, Pulsar, Socket.IO, SOAP, JSON-RPC, OData. Reach for this seam when you want Bowire to discover services + methods, send requests, and consume responses over a transport it doesn't already cover. Once registered, the protocol gets a tab in the Compose rail, its own discovery path, and its own slot in the recorder + mock host.

## The interface

`IBowireProtocol` lives in `src/Kuestenlogik.Bowire/IBowireProtocol.cs`. The public surface:

```csharp
public interface IBowireProtocol
{
    string Name { get; }
    string Id { get; }
    string IconSvg { get; }
    string Description => "";

    void Initialize(IServiceProvider? serviceProvider) { }
    IReadOnlyList<BowirePluginSetting> Settings => [];

    Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default);

    Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);

    IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);

    Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default);
}
```

What each member does:

- **`Name`** — display label shown on the protocol tab in the workbench.
- **`Id`** — short snake-lower identifier (e.g. `"grpc"`, `"rest"`, `"signalr"`). Used in URLs, recording manifests, and the `--disable-plugin` CLI flag.
- **`IconSvg`** — inline SVG used as the protocol tab glyph. Whatever you return is dropped into the DOM verbatim; keep it scoped to a 16×16 viewbox to match the built-ins.
- **`Description`** — optional one-line description shown next to the plugin in Settings → Plugins (≤ 100 characters).
- **`Initialize`** — called after registration, before any discovery / invoke call. The embedded-mode service provider rides through this so plugins can read `IConfiguration`, register their own services, and capture an `IHttpClientFactory` if needed. The default is a no-op so simple plugins can ignore it.
- **`Settings`** — schema for the per-plugin settings panel (toggles / inputs); empty by default.
- **`DiscoverAsync`** — given a server URL, return the catalogue of services + methods. The default implementation isn't optional: every protocol must produce something here, even if "discovery" just means "introspect a static config file" or "probe a well-known URL."
- **`InvokeAsync`** — unary or client-streaming dispatch. `jsonMessages` carries the request envelope(s); the return value (`InvokeResult` in `Kuestenlogik.Bowire.Models`) lifts response status + body + headers.
- **`InvokeStreamAsync`** — server-streaming or duplex; returns an `IAsyncEnumerable<string>` of response payloads.
- **`OpenChannelAsync`** — returns an `IBowireChannel` for fully interactive duplex traffic (the workbench's "Open channel" affordance binds to this). Return `null` for protocols that don't support interactive channels.

For interactive channels the contract is on `IBowireChannel` in the same file:

```csharp
public interface IBowireChannel : IAsyncDisposable
{
    string Id { get; }
    bool IsClientStreaming { get; }
    bool IsServerStreaming { get; }
    int SentCount { get; }
    bool IsClosed { get; }
    long ElapsedMs { get; }
    string? NegotiatedSubProtocol => null;

    Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);
    IAsyncEnumerable<string> ReadResponsesAsync(CancellationToken ct = default);
}
```

## Minimal working example

The REST plugin (`src/Kuestenlogik.Bowire.Protocol.Rest/BowireRestProtocol.cs`) is the most digestible in-repo template — it discovers via OpenAPI, dispatches over HTTP, and shows how `Initialize` plumbs the host's `IConfiguration` through:

```csharp
public sealed class BowireRestProtocol : IBowireProtocol, IInlineHttpInvoker, IDisposable
{
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // ... per-server schema cache + probe-path resolution cache ...
    private IServiceProvider? _serviceProvider;

    public string Name => "REST";
    public string Description =>
        "OpenAPI / Swagger — discover + invoke HTTP services described by an OpenAPI document.";
    public string Id => "rest";
    public string IconSvg => """<svg viewBox="0 0 24 24" ... </svg>""";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        _serviceProvider = serviceProvider;
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromSeconds(30));
    }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default) { ... }

    // ...InvokeAsync, InvokeStreamAsync, OpenChannelAsync...
}
```

The full file is ~1500 lines because REST has to handle uploaded specs + embedded discovery + a probe sweep over eight well-known OpenAPI paths. For your first plugin, look at the discovery / invoke methods in isolation — the cache + probe logic is REST-specific.

The optional secondary interfaces (`IInlineHttpInvoker` here, `IInlineSseSubscriber`, `IInlineWebSocketChannel`) are how other plugins reach across the registry without taking a compile-time reference. If your protocol exposes a primitive other plugins might want to reuse (an HTTP invoker, a WS channel), declaring an inline-interface implementation lets them ask `BowireProtocolRegistry.FindHttpInvoker()` for it.

## Registration

Auto-discovery is the only path. `BowireProtocolRegistry.Discover` (in `src/Kuestenlogik.Bowire/BowireProtocolRegistry.cs`) does the work:

1. Force-loads every `Kuestenlogik.Bowire*.dll` next to the entry assembly.
2. Walks `AppDomain.CurrentDomain.GetAssemblies()` filtered to names containing `Bowire`.
3. For each assembly, picks every concrete `IBowireProtocol` implementation, invokes the parameterless constructor through `Activator.CreateInstance`, and `Register()`s the instance.
4. Honours the `Bowire:DisabledPlugins` config / `--disable-plugin` CLI flag — disabled ids are recorded but skipped.
5. After the .NET sweep, also asks `SidecarPluginDiscovery.Discover` for any polyglot plugins in `~/.bowire/plugins/<id>/`.

Every plugin must therefore expose a **parameterless public constructor**. Any exception escaping it (or the assembly's static ctor) is caught and logged; one bad DLL never aborts the scan.

After registration, Core calls `protocol.Initialize(serviceProvider)` so plugins can grab the host's `IConfiguration` and any DI services they need.

There's a second seam for the DI registration side of the story: if your plugin needs to register its own services at `AddBowire()` time (DI singletons, hosted services, options binding), implement `IBowireProtocolServices` next to your protocol class. The `AddBowire()` sweep instantiates every implementation and calls `ConfigureServices(IServiceCollection)` on it. The Welle 2 packages use `IBowireServiceContribution` for the same purpose at a broader scope (no protocol identity attached).

## See also

- <xref:Kuestenlogik.Bowire.IBowireProtocol> — auto-generated interface reference.
- [Protocol Guides](../protocols/index.md) — behavioural conventions (how discovery should resolve, what metadata shapes Bowire expects).
- [Sidecar Plugins](../architecture/sidecar-plugins.md) — JSON-RPC bridge for non-.NET plugins.
- [Plugin architecture](../architecture/plugin-architecture.md) — registries, lifetimes, plugin install paths.
