// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// Wire-level tests for <see cref="BowireMcpProtocol.DiscoverAsync"/> against a
/// hand-rolled JSON-RPC server.
/// <para>
/// Every other MCP server in this repo's test suite is built by the SDK, which
/// means it is always well-formed and always speaks the newest revision — so
/// the paths that actually matter after the 2.0.0 bump had no coverage at all:
/// which handshake goes out first, and what happens when a third-party server
/// answers <c>tools/list</c> with a payload the new deserializer rejects. A raw
/// <see cref="HttpListener"/> is the only way to say "reply with exactly these
/// bytes".
/// </para>
/// </summary>
public sealed class McpDiscoveryWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A well-formed tool, used wherever the test's subject is something other
    /// than the tool payload itself.
    /// </summary>
    private const string OneGoodTool =
        """{"tools":[{"name":"echo","description":"Echo text back","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}]}""";

    [Fact]
    public async Task DiscoverAsync_Probes_ServerDiscover_First_And_Never_Reaches_Initialize()
    {
        // The headline of MCP revision 2026-07-28 (SEP-2575): a client that
        // pins no ProtocolVersion — which is Bowire, it passes no
        // McpClientOptions at all — leads with `server/discover` and only
        // falls back to the legacy handshake if that fails. Asserting the
        // *order* is the point: discovery would keep passing just as green if
        // the SDK silently downgraded to `initialize` on every call.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true),
            RequestMethods.ToolsList => OneGoodTool,
            _ => null,
        });

        var protocol = new BowireMcpProtocol();
        var services = await protocol.DiscoverAsync(server.Url, showInternalServices: false, Ct);

        var tools = Assert.Single(services);
        Assert.Equal("Tools", tools.Name);
        Assert.Equal("echo", Assert.Single(tools.Methods).Name);

        Assert.Equal(RequestMethods.ServerDiscover, server.ReceivedMethods[0]);
        Assert.DoesNotContain(RequestMethods.Initialize, server.ReceivedMethods);
    }

    [Fact]
    public async Task DiscoverAsync_Falls_Back_To_Initialize_When_The_Server_Has_No_Discover()
    {
        // The other half of the negotiation: a 1.x-era server answers the
        // probe with -32601 and the client must downgrade rather than give up.
        // Bowire's plugin has to keep working against both generations for as
        // long as anyone is running an older server.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => null,
            RequestMethods.Initialize => InitializeJson(tools: true),
            RequestMethods.ToolsList => OneGoodTool,
            _ => null,
        });

        var protocol = new BowireMcpProtocol();
        var services = await protocol.DiscoverAsync(server.Url, showInternalServices: false, Ct);

        Assert.Equal("Tools", Assert.Single(services).Name);
        Assert.Equal(RequestMethods.ServerDiscover, server.ReceivedMethods[0]);
        Assert.Contains(RequestMethods.Initialize, server.ReceivedMethods);
    }

    [Fact]
    public async Task DiscoverAsync_Reports_A_Tool_Missing_InputSchema_Instead_Of_Silently_Dropping_Tools()
    {
        // SDK 2.0 made Tool.inputSchema required at deserialization time, so
        // ONE malformed tool now throws for the whole tools/list page. Under
        // the old blanket `catch { return; }` that rendered as a tree with no
        // Tools node — indistinguishable from a server that simply has no
        // tools. It must reach the operator instead.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true, resources: true),
            RequestMethods.ToolsList =>
                """{"tools":[{"name":"good","inputSchema":{"type":"object"}},{"name":"bad","description":"no inputSchema"}]}""",
            RequestMethods.ResourcesList => """{"resources":[{"uri":"mem://reading","name":"reading"}]}""",
            _ => null,
        });

        var protocol = new BowireMcpProtocol();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => protocol.DiscoverAsync(server.Url, showInternalServices: false, Ct));

        Assert.Contains("tools/list", ex.Message, StringComparison.Ordinal);
        // The surfaces that DID answer are named, so an operator does not also
        // have to wonder where the resources went.
        Assert.Contains("Resources", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_Records_A_Malformed_Tools_Payload_As_An_Error_Attempt()
    {
        // The end of the same wire: BowireDiscoveryProbe (#534) is what turns
        // a plugin throw into the per-plugin attempt the UI, the CLI and the
        // bowire.discover MCP tool all render. `error` with a message beats
        // `empty` with none, which is what this used to be.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true),
            RequestMethods.ToolsList => """{"tools":[{"name":"bad","description":"no inputSchema"}]}""",
            _ => null,
        });

        var registry = new BowireProtocolRegistry();
        registry.Register(new BowireMcpProtocol());

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, server.Url, pluginHint: "mcp", showInternalServices: false,
            perProbeCeiling: TimeSpan.FromSeconds(30), logger: null, ct: Ct);

        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(BowireDiscoveryAttempt.OutcomeError, attempt.Outcome);
        Assert.Contains("tools/list", attempt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_Stays_Silent_When_An_Advertised_Surface_Answers_MethodNotFound()
    {
        // A server may advertise a capability and still not implement the
        // list call. That is "no entries", not a fault — if it were reported
        // the plugin would go `error` on servers that work perfectly well, and
        // the diagnostics channel would be noise within a day.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true, resources: true, prompts: true),
            RequestMethods.ToolsList => OneGoodTool,
            // resources/list and prompts/list fall through to -32601.
            _ => null,
        });

        var protocol = new BowireMcpProtocol();
        var services = await protocol.DiscoverAsync(server.Url, showInternalServices: false, Ct);

        Assert.Equal("Tools", Assert.Single(services).Name);
    }

    [Fact]
    public async Task DiscoverAsync_Stays_Silent_When_A_Capability_Is_Absent_Altogether()
    {
        // Same silence, reached a different way: here the server never
        // ADVERTISES resources or prompts in its discover reply, rather than
        // advertising them and then answering -32601.
        //
        // Measured, because the intuitive assumption is wrong: the SDK does
        // NOT short-circuit the list call for an unadvertised capability. It
        // sends resources/list and prompts/list anyway and lets the server
        // reject them, so this path arrives at ClassifyListFailure exactly
        // like the advertised-but-missing case. Asserting "no round trip"
        // here would be asserting an SDK behaviour that does not exist.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true),
            RequestMethods.ToolsList => OneGoodTool,
            _ => null,
        });

        var protocol = new BowireMcpProtocol();
        var services = await protocol.DiscoverAsync(server.Url, showInternalServices: false, Ct);

        // The contract that matters: an unadvertised surface contributes no
        // service and — critically — does not fail the whole probe, so the
        // one surface the server does have still reaches the tree.
        Assert.Equal("Tools", Assert.Single(services).Name);
        Assert.Contains(RequestMethods.ServerDiscover, server.ReceivedMethods);
    }

    // ----- payload builders ---------------------------------------------

    /// <summary>
    /// Serialise a <see cref="DiscoverResult"/> through the SDK's own options
    /// rather than hand-writing the JSON: the point of these tests is what
    /// Bowire does with a reply, not whether the author guessed the SDK's
    /// property names right.
    /// </summary>
    private static string DiscoverJson(bool tools = false, bool resources = false, bool prompts = false)
        => JsonSerializer.Serialize(
            new DiscoverResult
            {
                SupportedVersions = ["2026-07-28"],
                Capabilities = BuildCapabilities(tools, resources, prompts),
            },
            McpJsonUtilities.DefaultOptions);

    private static string InitializeJson(bool tools = false, bool resources = false, bool prompts = false)
        => JsonSerializer.Serialize(
            new InitializeResult
            {
                ProtocolVersion = "2025-06-18",
                Capabilities = BuildCapabilities(tools, resources, prompts),
                ServerInfo = new Implementation { Name = "raw-json-rpc-fake", Version = "1.0.0" },
            },
            McpJsonUtilities.DefaultOptions);

    private static ServerCapabilities BuildCapabilities(bool tools, bool resources, bool prompts)
        => new()
        {
            Tools = tools ? new ToolsCapability() : null,
            Resources = resources ? new ResourcesCapability() : null,
            Prompts = prompts ? new PromptsCapability() : null,
        };
}

