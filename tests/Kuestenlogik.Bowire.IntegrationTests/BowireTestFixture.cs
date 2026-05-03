// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.IntegrationTests.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

public sealed class BowireTestFixture : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // Ensure protocol plugin assemblies are loaded into the AppDomain
        // before BowireProtocolRegistry.Discover() scans for IBowireProtocol implementations.
        EnsureProtocolAssembliesLoaded();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Strip every default logging provider — the Windows EventLog provider
        // is added by ASP.NET's default logging setup and tries to open an
        // event source the first time Bowire calls LogWarning, which fails
        // without admin rights and crashes the request pipeline. Tests don't
        // need real logging output anyway.
        builder.Logging.ClearProviders();

        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.UseStaticFiles();
        app.MapGrpcService<Services.GreeterService>();
        app.MapGrpcReflectionService();
        app.MapHub<ChatHub>("/chathub");
        app.MapBowire("/bowire");

        await app.StartAsync(TestContext.Current.CancellationToken);
        Host = app;
        Client = app.GetTestClient();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureProtocolAssembliesLoaded()
    {
        // Force protocol plugin assemblies into the AppDomain so that
        // BowireProtocolRegistry.Discover() finds IBowireProtocol implementations.
        // typeof() triggers assembly loading by the CLR.
        _ = typeof(Kuestenlogik.Bowire.Protocol.Grpc.BowireGrpcProtocol).Assembly;
        _ = typeof(Kuestenlogik.Bowire.Protocol.SignalR.BowireSignalRProtocol).Assembly;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync(TestContext.Current.CancellationToken);
        Host.Dispose();
    }
}
