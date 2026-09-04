// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.IntegrationTests.Services;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Calling a gRPC server that does not answer reflection (#653).
/// </summary>
/// <remarks>
/// <para>
/// Every other gRPC test in this project hosts <c>MapGrpcReflectionService()</c>
/// alongside the greeter. This one deliberately does not — reflection off is
/// the recommended production state, the one Bowire's own scanner recommends,
/// and until now it meant the plugin could not call the server at all.
/// </para>
/// <para>
/// The descriptor set is built from the generated <c>greeter.proto</c>
/// descriptors at runtime rather than shipping a checked-in <c>.protoset</c>
/// fixture: a fixture would silently rot the first time the proto changed, and
/// the point of the test is the transport path, not the bytes.
/// </para>
/// </remarks>
public sealed class GrpcDescriptorSetIntegrationTests
{
    [Fact]
    public async Task ASuppliedDescriptorSetCallsAServerWithNoReflection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartGreeterWithoutReflectionAsync();

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GrpcDescriptorSetMarkerKey] = InlineMarker(),
        };

        var result = await new BowireGrpcProtocol().InvokeAsync(
            host.Url, "test.Greeter", "SayHello",
            ["""{"name":"descriptor set","count":1}"""],
            showInternalServices: false, metadata: metadata, ct);

        Assert.Contains("descriptor set", result.Response ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutOneTheSameCallFailsAndSaysWhatToSupply()
    {
        // The baseline this feature exists to change. The message matters as
        // much as the failure: the old one said "No file descriptors for
        // 'test.Greeter'", which names an internal concept and offers no way
        // out. Someone hitting it should learn that reflection is off and what
        // to hand us instead.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartGreeterWithoutReflectionAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            new BowireGrpcProtocol().InvokeAsync(
                host.Url, "test.Greeter", "SayHello",
                ["""{"name":"nothing supplied","count":1}"""],
                showInternalServices: false, metadata: null, ct));

        Assert.Contains("descriptor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("descriptor_set_out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMarkerNeverReachesTheServerAsAHeader()
    {
        // The descriptor set is configuration for the plugin. Forwarded as a
        // gRPC header it would be a multi-kilobyte base64 blob on every call,
        // and on a real server most likely a protocol error — headers are not
        // where a schema belongs.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartGreeterWithoutReflectionAsync();

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GrpcDescriptorSetMarkerKey] = InlineMarker(),
            ["x-probe"] = "kept",
        };

        var result = await new BowireGrpcProtocol().InvokeAsync(
            host.Url, "test.Greeter", "SayHello",
            ["""{"name":"header check","count":1}"""],
            showInternalServices: false, metadata: metadata, ct);

        // The call succeeding at all is the assertion: GreeterService echoes
        // the name back, and a marker leaking into the headers would have had
        // to survive Kestrel's header limits to get here.
        Assert.Contains("header check", result.Response ?? "", StringComparison.Ordinal);
    }

    // Kept as a literal rather than referencing the internal constant: the test
    // should notice if the wire key ever changes, not follow it silently.
    private const string GrpcDescriptorSetMarkerKey = "__bowireGrpcDescriptors__";

    /// <summary>
    /// `greeter.proto` and its transitive imports as a FileDescriptorSet —
    /// what `protoc --descriptor_set_out --include_imports` would produce.
    /// </summary>
    private static string InlineMarker()
    {
        var set = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(FileDescriptor fd)
        {
            if (!seen.Add(fd.Name)) return;
            // Imports first — the builder resolves cross-references between the
            // files it is handed, so a dependent before its dependency is the
            // one ordering that cannot be repaired later.
            foreach (var dep in fd.Dependencies) Add(dep);
            set.File.Add(FileDescriptorProto.Parser.ParseFrom(fd.SerializedData));
        }

        Add(GreeterReflection.Descriptor);

        var json = JsonSerializer.Serialize(new { base64 = Convert.ToBase64String(set.ToByteArray()) });
        return json;
    }

    private static async Task<GreeterHost> StartGreeterWithoutReflectionAsync()
    {
        var url = $"http://127.0.0.1:{GetFreeTcpPort()}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        // No AddGrpcReflection / MapGrpcReflectionService — that is the point.

        var app = builder.Build();
        app.MapGrpcService<GreeterService>();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new GreeterHost(app, url);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class GreeterHost(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
