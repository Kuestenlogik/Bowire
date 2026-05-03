// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.SocketIo;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the Socket.IO protocol plugin's identity, defensive guards on
/// <c>DiscoverAsync</c>, and the private message-schema helpers. The
/// connect-and-listen path needs a live Engine.IO peer (Socket.IO is an
/// HTTP-polling/websocket negotiation protocol, not a fixed wire format) —
/// that path is covered by the integration suite. Everything else is
/// exercised here without binding a port.
/// </summary>
public sealed class BowireSocketIoProtocolTests
{
    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireSocketIoProtocol();

        Assert.Equal("Socket.IO", protocol.Name);
        Assert.Equal("socketio", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_IBowireProtocol()
    {
        var protocol = new BowireSocketIoProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
    }

    [Fact]
    public void Initialize_Accepts_Null_Service_Provider()
    {
        var protocol = new BowireSocketIoProtocol();

        protocol.Initialize(null);
    }

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var protocol = new BowireSocketIoProtocol();

        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Whitespace_Url_Returns_Empty()
    {
        var protocol = new BowireSocketIoProtocol();

        var services = await protocol.DiscoverAsync(
            "   ", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Non_Http_Url_Returns_Empty()
    {
        var protocol = new BowireSocketIoProtocol();

        var services = await protocol.DiscoverAsync(
            "ws://example.com", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Unreachable_Server_Returns_Empty()
    {
        // 127.0.0.2:1 is not bound; SocketIOClient will time out / fail to
        // connect, which the plugin swallows in its catch and returns []. The
        // 5-second ConnectionTimeout in the plugin keeps this snappy.
        var protocol = new BowireSocketIoProtocol();

        var services = await protocol.DiscoverAsync(
            "http://127.0.0.2:1", showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_Unreachable_Server_Throws()
    {
        // InvokeAsync has no graceful "return InvokeResult with error" path
        // — the connect failure propagates so the caller surfaces it. Asserting
        // any exception (rather than a specific subclass) keeps the test
        // resilient across SocketIOClient versions.
        var protocol = new BowireSocketIoProtocol();

        await Assert.ThrowsAnyAsync<Exception>(() => protocol.InvokeAsync(
            "http://127.0.0.2:1",
            service: "Socket.IO", method: "socketio/emit",
            jsonMessages: ["{\"event\":\"ping\",\"data\":\"x\"}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvokeStreamAsync_Unreachable_Server_Throws_When_Iterated()
    {
        // InvokeStreamAsync is an `async IAsyncEnumerable<string>` — the
        // connect step happens on first iteration, so consumption is what
        // surfaces the failure. Bounding to one iteration keeps the test
        // snappy regardless of underlying retry behaviour.
        var protocol = new BowireSocketIoProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in protocol.InvokeStreamAsync(
                "http://127.0.0.2:1",
                service: "Socket.IO", method: "listen",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: null,
                ct: cts.Token))
            {
                // Should never yield — connect fails first.
                break;
            }
        });
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null_Because_SocketIo_Uses_The_Stream_API()
    {
        var protocol = new BowireSocketIoProtocol();

        var channel = await protocol.OpenChannelAsync(
            "http://example.com", service: "Socket.IO", method: "socketio/emit",
            showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    // ---- Private static helpers ----
    //
    // Build* methods are private because they're trivial factories; they're
    // only reached from a successful DiscoverAsync (which needs a live
    // Engine.IO peer). Reaching for reflection here keeps coverage on the
    // schema definitions without spinning up a Socket.IO server fixture.
    [Fact]
    public void BuildEmitInput_Has_Event_And_Data_Fields()
    {
        var msg = InvokeStaticHelper<BowireMessageInfo>("BuildEmitInput");

        Assert.Equal("SocketIoEmitRequest", msg.Name);
        Assert.Equal("socketio.EmitRequest", msg.FullName);
        Assert.Equal(2, msg.Fields.Count);

        var ev = msg.Fields[0];
        Assert.Equal("event", ev.Name);
        Assert.True(ev.Required);
        Assert.Equal("\"message\"", ev.Example);

        var data = msg.Fields[1];
        Assert.Equal("data", data.Name);
        Assert.False(data.Required);
    }

    [Fact]
    public void BuildListenInput_Has_Single_Event_Field()
    {
        var msg = InvokeStaticHelper<BowireMessageInfo>("BuildListenInput");

        Assert.Equal("SocketIoListenRequest", msg.Name);
        Assert.Equal("socketio.ListenRequest", msg.FullName);
        var field = Assert.Single(msg.Fields);
        Assert.Equal("event", field.Name);
        Assert.Equal("string", field.Type);
    }

    [Fact]
    public void BuildEmptyInput_Is_Empty_Message()
    {
        var msg = InvokeStaticHelper<BowireMessageInfo>("BuildEmptyInput");

        Assert.Equal("Empty", msg.Name);
        Assert.Equal("socketio.Empty", msg.FullName);
        Assert.Empty(msg.Fields);
    }

    [Fact]
    public void BuildEmptyOutput_Has_Event_And_Status_Fields()
    {
        var msg = InvokeStaticHelper<BowireMessageInfo>("BuildEmptyOutput");

        Assert.Equal("SocketIoEmitResponse", msg.Name);
        Assert.Equal(["event", "status"], msg.Fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void BuildEventOutput_Carries_Event_Data_Timestamp()
    {
        var msg = InvokeStaticHelper<BowireMessageInfo>("BuildEventOutput");

        Assert.Equal("SocketIoEvent", msg.Name);
        Assert.Equal(["event", "data", "timestamp"], msg.Fields.Select(f => f.Name).ToArray());
    }

    private static T InvokeStaticHelper<T>(string methodName)
    {
        var method = typeof(BowireSocketIoProtocol).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, null);
        Assert.NotNull(result);
        return (T)result!;
    }
}
