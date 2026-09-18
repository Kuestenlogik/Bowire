// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Sse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Sse.Tests;

/// <summary>
/// <see cref="SseEndpointDiscovery"/> called directly — the self-origin
/// gate, the ad-hoc single-endpoint service, and the shape of what
/// discovery hands the workbench.
/// </summary>
/// <remarks>
/// The existing suite reaches this through <c>BowireSseProtocol</c>, which
/// covers the two scan paths and leaves the decisions around them: whether
/// the workbench's own routes are in scope at all, and what
/// <c>bowire --url sse@…</c> builds when there is nothing to scan.
/// </remarks>
[Collection<SseTestGroup>]
public sealed class SseEndpointDiscoveryUnitTests : IDisposable
{
    public SseEndpointDiscoveryUnitTests() => BowireSseProtocol.ClearRegisteredEndpoints();

    public void Dispose()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
        GC.SuppressFinalize(this);
    }

    // ---- the self-origin gate ----

    [Fact]
    public async Task An_External_Source_Does_Not_Pick_Up_The_Workbench_Own_Routes()
    {
        // The operator adds a source and sees a service they never
        // configured: "unter der source http://localhost:5181/api/locations
        // sehe ich 1 service (SSE ENDPOINTS), … beim aufruf nur: Stream
        // error occurred." The workbench's own SSE routes were being
        // reported under every external URL, and invoking one went nowhere,
        // because the stream it named is not on that host.
        await using var app = await StartedHostAsync();

        var services = SseEndpointDiscovery.Discover(
            [], app.Services, "https://somewhere-else.example.com/api/locations");

        Assert.Empty(services);
    }

    [Fact]
    public async Task The_Workbench_Own_Url_Still_Sees_Them()
    {
        // The gate must not cost the case it was added around.
        await using var app = await StartedHostAsync();

        var services = SseEndpointDiscovery.Discover([], app.Services, app.Urls.First());

        var method = Assert.Single(Assert.Single(services).Methods);
        Assert.Equal("SSE/events/ticker", method.FullName);
    }

    [Fact]
    public async Task Embedded_Mode_With_No_Url_Scans_Anyway()
    {
        // Null / empty is a host with no URL context rather than a foreign
        // one, and it is what the embedded path passes.
        await using var app = await StartedHostAsync();

        Assert.Single(SseEndpointDiscovery.Discover([], app.Services, serverUrl: null));
        Assert.Single(SseEndpointDiscovery.Discover([], app.Services, serverUrl: ""));
    }

    [Fact]
    public void Without_A_Service_Provider_Only_What_Was_Registered_Counts()
    {
        var services = SseEndpointDiscovery.Discover(
            [new SseEndpointInfo("/events/manual", "Manual")], serviceProvider: null);

        Assert.Equal("Manual", Assert.Single(Assert.Single(services).Methods).Name);
    }

    // ---- what comes back ----

    [Fact]
    public void Nothing_Found_Is_No_Service_Rather_Than_An_Empty_One()
    {
        // An empty "SSE Endpoints" entry in the service list reads as a
        // server that offers SSE and has no streams, which is a different
        // claim from not speaking SSE.
        Assert.Empty(SseEndpointDiscovery.Discover([], serviceProvider: null));
    }

    [Fact]
    public void One_Path_Is_One_Method_However_It_Was_Cased()
    {
        // Route matching is case-insensitive, so two spellings of one path
        // are one stream; listing both would invite subscribing twice.
        var services = SseEndpointDiscovery.Discover(
            [
                new SseEndpointInfo("/events/ticker", "First"),
                new SseEndpointInfo("/Events/Ticker", "Second"),
            ],
            serviceProvider: null);

        var method = Assert.Single(Assert.Single(services).Methods);
        Assert.Equal("First", method.Name);
    }

    [Fact]
    public void Every_Endpoint_Is_Server_Streaming_And_Never_Client_Streaming()
    {
        // SSE is one-directional by definition; the workbench picks its UI
        // off these flags, and a client-streaming SSE method would offer a
        // request editor for something that accepts no requests.
        var services = SseEndpointDiscovery.Discover(
            [new SseEndpointInfo("/events/ticker", "Ticker")], serviceProvider: null);

        var method = Assert.Single(Assert.Single(services).Methods);
        Assert.True(method.ServerStreaming);
        Assert.False(method.ClientStreaming);
        Assert.Equal("ServerStreaming", method.MethodType);
    }

    [Fact]
    public void An_Event_Carries_The_Four_Fields_The_Wire_Format_Defines()
    {
        // id / event / data / retry is the whole of the SSE frame. The
        // workbench renders from this shape, so a missing field is a field
        // the operator cannot see arriving.
        var services = SseEndpointDiscovery.Discover(
            [new SseEndpointInfo("/events/ticker", "Ticker")], serviceProvider: null);

        var output = Assert.Single(Assert.Single(services).Methods).OutputType;

        Assert.Equal(["id", "event", "data", "retry"], output.Fields.Select(f => f.Name));
        Assert.Equal("int32", output.Fields.Single(f => f.Name == "retry").Type);
    }

    [Fact]
    public void The_Service_Is_Labelled_As_Coming_From_Sse()
    {
        // Source drives the badge and the grouping; unset, the entry sits
        // among the gRPC reflection results with nothing to tell them apart.
        var services = SseEndpointDiscovery.Discover(
            [new SseEndpointInfo("/events/ticker", "Ticker")], serviceProvider: null);

        Assert.Equal("sse", Assert.Single(services).Source);
    }

    // ---- bowire --url sse@… ----

    [Fact]
    public void An_Ad_Hoc_Subscription_Is_Named_After_The_Url_Path()
    {
        var service = SseEndpointDiscovery.BuildAdHocService("https://harbor.example.com/stream/berths");

        Assert.Equal("/stream/berths", service.Package);
        var method = Assert.Single(service.Methods);
        Assert.Equal("SSE/stream/berths", method.FullName);
        Assert.Contains("https://harbor.example.com/stream/berths", method.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Ad_Hoc_Subscription_Keeps_The_Query()
    {
        // The query is how these streams are usually filtered, and dropping
        // it would silently subscribe to something wider than was asked for.
        var service = SseEndpointDiscovery.BuildAdHocService("https://harbor.example.com/stream?topic=berths");

        Assert.Equal("/stream?topic=berths", service.Package);
    }

    [Fact]
    public void An_Unparseable_Url_Still_Yields_A_Subscription()
    {
        // The caller has already reached the server and seen
        // text/event-stream, so something is there to subscribe to even if
        // the string will not parse as a URI.
        var service = SseEndpointDiscovery.BuildAdHocService("not a url");

        Assert.Equal("/", service.Package);
        Assert.Single(service.Methods);
    }

    // ---- harness ----

    /// <summary>
    /// A started host carrying one SSE route, listening on a real port so
    /// the self-origin check has addresses to compare against.
    /// </summary>
    private static async Task<WebApplication> StartedHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/events/ticker", () => "ok")
           .WithMetadata(new SseEndpointAttribute { Description = "Ticker" });
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
