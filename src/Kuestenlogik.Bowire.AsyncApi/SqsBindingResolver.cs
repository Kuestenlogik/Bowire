// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// SQS binding for AsyncAPI documents. Same shape as the Kafka / AMQP
/// / NATS / SNS resolvers: looks up the SQS wire plugin
/// (<c>Kuestenlogik.Bowire.Protocol.Sqs</c>, id <c>"sqs"</c>) at
/// invocation time via the shared <see cref="BowireProtocolRegistry"/>
/// and forwards the publish call there. No hard csproj reference on
/// the wire plugin — the registry only finds it when the host has the
/// plugin loaded (CLI bundle or embedded host's NuGet set).
///
/// The wire plugin itself is still pending — until it lands, the
/// resolver degrades gracefully and returns a clear "no plugin
/// loaded" error pointing at the future NuGet package. Keeping the
/// resolver in place now means the AsyncAPI loader recognises the
/// binding key at discovery time and surfaces a meaningful invoke
/// error rather than the generic "no resolver registered" fall-through.
///
/// AsyncAPI SQS spec (per asyncapi/bindings/sqs) field surface:
/// <list type="bullet">
///   <item><b>operationBindings.sqs</b>: <c>queues</c> (target queue
///     list — name, fifoQueue, deduplicationScope, deliveryDelay,
///     visibilityTimeout, receiveMessageWaitTime, messageRetentionPeriod,
///     redrivePolicy.deadLetterQueue, redrivePolicy.maxReceiveCount).</item>
///   <item><b>messageBindings.sqs</b>: per-message FIFO control —
///     <c>messageGroupId</c>, <c>messageDeduplicationId</c>.</item>
///   <item><b>channelBindings.sqs</b>: queue object at channel scope,
///     same shape as the operation-level <c>queues</c> entries.</item>
/// </list>
/// Every scalar field that lands in the binding block gets forwarded
/// verbatim onto the metadata bag the SQS plugin reads — the plugin
/// acts on the keys it knows (<c>queue.name</c>, <c>messageGroupId</c>,
/// <c>messageDeduplicationId</c>, <c>deadLetterQueue</c>); unknown
/// entries ride along for diagnostics + future use.
/// </summary>
internal sealed class SqsBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    public SqsBindingResolver(BowireProtocolRegistry registry)
    {
        _registry = registry;
    }

    public string BindingId => "sqs";

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        // Same deferral as Kafka / AMQP / NATS / SNS: method materialisation
        // happens in BowireAsyncApiProtocol.MapV3Channels / MapV2Channels.
        // The resolver is invocation-side only until per-binding method
        // metadata (queue ARN, messageGroupId, deadLetterQueue) needs
        // to surface on the method itself.
        throw new NotImplementedException(
            "SqsBindingResolver.BuildMethod is reserved for a future " +
            "phase where per-binding method metadata (queue.name, " +
            "messageGroupId, deadLetterQueue) needs to surface on the " +
            "method. Current phase builds methods directly from the " +
            "V3/V2 operation block.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var sqs = _registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, "sqs", StringComparison.OrdinalIgnoreCase));

        if (sqs is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] =
                        "AsyncAPI document declares an SQS binding, but no SQS " +
                        "plugin is loaded. CLI: will ship pre-bundled with the " +
                        "Kuestenlogik.Bowire.Tool once the SQS wire plugin lands. " +
                        "Embedded: add the Kuestenlogik.Bowire.Protocol.Sqs NuGet " +
                        "package to your host when available."
                });
        }

        // Doc bindings populate the metadata bag the SQS plugin reads;
        // caller-supplied metadata wins so a UI override (one-off
        // message-group / dedup id for a single publish) isn't lost
        // to the doc defaults.
        var mergedMetadata = MergeSqsBindingFields(channel.BindingFields, metadata);

        // The SQS plugin's invoke contract (parallels the other AWS
        // resolvers): the channel address carries the queue name or
        // ARN, the plugin maps it onto a SendMessage call via AWS SDK.
        //   serverUrl = AWS endpoint URL (sqs://<region> or
        //               explicit https://sqs.<region>.amazonaws.com)
        //   service   = queue name / ARN ← channel.address from AsyncAPI
        //   method    = "send" (the plugin's literal verb for unary publish)
        //   metadata  = messageGroupId / messageDeduplicationId / deadLetterQueue
        return await sqs.InvokeAsync(
            serverUrl: channel.ServerUrl,
            service: channel.ChannelAddress,
            method: "send",
            jsonMessages: jsonMessages,
            showInternalServices: false,
            metadata: mergedMetadata,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translate AsyncAPI's <c>bindings.sqs.*</c> field map into the
    /// metadata keys the Bowire SQS plugin reads. Every binding scalar
    /// is forwarded verbatim — the plugin acts on the keys it knows
    /// (FIFO controls + dead-letter routing); unknown entries ride
    /// along for diagnostics + future use (same policy as the Kafka
    /// resolver).
    /// </summary>
    private static Dictionary<string, string> MergeSqsBindingFields(
        IReadOnlyDictionary<string, string>? bindingFields,
        IReadOnlyDictionary<string, string>? callerMetadata)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bindingFields is not null)
        {
            foreach (var (k, v) in bindingFields) merged[k] = v;
        }
        if (callerMetadata is not null)
        {
            foreach (var (k, v) in callerMetadata) merged[k] = v;
        }
        return merged;
    }
}
