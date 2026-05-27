// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Pulsar;

/// <summary>
/// Bowire protocol plugin for Apache Pulsar brokers. Built on the
/// Apache-maintained <c>DotPulsar</c> client.
/// </summary>
/// <remarks>
/// <para>
/// Discovery hits the HTTP admin API
/// (<c>/admin/v2/persistent/&lt;tenant&gt;/&lt;namespace&gt;</c>) to
/// enumerate topics. The broker URL itself (<c>pulsar://</c>) is what
/// the binary protocol connects to for produce/subscribe.
/// </para>
/// <para>
/// Produce is unary — one message in, one ack back. Subscribe is
/// server-streaming and tails the topic from the latest message (the
/// workbench is for inspection, not durable consumption; durable
/// state would leak across browser sessions).
/// </para>
/// </remarks>
// CA1001: _http belongs to the protocol registry's lifetime.
#pragma warning disable CA1001
public sealed class BowirePulsarProtocol : IBowireProtocol
#pragma warning restore CA1001
{
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string Name => "Pulsar";
    public string Id => "pulsar";

    // Apache Pulsar logo — stylised lighthouse beam. Kept monochrome so
    // it picks up the theme's currentColor like the rest of the
    // messaging-family icons.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" width="16" height="16" aria-hidden="true"><path d="M12 3v6"/><circle cx="12" cy="11" r="2.2"/><path d="M5 19l4-6"/><path d="M19 19l-4-6"/><path d="M3 21h18"/></svg>""";

    public IReadOnlyList<BowirePluginSetting> Settings =>
    [
        new("namespaces", "Namespaces to scan",
            "Comma-separated tenant/namespace pairs (default: public/default)",
            "string", "public/default"),
        new("subscribeFromLatest", "Subscribe from latest",
            "Stream only messages produced after subscribe; off = replay backlog",
            "bool", true),
    ];

    public void Initialize(IServiceProvider? serviceProvider)
    {
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromSeconds(15));
    }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var endpoints = PulsarConnectionHelper.Resolve(serverUrl);
        if (endpoints is null) return [];

        var namespaces = ParseNamespaces(GetSetting("namespaces", "public/default"));
        return await PulsarDiscovery.ListTopicsAsync(_http, endpoints, namespaces, serverUrl, ct)
            .ConfigureAwait(false);
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var endpoints = PulsarConnectionHelper.Resolve(serverUrl);
        if (endpoints is null)
            return new InvokeResult(null, 0, "Invalid Pulsar server URL", new());

        var route = ParseRoute(method);
        if (route is null || route.Op != "produce")
        {
            return new InvokeResult(null, 0,
                "Unknown Pulsar route '" + method + "' — expected pulsar/topic/<name>/produce", new());
        }

        var topic = ResolveTopic(route.Topic, metadata);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var client = PulsarClient.Builder()
                .ServiceUrl(new Uri(endpoints.BrokerUrl))
                .Build();

            await using var producer = client.NewProducer(Schema.String)
                .Topic(topic)
                .Create();

            var payload = jsonMessages.FirstOrDefault() ?? "";
            var msgId = await producer.Send(payload, ct).ConfigureAwait(false);
            sw.Stop();

            return new InvokeResult(
                Response: JsonSerializer.Serialize(new
                {
                    topic,
                    message_id = msgId.ToString(),
                    bytes = Encoding.UTF8.GetByteCount(payload),
                }),
                DurationMs: sw.ElapsedMilliseconds,
                Status: "OK",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["topic"] = topic,
                    ["message_id"] = msgId.ToString() ?? "",
                });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new InvokeResult(null, sw.ElapsedMilliseconds, ex.Message, new());
        }
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var endpoints = PulsarConnectionHelper.Resolve(serverUrl);
        if (endpoints is null) yield break;

        var route = ParseRoute(method);
        if (route is null || route.Op != "subscribe") yield break;

        var topic = ResolveTopic(route.Topic, metadata);
        var subscriptionName = metadata?.GetValueOrDefault("subscription_name")
            ?? "bowire-" + Guid.NewGuid().ToString("N")[..8];
        // Per-invoke override wins over the plugin-level default —
        // workbench users can subscribe from Earliest on a specific
        // tail without flipping the global setting.
        var fromLatestRaw = metadata?.GetValueOrDefault("from_latest")
            ?? GetSetting("subscribeFromLatest", "true");
        var fromLatest = string.Equals(fromLatestRaw, "true", StringComparison.OrdinalIgnoreCase);

        await using var client = PulsarClient.Builder()
            .ServiceUrl(new Uri(endpoints.BrokerUrl))
            .Build();

        await using var consumer = client.NewConsumer(Schema.String)
            .SubscriptionName(subscriptionName)
            // SubscriptionType.Exclusive matches the workbench's
            // one-viewer-at-a-time model — Shared would spread messages
            // across the workbench and the user's real apps.
            .SubscriptionType(SubscriptionType.Exclusive)
            .InitialPosition(fromLatest
                ? SubscriptionInitialPosition.Latest
                : SubscriptionInitialPosition.Earliest)
            .Topic(topic)
            .Create();

        await foreach (var msg in consumer.Messages(ct).ConfigureAwait(false))
        {
            yield return JsonSerializer.Serialize(new
            {
                topic,
                message_id = msg.MessageId.ToString(),
                payload = msg.Value(),
                publish_time = msg.PublishTime,
            });
            // Ack so the broker can advance the subscription cursor
            // — without acks the workbench would replay the same
            // messages on every reconnect.
            await consumer.Acknowledge(msg, ct).ConfigureAwait(false);
        }
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    // ----- helpers ------------------------------------------------------

    /// <summary>
    /// Comma-separated namespaces string → list. Drops empty entries
    /// so trailing commas / accidental whitespace don't crash discovery.
    /// </summary>
    internal static List<string> ParseNamespaces(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ["public/default"];
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// FullName comes through as <c>pulsar/topic/&lt;full-name&gt;/&lt;op&gt;</c>.
    /// Returns null for hand-typed method names that don't match this
    /// shape so the invoke can surface a helpful error.
    /// </summary>
    internal static PulsarRoute? ParseRoute(string method)
    {
        if (string.IsNullOrEmpty(method)) return null;
        const string prefix = "pulsar/topic/";
        if (!method.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rest = method[prefix.Length..];
        var lastSlash = rest.LastIndexOf('/');
        if (lastSlash <= 0 || lastSlash >= rest.Length - 1) return null;

        var topic = rest[..lastSlash];
        var op = rest[(lastSlash + 1)..];
        return new PulsarRoute(topic, op);
    }

    /// <summary>
    /// Resolve the actual topic name to talk to. Discovery routes carry
    /// the topic via the FullName; the metadata <c>topic</c> override
    /// lets users invoke against topics that weren't in the scan (e.g.
    /// topics in a different tenant).
    /// </summary>
    private static string ResolveTopic(string discoveryTopic, Dictionary<string, string>? metadata)
    {
        var raw = metadata?.GetValueOrDefault("topic");
        if (!string.IsNullOrWhiteSpace(raw))
            return PulsarConnectionHelper.NormaliseTopicName(raw!);
        return PulsarConnectionHelper.NormaliseTopicName(discoveryTopic);
    }

    private string GetSetting(string key, string fallback)
    {
        var s = Settings.FirstOrDefault(x => x.Key == key);
        return s?.DefaultValue?.ToString() ?? fallback;
    }

    internal sealed record PulsarRoute(string Topic, string Op);
}
