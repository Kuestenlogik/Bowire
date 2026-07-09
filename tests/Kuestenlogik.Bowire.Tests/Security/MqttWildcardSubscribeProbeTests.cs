// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the active MQTT wildcard-subscribe privilege probe (#396):
/// delivery-based verdict against an operator-supplied topic scope, driven
/// through a stream fake that re-delivers a configured set of topics on the
/// <c>#</c> subscription.
/// </summary>
public sealed class MqttWildcardSubscribeProbeTests
{
    private static readonly string[] s_auth = ["Authorization: Bearer x"];
    private static readonly string[] s_noAuth = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ActiveScanOptions Scope(params string[] expected)
        => new() { DurationSeconds = 1, ExpectedTopics = expected };

    [Theory]
    [InlineData("a/b/c", "a/b/c", true)]
    [InlineData("a/+/c", "a/b/c", true)]
    [InlineData("a/+/c", "a/b/d", false)]
    [InlineData("a/#", "a/b/c", true)]
    [InlineData("a/#", "a", true)]      // '#' matches the parent level too
    [InlineData("#", "x/y/z", true)]
    [InlineData("sport/+", "sport/tennis", true)]
    [InlineData("sport/+", "sport/tennis/player", false)]
    [InlineData("a/b", "a/b/c", false)] // shorter filter without '#'
    public void TopicFilterMatches_FollowsMqttSemantics(string filter, string topic, bool expected)
        => Assert.Equal(expected, MqttWildcardSubscribeProbe.TopicFilterMatches(filter, topic));

    [Fact]
    public async Task NonBrokerScheme_Silent()
        => Assert.Empty(await new MqttWildcardSubscribeProbe().RunAsync("https://x", new StreamFake(), s_auth, Scope("a/#"), Ct));

    [Fact]
    public async Task NoAuthHeader_Silent()
        => Assert.Empty(await new MqttWildcardSubscribeProbe().RunAsync("mqtt://x:1883", new StreamFake(), s_noAuth, Scope("a/#"), Ct));

    [Fact]
    public async Task OutOfScopeTopicDelivered_FlagsOverBroad()
    {
        var fake = new StreamFake("tenantA/telemetry", "tenantB/secrets");
        var f = Assert.Single(await new MqttWildcardSubscribeProbe().RunAsync(
            "mqtt://x:1883", fake, s_auth, Scope("tenantA/#"), Ct));

        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API1-MQTT-WILDCARD-OVERBROAD", f.Template.Recording.Vulnerability?.Id);
        Assert.Contains("tenantB/secrets", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllDeliveredInScope_ReportsSafe()
    {
        var fake = new StreamFake("tenantA/telemetry", "tenantA/status");
        var f = Assert.Single(await new MqttWildcardSubscribeProbe().RunAsync(
            "mqtt://x:1883", fake, s_auth, Scope("tenantA/#"), Ct));

        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("WILDCARD-SCOPED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoScopeSupplied_ObservationOnly()
    {
        var fake = new StreamFake("a/b", "c/d");
        var f = Assert.Single(await new MqttWildcardSubscribeProbe().RunAsync(
            "mqtt://x:1883", fake, s_auth, new ActiveScanOptions { DurationSeconds = 1 }, Ct));

        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("WILDCARD-NO-SCOPE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeThrows_Inconclusive()
    {
        var fake = new StreamFake { Throw = true };
        var f = Assert.Single(await new MqttWildcardSubscribeProbe().RunAsync(
            "mqtt://x:1883", fake, s_auth, Scope("a/#"), Ct));

        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("WILDCARD-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // Stream fake: InvokeStreamAsync yields one envelope per configured topic.
    private sealed class StreamFake : IBowireProtocol
    {
        private readonly string[] _topics;
        public StreamFake(params string[] topics) => _topics = topics;
        public bool Throw { get; init; }

        public string Id => "mqtt";
        public string Name => "mqtt";
        public string IconSvg => "";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", []));

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (Throw) throw new InvalidOperationException("broker refused wildcard subscribe");
            foreach (var topic in _topics)
            {
                ct.ThrowIfCancellationRequested();
                yield return $"{{\"topic\":\"{topic}\",\"qos\":0,\"payload\":\"x\"}}";
            }
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
