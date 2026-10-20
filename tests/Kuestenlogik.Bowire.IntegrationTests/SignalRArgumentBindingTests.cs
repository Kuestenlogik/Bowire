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
/// loopback Kestrel: named form fields reach the parameter of that name,
/// and the messages of a client stream reach the stream parameter.
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

    [Fact]
    public async Task Client_Stream_Messages_Reach_The_Stream_Parameter()
    {
        await using var host = await StartHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/args",
            method: nameof(ArgsHub.Sum),
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            Assert.True(channel!.IsClientStreaming);
            Assert.False(channel.IsServerStreaming);

            // Sum(IAsyncEnumerable<int> numbers) has ONE parameter, the
            // stream; each message sent is one item on it.
            await channel.SendAsync("1", TestContext.Current.CancellationToken);
            await channel.SendAsync("2", TestContext.Current.CancellationToken);
            await channel.SendAsync("3", TestContext.Current.CancellationToken);
            await channel.CloseAsync(TestContext.Current.CancellationToken);

            var responses = new List<string>();
            await foreach (var r in channel.ReadResponsesAsync(TestContext.Current.CancellationToken))
                responses.Add(r);

            Assert.Equal("6", Assert.Single(responses));
        }
    }

    [Fact]
    public async Task Duplex_Stream_Messages_Reach_The_Stream_Parameter()
    {
        await using var host = await StartHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/args",
            method: nameof(ArgsHub.Doubled),
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            Assert.True(channel!.IsClientStreaming);
            Assert.True(channel.IsServerStreaming);

            await channel.SendAsync("4", TestContext.Current.CancellationToken);
            await channel.SendAsync("5", TestContext.Current.CancellationToken);
            await channel.CloseAsync(TestContext.Current.CancellationToken);

            var responses = new List<string>();
            await foreach (var r in channel.ReadResponsesAsync(TestContext.Current.CancellationToken))
                responses.Add(r);

            Assert.Equal(["8", "10"], responses);
        }
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

    public async Task<int> Sum(IAsyncEnumerable<int> numbers)
    {
        var total = 0;
        await foreach (var n in numbers)
            total += n;
        return total;
    }

    public async IAsyncEnumerable<int> Doubled(IAsyncEnumerable<int> numbers)
    {
        await foreach (var n in numbers)
            yield return n * 2;
    }
}
#pragma warning restore CA1822
