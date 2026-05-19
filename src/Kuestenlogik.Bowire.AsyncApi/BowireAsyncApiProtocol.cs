// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Kuestenlogik.Bowire.Models;
using Neuroglia.AsyncApi;
using Neuroglia.AsyncApi.v3;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// AsyncAPI as a Bowire discovery source. Implements <see cref="IBowireProtocol"/>
/// so it slots into the existing assembly-scanned registry, URL-hint routing
/// (<c>asyncapi@./doc.yaml</c>), and recording/mock plumbing — but it does not
/// own a wire. <see cref="DiscoverAsync"/> parses an AsyncAPI document and
/// turns its channels into <see cref="BowireServiceInfo"/> nodes; the actual
/// publish/subscribe calls in <see cref="InvokeAsync"/> are routed to the
/// matching wire plugin (MQTT, Kafka, WebSocket, …) at runtime via
/// <c>BowireProtocolRegistry</c>.
///
/// Phase A2: the loader is wired up — <see cref="DiscoverAsync"/> reads an
/// AsyncAPI 3.0 document, walks its channels, and emits one
/// <see cref="BowireServiceInfo"/> per channel. The binding-translator layer
/// (which routes invocations to MQTT / Kafka / WebSocket via
/// <c>BowireProtocolRegistry</c>) lands in Phase A3 — until then,
/// <see cref="InvokeAsync"/> still raises <see cref="NotSupportedException"/>.
/// AsyncAPI 2.x parsing is supported by the SDK reader but not yet mapped
/// here; only V3 documents produce services.
/// </summary>
public sealed class BowireAsyncApiProtocol : IBowireProtocol
{
    // Static loader — Neuroglia's reader is stateless and the DI graph
    // boilerplate is cheap to amortise across discovery calls.
    private static readonly AsyncApiDocumentLoader Loader = new();

    // Discovered documents are cached by their source URL so InvokeAsync
    // can look up channel + operation + server without re-parsing the
    // YAML on every call. Eviction policy is "rediscover replaces" — a
    // fresh DiscoverAsync of the same URL overwrites the cached entry.
    private readonly ConcurrentDictionary<string, V3AsyncApiDocument> _documents = new(StringComparer.Ordinal);

    // Resolvers picked up at Initialize time (one per wire-binding key).
    // The dict is populated once the host hands us a service provider
    // carrying a BowireProtocolRegistry; until then InvokeAsync returns
    // a clear "no registry available" error rather than guessing.
    private readonly Dictionary<string, IAsyncApiBindingResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "AsyncAPI";

    public string Id => "asyncapi";

    public string IconSvg =>
        // Placeholder glyph — three stacked horizontal lines with a small
        // arrow head suggesting "channels with messages flowing". Will be
        // swapped for the official AsyncAPI mark (subject to licensing
        // check) when the UI surface lands.
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h12"/><path d="M4 12h8"/><path d="M4 17h14"/><path d="M18 7l3 0"/><path d="M15 12l3 0"/><path d="M21 17l-3 -3"/><path d="M21 17l-3 3"/></svg>""";

    /// <summary>
    /// Hook the AsyncAPI protocol into the host's <see cref="BowireProtocolRegistry"/>.
    /// We need the registry to dispatch invocations to wire plugins (MQTT,
    /// Kafka, …); without it the plugin still loads documents and renders
    /// services, but <see cref="InvokeAsync"/> can't route anywhere.
    ///
    /// Called by <c>MapBowire()</c> and <c>WithMcpAdapter()</c> on every
    /// IBowireProtocol after discovery scans pick it up. Standalone CLI
    /// passes its own ServiceProvider too.
    /// </summary>
    public void Initialize(IServiceProvider? serviceProvider)
    {
        if (serviceProvider is null) return;
        var registry = (BowireProtocolRegistry?)serviceProvider.GetService(typeof(BowireProtocolRegistry));
        if (registry is null) return;

        // Resolvers are owned by this plugin and pinned to the registry the
        // host wires up at startup. Phase A3 ships only the MQTT resolver;
        // Phase A4+ adds Kafka / WebSocket / AMQP / NATS alongside the
        // corresponding wire-plugin landings.
        _resolvers["mqtt"] = new MqttBindingResolver(registry);
    }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        // Early-out for any URL that doesn't look like an AsyncAPI source.
        // Discovery probes every plugin against every `bowire --url X`
        // invocation, and the AsyncAPI loader would otherwise try to
        // HTTP-GET arbitrary gRPC / REST / MQTT endpoints just to fail
        // on parse. The extension check matches the OpenAPI plugin's
        // habit of only engaging when the URL ends in .yaml/.json.
        if (!AsyncApiDocumentLoader.LooksLikeAsyncApiSource(serverUrl))
        {
            return [];
        }

