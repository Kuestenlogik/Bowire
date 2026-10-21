// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// The adapter's tool surface: what <c>tools/list</c> offers for an upstream
/// and what <c>tools/call</c> runs, held against an in-memory plugin.
/// </summary>
public sealed class McpAdapterToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BowireMcpAdapterOptions Upstream => new() { UpstreamServerUrl = "http://upstream.example" };

    [Fact]
    public async Task Only_Unary_Methods_Are_Listed_As_Tools()
    {
        var (registry, _) = Registry();

        var tools = await BowireMcpAdapterServiceCollectionExtensions.ListAdapterToolsAsync(registry, Upstream, Ct);

        Assert.Equal("weather_Weather_Get", Assert.Single(tools).Name);
    }

    [Fact]
    public async Task A_Unary_Tool_Is_Called()
    {
        var (registry, stub) = Registry();

        var result = await BowireMcpAdapterServiceCollectionExtensions.CallAdapterToolAsync(
            registry, Upstream, "weather_Weather_Get", Args("""{"city":"Kiel"}"""), Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(["Get"], stub.Invoked);
    }

    [Fact]
    public async Task A_Streaming_Method_That_Was_Never_Listed_Is_Not_Called()
    {
        // tools/list leaves streaming methods out, because one tools/call
        // cannot carry a stream. Calling one by its would-be name has to be
        // as unknown as any other unlisted tool, not a unary invoke of a
        // streaming method.
        var (registry, stub) = Registry();

        await Assert.ThrowsAsync<McpException>(() =>
            BowireMcpAdapterServiceCollectionExtensions.CallAdapterToolAsync(
                registry, Upstream, "weather_Weather_Watch", Args("{}"), Ct));

        Assert.Empty(stub.Invoked);
    }

    [Fact]
    public async Task The_Bearer_Token_Travels_With_Discovery_And_The_Call()
    {
        var (registry, stub) = Registry();
        var upstream = Upstream;
        upstream.BearerToken = "s3cret";

        await BowireMcpAdapterServiceCollectionExtensions.CallAdapterToolAsync(
            registry, upstream, "weather_Weather_Get", Args("{}"), Ct);

        Assert.Equal("Bearer s3cret", stub.DiscoveryAuthorization);
        Assert.Equal("Bearer s3cret", stub.InvokeAuthorization);
    }

    [Fact]
    public async Task Without_A_Bearer_Token_No_Authorization_Is_Sent()
    {
        var (registry, stub) = Registry();

        await BowireMcpAdapterServiceCollectionExtensions.CallAdapterToolAsync(
            registry, Upstream, "weather_Weather_Get", Args("{}"), Ct);

        Assert.Null(stub.DiscoveryAuthorization);
        Assert.Null(stub.InvokeAuthorization);
    }

    [Fact]
    public async Task An_Upstream_Call_That_Outlasts_The_Request_Timeout_Is_Cancelled()
    {
        // The stub's invoke completes only when its token is cancelled, so
        // the call returns exactly when the adapter's own timeout fires.
        var (registry, stub) = Registry();
        stub.InvokeWaitsForCancellation = true;
        var upstream = Upstream;
        upstream.RequestTimeout = TimeSpan.FromMilliseconds(1);

        var result = await BowireMcpAdapterServiceCollectionExtensions.CallAdapterToolAsync(
            registry, upstream, "weather_Weather_Get", Args("{}"), Ct);

        Assert.True(result.IsError);
        Assert.False(Ct.IsCancellationRequested);
    }

    private static Dictionary<string, JsonElement> Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static (BowireProtocolRegistry Registry, StubUpstream Stub) Registry()
    {
        var stub = new StubUpstream();
        var registry = new BowireProtocolRegistry();
        registry.Register(stub);
        return (registry, stub);
    }

    /// <summary>One service with a unary and a server-streaming method.</summary>
    private sealed class StubUpstream : IBowireProtocol
    {
        public List<string> Invoked { get; } = [];
        public string? DiscoveryAuthorization { get; private set; }
        public string? InvokeAuthorization { get; private set; }
        public bool InvokeWaitsForCancellation { get; set; }

        public string Id => "stub";
        public string Name => "Stub";
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            var msg = new BowireMessageInfo("Empty", "weather.Empty", []);
            return Task.FromResult(new List<BowireServiceInfo>
            {
                new("weather.Weather", "weather",
                [
                    new BowireMethodInfo("Get", "weather.Weather/Get", false, false, msg, msg, "Unary"),
                    new BowireMethodInfo("Watch", "weather.Weather/Watch", false, true, msg, msg, "ServerStreaming"),
                ]),
            });
        }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default)
        {
            DiscoveryAuthorization = metadata?.GetValueOrDefault("Authorization");
            return DiscoverAsync(serverUrl, showInternalServices, ct);
        }

        public async Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            Invoked.Add(method);
            InvokeAuthorization = metadata?.GetValueOrDefault("Authorization");
            if (InvokeWaitsForCancellation)
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new InvokeResult("{}", 1, "OK", new Dictionary<string, string>());
        }

        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
