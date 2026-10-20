// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.SignalR;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// How the SignalR plugin turns what the workbench sends into the
/// arguments a hub method is bound with, checked against a real hub on
/// loopback Kestrel: named form fields reach the parameter of that name.
/// </summary>
public sealed class SignalRArgumentBindingTests
{
    [Fact]
    public async Task Form_Fields_Bind_By_Parameter_Name_Not_By_Json_Order()
    {
        await using var host = await StartHostAsync();
        var protocol = host.CreateProtocol();

        // A JSON object is unordered: the fields name the parameters, so
        // {"label":…,"count":…} is Describe(count: 3, label: "x").
        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "/args",
            method: nameof(ArgsHub.Describe),
            jsonMessages: ["""{"label":"x","count":3}"""],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Contains("3:x", result.Response!, StringComparison.Ordinal);
    }

    private static async Task<ArgsHubHost> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.MapHub<ArgsHub>("/args");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ArgsHubHost(app, LoopbackHost.BaseAddress(app.Services));
    }

    private sealed class ArgsHubHost(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;

        public BowireSignalRProtocol CreateProtocol()
        {
            var p = new BowireSignalRProtocol();
            p.Initialize(app.Services);
            return p;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }
}

/// <summary>Hub fixture for <see cref="SignalRArgumentBindingTests"/>.</summary>
// CA1822: SignalR binds hub methods only among public instance methods.
#pragma warning disable CA1822
internal sealed class ArgsHub : Hub
{
    public string Describe(int count, string label) => $"{count}:{label}";
}
#pragma warning restore CA1822
