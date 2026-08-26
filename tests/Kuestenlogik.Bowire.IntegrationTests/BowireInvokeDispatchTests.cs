// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// <c>POST /api/invoke</c> — everything that happens to a request between the
/// workbench and the protocol plugin.
/// </summary>
/// <remarks>
/// <para>
/// The handler does a surprising amount before dispatch: it splits a
/// <c>hint@url</c>, moves query-string API keys off the metadata dictionary and
/// onto the URL, and smuggles a binary body through metadata under reserved
/// keys. Each of those rewrites what the plugin sees, and none of them is
/// visible in the response — a mistake here sends a well-formed request to
/// almost the right place and reports success.
/// </para>
/// <para>
/// So the assertions are on what the plugin was handed, captured by a stub
/// protocol registered in place of the real registry. That registry is a
/// process-wide static, which is why this suite runs in the serialised
/// <c>BowireProtocolRegistry</c> collection — a suite in another collection
/// installing its own registry mid-test would silently dispatch these calls
/// to a different plugin.
/// </para>
/// </remarks>
[Collection("BowireProtocolRegistry")]
public sealed class BowireInvokeDispatchTests : IDisposable
{
    private readonly RecordingProtocol _plugin = new();

    public void Dispose() => BowireEndpointHelpers.ResetRegistry();

    /// <summary>A protocol plugin that answers OK and remembers the call.</summary>
    private sealed class RecordingProtocol : IBowireProtocol
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string IconSvg => "<svg/>";

        public string? ServerUrl { get; private set; }
        public string? Service { get; private set; }
        public string? Method { get; private set; }
        public List<string>? Messages { get; private set; }
        public Dictionary<string, string>? Metadata { get; private set; }
        public Exception? Throws { get; set; }

        /// <summary>Frames the stream yields; empty by default.</summary>
        public List<string> Frames { get; } = [];

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            ServerUrl = serverUrl;
            Service = service;
            Method = method;
            Messages = jsonMessages;
            Metadata = metadata;
            if (Throws is not null) throw Throws;
            return Task.FromResult(new InvokeResult(
                """{"ok":true}""", 12, "OK", new Dictionary<string, string> { ["x-stub"] = "1" }));
        }

#pragma warning disable CS1998 // The frames are in memory; nothing to await.
        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ServerUrl = serverUrl;
            Service = service;
            Method = method;
            Messages = jsonMessages;
            Metadata = metadata;
            if (Throws is not null) throw Throws;
            foreach (var frame in Frames) yield return frame;
        }
