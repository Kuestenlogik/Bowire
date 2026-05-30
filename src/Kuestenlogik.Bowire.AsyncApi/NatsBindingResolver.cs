// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// NATS binding for AsyncAPI documents. Looks up the NATS wire plugin
/// (<c>Kuestenlogik.Bowire.Protocol.Nats</c>, id <c>"nats"</c>) at
/// invocation time via the shared <see cref="BowireProtocolRegistry"/>
/// and forwards the publish call there. Same shape as the Kafka and
/// AMQP resolvers: no hard csproj reference on the NATS plugin — the
/// registry only finds it when the host has the plugin loaded (CLI
/// bundle or embedded host's NuGet set).
///
/// Phase C scope: handles <c>send</c> operations as a unary publish
/// through <see cref="IBowireProtocol.InvokeAsync"/> (which the NATS
/// plugin maps onto a Core NATS publish). <c>receive</c> operations
/// (server-stream subscribe) need channel-streaming and land alongside
/// the channel-surface work in a later phase, matching how the Kafka
/// resolver defers consume.
///
/// Binding fields the AsyncAPI NATS spec defines at the operation
/// level — <c>queue</c> (queue-group name) and <c>replyTo</c> (subject
/// for the request/reply pair) — are pulled out by
/// <see cref="AsyncApiBindingsExtractor"/> and translated onto the
/// metadata bag the NATS plugin reads:
/// <list type="bullet">
///   <item><c>queue</c> → <c>queue_group</c> (the plugin's existing
///     subscribe-side hint).</item>
///   <item><c>replyTo</c> → <c>reply_to</c> (forwarded verbatim so a
///     future plugin release can pick it up without touching this
///     resolver).</item>
/// </list>
/// Other binding fields are passed through unchanged for diagnostics.
/// </summary>
internal sealed class NatsBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    public NatsBindingResolver(BowireProtocolRegistry registry)
    {
        _registry = registry;
    }

    public string BindingId => "nats";

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        // Same deferral as Kafka / AMQP: method materialisation
        // happens in BowireAsyncApiProtocol.MapV3Channels / V2.
        // Reserved for when per-binding method metadata (queue,
        // replyTo) needs to surface on the method itself.
        throw new NotImplementedException(
            "NatsBindingResolver.BuildMethod is reserved for a future " +
            "phase where per-binding method metadata (queue, replyTo) " +
            "needs to surface on the method. Current phase builds " +
            "methods directly from the V3/V2 operation block.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var nats = _registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, "nats", StringComparison.OrdinalIgnoreCase));

        if (nats is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] =
                        "AsyncAPI document declares a NATS binding, but no NATS " +
                        "plugin is loaded. CLI: ships pre-bundled with the " +
                        "Kuestenlogik.Bowire.Tool. Embedded: add the " +
                        "Kuestenlogik.Bowire.Protocol.Nats NuGet package to your host."
                });
        }

        // Doc bindings populate the metadata bag the NATS plugin
        // reads; caller-supplied metadata wins so a UI override (one-
        // off queue / reply-subject for a single publish) doesn't get
        // overwritten by the doc defaults.
        var mergedMetadata = MergeNatsBindingFields(channel.BindingFields, metadata);

        // The NATS plugin's invoke contract (Phase 1 subject form):
        //   serverUrl = NATS URL (nats://host:4222)
        //   service   = subject  ← channel.address from AsyncAPI
        //   method    = "publish" (the plugin's literal verb for unary pub)
        //   metadata  = queue_group / reply_to + any pass-through bindings
        return await nats.InvokeAsync(
            serverUrl: channel.ServerUrl,
            service: channel.ChannelAddress,
            method: "publish",
            jsonMessages: jsonMessages,
            showInternalServices: false,
            metadata: mergedMetadata,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translate AsyncAPI's <c>bindings.nats.*</c> field map into the
    /// metadata keys the Bowire NATS plugin reads.
    /// <para>
    /// Currently consumed by the plugin: <c>queue_group</c> (subscribe
    /// hint). The <c>reply_to</c> hint rides along verbatim so a
    /// future request/reply-aware plugin release can pick it up
    /// without touching this resolver. Unrecognised keys also pass
    /// through unchanged for diagnostics.
    /// </para>
    /// </summary>
    private static Dictionary<string, string> MergeNatsBindingFields(
        IReadOnlyDictionary<string, string>? bindingFields,
        IReadOnlyDictionary<string, string>? callerMetadata)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bindingFields is not null)
        {
            foreach (var (k, v) in bindingFields)
            {
                // Rename the two spec-named keys onto the plugin's
                // existing metadata keys; everything else passes
                // through unchanged.
                if (string.Equals(k, "queue", StringComparison.OrdinalIgnoreCase))
                    merged["queue_group"] = v;
                else if (string.Equals(k, "replyTo", StringComparison.OrdinalIgnoreCase))
                    merged["reply_to"] = v;
                else
                    merged[k] = v;
            }
        }
        if (callerMetadata is not null)
        {
            foreach (var (k, v) in callerMetadata) merged[k] = v;
        }
        return merged;
    }
}
