// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Discovery in a <c>bowire test</c> run (#1053): only for a plugin that needs
/// it before invoking, and once per server URL for the run.
/// </summary>
public sealed class RunDiscoveryTests
{
    [Fact]
    public async Task A_Plugin_That_Needs_Discovery_Is_Discovered_Once_Per_Url()
    {
        var protocol = new CountingProtocol(needsDiscovery: true);
        var discovery = new RunDiscovery();
        var ct = TestContext.Current.CancellationToken;

        foreach (var url in new[] { "http://a", "http://a", "http://b", "http://a" })
            Assert.Null(await discovery.EnsureAsync(protocol, url, ct));

        Assert.Equal(["http://a", "http://b"], protocol.Discovered);
    }

    [Fact]
    public async Task A_Plugin_That_Discovers_On_Its_Own_Is_Left_Alone()
    {
        var protocol = new CountingProtocol(needsDiscovery: false);
        Assert.Null(await new RunDiscovery().EnsureAsync(protocol, "http://a", TestContext.Current.CancellationToken));
        Assert.Empty(protocol.Discovered);
    }

    [Fact]
    public async Task A_Failure_Is_Reported_For_Every_Call_Without_Trying_Again()
    {
        var protocol = new CountingProtocol(needsDiscovery: true) { Fail = true };
        var discovery = new RunDiscovery();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("unreachable", await discovery.EnsureAsync(protocol, "http://a", ct));
        Assert.Equal("unreachable", await discovery.EnsureAsync(protocol, "http://a", ct));
        Assert.Single(protocol.Discovered);
    }

    [Fact]
    public void Rest_Needs_No_Discovery_Before_Invoking()
    {
        using var rest = new Kuestenlogik.Bowire.Protocol.Rest.BowireRestProtocol();
        Assert.False(((IBowireProtocol)rest).InvokeNeedsDiscovery);
    }

    private sealed class CountingProtocol(bool needsDiscovery) : IBowireProtocol
    {
        public List<string> Discovered { get; } = [];
        public bool Fail { get; init; }
        public string Name => "Counting";
        public string Id => "counting";
        public string IconSvg => "";
        public bool InvokeNeedsDiscovery => needsDiscovery;

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            Discovered.Add(serverUrl);
            return Fail ? throw new HttpRequestException("unreachable") : Task.FromResult(new List<BowireServiceInfo>());
        }

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
