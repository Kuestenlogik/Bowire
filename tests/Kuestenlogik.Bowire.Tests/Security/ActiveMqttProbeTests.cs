// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the active (mutating) MQTT retained-message-poisoning probe
/// (#395) and the active-probe suite runner. Driven through a stateful
/// <see cref="RetainedFake"/> that records PUBLISHes and optionally re-delivers
/// the retained message to a fresh subscriber — modelling a broker that does /
/// doesn't persist arbitrary retained writes, without a live MQTT server.
/// </summary>
public sealed class ActiveMqttProbeTests
{
    private static readonly string[] s_auth = ["Authorization: Bearer x"];
    private static readonly string[] s_noAuth = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ActiveScanOptions s_active = new();

    [Fact]
    public async Task NonBrokerScheme_Silent()
    {
        var probe = new MqttRetainedPoisoningProbe();
        var fake = new RetainedFake();
        Assert.Empty(await probe.RunAsync("https://api.example.com", fake, s_noAuth, s_active, Ct));
        Assert.Empty(fake.Publishes); // never touched the plugin
    }

    [Fact]
    public async Task RetainedRedelivered_FlagsPoisoning()
    {
        var probe = new MqttRetainedPoisoningProbe();
        var fake = new RetainedFake { RedeliverRetained = true };

        var f = Assert.Single(await probe.RunAsync("mqtt://broker:1883", fake, s_auth, s_active, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API8-MQTT-RETAINED-POISONING", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("API8-2023-SECMISCONF", f.Template.Recording.Vulnerability?.OwaspApi);
    }

    [Fact]
    public async Task RetainedNotRedelivered_ReportsClean()
    {
        var probe = new MqttRetainedPoisoningProbe();
        var fake = new RetainedFake { RedeliverRetained = false };

        var f = Assert.Single(await probe.RunAsync("mqtt://broker:1883", fake, s_auth, s_active, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("RETAINED-CLEAN", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejected_Inconclusive()
    {
        var probe = new MqttRetainedPoisoningProbe();
        var fake = new RetainedFake { PublishStatus = "Error" };

        var f = Assert.Single(await probe.RunAsync("mqtt://broker:1883", fake, s_auth, s_active, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("RETAINED-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamespacesThrowawayTopic_AndClearsRetainedOnCleanup()
    {
        var probe = new MqttRetainedPoisoningProbe();
        var fake = new RetainedFake { RedeliverRetained = true };

        await probe.RunAsync("mqtt://broker:1883", fake, s_auth, s_active, Ct);

        // Unique namespaced topic used for every publish.
        Assert.All(fake.Publishes, p => Assert.StartsWith("bowire/probe/", p.Topic, StringComparison.Ordinal));
        // Two publishes: the retained plant, then the empty-payload clear.
        Assert.Equal(2, fake.Publishes.Count);
        Assert.True(fake.Publishes[0].Retain);
        Assert.NotEqual("", fake.Publishes[0].Payload);
        // Cleanup clears the retained message with an empty retained payload.
        Assert.Equal("", fake.Publishes[1].Payload);
        Assert.True(fake.Publishes[1].Retain);
        // Same topic for plant + clear.
        Assert.Equal(fake.Publishes[0].Topic, fake.Publishes[1].Topic);
    }

    [Fact]
    public async Task ActiveSuiteRunner_PluginAbsent_Skips()
    {
        var registry = new BowireProtocolRegistry(); // no mqtt plugin
        var findings = await OwaspApiSuite.RunActiveProtocolProbesAsync(
            "mqtt://broker:1883", registry, s_auth, s_active, TimeSpan.FromSeconds(5), Ct);

        // Every registered active mqtt probe skips with a PLUGIN-ABSENT marker
        // when the plugin isn't loaded (count grows as probes are added).
        Assert.NotEmpty(findings);
        Assert.All(findings, f =>
        {
            Assert.Equal(ScanFindingStatus.Skipped, f.Status);
            Assert.Contains("PLUGIN-ABSENT", f.Template.Recording.Id, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ActiveSuiteRunner_ResolvesPluginAndRuns()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new RetainedFake { RedeliverRetained = true });

        // 127.0.0.1:1 is a closed port → the self-contained will-abuse probe's
        // own MQTT connect fails fast (refused) rather than hanging, while the
        // fake-plugin-driven retained/wildcard probes ignore the address.
        var findings = await OwaspApiSuite.RunActiveProtocolProbesAsync(
            "mqtt://127.0.0.1:1", registry, s_auth, s_active, TimeSpan.FromSeconds(5), Ct);

        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API8-MQTT-RETAINED-POISONING");
    }

    [Fact]
    public async Task WillAbuse_NonBrokerScheme_Silent()
        => Assert.Empty(await new MqttWillMessageAbuseProbe().RunAsync("https://api.example.com", new RetainedFake(), s_auth, s_active, Ct));

    // Stateful MQTT plugin fake: records every PUBLISH; a fresh SUBSCRIBE
    // re-delivers the first retained payload iff RedeliverRetained is set.
    private sealed class RetainedFake : IBowireProtocol
    {
        public string Id => "mqtt";
        public string Name => "mqtt";
        public string IconSvg => "";
        public bool RedeliverRetained { get; init; }
        public string PublishStatus { get; init; } = "OK";
        public List<(string Topic, string Payload, bool Retain)> Publishes { get; } = [];

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            var retain = metadata?.TryGetValue("retain", out var r) == true && string.Equals(r, "true", StringComparison.OrdinalIgnoreCase);
            Publishes.Add((method, jsonMessages.FirstOrDefault() ?? "", retain));
            return Task.FromResult(new InvokeResult(null, 0, PublishStatus, []));
        }

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (!RedeliverRetained) yield break; // broker didn't persist → nothing re-delivered
            // Re-deliver the first retained (non-empty) payload published to this topic.
            var retained = Publishes.FirstOrDefault(p => p.Topic == method && p.Payload.Length > 0);
            if (retained.Payload is { Length: > 0 })
                yield return $"{{\"topic\":\"{method}\",\"retain\":true,\"payload\":{retained.Payload}}}";
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
