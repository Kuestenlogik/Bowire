// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.IntegrationTests.Services;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// An embedding host, discovered and called from the descriptors compiled
/// into it (#992).
/// </summary>
/// <remarks>
/// <para>
/// <c>MapBowire()</c> used to map the host's gRPC reflection service so that
/// Bowire could read the host's own services back over the wire. Every host
/// that embedded Bowire thereby published its whole contract on two endpoints
/// it had not asked for, and its own workbench depended on a network round
/// trip to itself.
/// </para>
/// <para>
/// None of the hosts here map the reflection service unless the test is
/// about opting in. What they share with a real host is the one line that
/// matters: <c>MapGrpcService&lt;T&gt;()</c>.
/// </para>
/// <para>
/// Each test asks a plugin instance it initialised with its own host, not
/// <c>/bowire/api/services</c>. The registry behind that endpoint is one per
/// process, so with several hosts alive in a parallel run the endpoint of one
/// answers with the plugins of whichever host was mapped last.
/// </para>
/// </remarks>
public sealed class GrpcEmbeddedDescriptorsIntegrationTests : IDisposable
{
    // The gRPC plugin also reads the uploaded descriptor sets, and those live
    // in the identity's storage: without a root of its own this class would
    // read whatever the developer's workbench holds.
    private readonly TempUserRoot _storage = new("grpc-embedded");

