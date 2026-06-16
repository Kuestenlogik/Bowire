// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Pulsar;

/// <summary>
/// Walks the Pulsar HTTP admin API to enumerate topics in the default
/// <c>public/default</c> namespace (and any extra namespaces the user
/// configured via the <c>namespaces</c> setting). Each topic becomes
/// a Bowire service exposing two methods: <c>produce</c> (Unary) and
/// <c>subscribe</c> (ServerStreaming).
/// </summary>
/// <remarks>
/// Pulsar's "list all topics" call is a per-namespace HTTP GET — there's
/// no broker-wide topic catalogue without enumerating namespaces. We
/// default to <c>public/default</c> because that's the convention
/// pulsar-standalone ships, then merge whatever extra namespaces the
/// user passes in.
/// </remarks>
internal static class PulsarDiscovery
{
    /// <summary>
    /// Build the Bowire service tree from the admin REST surface. The
    /// HttpClient is passed in (rather than constructed here) so the
    /// plugin can wire it through <c>BowireHttpClientFactory</c> for
    /// localhost-cert + TLS opt-ins.
    /// </summary>
    public static async Task<List<BowireServiceInfo>> ListTopicsAsync(
        HttpClient http, PulsarEndpoints endpoints, IEnumerable<string> namespaces,
        string originUrl, CancellationToken ct)
    {
        var services = new List<BowireServiceInfo>();
        foreach (var ns in namespaces)
        {
            var topics = await ListNamespaceTopicsAsync(http, endpoints, ns, ct).ConfigureAwait(false);
            if (topics.Count == 0) continue;

            foreach (var topic in topics)
            {
                var shortName = ShortTopicName(topic);
                var methods = BuildTopicMethods(topic);
                services.Add(new BowireServiceInfo(shortName, "pulsar", methods)
                {
                    Source = "pulsar",
                    OriginUrl = originUrl,
                    Description = "Pulsar topic " + topic,
                });
            }
        }
        return services;
    }

    /// <summary>
    /// One service per topic gets exactly two methods: produce (Unary)
    /// and subscribe (ServerStreaming). The FullName encodes the topic
    /// so the invoke side can route without a second lookup.
    /// </summary>
    internal static List<BowireMethodInfo> BuildTopicMethods(string fullTopicName)
    {
        var payloadField = new BowireFieldInfo(
            Name: "payload", Number: 1, Type: "string", Label: "optional",
            IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null)
        { Source = "body" };

        var produceIn = new BowireMessageInfo("ProduceRequest", "ProduceRequest", [payloadField]);
        var emptyOut = new BowireMessageInfo("Ack", "Ack", []);

        return
        [
            new BowireMethodInfo(
                Name: "produce",
                FullName: "pulsar/topic/" + fullTopicName + "/produce",
                ClientStreaming: false, ServerStreaming: false,
                InputType: produceIn, OutputType: emptyOut,
                MethodType: "Unary")
            {
                Summary = "Produce one message to the topic",
            },
            new BowireMethodInfo(
                Name: "subscribe",
                FullName: "pulsar/topic/" + fullTopicName + "/subscribe",
                ClientStreaming: false, ServerStreaming: true,
                InputType: new BowireMessageInfo("SubscribeRequest", "SubscribeRequest", []),
                OutputType: new BowireMessageInfo("Message", "Message", []),
                MethodType: "ServerStreaming")
            {
                Summary = "Subscribe and stream messages as they arrive",
            },
        ];
    }

    /// <summary>
    /// Fetch the topic list for one namespace. Returns an empty list
    /// when the admin endpoint is unreachable / unauthorised / the
    /// namespace doesn't exist — the plugin should keep going with the
    /// other namespaces, not bomb out the whole discovery.
    /// </summary>
    private static async Task<List<string>> ListNamespaceTopicsAsync(
        HttpClient http, PulsarEndpoints endpoints, string ns, CancellationToken ct)
    {
        // Skip blanks the user may have left in the config.
        if (string.IsNullOrWhiteSpace(ns)) return [];

        // ns is "tenant/namespace" — validate so a bad config doesn't
        // round-trip a 404.
        if (!ns.Contains('/', StringComparison.Ordinal)) return [];

        var url = new Uri($"{endpoints.AdminBaseUrl}/admin/v2/persistent/{ns}");
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseTopicJson(json);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Pulsar's admin returns a JSON array of fully-qualified topic
    /// names (<c>persistent://public/default/foo</c>). We accept any
    /// JSON string array and pass through unknown shapes as empty.
    /// </summary>
    internal static List<string> ParseTopicJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(el => el.ValueKind == JsonValueKind.String)
                .Select(el => el.GetString())
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Strip <c>persistent://tenant/ns/</c> off a fully-qualified topic
    /// name so the UI shows just the trailing leaf. Falls back to the
    /// full name when it doesn't follow the convention.
    /// </summary>
    internal static string ShortTopicName(string fullTopicName)
    {
        if (string.IsNullOrEmpty(fullTopicName)) return fullTopicName;
        var slash = fullTopicName.LastIndexOf('/');
        return slash >= 0 && slash < fullTopicName.Length - 1
            ? fullTopicName[(slash + 1)..]
            : fullTopicName;
    }
}
