// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.IntegrationTests.Services;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end proof for the active gRPC concurrent-stream probe (#399): drives
/// the real <see cref="BowireGrpcProtocol"/> (reflection discovery + N
/// concurrent server-streams via <c>SayHelloStream</c>) against a live Kestrel
/// HTTP/2 host running <see cref="GreeterService"/>. The stock test server sets
/// no per-client stream limit, so the probe should honestly report "no limit
/// observed at N".
/// </summary>
public sealed class GrpcConcurrentStreamProbeIntegrationTests
{
    static GrpcConcurrentStreamProbeIntegrationTests()
        => AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

    [Fact]
    public async Task ConcurrentStreams_LiveServerNoLimit_ReportsNoLimitAtN()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartGreeterHostAsync(ct);
        var protocol = host.CreateProtocol();
        var probe = new GrpcConcurrentStreamProbe();

        var findings = await OwaspApiSuite.RunActiveProtocolProbesAsync(
            host.BaseUrl,
            BuildRegistry(protocol),
            ["Authorization: Bearer x"],
            new ActiveScanOptions { Concurrency = 5 },
            TimeSpan.FromSeconds(30),
            ct);

        // Only the gRPC probe should produce a real verdict here (the mqtt
        // probes self-skip on a non-broker http target).
        var f = Assert.Single(findings, x => x.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API4-GRPC-CONCURRENT-STREAMS");
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Contains("N=5", f.Detail, StringComparison.Ordinal);
    }

    private static BowireProtocolRegistry BuildRegistry(BowireGrpcProtocol grpc)
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(grpc);
        return registry;
    }

    private static async Task<GreeterHost> StartGreeterHostAsync(CancellationToken ct)
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(o => o.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();

        var app = builder.Build();
        app.MapGrpcService<GreeterService>();
        app.MapGrpcReflectionService();

        await app.StartAsync(ct);
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

    private sealed class GreeterHost(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;

        public BowireGrpcProtocol CreateProtocol()
        {
            var p = new BowireGrpcProtocol();
            p.Initialize(app.Services);
            return p;
        }

        public async ValueTask DisposeAsync()
        {
            try { await app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            try { await app.DisposeAsync(); } catch { /* best-effort */ }
        }
    }
}