#pragma warning restore CS1998

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
    }

    private async Task<IHost> BuildHost(bool withPlugin = true)
    {
        var registry = new BowireProtocolRegistry();
        if (withPlugin) registry.Register(_plugin);
        BowireEndpointHelpers.SetRegistry(registry);

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e =>
                           e.MapBowireInvokeEndpoints(new BowireOptions(), basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Invoke(
        IHost host, string json, string? serverUrlQuery = null)
    {
        var path = serverUrlQuery is null
            ? "/api/invoke"
            : $"/api/invoke?serverUrl={Uri.EscapeDataString(serverUrlQuery)}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await host.GetTestClient().PostAsync(
            new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    private const string Minimal = """
        {"protocol":"stub","service":"orders.v1.OrderService","method":"GetOrder","messages":["{}"]}
        """;

    // ---- the normal path ----

    [Fact]
    public async Task A_Call_Reaches_The_Plugin_With_What_The_Workbench_Sent()
    {
        using var host = await BuildHost();

        var (status, body) = await Invoke(host, Minimal, "https://api.example.com");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("https://api.example.com", _plugin.ServerUrl);
        Assert.Equal("orders.v1.OrderService", _plugin.Service);
        Assert.Equal("GetOrder", _plugin.Method);
        Assert.Equal("OK", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_Response_Carries_The_Fields_The_Workbench_Renders()
    {
        // Response body, duration and status are three separate panes in the
        // UI; a renamed field empties one of them silently.
        using var host = await BuildHost();

        var (_, body) = await Invoke(host, Minimal, "https://api.example.com");

        Assert.True(body.TryGetProperty("response", out _));
        Assert.True(body.TryGetProperty("duration_ms", out _));
        Assert.True(body.TryGetProperty("status", out _));
        Assert.True(body.TryGetProperty("metadata", out _));
    }

    [Fact]
    public async Task A_Call_With_No_Messages_Sends_An_Empty_Object()
    {
        // Every plugin expects at least one message; defaulting to "{}" is
        // what keeps a parameterless method callable from the UI.
        using var host = await BuildHost();

        await Invoke(host,
            """{"protocol":"stub","service":"S","method":"M"}""", "https://api.example.com");

        Assert.Equal(["{}"], _plugin.Messages);
    }

    // ---- the hint@url form ----

    [Fact]
    public async Task A_Protocol_Hint_Is_Stripped_Off_The_Url_Before_The_Plugin_Sees_It()
    {
        // The plugin gets a URL it can connect to, not the workbench's
        // routing decoration.
        using var host = await BuildHost();

        await Invoke(host, Minimal, "stub@https://api.example.com");

        Assert.Equal("https://api.example.com", _plugin.ServerUrl);
    }

    [Fact]
    public async Task A_Transport_Variant_Hint_Reaches_The_Plugin_As_Metadata()
    {
        // grpcweb@ pins the gRPC plugin and flips it to gRPC-Web. The flip
        // travels as a reserved metadata header, which is the only way the
        // plugin can tell the two apart.
        using var host = await BuildHost();

        await Invoke(host,
            """{"protocol":"stub","service":"S","method":"M","messages":["{}"]}""",
            "grpcweb@https://api.example.com");

        // The hint pins "grpc", which is not loaded here — dispatch falls back
        // to the only registered plugin, and the transport bit is still on the
        // metadata it receives.
        Assert.NotNull(_plugin.Metadata);
        Assert.Contains(_plugin.Metadata, kv => kv.Key.Contains("Transport", StringComparison.OrdinalIgnoreCase));
    }

    // ---- query-string API keys ----

    [Fact]
    public async Task A_Query_Auth_Entry_Moves_Onto_The_Url_And_Off_The_Headers()
    {
        // The apikey helper marks "this goes on the URL" with a magic prefix
        // because metadata is the only channel it has. Leaving the entry in
        // the dictionary would send the key as a header as well — the same
        // secret to the same server twice, in a place nobody expects it.
        using var host = await BuildHost();
        var body = """
            {"protocol":"stub","service":"S","method":"M","messages":["{}"],
             "metadata":{"__bowireQuery__api_key":"s3cret","X-Real-Header":"kept"}}
            """;

        await Invoke(host, body, "https://api.example.com/v1");

        Assert.Contains("api_key=s3cret", _plugin.ServerUrl!, StringComparison.Ordinal);
        Assert.NotNull(_plugin.Metadata);
        Assert.DoesNotContain(_plugin.Metadata, kv => kv.Key.StartsWith("__bowireQuery__", StringComparison.Ordinal));
        Assert.Equal("kept", _plugin.Metadata["X-Real-Header"]);
    }

    // ---- binary bodies (#290) ----

    [Fact]
    public async Task A_Binary_Body_Travels_As_Reserved_Metadata_Keys()
    {
        // Rather than a new parameter on 50-odd InvokeAsync implementations.
        // The REST plugin reads these three; everyone else ignores them.
        using var host = await BuildHost();
        var body = """
            {"protocol":"stub","service":"S","method":"M","messages":["{}"],
             "bodyBinary":"aGVsbG8=","bodyBinaryContentType":"image/png","bodyBinaryName":"logo.png"}
            """;

        var (status, response) = await Invoke(host, body, "https://api.example.com");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(_plugin.Metadata is not null, $"plugin saw no metadata; response was {response}");
        Assert.Equal("aGVsbG8=", _plugin.Metadata!["X-Bowire-Body-Binary"]);
        Assert.Equal("image/png", _plugin.Metadata["X-Bowire-Body-Binary-Content-Type"]);
        Assert.Equal("logo.png", _plugin.Metadata["X-Bowire-Body-Binary-Name"]);
    }

    [Fact]
    public async Task A_Binary_Body_Without_A_Content_Type_Falls_Back_To_Octet_Stream()
    {
        using var host = await BuildHost();
        var body = """
            {"protocol":"stub","service":"S","method":"M","messages":["{}"],"bodyBinary":"aGVsbG8="}
            """;

        var (status, response) = await Invoke(host, body, "https://api.example.com");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(_plugin.Metadata is not null, $"plugin saw no metadata; response was {response}");
        Assert.Equal("application/octet-stream", _plugin.Metadata!["X-Bowire-Body-Binary-Content-Type"]);
        Assert.DoesNotContain("X-Bowire-Body-Binary-Name", _plugin.Metadata.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task No_Binary_Body_Means_No_Reserved_Keys_At_All()
    {
        // The ordinary case has to stay clean: a plugin that sniffs for these
        // keys must not find empty ones.
        using var host = await BuildHost();

        await Invoke(host, Minimal, "https://api.example.com");

        Assert.True(_plugin.Metadata is null
            || !_plugin.Metadata.Keys.Any(k => k.StartsWith("X-Bowire-Body-Binary", StringComparison.Ordinal)));
    }

    // ---- refusals ----

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_A_400_Naming_The_Parse_Error()
    {
        using var host = await BuildHost();

        var (status, body) = await Invoke(host, "{ not json", "https://api.example.com");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("urn:bowire:invalid-input", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_Empty_Body_Says_Which_Fields_It_Wanted()
    {
        // The message doubles as the endpoint's documentation for anyone
        // driving it by hand.
        using var host = await BuildHost();

        var (status, body) = await Invoke(host, "null", "https://api.example.com");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("protocol", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_No_Plugin_Loaded_At_All_The_Refusal_Says_What_To_Install()
    {
        // A fresh install with no protocol packages. 502 rather than 404: the
        // request was fine, Bowire just has nothing to dispatch it with.
        using var host = await BuildHost(withPlugin: false);

        var (status, body) = await Invoke(host, Minimal, "https://api.example.com");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("urn:bowire:invoke:no-plugin", body.GetProperty("type").GetString());
        Assert.Contains("Install", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcoding_Without_The_Rest_Plugin_Names_The_Package_To_Install()
    {
        // 501 rather than 500: the feature is not broken, it is not installed.
        using var host = await BuildHost();
        var body = """
            {"protocol":"stub","service":"S","method":"M","messages":["{}"],
             "transcodedMethod":{"httpMethod":"GET","httpPath":"/v1/orders/{id}"}}
            """;

        var (status, problem) = await Invoke(host, body, "https://api.example.com");

        Assert.Equal(HttpStatusCode.NotImplemented, status);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Rest",
            problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Plugin_That_Throws_Becomes_An_Upstream_Error_With_Its_Message()
    {
        // Whatever a third-party plugin throws is an upstream failure, not a
        // Bowire crash — and the operator needs the plugin's own words to
        // tell a wrong URL from a wrong payload.
        using var host = await BuildHost();
        _plugin.Throws = new InvalidOperationException("upstream said no");

        var (status, body) = await Invoke(host, Minimal, "https://api.example.com");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("upstream said no",
            body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Equal("InvalidOperationException", body.GetProperty("exceptionType").GetString());
    }

    // ---- the SSE stream ----
    //
    // Everything the unary path does to a request happens here too — the
    // hint@url split, the query-auth move, the plugin pick — but re-derived
    // from query parameters instead of a JSON body. Two implementations of
    // one rule is exactly where they drift apart.

    private static async Task<string> Stream(IHost host, string query)
    {
        using var resp = await host.GetTestClient().GetAsync(
            new Uri($"/api/invoke/stream?{query}", UriKind.Relative),
            TestContext.Current.CancellationToken);
        return await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_Stream_Without_A_Service_Or_Method_Is_A_400()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/invoke/stream?serverUrl=https%3A%2F%2Fapi.example.com", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Every_Frame_Arrives_As_Its_Own_Sse_Event_In_Order()
    {
        using var host = await BuildHost();
        _plugin.Frames.AddRange(["""{"n":1}""", """{"n":2}""", """{"n":3}"""]);

        var body = await Stream(host, "service=S&method=M&serverUrl=https%3A%2F%2Fapi.example.com");

        var frames = body.Split("data: ", StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.StartsWith('{'))
            .ToList();
        Assert.Equal(4, frames.Count);   // three frames plus the done event's payload
        Assert.Contains("\"index\":0", body, StringComparison.Ordinal);
        Assert.Contains("\"index\":2", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Stream_Ends_With_A_Done_Event()
    {
        // The workbench closes the EventSource on it; without it the pane
        // sits there looking like the stream is still open.
        using var host = await BuildHost();
        _plugin.Frames.Add("""{"n":1}""");

        var body = await Stream(host, "service=S&method=M&serverUrl=https%3A%2F%2Fapi.example.com");

        Assert.Contains("event: done", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Frame_Carries_Its_Offset_From_The_Start_Of_The_Stream()
    {
        // Not a wall-clock timestamp: recordings persist this so replay can
        // pace the frames at the original cadence on a host whose clock has
        // nothing to do with the capture's.
        using var host = await BuildHost();
        _plugin.Frames.Add("""{"n":1}""");

        var body = await Stream(host, "service=S&method=M&serverUrl=https%3A%2F%2Fapi.example.com");

        Assert.Contains("timestampMs", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Streaming_Hint_Is_Stripped_And_Its_Transport_Bit_Kept()
    {
        // The same rule as the unary path, re-derived from the query string.
        using var host = await BuildHost();

        await Stream(host, "service=S&method=M&serverUrl=grpcweb%40https%3A%2F%2Fapi.example.com");

        Assert.Equal("https://api.example.com", _plugin.ServerUrl);
        Assert.NotNull(_plugin.Metadata);
        Assert.Contains(_plugin.Metadata, kv => kv.Key.Contains("Transport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_Query_Auth_Entry_Moves_Onto_The_Url_Here_Too()
    {
        using var host = await BuildHost();
        var metadata = Uri.EscapeDataString("""{"__bowireQuery__api_key":"s3cret","X-Real":"kept"}""");

        await Stream(host, $"service=S&method=M&metadata={metadata}&serverUrl=https%3A%2F%2Fapi.example.com");

        Assert.Contains("api_key=s3cret", _plugin.ServerUrl!, StringComparison.Ordinal);
        Assert.DoesNotContain(_plugin.Metadata!, kv => kv.Key.StartsWith("__bowireQuery__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Messages_That_Will_Not_Parse_Fall_Back_To_An_Empty_Object()
    {
        // A truncated query parameter must not take the stream down; the call
        // still goes out with the same default the unary path uses.
        using var host = await BuildHost();

        await Stream(host, "service=S&method=M&messages=%7Bnot-json&serverUrl=https%3A%2F%2Fapi.example.com");

        Assert.Equal(["{}"], _plugin.Messages);
    }

    [Fact]
    public async Task Metadata_That_Will_Not_Parse_Is_Dropped_Rather_Than_Fatal()
    {
        using var host = await BuildHost();

        await Stream(host, "service=S&method=M&metadata=%7Bnot-json&serverUrl=https%3A%2F%2Fapi.example.com");

        Assert.Equal("S", _plugin.Service);
    }

    [Fact]
    public async Task With_No_Plugin_The_Stream_Says_So_As_An_Error_Event()
    {
        // An SSE response is already committed by then, so the refusal has to
        // travel as a frame rather than as a status code.
        using var host = await BuildHost(withPlugin: false);

        var body = await Stream(host, "service=S&method=M&serverUrl=https%3A%2F%2Fapi.example.com");

        Assert.Contains("event: error", body, StringComparison.Ordinal);
        Assert.Contains("No protocol plugin", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Plugin_That_Throws_Mid_Stream_Reports_It_As_An_Error_Event()
    {
        using var host = await BuildHost();
        _plugin.Throws = new InvalidOperationException("upstream closed");

        var body = await Stream(host, "service=S&method=M&serverUrl=https%3A%2F%2Fapi.example.com");

        Assert.Contains("event: error", body, StringComparison.Ordinal);
        Assert.Contains("upstream closed", body, StringComparison.Ordinal);
    }
}