        IAsyncApiDocument document;
        try
        {
            document = await Loader.LoadAsync(serverUrl, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (HttpRequestException) { return []; }

        // Phase A2 only maps V3 documents; V2 support arrives later.
        if (document is not V3AsyncApiDocument v3)
        {
            return [];
        }

        // Cache the parsed document so InvokeAsync can find the channel +
        // operation + servers without re-reading the YAML. Keyed by the
        // exact serverUrl the discovery dispatcher passed in so multi-URL
        // workbenches keep each AsyncAPI document separate.
        _documents[serverUrl] = v3;

        return MapV3Channels(v3, serverUrl);
    }

    /// <summary>
    /// V3 channel + operation walker. Each channel becomes one
    /// <see cref="BowireServiceInfo"/>; each operation that targets that
    /// channel becomes one <see cref="BowireMethodInfo"/> on the service.
    /// The operation's <c>action</c> (send / receive) drives the streaming
    /// direction:
    /// <list type="bullet">
    ///   <item><c>send</c> → client streams toward the broker (we publish)</item>
    ///   <item><c>receive</c> → server streams toward us (we subscribe)</item>
    /// </list>
    /// Channels that no operation references still get a service entry so the
    /// sidebar shows the discovered topology — they just stay empty until the
    /// author adds the operations.
    /// </summary>
    private static List<BowireServiceInfo> MapV3Channels(V3AsyncApiDocument document, string sourceUrl)
    {
        var apiTitle = document.Info?.Title ?? "AsyncAPI";
        var apiVersion = document.Info?.Version;
        var services = new List<BowireServiceInfo>();
        if (document.Channels is null) return services;

        // Bucket operations by the channel they $ref so each channel-service
        // only iterates its own operations below. Operations whose $ref
        // can't be resolved get dropped — they wouldn't have anywhere to
        // attach in Bowire's service/method tree anyway.
        var opsByChannel = new Dictionary<string, List<(string Key, V3OperationDefinition Op)>>(StringComparer.Ordinal);
        if (document.Operations is not null)
        {
            foreach (var (opKey, op) in document.Operations)
            {
                var channelKey = ResolveChannelRef(op.Channel?.Reference);
                if (channelKey is null) continue;
                if (!opsByChannel.TryGetValue(channelKey, out var list))
                {
                    list = [];
                    opsByChannel[channelKey] = list;
                }
                list.Add((opKey, op));
            }
        }

        foreach (var (channelKey, channel) in document.Channels)
        {
            var methods = new List<BowireMethodInfo>();
            if (opsByChannel.TryGetValue(channelKey, out var channelOps))
            {
                foreach (var (opKey, op) in channelOps)
                {
                    var isSend = op.Action == V3OperationAction.Send;
                    methods.Add(new BowireMethodInfo(
                        Name: opKey,
                        FullName: $"{apiTitle}.{channelKey}.{opKey}",
                        ClientStreaming: isSend,
                        ServerStreaming: !isSend,
                        InputType: new BowireMessageInfo(opKey + "Request", opKey + "Request", []),
                        OutputType: new BowireMessageInfo(opKey + "Response", opKey + "Response", []),
                        MethodType: isSend ? "asyncapi-send" : "asyncapi-receive")
                    {
                        Summary = channel.Title ?? channel.Summary,
                        Description = channel.Description,
                        // HttpPath gets the channel address so the sidebar
                        // can show `smarthome/light/measured` next to the
                        // operation key — re-using the existing REST slot
                        // is a cheap way to render the channel-address
                        // hint without a new model field.
                        HttpPath = channel.Address
                    });
                }
            }

            services.Add(new BowireServiceInfo(
                Name: channelKey,
                Package: apiTitle,
                Methods: methods)
            {
                Source = "asyncapi",
                Description = document.Info?.Description,
                Version = apiVersion,
                OriginUrl = sourceUrl,
                IsUploaded = false
            });
        }

        return services;
    }

    /// <summary>
    /// Pull the channel key out of an AsyncAPI <c>$ref</c> pointer.
    /// Accepts <c>#/channels/lightingMeasured</c> form; everything else
    /// returns <c>null</c> so the operation gets dropped rather than
    /// landing on a wrong channel.
    /// </summary>
    private static string? ResolveChannelRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        const string prefix = "#/channels/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var key = reference[prefix.Length..];
        return string.IsNullOrEmpty(key) ? null : key;
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // service = channel key (e.g. "lightingMeasured")
        // method  = operation key (e.g. "sendTurnOnOff")
        //
        // Walk: cached document → operation lookup → resolve channel + first
        // server → pick the resolver whose binding matches the server's
        // declared protocol → delegate.
        if (!_documents.TryGetValue(serverUrl, out var document))
        {
            return Error("AsyncAPI document has not been discovered yet. " +
                $"Call discover with serverUrl '{serverUrl}' first.");
        }

        if (document.Operations is null || !document.Operations.TryGetValue(method, out var operation))
        {
            return Error($"Operation '{method}' not found in AsyncAPI document.");
        }

        // Server selection: Phase A3 picks the first declared server whose
        // protocol matches a resolver we have. Multi-server documents and
        // per-channel servers[] overrides arrive in Phase A4.
        if (document.Servers is null || document.Servers.Count == 0)
        {
            return Error("AsyncAPI document declares no servers; cannot route invocation.");
        }

        var (serverName, server) = document.Servers.First();
        if (string.IsNullOrWhiteSpace(server.Protocol))
        {
            return Error($"Server '{serverName}' has no protocol declared.");
        }

        if (!_resolvers.TryGetValue(server.Protocol, out var resolver))
        {
            return Error(
                $"No AsyncAPI binding resolver registered for protocol '{server.Protocol}'. " +
                "Phase A ships the MQTT resolver only; Kafka / WebSocket / AMQP / NATS " +
                "join as their wire plugins land (see ROADMAP: AsyncAPI as a discovery source).");
        }

        var channelKey = ResolveChannelRef(operation.Channel?.Reference);
        if (channelKey is null || !document.Channels!.TryGetValue(channelKey, out var channel))
        {
            return Error($"Operation '{method}' references an unknown channel.");
        }

        var brokerUrl = $"{server.Protocol}://{server.Host}{server.PathName ?? string.Empty}";
        var ctx = new AsyncApiChannelContext(
            ServerUrl: brokerUrl,
            ChannelAddress: channel.Address ?? channelKey,
            OperationAction: operation.Action == V3OperationAction.Send ? "send" : "receive",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        return await resolver.InvokeAsync(ctx, jsonMessages, metadata, ct).ConfigureAwait(false);
    }

    private static InvokeResult Error(string message) =>
        new(Response: null, DurationMs: 0, Status: "Error",
            Metadata: new Dictionary<string, string> { ["error"] = message });

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        throw new NotSupportedException(
            "AsyncAPI discovery source has not loaded any channels yet — " +
            "InvokeStreamAsync will be wired through the binding-resolver " +
            "layer (Phase A2).");
#pragma warning disable CS0162 // Unreachable code — the yield keeps the
                              // method an iterator so the signature stays
                              // an IAsyncEnumerable<string>.
        yield break;
#pragma warning restore CS0162
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        // Streaming channels arrive with Phase B (WebSocket + Kafka
        // bindings). Phase A only covers MQTT publish/subscribe, which
        // goes through InvokeAsync / InvokeStreamAsync.
        return Task.FromResult<IBowireChannel?>(null);
    }
}
