// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Kuestenlogik.Bowire.Protocol.WebSocket;

namespace Kuestenlogik.Bowire.Protocol.WebSocket.Tests;

/// <summary>
/// Targeted tests for the gaps in <see cref="WebSocketBowireChannel"/>
/// (the private <c>ParseOutgoingFrame</c> branches the live-channel
/// tests can't reach without an actual WebSocket peer) and
/// <see cref="WebSocketEndpointDiscovery"/>'s embedded-routing path —
/// the EndpointDataSource branch that resolves <see cref="WebSocketEndpointAttribute"/>
/// metadata directly off routed endpoints.
/// </summary>
public sealed class WebSocketChannelAndDiscoveryGapsTests
{
    // ---- WebSocketBowireChannel.ParseOutgoingFrame (private) ---------
    // The JS layer wraps sends as { type, text|binary, base64|data }.
    // The send loop only consumes the parsed OutgoingFrame so all the
    // shape logic needs reflection-driven tests to pin.

    [Fact]
    public void ParseOutgoingFrame_Empty_Returns_Empty_Text_Frame()
    {
        var f = ParseOutgoingFrame("");
        Assert.False(f.IsBinary);
        Assert.Equal("", f.Text);
        Assert.Null(f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_Whitespace_Returns_Empty_Text_Frame()
    {
        var f = ParseOutgoingFrame("   ");
        Assert.False(f.IsBinary);
        Assert.Equal("", f.Text);
        Assert.Null(f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_RawString_NotJson_Sent_As_Text()
    {
        // curl-style raw send — no JSON envelope. The whole payload
        // becomes the text frame so the simplest "just send this"
        // case still works.
        var f = ParseOutgoingFrame("hello world");
        Assert.False(f.IsBinary);
        Assert.Equal("hello world", f.Text);
        Assert.Null(f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_TextEnvelope_UnwrapsTextField()
    {
        var f = ParseOutgoingFrame("""{"type":"text","text":"hi"}""");
        Assert.False(f.IsBinary);
        Assert.Equal("hi", f.Text);
        Assert.Null(f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_TextEnvelope_MissingTextField_FallsBack_To_RawJson()
    {
        // The envelope says text but there's no `text` key — the
        // helper falls through to the data-key check and ultimately
        // surfaces the raw JSON as a text frame, so the user sees
        // something on the wire instead of an empty frame.
        var raw = """{"type":"text"}""";
        var f = ParseOutgoingFrame(raw);
        Assert.False(f.IsBinary);
        Assert.Equal(raw, f.Text);
        Assert.Null(f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_DataKeyFallback_TreatedAsText()
    {
        // The "data" fallback fires only when type is present but
        // doesn't match text/binary — convenience for explicit
        // envelopes that nonetheless carry a `data` payload.
        var f = ParseOutgoingFrame("""{"type":"echo","data":"payload"}""");
        Assert.False(f.IsBinary);
        Assert.Equal("payload", f.Text);
        Assert.Null(f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_NoTypeKey_RawJson_KeptAsText()
    {
        // No `type` key → the helper falls through to the raw-text
        // branch with the whole JSON payload preserved.
        var raw = """{"data":"payload"}""";
        var f = ParseOutgoingFrame(raw);
        Assert.False(f.IsBinary);
        Assert.Equal(raw, f.Text);
    }

    [Fact]
    public void ParseOutgoingFrame_BinaryEnvelope_DecodesBase64()
    {
        // base64 "QUJD" → "ABC"
        var f = ParseOutgoingFrame("""{"type":"binary","base64":"QUJD"}""");
        Assert.True(f.IsBinary);
        Assert.Null(f.Text);
        Assert.NotNull(f.Bytes);
        Assert.Equal([0x41, 0x42, 0x43], f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_BinaryEnvelope_EmptyBase64_YieldsEmptyByteArray()
    {
        var f = ParseOutgoingFrame("""{"type":"binary","base64":""}""");
        Assert.True(f.IsBinary);
        Assert.NotNull(f.Bytes);
        Assert.Empty(f.Bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_NonObjectJson_TreatedAsRawText()
    {
        // A JSON array → not an envelope → kept verbatim as text.
        var raw = """["one","two"]""";
        var f = ParseOutgoingFrame(raw);
        Assert.False(f.IsBinary);
        Assert.Equal(raw, f.Text);
    }

    [Fact]
    public void ParseOutgoingFrame_MalformedJson_TreatedAsRawText()
    {
        // Not parseable JSON → the catch lands and the whole payload
        // becomes a text frame.
        var raw = """{"type":"text",broken""";
        var f = ParseOutgoingFrame(raw);
        Assert.False(f.IsBinary);
        Assert.Equal(raw, f.Text);
    }

    [Fact]
    public void ParseOutgoingFrame_UnknownEnvelopeType_FallsThroughToRawText()
    {
        // type="ping" isn't recognised — the helper falls through
        // every branch and returns the raw payload as a text frame.
        var raw = """{"type":"ping","text":"x"}""";
        var f = ParseOutgoingFrame(raw);
        Assert.False(f.IsBinary);
        Assert.Equal(raw, f.Text);
    }

    // ---- Discovery against EndpointDataSource ------------------------
    // The embedded-routing branch in WebSocketEndpointDiscovery looks
    // at every IEndpointConventionBuilder-registered route and picks
    // out ones that carry the [WebSocketEndpoint] metadata. Drive it
    // via a hand-rolled EndpointDataSource so we don't need a live
    // WebApplication.

    [Fact]
    public void Discovery_ServiceProvider_With_DataSource_Picks_Up_Routed_Endpoint()
    {
        var routeEndpoint = BuildRouteEndpoint(
            "/ws/embedded",
            new WebSocketEndpointAttribute("Embedded chat", "via routing"));

        var sp = BuildServiceProviderWith(routeEndpoint);

        var services = InvokeDiscover([], sp);

        var svc = Assert.Single(services);
        Assert.Equal("Embedded chat", svc.Name);
        var method = Assert.Single(svc.Methods);
        Assert.Equal("/ws/embedded", method.Name);
        Assert.Equal("Embedded chat", method.Summary);
        Assert.Equal("via routing", method.Description);
    }

    [Fact]
    public void Discovery_Routed_Endpoint_WithExplicit_Group_Honoured_From_EndpointMetadata()
    {
        // [WebSocketGroup] on the endpoint metadata overrides the
        // single-endpoint-DisplayName fallback — the group label
        // wins for the service name.
        var routeEndpoint = BuildRouteEndpoint(
            "/ws/embedded",
            new WebSocketEndpointAttribute("Friendly"),
            new WebSocketGroupAttribute("Realtime"));

        var sp = BuildServiceProviderWith(routeEndpoint);

        var services = InvokeDiscover([], sp);

        var svc = Assert.Single(services);
        Assert.Equal("Realtime", svc.Name);
    }

    [Fact]
    public void Discovery_Already_Registered_Path_Does_Not_Get_Re_Added_From_Routing()
    {
        // If the same path is already in the registered list, the
        // EndpointDataSource scan must skip it (duplicate-suppression
        // is keyed on path).
        var routeEndpoint = BuildRouteEndpoint(
            "/ws/echo",
            new WebSocketEndpointAttribute("FromRouting"));

        var sp = BuildServiceProviderWith(routeEndpoint);

        var registered = new List<WebSocketEndpointInfo>
        {
            new("/ws/echo", "FromRegistered", "via registry"),
        };

        var services = InvokeDiscover(registered, sp);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);
        // Registered entry wins — the routing duplicate is dropped.
        Assert.Equal("FromRegistered", method.Summary);
    }

    [Fact]
    public void Discovery_Routed_Endpoint_WithoutAttribute_Is_Ignored()
    {
        // Only routes carrying [WebSocketEndpoint] surface — plain
        // MapGet without the attribute is irrelevant to the WS plugin.
        var routeEndpoint = BuildRouteEndpoint(
            "/api/hello",
            // No WebSocketEndpointAttribute on the metadata.
            new object());

        var sp = BuildServiceProviderWith(routeEndpoint);

        Assert.Empty(InvokeDiscover([], sp));
    }

    [Fact]
    public void Discovery_DataSource_Throwing_Falls_Back_To_Registered_Endpoints()
    {
        // The discovery catch-all swallows EndpointDataSource failures
        // so embedded-mode brokenness can't block the registered list.
        var sp = BuildServiceProviderWith(new ThrowingEndpointDataSource());

        var registered = new List<WebSocketEndpointInfo>
        {
            new("/ws/echo", "Echo", "ok"),
        };

        var services = InvokeDiscover(registered, sp);
        var svc = Assert.Single(services);
        Assert.Equal("Echo", svc.Name);
    }

    // ---- helpers -----------------------------------------------------

    private static FrameView ParseOutgoingFrame(string json)
    {
        var method = typeof(WebSocketBowireChannel).GetMethod(
            "ParseOutgoingFrame", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseOutgoingFrame not found");
        var result = method.Invoke(null, [json])
            ?? throw new InvalidOperationException("ParseOutgoingFrame returned null");

        // OutgoingFrame is a private readonly record struct — read the
        // record-positional auto-properties via public-property reflection
        // (record structs emit public init-only getters for positional
        // parameters, even when the struct itself is private).
        var t = result.GetType();
        T Get<T>(string name) => (T)t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(result)!;
        T? GetN<T>(string name) where T : class =>
            (T?)t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(result);

        return new FrameView(Get<bool>("IsBinary"), GetN<string>("Text"), GetN<byte[]>("Bytes"));
    }

    private sealed record FrameView(bool IsBinary, string? Text, byte[]? Bytes);

    private static RouteEndpoint BuildRouteEndpoint(string pattern, params object[] metadata)
    {
        // RequestDelegate that never gets invoked — we only need the
        // metadata + RoutePattern for discovery scanning.
        RequestDelegate del = _ => Task.CompletedTask;
        return new RouteEndpoint(
            del,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            new EndpointMetadataCollection(metadata),
            displayName: pattern);
    }

    private static ServiceProvider BuildServiceProviderWith(params Endpoint[] endpoints)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<EndpointDataSource>(new InMemoryEndpointDataSource(endpoints));
        return sc.BuildServiceProvider();
    }

    private static ServiceProvider BuildServiceProviderWith(EndpointDataSource ds)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(ds);
        return sc.BuildServiceProvider();
    }

    private static List<Kuestenlogik.Bowire.Models.BowireServiceInfo> InvokeDiscover(
        IReadOnlyList<WebSocketEndpointInfo> registered, IServiceProvider? sp) =>
        // serverUrl param added by the self-origin-gate fix (#322); null
        // skips the gate so the local EndpointDataSource scan still runs
        // (embedded-mode contract). Reflection needs explicit args even
        // for optional defaults.
        (List<Kuestenlogik.Bowire.Models.BowireServiceInfo>)typeof(BowireWebSocketProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.WebSocket.WebSocketEndpointDiscovery")!
            .GetMethod("Discover", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [registered, sp, null])!;

    private sealed class InMemoryEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => NeverChange.Instance;
    }

    private sealed class ThrowingEndpointDataSource : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints =>
            throw new InvalidOperationException("intentional discovery failure");
        public override IChangeToken GetChangeToken() => NeverChange.Instance;
    }

    /// <summary>
    /// Minimal <see cref="IChangeToken"/> that never fires — Microsoft.
    /// Extensions.Primitives doesn't ship a public singleton (the
    /// internal <c>NullChangeToken</c> is sealed-internal), so we
    /// roll our own. The discovery scanner only checks
    /// <see cref="EndpointDataSource.Endpoints"/>; the change token
    /// is required by the contract but never consumed in these tests.
    /// </summary>
    private sealed class NeverChange : IChangeToken
    {
        public static readonly NeverChange Instance = new();
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            EmptyDisposable.Instance;

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