/// <summary>
/// A Streamable-HTTP MCP server that is not the SDK: one <see cref="HttpListener"/>
/// that answers every JSON-RPC POST with whatever the test's dispatch function
/// returns, verbatim. That verbatim is the whole reason it exists — an
/// SDK-built server cannot emit a tool without an <c>inputSchema</c>, so the
/// failure mode SDK 2.0 introduced is unreachable through one.
/// </summary>
internal sealed class RawJsonRpcMcpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<string, string?> _dispatch;
    private readonly List<string> _received = [];
    private readonly System.Threading.Lock _gate = new();
    private volatile bool _stopped;

    /// <param name="dispatch">
    /// Maps a JSON-RPC method name to the raw JSON for the <c>result</c>
    /// member. Returning <see langword="null"/> replies <c>-32601
    /// MethodNotFound</c>, which is how a server says "I don't speak that" —
    /// used here for both the discover-probe fallback and the
    /// advertised-but-unimplemented list calls.
    /// </param>
    public RawJsonRpcMcpServer(Func<string, string?> dispatch)
    {
        _dispatch = dispatch;
        Url = $"http://localhost:{FreePort()}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Base URL, with the trailing slash the plugin trims off.</summary>
    public string Url { get; }

    /// <summary>
    /// Every method name the server was asked for, in arrival order. Order is
    /// the assertion that matters: it is what distinguishes "negotiated via
    /// server/discover" from "silently downgraded to initialize".
    /// </summary>
    public IReadOnlyList<string> ReceivedMethods
    {
        get { lock (_gate) { return [.. _received]; } }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopped)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            lock (_gate) { _received.Add(method); }

            // No id means a notification (notifications/initialized on the
            // legacy path). The spec wants 202 and an empty body.
            if (!root.TryGetProperty("id", out var id))
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            // Concatenated rather than interpolated: the JSON is dense in
            // braces and a raw interpolated literal would need three-deep
            // escaping to say so.
            const string head = """{"jsonrpc":"2.0","id":""";
            const string errorTail = ""","error":{"code":-32601,"message":"Method not found"}}""";
            const string resultMid = ""","result":""";

            var result = _dispatch(method);
            var envelope = result is null
                ? head + id.GetRawText() + errorTail
                : head + id.GetRawText() + resultMid + result + "}";

            var bytes = Encoding.UTF8.GetBytes(envelope);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
        // The client disposes its transport mid-flight on the paths under
        // test; a broken pipe here is the test tearing down, not a failure.
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch (Exception) { /* already gone */ }
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Close();
    }
}
