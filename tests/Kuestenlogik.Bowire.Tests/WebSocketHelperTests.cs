// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.WebSocket;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Direct coverage for the synchronous helpers inside the WebSocket
/// plugin — URL parsing, sub-protocol header extraction, ad-hoc service
/// builder, plugin metadata, registration entry-points, and the
/// discovery-side <c>ResolveGroupName</c> branch logic. Reaches the
/// pieces the channel-based connect path can't exercise without a live
/// WebSocket peer.
/// </summary>
public sealed class WebSocketHelperTests : IDisposable
{
    public WebSocketHelperTests() => BowireWebSocketProtocol.ClearRegisteredEndpoints();
    public void Dispose()
    {
        BowireWebSocketProtocol.ClearRegisteredEndpoints();
        GC.SuppressFinalize(this);
    }

    // ---- Identity / metadata surface ----

    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireWebSocketProtocol();

        Assert.Equal("WebSocket", protocol.Name);
        Assert.Equal("websocket", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void SubProtocolMetadataKey_Is_Public_Constant()
    {
        // Documented public surface — pinning the literal so renames
        // surface as a build break across plugin consumers (graphql-
        // transport-ws, sample apps, …) instead of a silent header drop.
        Assert.Equal("X-Bowire-WebSocket-Subprotocol", BowireWebSocketProtocol.SubProtocolMetadataKey);
    }

    [Fact]
    public void Settings_Exposes_Auto_Json_And_Binary_Hex_Toggles()
    {
        var protocol = new BowireWebSocketProtocol();

        var settings = protocol.Settings;

        Assert.Equal(2, settings.Count);
        Assert.Contains(settings, s => s.Key == "autoInterpretJson");
        Assert.Contains(settings, s => s.Key == "showBinaryAsHex");
    }

    [Fact]
    public void Initialize_Accepts_Null_ServiceProvider()
    {
        var protocol = new BowireWebSocketProtocol();

        protocol.Initialize(null);
    }

    // ---- Static registration ----

    [Fact]
    public void RegisterEndpoint_Throws_On_Null()
    {
        Assert.Throws<ArgumentNullException>(() => BowireWebSocketProtocol.RegisterEndpoint(null!));
    }

    [Fact]
    public void RegisterEndpoint_Adds_To_Internal_List()
    {
        BowireWebSocketProtocol.RegisterEndpoint(new WebSocketEndpointInfo("/ws/a", "A", "First"));
        BowireWebSocketProtocol.RegisterEndpoint(new WebSocketEndpointInfo("/ws/b", "B", "Second"));

        Assert.Equal(2, BowireWebSocketProtocol.RegisteredEndpoints.Count);
        Assert.Equal("/ws/a", BowireWebSocketProtocol.RegisteredEndpoints[0].Path);
        Assert.Equal("/ws/b", BowireWebSocketProtocol.RegisteredEndpoints[1].Path);
    }

    [Fact]
    public void ClearRegisteredEndpoints_Empties_The_List()
    {
        BowireWebSocketProtocol.RegisterEndpoint(new WebSocketEndpointInfo("/ws/x", null, null));

        BowireWebSocketProtocol.ClearRegisteredEndpoints();

        Assert.Empty(BowireWebSocketProtocol.RegisteredEndpoints);
    }

    // ---- InvokeAsync / InvokeStreamAsync — both intentionally inert ----

    [Fact]
    public async Task InvokeAsync_Returns_Channel_Only_Hint_Status()
    {
        var protocol = new BowireWebSocketProtocol();

        var result = await protocol.InvokeAsync(
            "ws://example.com", "WebSocket", "/ws/echo",
            ["{}"], showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(result.Response);
        Assert.Equal(0, result.DurationMs);
        Assert.Contains("channel-only", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_Nothing()
    {
        var protocol = new BowireWebSocketProtocol();

        var collected = new List<string>();
        await foreach (var msg in protocol.InvokeStreamAsync(
            "ws://example.com", "WebSocket", "/ws/echo",
            ["{}"], showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            collected.Add(msg);
        }

        Assert.Empty(collected);
    }

    // ---- ExtractSubProtocols (private static) ----

    [Fact]
    public void ExtractSubProtocols_Null_Metadata_Returns_Both_Null()
    {
        var (headers, protos) = InvokeExtractSubProtocols(null);

        Assert.Null(headers);
        Assert.Null(protos);
    }

    [Fact]
    public void ExtractSubProtocols_No_Magic_Key_Returns_Original_Headers_And_Null_Protos()
    {
        var meta = new Dictionary<string, string> { ["Authorization"] = "Bearer x" };

        var (headers, protos) = InvokeExtractSubProtocols(meta);

        Assert.NotNull(headers);
        Assert.Equal("Bearer x", headers!["Authorization"]);
        Assert.Null(protos);
    }

    [Fact]
    public void ExtractSubProtocols_Strips_Magic_Key_And_Splits_Csv()
    {
        var meta = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer x",
            [BowireWebSocketProtocol.SubProtocolMetadataKey] = "graphql-transport-ws, mqttv5"
        };

        var (headers, protos) = InvokeExtractSubProtocols(meta);

        Assert.NotNull(headers);
        Assert.False(headers!.ContainsKey(BowireWebSocketProtocol.SubProtocolMetadataKey));
        Assert.Single(headers);
        Assert.Equal("Bearer x", headers["Authorization"]);

        Assert.NotNull(protos);
        Assert.Equal(["graphql-transport-ws", "mqttv5"], protos!.ToArray());
    }

    [Fact]
    public void ExtractSubProtocols_Case_Insensitive_Lookup()
    {
        // Lowercase variant — same semantic, the helper does a case-
        // insensitive match before stripping.
        var meta = new Dictionary<string, string>
        {
            ["x-bowire-websocket-subprotocol"] = "graphql-transport-ws"
        };

        var (headers, protos) = InvokeExtractSubProtocols(meta);

        Assert.NotNull(headers);
        Assert.Empty(headers!);
        Assert.NotNull(protos);
        Assert.Equal(["graphql-transport-ws"], protos!.ToArray());
    }

    [Fact]
    public void ExtractSubProtocols_Empty_Value_Returns_Null_Protos()
    {
        var meta = new Dictionary<string, string>
        {
            [BowireWebSocketProtocol.SubProtocolMetadataKey] = ""
        };

        var (headers, protos) = InvokeExtractSubProtocols(meta);

        Assert.NotNull(headers);
        Assert.Empty(headers!);
        Assert.Null(protos);
    }

    // ---- ExtractPath (private static) ----

    [Fact]
    public void ExtractPath_Absolute_Url_Returns_PathAndQuery()
    {
        Assert.Equal("/ws/echo?room=42",
            InvokeExtractPath("ws://example.com:8080/ws/echo?room=42"));
    }

    [Fact]
    public void ExtractPath_Invalid_Url_Returns_Slash()
    {
        // The helper's catch-all returns "/" so the ad-hoc path stays
        // valid even when the user pasted nonsense.
        Assert.Equal("/", InvokeExtractPath("not-a-url"));
    }

    // ---- IsWebSocketUrl (private static) ----

    [Theory]
    [InlineData("ws://example.com")]
    [InlineData("WS://EXAMPLE.COM")]
    [InlineData("wss://example.com:8080/path")]
    [InlineData("WsS://x")]
    public void IsWebSocketUrl_Recognises_Ws_And_Wss_Case_Insensitive(string url)
    {
        Assert.True(InvokeIsWebSocketUrl(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("mqtt://broker:1883")]
    public void IsWebSocketUrl_Rejects_Other_Schemes(string url)
    {
        Assert.False(InvokeIsWebSocketUrl(url));
    }

    // ---- ResolveUri (private static) ----

    [Fact]
    public void ResolveUri_Absolute_WebSocket_Method_Wins()
    {
        var uri = InvokeResolveUri("http://ignored", "ws://other.example/ws/echo");

        Assert.NotNull(uri);
        Assert.Equal("ws://other.example/ws/echo", uri!.ToString());
    }

    [Fact]
    public void ResolveUri_Empty_ServerUrl_Returns_Null_When_Method_Not_Absolute()
    {
        Assert.Null(InvokeResolveUri("", "/ws/echo"));
    }

    [Fact]
    public void ResolveUri_Http_Base_Becomes_Ws_Base()
    {
        var uri = InvokeResolveUri("http://localhost:5000", "/ws/echo");

        Assert.NotNull(uri);
        Assert.Equal("ws://localhost:5000/ws/echo", uri!.ToString());
    }

    [Fact]
    public void ResolveUri_Https_Base_Becomes_Wss_Base()
    {
        var uri = InvokeResolveUri("https://localhost:5001", "/ws/echo");

        Assert.NotNull(uri);
        Assert.Equal("wss://localhost:5001/ws/echo", uri!.ToString());
    }

    [Fact]
    public void ResolveUri_Adhoc_Case_BasePath_Equals_Method_Returns_Base()
    {
        // The standalone ad-hoc discovery stores `method = baseUri.PathAndQuery`
        // — so when the base URL already has the same path, the helper
        // returns the base URL verbatim instead of joining (and accidentally
        // doubling the path).
        var uri = InvokeResolveUri("ws://example.com/ws/echo", "/ws/echo");

        Assert.NotNull(uri);
        Assert.Equal("ws://example.com/ws/echo", uri!.ToString());
    }

    [Fact]
    public void ResolveUri_Slash_Method_Returns_Base()
    {
        var uri = InvokeResolveUri("ws://example.com/anything", "/");

        Assert.NotNull(uri);
        Assert.Equal("ws://example.com/anything", uri!.ToString());
    }

    [Fact]
    public void ResolveUri_Malformed_Base_Returns_Null()
    {
        Assert.Null(InvokeResolveUri("not a url", "/path"));
    }

    // ---- BuildAdHocService (private static) ----

    [Fact]
    public void BuildAdHocService_Single_Method_Carries_Path_From_Url()
    {
        var svc = InvokeBuildAdHocService("ws://example.com:8080/ws/echo?room=42");

        Assert.Equal("WebSocket", svc.Name);
        Assert.Equal("websocket", svc.Source);
        var method = Assert.Single(svc.Methods);
        Assert.Equal("/ws/echo?room=42", method.Name);
        Assert.Equal("Duplex", method.MethodType);
        Assert.True(method.ClientStreaming);
        Assert.True(method.ServerStreaming);
        Assert.Contains("Connect to ws://example.com", method.Summary, StringComparison.Ordinal);
    }

    // ---- WebSocketEndpointDiscovery static surface ----

    [Fact]
    public void Discovery_Empty_Registered_With_Null_Provider_Returns_Empty()
    {
        var services = InvokeDiscover([], null);

        Assert.Empty(services);
    }

    [Fact]
    public void Discovery_Single_Registered_Endpoint_Promotes_DisplayName_To_Service_Name()
    {
        // The service-name fallback for a single endpoint surfaces the
        // user-friendly DisplayName instead of the generic "WebSocket"
        // umbrella, so the sidebar reads `Chat → /ws/chat` when only one
        // endpoint is wired up.
        var services = InvokeDiscover(
            [new WebSocketEndpointInfo("/ws/chat", "Chat room", "Group chat")],
            null);

        var svc = Assert.Single(services);
        Assert.Equal("Chat room", svc.Name);
    }

    [Fact]
    public void Discovery_Multiple_Endpoints_Default_To_WebSocket_Umbrella()
    {
        var services = InvokeDiscover(
            [
                new WebSocketEndpointInfo("/ws/a", "A", null),
                new WebSocketEndpointInfo("/ws/b", "B", null)
            ],
            null);

        var svc = Assert.Single(services);
        Assert.Equal("WebSocket", svc.Name);
        Assert.Equal(2, svc.Methods.Count);
    }

    [Fact]
    public void Discovery_Endpoints_With_Explicit_Group_Are_Partitioned()
    {
        var services = InvokeDiscover(
            [
                new WebSocketEndpointInfo("/ws/chat", "Chat", null, "Realtime"),
                new WebSocketEndpointInfo("/ws/feed", "Feed", null, "Realtime"),
                new WebSocketEndpointInfo("/ws/admin", "Admin", null, "Ops")
            ],
            null);

        Assert.Equal(2, services.Count);
        var realtime = services.Single(s => s.Name == "Realtime");
        var ops = services.Single(s => s.Name == "Ops");
        Assert.Equal(2, realtime.Methods.Count);
        Assert.Single(ops.Methods);
    }

    [Fact]
    public void Discovery_Method_Inherits_HttpMethod_Get_For_Mock_Replay()
    {
        // The mock-server matcher keys WebSocket steps the same way it
        // keys REST: (httpVerb, httpPath). Discovery has to surface "GET"
        // explicitly so a recorded WS upgrade replays cleanly.
        var services = InvokeDiscover(
            [new WebSocketEndpointInfo("/ws/echo", "Echo", null)],
            null);

        var method = services.Single().Methods.Single();
        Assert.Equal("GET", method.HttpMethod);
        Assert.Equal("/ws/echo", method.HttpPath);
    }

    // ---- Reflection plumbing ----

    private static (Dictionary<string, string>? Headers, IReadOnlyList<string>? Protos)
        InvokeExtractSubProtocols(Dictionary<string, string>? metadata)
    {
        var method = typeof(BowireWebSocketProtocol).GetMethod(
            "ExtractSubProtocols", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, [metadata])!;
        var tuple = (System.Runtime.CompilerServices.ITuple)result;
        return ((Dictionary<string, string>?)tuple[0], (IReadOnlyList<string>?)tuple[1]);
    }

    private static string InvokeExtractPath(string url) =>
        (string)typeof(BowireWebSocketProtocol)
            .GetMethod("ExtractPath", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [url])!;

    private static bool InvokeIsWebSocketUrl(string url) =>
        (bool)typeof(BowireWebSocketProtocol)
            .GetMethod("IsWebSocketUrl", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [url])!;

    private static Uri? InvokeResolveUri(string serverUrl, string method) =>
        (Uri?)typeof(BowireWebSocketProtocol)
            .GetMethod("ResolveUri", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [serverUrl, method]);

    private static BowireServiceInfo InvokeBuildAdHocService(string serverUrl) =>
        (BowireServiceInfo)typeof(BowireWebSocketProtocol)
            .GetMethod("BuildAdHocService", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [serverUrl])!;

    private static List<BowireServiceInfo> InvokeDiscover(
        IReadOnlyList<WebSocketEndpointInfo> registered, IServiceProvider? sp) =>
        (List<BowireServiceInfo>)typeof(BowireWebSocketProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.WebSocket.WebSocketEndpointDiscovery")!
            .GetMethod("Discover", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [registered, sp])!;
}
