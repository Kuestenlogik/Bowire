// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// SNS binding for AsyncAPI documents. Same shape as the Kafka / AMQP
/// / NATS resolvers: looks up the SNS wire plugin
/// (<c>Kuestenlogik.Bowire.Protocol.Sns</c>, id <c>"sns"</c>) at
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
/// AsyncAPI SNS spec (per asyncapi/bindings/sns) field surface:
/// <list type="bullet">
///   <item><b>operationBindings.sns</b>: <c>topic.name</c>,
///     <c>topic.ordered</c>, <c>topic.fifoTopic</c>,
///     <c>topic.deduplicationScope</c>, <c>topic.fifoThroughputLimit</c>,
///     <c>consumers</c> (subscription target list — see SQS resolver
///     for the receive side), <c>deliveryPolicy</c>.</item>
///   <item><b>messageBindings.sns</b>: <c>subject</c>,
///     <c>messageAttributes</c> (typed key/value), <c>filterPolicy</c>
///     / <c>filterPolicyScope</c>.</item>
///   <item><b>channelBindings.sns</b>: same <c>topic</c> object as
///     operation-level, so authors can describe the SNS topic at
///     either scope.</item>
/// </list>
/// Every scalar field that lands in the binding block gets forwarded
/// verbatim onto the metadata bag the SNS plugin reads — the plugin
/// acts on the keys it knows (<c>topic.name</c>, <c>subject</c>,
/// <c>messageAttributes</c>, <c>filterPolicy</c>), unknown entries
/// ride along for diagnostics + future use (matches Kafka / AMQP
/// policy).
/// </summary>
internal sealed class SnsBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    public SnsBindingResolver(BowireProtocolRegistry registry)
    {
        _registry = registry;
    }

    public string BindingId => "sns";

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        // Same deferral as Kafka / AMQP / NATS: method materialisation
        // happens in BowireAsyncApiProtocol.MapV3Channels / MapV2Channels.
        // The resolver is invocation-side only until per-binding method
        // metadata (topic ARN, subject, filterPolicy) needs to surface
        // on the method itself.
        throw new NotImplementedException(
            "SnsBindingResolver.BuildMethod is reserved for a future " +
            "phase where per-binding method metadata (topic.name, subject, " +
            "filterPolicy) needs to surface on the method. Current phase " +
            "builds methods directly from the V3/V2 operation block.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var sns = _registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, "sns", StringComparison.OrdinalIgnoreCase));

        if (sns is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] =
                        "AsyncAPI document declares an SNS binding, but no SNS " +
                        "plugin is loaded. CLI: will ship pre-bundled with the " +
                        "Kuestenlogik.Bowire.Tool once the SNS wire plugin lands. " +
                        "Embedded: add the Kuestenlogik.Bowire.Protocol.Sns NuGet " +
                        "package to your host when available."
                });
        }

        // Doc bindings populate the metadata bag the SNS plugin reads;
        // caller-supplied metadata wins so a UI override (one-off
        // subject / filter for a single publish) isn't lost to the doc
        // defaults.
        var mergedMetadata = MergeSnsBindingFields(channel.BindingFields, metadata);

        // The SNS plugin's invoke contract (parallels the other AWS
        // resolvers): the channel address carries the topic name or
        // ARN, the plugin maps it onto a Publish call via AWS SDK.
        //   serverUrl = AWS endpoint URL (sns://<region> or
        //               explicit https://sns.<region>.amazonaws.com)
        //   service   = topic name / ARN ← channel.address from AsyncAPI
        //   method    = "publish" (the plugin's literal verb for unary publish)
        //   metadata  = subject / messageAttributes / filterPolicy
        return await sns.InvokeAsync(
            serverUrl: channel.ServerUrl,
            service: channel.ChannelAddress,
            method: "publish",
            jsonMessages: jsonMessages,
            showInternalServices: false,
            metadata: mergedMetadata,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translate AsyncAPI's <c>bindings.sns.*</c> field map into the
    /// metadata keys the Bowire SNS plugin reads. Every binding scalar
    /// is forwarded verbatim — the plugin acts on the keys it knows;
    /// unknown entries ride along for diagnostics + future use (same
    /// policy as the Kafka resolver). Single translation applied
    /// here: the AsyncAPI <c>topic.name</c> nested-scalar key is left
    /// unchanged because <see cref="AsyncApiBindingsExtractor"/>
    /// already drops non-scalar children. Authors who want flat
    /// access write the topic name at the operation level via
    /// <c>name: orders-topic</c>, which lands here directly.
    /// </summary>
    private static Dictionary<string, string> MergeSnsBindingFields(
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