    public void Dispose()
    {
        _storage.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task<WebApplication> StartAsync(
        HttpProtocols protocols,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        builder.WebHost.ConfigureKestrel(o => o.ConfigureEndpointDefaults(l => l.Protocols = protocols));
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddBowire();
        services?.Invoke(builder.Services);

        var app = builder.Build();
        if (map is null) app.MapGrpcService<GreeterService>();
        else map(app);
        app.MapBowire("/bowire");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static BowireGrpcProtocol PluginOf(WebApplication app)
    {
        var plugin = new BowireGrpcProtocol();
        plugin.Initialize(app.Services);
        return plugin;
    }

    private static bool MapsReflection(WebApplication app)
        => app.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Any(e => (e.RoutePattern.RawText ?? "").Contains("grpc.reflection", StringComparison.Ordinal));

    [Fact]
    public async Task MapBowire_Maps_No_Reflection_Endpoint()
    {
        await using var app = await StartAsync(HttpProtocols.Http2);

        Assert.False(MapsReflection(app));
    }

    [Fact]
    public async Task The_Reflection_Endpoint_Is_Mapped_When_The_Host_Asks_For_It()
    {
        await using var app = await StartAsync(
            HttpProtocols.Http2,
            services => services.AddBowireGrpc(o => o.MapReflectionService = true));

        Assert.True(MapsReflection(app));
    }

    [Fact]
    public async Task The_Hosts_Mapped_Services_Are_Discovered_Without_Reflection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(HttpProtocols.Http2);

        var services = await PluginOf(app).DiscoverAsync(
            LoopbackHost.BaseAddress(app.Services), showInternalServices: false, ct);

        var greeter = Assert.Single(services, s => s.Name == "test.Greeter");
        // Unary and streaming alike: the descriptor names all of them.
        Assert.Equal(
            ["ChatHellos", "CollectHellos", "ListPolicies", "SayHello", "SayHelloStream"],
            greeter.Methods.Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Contains(
            greeter.Methods.Single(m => m.Name == "SayHello").InputType!.Fields, f => f.Name == "name");
    }

    [Fact]
    public async Task A_Unary_Call_And_A_Stream_Go_Through_The_Hosts_Descriptors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(HttpProtocols.Http2);
        var plugin = PluginOf(app);
        var url = LoopbackHost.BaseAddress(app.Services);

        var unary = await plugin.InvokeAsync(
            url, "test.Greeter", "SayHello", ["""{"name":"embedded","count":1}"""],
            showInternalServices: false, metadata: null, ct);
        Assert.Contains("Hello embedded!", unary.Response ?? "", StringComparison.Ordinal);

        var frames = new List<string>();
        await foreach (var frame in plugin.InvokeStreamAsync(
            url, "test.Greeter", "SayHelloStream", ["""{"name":"embedded","count":2}"""],
            showInternalServices: false, metadata: null, ct))
        {
            frames.Add(frame);
        }
        Assert.Equal(2, frames.Count);

        await using var channel = await plugin.OpenChannelAsync(
            url, "test.Greeter", "ChatHellos", showInternalServices: false, metadata: null, ct);
        Assert.NotNull(channel);
    }

    [Fact]
    public async Task Discovering_Itself_The_Host_Asks_Nothing_Over_The_Wire()
    {
        // The workbench is usually served from an HTTP/1.1 port. A reflection
        // call to one neither succeeds nor fails promptly: it sits until the
        // discovery ceiling cancels it, and the plugin's whole answer goes
        // with it. Cancelling up front shows that no call is made: listing
        // descriptors in memory has nothing to wait for.
        await using var app = await StartAsync(HttpProtocols.Http1);
        var plugin = PluginOf(app);
        var url = LoopbackHost.BaseAddress(app.Services);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var services = await plugin.DiscoverAsync(
            url, showInternalServices: false, TestContext.Current.CancellationToken);
        var withMetadata = await plugin.DiscoverAsync(
            url, showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        Assert.Single(services, s => s.Name == "test.Greeter");
        Assert.Single(withMetadata, s => s.Name == "test.Greeter");
        // No assertion on the clock beyond this: an h2c handshake against an
        // HTTP/1.1 port took seconds, reading descriptors takes none.
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_Descriptor_Named_In_The_Options_Is_Discovered_Without_A_Mapped_Service()
    {
        // A service that lives elsewhere but whose generated code is in this
        // process: nothing is mapped here, the descriptor is named instead.
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(
            HttpProtocols.Http1,
            services => services.AddBowireGrpc(o => o.Descriptors.Add(GreeterReflection.Descriptor)),
            map: _ => { });

        var services = await PluginOf(app).DiscoverAsync(
            LoopbackHost.BaseAddress(app.Services), showInternalServices: false, ct);

        Assert.Single(services, s => s.Name == "test.Greeter");
    }

    [Fact]
    public async Task A_Host_Supplied_Descriptor_Set_Is_Discovered_For_Services_In_Another_Process()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = GrpcDescriptorSetIntegrationTests.GreeterDescriptorSet();
        await using var app = await StartAsync(
            HttpProtocols.Http1,
            services => services.AddBowireGrpc(o => o.DescriptorSet = bytes),
            map: _ => { });

        var services = await PluginOf(app).DiscoverAsync(
            LoopbackHost.BaseAddress(app.Services), showInternalServices: false, ct);

        Assert.Single(services, s => s.Name == "test.Greeter");
    }

    [Fact]
    public async Task A_Descriptor_Set_File_Is_Read_Relative_To_The_Content_Root()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_storage.Root, "greeter.protoset");
        await File.WriteAllBytesAsync(path, GrpcDescriptorSetIntegrationTests.GreeterDescriptorSet(), ct);
        await using var app = await StartAsync(
            HttpProtocols.Http1,
            services => services.AddBowireGrpc(o => o.DescriptorSetFile = path),
            map: _ => { });

        var services = await PluginOf(app).DiscoverAsync(
            LoopbackHost.BaseAddress(app.Services), showInternalServices: false, ct);

        Assert.Single(services, s => s.Name == "test.Greeter");
    }

    [Fact]
    public async Task A_Descriptor_Set_File_That_Is_Missing_Says_So_And_Names_The_Path()
    {
        // Quietly showing less would send the operator looking at the server
        // instead of at the path they configured.
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_storage.Root, "absent.protoset");
        await using var app = await StartAsync(
            HttpProtocols.Http1,
            services => services.AddBowireGrpc(o => o.DescriptorSetFile = path),
            map: _ => { });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PluginOf(app).DiscoverAsync(
            LoopbackHost.BaseAddress(app.Services), showInternalServices: false, ct));

        Assert.Contains("absent.protoset", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mapped_Services_Can_Be_Left_Out()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(
            HttpProtocols.Http2,
            services => services.AddBowireGrpc(o => o.UseMappedServices = false));

        // Nothing held, so the plugin is back to asking the server, and this
        // one does not answer reflection.
        var services = await PluginOf(app).DiscoverAsync(
            LoopbackHost.BaseAddress(app.Services), showInternalServices: false, ct);

        Assert.DoesNotContain(services, s => s.Name == "test.Greeter");
    }
}
