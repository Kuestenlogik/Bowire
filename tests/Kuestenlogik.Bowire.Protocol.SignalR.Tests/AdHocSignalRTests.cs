// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.SignalR.Tests;

/// <summary>
/// Separate-target mode (#510): the negotiate-probed ad-hoc hub surface.
/// Discovery gating (hint marker + negotiate handshake), the payload
/// parser behind the generic <c>invoke</c>/<c>stream</c> methods, and the
/// channel opt-out are all covered here. The live fixture is a tiny
/// Kestrel app faking <c>POST /hub/negotiate</c> — same pattern as the
/// SSE live-server tests, no external infrastructure.
/// </summary>
public sealed class AdHocSignalRTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _baseUrl;

    public AdHocSignalRTests()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel((Action<KestrelServerOptions>)(o => o.Listen(IPAddress.Loopback, 0)));
        builder.Logging.ClearProviders();

        _app = builder.Build();

        // Faithful negotiate shape (negotiateVersion=1 response).
        _app.MapPost("/hub/negotiate", (HttpContext ctx) =>
            Results.Json(new
            {
                negotiateVersion = 1,
                connectionId = "abc",
                connectionToken = "tok",
                availableTransports = Array.Empty<object>()
            }));
        // A path that is a plain HTTP endpoint, not a hub.
        _app.MapPost("/plain/negotiate", (HttpContext ctx) => Results.NotFound());

        _app.Start();

        var addressFeature = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        _baseUrl = addressFeature!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string WithMarker(string url) =>
        url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + BowireSignalRProtocol.AdHocHintMarker;

    // ---- discovery gating ----

    [Fact]
    public async Task Discover_Without_Hint_Marker_Returns_Empty_For_External_Url()
    {
        var protocol = new BowireSignalRProtocol();

        // Un-hinted fan-out: even a URL whose negotiate WOULD succeed must
        // not grow an ad-hoc service — the hint marker is the gate.
        var services = await protocol.DiscoverAsync($"{_baseUrl}/hub", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_With_Marker_And_Negotiating_Server_Yields_AdHoc_Service()
    {
        var protocol = new BowireSignalRProtocol();

        var services = await protocol.DiscoverAsync(WithMarker($"{_baseUrl}/hub"), showInternalServices: false, TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal(BowireSignalRProtocol.AdHocServiceName, svc.Name);
        // OriginUrl must be the clean URL — the marker is a discovery-only
        // side channel and must never leak into the invoke paths.
        Assert.Equal($"{_baseUrl}/hub", svc.OriginUrl);
        Assert.Equal("/hub", svc.Package);

        Assert.Collection(svc.Methods,
            invoke =>
            {
                Assert.Equal("invoke", invoke.Name);
                Assert.Equal("Unary", invoke.MethodType);
                Assert.False(invoke.ServerStreaming);
            },
            stream =>
            {
                Assert.Equal("stream", stream.Name);
                Assert.Equal("ServerStreaming", stream.MethodType);
                Assert.True(stream.ServerStreaming);
            });
    }

    [Fact]
    public async Task Discover_With_Marker_But_Non_Hub_Server_Returns_Empty()
    {
        var protocol = new BowireSignalRProtocol();

        var services = await protocol.DiscoverAsync(WithMarker($"{_baseUrl}/plain"), showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_With_Marker_But_No_Server_Returns_Empty()
    {
        var protocol = new BowireSignalRProtocol();

        // Reserved port with nothing listening — the probe must swallow
        // the connection failure and report "no services", not throw.
        var services = await protocol.DiscoverAsync(WithMarker("http://127.0.0.1:1/hub"), showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    // ---- ad-hoc payload parser ----

    [Fact]
    public void ParseAdHocPayload_Extracts_Method_And_Typed_Args()
    {
        var (method, args, error) = BowireSignalRProtocol.ParseAdHocPayload(
            ["""{"method": "SendMessage", "args": ["alice", "hello"]}"""]);

        Assert.Null(error);
        Assert.Equal("SendMessage", method);
        Assert.Equal(2, args.Length);
        Assert.Equal("alice", args[0]);
        Assert.Equal("hello", args[1]);
    }

    [Fact]
    public void ParseAdHocPayload_Reparses_Form_Mode_String_Entries()
    {
        // The form pane's repeated-string field JSON-encodes every entry as
        // a string — typed numbers/booleans/objects must reach the hub with
        // their real types, bare words stay strings.
        var (method, args, error) = BowireSignalRProtocol.ParseAdHocPayload(
            ["""{"method": "M", "args": ["42", "true", "{\"x\":1}", "plain text"]}"""]);

        Assert.Null(error);
        Assert.Equal("M", method);
        // JsonElementToArg's number arm is a long/double ternary whose
        // common type is double — SignalR's serializer round-trips it
        // to the hub parameter type either way.
        Assert.Equal(42d, args[0]);
        Assert.True(args[1] is true);
        Assert.IsType<System.Text.Json.JsonElement>(args[2]);
        Assert.Equal("plain text", args[3]);
    }

    [Fact]
    public void ParseAdHocPayload_Missing_Method_Is_An_Error()
    {
        var (method, _, error) = BowireSignalRProtocol.ParseAdHocPayload(
            ["""{"args": ["x"]}"""]);

        Assert.Null(method);
        Assert.NotNull(error);
        Assert.Contains("method", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAdHocPayload_Non_Array_Args_Is_An_Error()
    {
        var (_, _, error) = BowireSignalRProtocol.ParseAdHocPayload(
            ["""{"method": "M", "args": "not-an-array"}"""]);

        Assert.NotNull(error);
        Assert.Contains("array", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void ParseAdHocPayload_Rejects_Malformed_Payloads(string payload)
    {
        var (_, _, error) = BowireSignalRProtocol.ParseAdHocPayload([payload]);

        Assert.NotNull(error);
    }

    [Fact]
    public void ParseAdHocPayload_Empty_Message_List_Is_An_Error()
    {
        var (_, _, error) = BowireSignalRProtocol.ParseAdHocPayload([]);

        Assert.NotNull(error);
    }

    [Fact]
    public void ParseAdHocPayload_Without_Args_Yields_Zero_Arguments()
    {
        var (method, args, error) = BowireSignalRProtocol.ParseAdHocPayload(
            ["""{"method": "Ping"}"""]);

        Assert.Null(error);
        Assert.Equal("Ping", method);
        Assert.Empty(args);
    }

    // ---- invoke-path behaviour ----

    [Fact]
    public async Task InvokeAsync_On_AdHoc_Service_Surfaces_Payload_Errors_As_Failed_Result()
    {
        var protocol = new BowireSignalRProtocol();

        // Malformed payload must fail fast with the usage hint — before
        // any connection attempt (the URL points nowhere).
        var result = await protocol.InvokeAsync(
            "http://127.0.0.1:1/hub", BowireSignalRProtocol.AdHocServiceName, "invoke",
            ["not json"], showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("method", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenChannelAsync_On_AdHoc_Service_Returns_Null()
    {
        var protocol = new BowireSignalRProtocol();

        // The ad-hoc surface routes through the invoke/stream APIs — a
        // channel would invoke the literal "invoke" on the hub.
        var channel = await protocol.OpenChannelAsync(
            "http://127.0.0.1:1/hub", BowireSignalRProtocol.AdHocServiceName, "invoke",
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }
}
