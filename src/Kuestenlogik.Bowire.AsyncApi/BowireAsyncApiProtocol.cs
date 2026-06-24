// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Neuroglia.AsyncApi;
using Neuroglia.AsyncApi.v2;
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

    // Discovered documents are cached by their source URL so
    // InvokeAsync can look up channel + operation + server without
    // re-parsing the YAML on every call. Eviction policy is
    // "rediscover replaces" — a fresh DiscoverAsync of the same URL
    // overwrites the cached entry. V2 and V3 documents go into
    // separate caches because their downstream lookup shapes are
    // different enough that branching at invoke time keeps the code
    // simpler than a tagged-union over IAsyncApiDocument.
    private readonly ConcurrentDictionary<string, V3AsyncApiDocument> _documents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, V2AsyncApiDocument> _v2Documents = new(StringComparer.Ordinal);

    // Per-document binding-field extraction (separate cache because we
    // walk the raw YAML via AsyncApiBindingsExtractor — the Neuroglia
    // SDK reader doesn't expose typed bindings, and on bindings.mqtt.qos
    // it actually crashes, see asyncapi/net-sdk#76). Keyed by source
    // URL → opKey → bindingId → field-map. Empty if the document
    // declares no operations-level bindings.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>>
        _bindingsByUrl = new(StringComparer.Ordinal);

    // Same idea for V2 documents, but the lookup shape is one level
    // deeper because V2 bindings nest under `channels[].publish` /
    // `channels[].subscribe`: source URL → channelKey → slot
    // ("publish"|"subscribe") → bindingId → field-map.
    private readonly ConcurrentDictionary<string,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>>>
        _v2BindingsByUrl = new(StringComparer.Ordinal);

    // Resolvers picked up at Initialize time (one per wire-binding key).
    // The dict is populated once the host hands us a service provider
    // carrying a BowireProtocolRegistry; until then InvokeAsync returns
    // a clear "no registry available" error rather than guessing.
    private readonly Dictionary<string, IAsyncApiBindingResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "AsyncAPI";
    public string Description => "AsyncAPI-described event-driven APIs — channel discovery across pub/sub bindings.";

    public string Id => "asyncapi";

    public string IconSvg =>
        // Official AsyncAPI mark from the asyncapi/brand repo
        // (logos/asyncapi/mark/primary/SVG/asyncapi-logo-mark--primary.svg).
        // Apache-2.0-licensed alongside the rest of the AsyncAPI initiative.
        // The brand gradient (cyan #2dccfd → purple #a829e2 → magenta
        // #e50e99) carries the plugin's identity across both dark and
        // light themes without needing a theme-aware swap — same pattern
        // the MQTT / gRPC / Kafka plugin icons use.
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 155.51 155.51"><defs><linearGradient id="aa-g1" x1="133.74" y1="21.76" x2="24.84" y2="130.67" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#2dccfd"/><stop offset="1" stop-color="#ad20e2"/></linearGradient><linearGradient id="aa-g2" x1="6471.36" y1="6969.64" x2="6362.46" y2="7078.54" gradientTransform="translate(7103.38 -6337.62) rotate(90)" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#a829e2"/><stop offset=".05" stop-color="#a829e2" stop-opacity=".84"/><stop offset=".11" stop-color="#a829e2" stop-opacity=".67"/><stop offset=".18" stop-color="#a829e2" stop-opacity=".51"/><stop offset=".25" stop-color="#a829e2" stop-opacity=".38"/><stop offset=".33" stop-color="#a829e2" stop-opacity=".28"/><stop offset=".43" stop-color="#a829e2" stop-opacity=".2"/><stop offset=".54" stop-color="#a829e2" stop-opacity=".14"/><stop offset=".68" stop-color="#a829e2" stop-opacity=".11"/><stop offset="1" stop-color="#a829e2" stop-opacity=".1"/></linearGradient><linearGradient id="aa-g3" x1="13419.24" y1="632.02" x2="13310.33" y2="740.92" gradientTransform="translate(13441 765.76) rotate(180)" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#e50e99"/><stop offset="1" stop-color="#a829e2" stop-opacity=".1"/></linearGradient><linearGradient id="aa-g4" x1="138.74" y1="-7.39" x2="29.84" y2="101.52" gradientTransform="translate(-5 29.16)" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#21d4fd"/><stop offset=".03" stop-color="#27cdfc" stop-opacity=".96"/><stop offset=".23" stop-color="#4e9cf4" stop-opacity=".7"/><stop offset=".43" stop-color="#6e73ee" stop-opacity=".49"/><stop offset=".61" stop-color="#8753e9" stop-opacity=".32"/><stop offset=".77" stop-color="#993ce5" stop-opacity=".2"/><stop offset=".9" stop-color="#a42ee3" stop-opacity=".13"/><stop offset="1" stop-color="#a829e2" stop-opacity=".1"/></linearGradient></defs><rect fill="url(#aa-g1)" x="8.88" y="8.88" width="137.75" height="137.75" rx="32.58"/><rect fill="url(#aa-g2)" x="8.88" y="8.88" width="137.75" height="137.75" rx="32.58"/><rect fill="url(#aa-g3)" x="8.88" y="8.88" width="137.75" height="137.75" rx="32.58"/><rect fill="url(#aa-g4)" x="8.88" y="8.88" width="137.75" height="137.75" rx="32.58"/><g fill="#fff"><polygon points="53.35 63.21 49.25 68.86 81.27 92.1 81.49 92.26 85.59 86.61 53.57 63.37 53.35 63.21"/><polygon points="74.27 63.37 74.05 63.21 69.95 68.86 101.97 92.1 102.19 92.26 106.29 86.61 74.27 63.37"/><path d="M77.78,34.29c-17.35,0-31.48,10.79-31.48,24.06v.27h7v-.27c0-9.42,11-17.08,24.5-17.08s24.51,7.66,24.51,17.08v.27h7v-.27C109.27,45.08,95.14,34.29,77.78,34.29Z"/><path d="M102.23,97.16c0,9.42-11,17.08-24.51,17.08s-24.5-7.66-24.5-17.08v-.27h-7v.27c0,13.26,14.13,24,31.48,24s31.49-10.79,31.49-24v-.27h-7Z"/></g></svg>""";

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
        // host wires up at startup. Phase A3 shipped MQTT; Phase B added
        // Kafka + WebSocket — both wire plugins exist in the Küstenlogik
        // family (Kafka as sibling repo, WebSocket as first-party). The
        // resolvers degrade gracefully when the matching wire plugin is
        // not loaded (they emit a clear error message pointing at the
        // NuGet package to add). Phase C will follow with AMQP / NATS
        // when those wire plugins land.
        //
        // `mqtt5` is the same resolver registered under a second key:
        // the AsyncAPI spec carries an independent binding for MQTT 5.0
        // (sessionExpiryInterval, receiveMaximum, …) but the publish
        // contract on the Bowire side is identical — qos/retain stay
        // the only mapped fields, the rest ride along the metadata bag.
        //
        // `http` doesn't need a wire plugin lookup at all — the resolver
        // drives HttpClient directly, because AsyncAPI HTTP-bound
        // documents don't carry an OpenAPI overlay the REST plugin
        // would need to invoke against.
        _resolvers["mqtt"] = new MqttBindingResolver(registry);
        _resolvers["mqtt5"] = new MqttBindingResolver(registry, bindingId: "mqtt5");
        _resolvers["kafka"] = new KafkaBindingResolver(registry);
        _resolvers["ws"] = new WebSocketBindingResolver(registry);
        _resolvers["http"] = new HttpBindingResolver();
        // Phase C — AMQP. One plugin, two binding keys: `amqp` for the
        // 0.9.1 spec, `amqp1` for the 1.0 spec. The wire plugin's URL
        // scheme decides which actual wire (RabbitMQ.Client vs
        // AMQPNetLite) carries the publish.
        _resolvers["amqp"] = new AmqpBindingResolver(registry);
        _resolvers["amqp1"] = new AmqpBindingResolver(registry, bindingId: "amqp1");
        // Phase C — NATS. Dispatches `send` operations to the NATS
        // wire plugin's publish method; `queue` / `replyTo` from
        // bindings.nats land on the plugin's `queue_group` /
        // `reply_to` metadata.
        _resolvers["nats"] = new NatsBindingResolver(registry);
        // Phase C — SNS + SQS. AWS wire plugins still pending; the
        // resolvers register now so document discovery recognises the
        // binding keys and the invoke path surfaces a meaningful
        // "plugin not loaded" error rather than the generic "no
        // resolver registered" fall-through. Once the wire plugins
        // land the resolvers wire through to the registered Bowire
        // protocol with id "sns" / "sqs" automatically.
        _resolvers["sns"] = new SnsBindingResolver(registry);
        _resolvers["sqs"] = new SqsBindingResolver(registry);
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

        AsyncApiDocumentLoader.LoadResult load;
        try
        {
            load = await Loader.LoadAsync(serverUrl, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (HttpRequestException) { return []; }

        // Workbench-Schema-Capture: stash the verbatim AsyncAPI YAML so
        // a recording made against this URL can be auto-stamped with
        // SourceSchema by the recording-save endpoint. Keyed by both
        // the source URL (./asyncapi.yaml or http://…) AND by each
        // resolved server URL from the doc — the recording's
        // step.serverUrl is one of the latter (mqtt://broker:1883 etc),
        // not the AsyncAPI doc URL itself.
        StashSourceSchema(serverUrl, load);

        switch (load.Document)
        {
            case V3AsyncApiDocument v3:
                // Cache the parsed document + extracted bindings so
                // InvokeAsync can find the channel + operation + server
                // + per-operation binding fields (qos, retain, …)
                // without re-reading the YAML. Keyed by the exact
                // serverUrl the discovery dispatcher passed in so
                // multi-URL workbenches keep each document separate.
                _documents[serverUrl] = v3;
                _bindingsByUrl[serverUrl] =
                    AsyncApiBindingsExtractor.ExtractV3OperationBindings(load.NormalisedYaml);
                var v3Messages = AsyncApiBindingsExtractor.ExtractV3OperationMessages(load.NormalisedYaml);
                return MapV3Channels(v3, serverUrl, v3Messages);
            case V2AsyncApiDocument v2:
                // V2 cache lives alongside V3 so InvokeAsync's branch
                // can find the channel + operation + server without
                // re-parsing. Bindings extraction for V2 walks the
                // inline channel.publish/subscribe.bindings — kept in
                // a separate cache because V2's lookup shape
                // (channelKey + slot) differs from V3's (opKey).
                _v2Documents[serverUrl] = v2;
                _v2BindingsByUrl[serverUrl] =
                    AsyncApiBindingsExtractor.ExtractV2ChannelBindings(load.NormalisedYaml);
                var v2Messages =
                    AsyncApiBindingsExtractor.ExtractV2OperationMessages(load.NormalisedYaml);
                return MapV2Channels(v2, serverUrl, v2Messages);
            default:
                return [];
        }
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
    /// <summary>
    /// Convention for naming multi-message overload methods: operation
    /// key, this delimiter, then the message name. <c>::</c> chosen
    /// because it's uncommon in YAML keys (so unambiguous to split
    /// back) and reads in the UI like a C++/Rust qualifier — exactly
    /// the "one of these messages" semantics the AsyncAPI spec gives
    /// the `messages[]` array.
    /// </summary>
    private const string MessageOverloadSeparator = "::";

    private static List<BowireServiceInfo> MapV3Channels(
        V3AsyncApiDocument document, string sourceUrl,
        IReadOnlyDictionary<string, IReadOnlyList<string>> messagesByOp)
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
                    var declaredMessages = messagesByOp.TryGetValue(opKey, out var names) ? names : [];

                    if (declaredMessages.Count > 1)
                    {
                        // Multi-message operation — emit one method per
                        // declared message so the sidebar surfaces the
                        // choice the spec offers. Name pattern is
                        // `opKey::messageName`; InvokeAsync splits on
                        // the separator to recover the operation key.
                        foreach (var messageName in declaredMessages)
                        {
                            methods.Add(BuildV3Method(
                                $"{opKey}{MessageOverloadSeparator}{messageName}",
                                messageName, isSend, channel, channelKey, apiTitle));
                        }
                    }
                    else
                    {
                        methods.Add(BuildV3Method(
                            opKey, messageName: null, isSend, channel, channelKey, apiTitle));
                    }
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

    /// <summary>
    /// Build a single V3 method node. <paramref name="methodName"/> is
    /// either <c>opKey</c> (single-message operation) or
    /// <c>opKey::messageName</c> (multi-message overload). When
    /// <paramref name="messageName"/> is non-null the input-type's name
    /// reflects the declared message, otherwise a synthetic
    /// <c>{op}Request</c> name keeps backwards compat with how Phase A3
    /// emitted single-method ops.
    /// </summary>
    private static BowireMethodInfo BuildV3Method(
        string methodName, string? messageName, bool isSend,
        V3ChannelDefinition channel, string channelKey, string apiTitle)
    {
        var inputName = messageName ?? methodName + "Request";
        var outputName = messageName is null ? methodName + "Response" : methodName + "Response";
        return new BowireMethodInfo(
            Name: methodName,
            FullName: $"{apiTitle}.{channelKey}.{methodName}",
            ClientStreaming: isSend,
            ServerStreaming: !isSend,
            InputType: new BowireMessageInfo(inputName, inputName, []),
            OutputType: new BowireMessageInfo(outputName, outputName, []),
            MethodType: isSend ? "asyncapi-send" : "asyncapi-receive")
        {
            Summary = channel.Title ?? channel.Summary,
            Description = channel.Description,
            // HttpPath gets the channel address so the sidebar
            // can show `smarthome/light/measured` next to the
            // method name — re-using the existing REST slot is
            // a cheap way to render the channel-address hint
            // without a new model field.
            HttpPath = channel.Address
        };
    }

    /// <summary>
    /// V2 channel + operation walker. Same shape as the V3 walker — one
    /// service per channel, methods carry the send/receive direction on
    /// <c>ClientStreaming</c> / <c>ServerStreaming</c> — but V2 wires
    /// operations into the channel directly via the <c>publish</c> and
    /// <c>subscribe</c> properties instead of a separate top-level
    /// <c>operations</c> block. Polarity inverts as in V3: AsyncAPI tags
    /// from the application's perspective; Bowire is the test client.
    /// <list type="bullet">
    ///   <item><c>channel.subscribe</c> = application subscribes from the
    ///     broker → Bowire receives (server-streaming, "asyncapi-receive")</item>
    ///   <item><c>channel.publish</c> = application publishes to the
    ///     broker → Bowire sends (client-streaming, "asyncapi-send")</item>
    /// </list>
    /// Channels without operations still get a service entry so the
    /// sidebar surfaces the topology — matches V3.
    /// </summary>
    private static List<BowireServiceInfo> MapV2Channels(
        V2AsyncApiDocument document, string sourceUrl,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> messagesByChannelSlot)
    {
        var apiTitle = document.Info?.Title ?? "AsyncAPI";
        var apiVersion = document.Info?.Version;
        var services = new List<BowireServiceInfo>();
        if (document.Channels is null) return services;

        foreach (var (channelKey, channel) in document.Channels)
        {
            var methods = new List<BowireMethodInfo>();
            messagesByChannelSlot.TryGetValue(channelKey, out var perSlot);

            if (channel.Publish is not null)
            {
                AppendV2Methods(methods, channel.Publish, channel, channelKey, apiTitle,
                    isSend: true, oneOfMessages: TryGetSlotMessages(perSlot, "publish"));
            }
            if (channel.Subscribe is not null)
            {
                AppendV2Methods(methods, channel.Subscribe, channel, channelKey, apiTitle,
                    isSend: false, oneOfMessages: TryGetSlotMessages(perSlot, "subscribe"));
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
    /// Pull the per-slot multi-message list for a V2 channel out of
    /// the extractor's nested map. Returns an empty list when the
    /// channel + slot pair doesn't declare a <c>message.oneOf[]</c>,
    /// which signals to <see cref="AppendV2Methods"/> that it should
    /// fall back to the single-method shape (using <c>op.Message</c>
    /// from the typed model).
    /// </summary>
    private static IReadOnlyList<string> TryGetSlotMessages(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? perSlot, string slotKey)
    {
        if (perSlot is null) return [];
        return perSlot.TryGetValue(slotKey, out var names) ? names : [];
    }

    /// <summary>
    /// V2 method emission with per-message overload support. Mirrors
    /// the V3 multi-message walker (<see cref="MapV3Channels"/>): when
    /// the slot declares more than one message in
    /// <c>message.oneOf[]</c> we emit one method per declared message
    /// named <c>opKey::messageName</c> so the sidebar surfaces the
    /// choice the spec offers. Single-message slots (the dominant
    /// shape in V2 documents in the wild) stay on the original
    /// single-method emission path.
    /// </summary>
    private static void AppendV2Methods(
        List<BowireMethodInfo> methods, V2OperationDefinition op, V2ChannelDefinition channel,
        string channelKey, string apiTitle, bool isSend, IReadOnlyList<string> oneOfMessages)
    {
        if (oneOfMessages.Count > 1)
        {
            // Multi-message slot — emit one method per declared
            // message. Operation name comes from operationId (when
            // set) or the fixed "publish"/"subscribe" label, then we
            // qualify it with the message-name segment via the same
            // `opKey::messageName` separator V3 uses.
            var opBase = !string.IsNullOrWhiteSpace(op.OperationId)
                ? op.OperationId!
                : (isSend ? "publish" : "subscribe");
            foreach (var messageName in oneOfMessages)
            {
                methods.Add(BuildV2Method(
                    op, channel, channelKey, apiTitle, isSend,
                    overrideName: $"{opBase}{MessageOverloadSeparator}{messageName}",
                    messageName: messageName));
            }
        }
        else
        {
            methods.Add(BuildV2Method(op, channel, channelKey, apiTitle, isSend,
                overrideName: null, messageName: null));
        }
    }

    /// <summary>
    /// Build the single method node for one V2 publish/subscribe slot.
    /// Method name comes from <c>operationId</c> when set (carrying the
    /// author's identifier into the sidebar), else falls back to the
    /// fixed "publish" / "subscribe" label so the topology stays
    /// readable on documents that don't bother naming operations.
    /// </summary>
    private static BowireMethodInfo BuildV2Method(
        V2OperationDefinition op, V2ChannelDefinition channel,
        string channelKey, string apiTitle, bool isSend,
        string? overrideName, string? messageName)
    {
        var opName = overrideName ??
            (!string.IsNullOrWhiteSpace(op.OperationId)
                ? op.OperationId!
                : (isSend ? "publish" : "subscribe"));
        // Per-message overloads adopt the declared message name as
        // the input-type name (mirrors V3's BuildV3Method shape). The
        // single-method path keeps the synthetic `{op}Request` name
        // for backwards compatibility with the V2 sample tests.
        var inputName = messageName ?? opName + "Request";
        var outputName = messageName ?? opName + "Response";
        return new BowireMethodInfo(
            Name: opName,
            FullName: $"{apiTitle}.{channelKey}.{opName}",
            ClientStreaming: isSend,
            ServerStreaming: !isSend,
            InputType: new BowireMessageInfo(inputName, inputName, []),
            OutputType: new BowireMessageInfo(outputName, outputName, []),
            MethodType: isSend ? "asyncapi-send" : "asyncapi-receive")
        {
            Summary = op.Summary ?? channel.Description,
            Description = op.Description ?? channel.Description,
            // V2 channels don't carry an `address:` field — the channel
            // key IS the address. Surface it on HttpPath so the sidebar
            // shows the topic / subject in the same slot V3 docs use.
            HttpPath = channelKey
        };
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // service = channel key (e.g. "lightingMeasured")
        // method  = operation key (e.g. "sendTurnOnOff")
        //
        // V2 + V3 documents diverge here: V3 has a separate top-level
        // operations block keyed by opKey, V2 inlines publish/subscribe
        // into the channel itself. We try V3 first (the dominant
        // spec version going forward), fall back to V2 if the URL was
        // cached as such, and only then report "not discovered".
        if (_v2Documents.TryGetValue(serverUrl, out var v2Document))
        {
            return await InvokeV2Async(
                serverUrl, v2Document, service, method, jsonMessages, metadata, ct)
                .ConfigureAwait(false);
        }

        if (!_documents.TryGetValue(serverUrl, out var document))
        {
            return Error("AsyncAPI document has not been discovered yet. " +
                $"Call discover with serverUrl '{serverUrl}' first.");
        }

        // Multi-message operations emit methods named `opKey::messageName`
        // (see BuildV3Method). Strip the suffix so the operation lookup
        // resolves to the single declared operation regardless of which
        // overload the caller invoked. The message-name itself is kept
        // around for potential message-aware binding fields / payload
        // schema selection in a later phase.
        var (opLookupKey, _) = SplitOverloadName(method);
        if (document.Operations is null || !document.Operations.TryGetValue(opLookupKey, out var operation))
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
                "Shipped resolvers: MQTT (mqtt / mqtt5), Kafka, WebSocket (ws), HTTP, " +
                "AMQP (amqp / amqp1), NATS, SNS, SQS. SNS / SQS resolvers degrade gracefully " +
                "until their wire plugins ship (see ROADMAP: AsyncAPI as a discovery source).");
        }

        var channelKey = ResolveChannelRef(operation.Channel?.Reference);
        if (channelKey is null || !document.Channels!.TryGetValue(channelKey, out var channel))
        {
            return Error($"Operation '{method}' references an unknown channel.");
        }

        var brokerUrl = $"{server.Protocol}://{server.Host}{server.PathName ?? string.Empty}";

        // Pick the binding-specific field map (qos, retain, topic, …)
        // that the bindings extractor pulled out at discovery time.
        // Lookup: source-URL → opKey → bindingId. Empty map = the
        // document declared no fields for this operation/binding
        // combo, resolvers will fall back to their defaults.
        IReadOnlyDictionary<string, string> bindingFields =
            _bindingsByUrl.TryGetValue(serverUrl, out var docBindings)
            && docBindings.TryGetValue(opLookupKey, out var opBindings)
            && opBindings.TryGetValue(server.Protocol, out var fields)
                ? fields
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var ctx = new AsyncApiChannelContext(
            ServerUrl: brokerUrl,
            ChannelAddress: channel.Address ?? channelKey,
            OperationAction: operation.Action == V3OperationAction.Send ? "send" : "receive",
            BindingFields: bindingFields);

        return await resolver.InvokeAsync(ctx, jsonMessages, metadata, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// V2 invocation path. V2's <c>service</c> is the channel key
    /// (which IS the topic in V2 — no separate <c>address:</c>),
    /// <c>method</c> is the <c>operationId</c> the author set on
    /// <c>channel.publish</c> or <c>channel.subscribe</c> (or the
    /// "publish"/"subscribe" fallback when not set). Server selection +
    /// resolver routing mirror the V3 path. Bindings extraction for V2
    /// is a follow-up — for now the resolver gets an empty
    /// <see cref="AsyncApiChannelContext.BindingFields"/> and falls
    /// back to its protocol-level defaults.
    /// </summary>
    private async Task<InvokeResult> InvokeV2Async(
        string serverUrl, V2AsyncApiDocument document, string service, string method,
        List<string> jsonMessages, Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        if (document.Channels is null || !document.Channels.TryGetValue(service, out var channel))
        {
            return Error($"Channel '{service}' not found in AsyncAPI v2 document.");
        }

        // V2 inlines operations as channel.publish / channel.subscribe.
        // Match the request's `method` against the operationId on each
        // slot (or the fallback "publish"/"subscribe" label MapV2Channels
        // synthesises when no operationId is set) so the call lands on
        // the right direction. Strip the per-message overload suffix
        // (`opKey::messageName`) the same way the V3 path does so a
        // multi-message slot's individual overload methods all resolve
        // back to the single underlying operationId.
        var (opLookupKey, _) = SplitOverloadName(method);
        var isSend = MatchesV2Operation(channel.Publish, opLookupKey, fallback: "publish");
        var isReceive = MatchesV2Operation(channel.Subscribe, opLookupKey, fallback: "subscribe");
        if (!isSend && !isReceive)
        {
            return Error($"Operation '{method}' not found on V2 channel '{service}'.");
        }

        if (document.Servers is null || document.Servers.Count == 0)
        {
            return Error("AsyncAPI v2 document declares no servers; cannot route invocation.");
        }

        var (serverName, server) = document.Servers.First();
        if (string.IsNullOrWhiteSpace(server.Protocol))
        {
            return Error($"V2 server '{serverName}' has no protocol declared.");
        }

        if (!_resolvers.TryGetValue(server.Protocol, out var resolver))
        {
            return Error(
                $"No AsyncAPI binding resolver registered for protocol '{server.Protocol}'. " +
                "Phase A ships the MQTT resolver only.");
        }

        var brokerUrl = string.IsNullOrWhiteSpace(server.Url?.OriginalString)
            ? $"{server.Protocol}://"
            : server.Url!.OriginalString;

        // V2 bindings: lookup by channel-key + slot ("publish"/"subscribe").
        // The slot follows the direction we just determined (send → publish,
        // receive → subscribe), then the binding-id matches the server
        // protocol exactly like the V3 path. Source URL came in from the
        // outer InvokeAsync so we go straight to the cache.
        var slotKey = isSend ? "publish" : "subscribe";
        IReadOnlyDictionary<string, string> bindingFields =
            _v2BindingsByUrl.TryGetValue(serverUrl, out var docBindings)
            && docBindings.TryGetValue(service, out var channelBindings)
            && channelBindings.TryGetValue(slotKey, out var slotBindings)
            && slotBindings.TryGetValue(server.Protocol, out var fields)
                ? fields
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var ctx = new AsyncApiChannelContext(
            ServerUrl: brokerUrl,
            ChannelAddress: service,
            OperationAction: isSend ? "send" : "receive",
            BindingFields: bindingFields);

        return await resolver.InvokeAsync(ctx, jsonMessages, metadata, ct).ConfigureAwait(false);
    }

    private static bool MatchesV2Operation(
        V2OperationDefinition? slot, string requestedMethod, string fallback)
    {
        if (slot is null) return false;
        var opName = !string.IsNullOrWhiteSpace(slot.OperationId) ? slot.OperationId! : fallback;
        return string.Equals(opName, requestedMethod, StringComparison.Ordinal);
    }

    /// <summary>
    /// Split <paramref name="methodName"/> into <c>(operationKey, messageName?)</c>
    /// using the <see cref="MessageOverloadSeparator"/> as the divider.
    /// Single-message methods (Phase A3 shape) split into
    /// <c>(opKey, null)</c>. Multi-message overloads return the
    /// message name as the second tuple element so future routing
    /// can do message-aware binding lookup or payload-schema picks.
    /// </summary>
    private static (string OperationKey, string? MessageName) SplitOverloadName(string methodName)
    {
        var idx = methodName.IndexOf(MessageOverloadSeparator, StringComparison.Ordinal);
        if (idx < 0) return (methodName, null);
        return (methodName[..idx], methodName[(idx + MessageOverloadSeparator.Length)..]);
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

    /// <summary>
    /// Push the loaded AsyncAPI YAML into the cross-plugin
    /// <see cref="SourceSchemaCache"/> under every key a downstream
    /// recording might look it up by:
    /// <list type="bullet">
    ///   <item>The <paramref name="sourceUrl"/> itself (the
    ///     <c>./asyncapi.yaml</c> / <c>http://…/asyncapi.yaml</c>
    ///     the user pointed Bowire at).</item>
    ///   <item>Each resolved server URL from <c>servers[]</c> in the
    ///     document — that's what recording-step.ServerUrl carries on
    ///     the wire plugins (mqtt://…, nats://…, kafka://…), so a
    ///     recording made through this discovery hits one of these
    ///     keys at save time.</item>
    /// </list>
    /// </summary>
    private static void StashSourceSchema(string sourceUrl, AsyncApiDocumentLoader.LoadResult load)
    {
        if (string.IsNullOrEmpty(load.NormalisedYaml)) return;
        var schema = new RecordingSourceSchema(
            Format: "asyncapi-3.0",
            Content: load.NormalisedYaml,
            SourceUrl: sourceUrl);

        SourceSchemaCache.Set(sourceUrl, schema);

        foreach (var resolved in EnumerateResolvedServerUrls(load.Document))
        {
            SourceSchemaCache.Set(resolved, schema);
        }
    }

    private static IEnumerable<string> EnumerateResolvedServerUrls(IAsyncApiDocument doc)
    {
        switch (doc)
        {
            case V3AsyncApiDocument v3 when v3.Servers is not null:
                foreach (var (_, server) in v3.Servers)
                {
                    if (server is null) continue;
                    var url = ResolvedV3ServerUrl(server);
                    if (!string.IsNullOrEmpty(url)) yield return url;
                }
                break;
            case V2AsyncApiDocument v2 when v2.Servers is not null:
                foreach (var (_, server) in v2.Servers)
                {
                    if (server is null) continue;
                    var url = ResolvedV2ServerUrl(server);
                    if (!string.IsNullOrEmpty(url)) yield return url;
                }
                break;
        }
    }

    private static string ResolvedV3ServerUrl(V3ServerDefinition server)
    {
        if (string.IsNullOrEmpty(server.Protocol) || string.IsNullOrEmpty(server.Host))
            return string.Empty;
        return $"{server.Protocol}://{server.Host}{server.PathName ?? string.Empty}";
    }

    private static string ResolvedV2ServerUrl(V2ServerDefinition server)
    {
        if (string.IsNullOrEmpty(server.Protocol) || string.IsNullOrEmpty(server.Url?.ToString()))
            return string.Empty;
        return server.Url.ToString();
    }
}
