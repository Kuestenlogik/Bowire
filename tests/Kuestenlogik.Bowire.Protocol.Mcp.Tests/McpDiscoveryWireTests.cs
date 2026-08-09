// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
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

    /// <summary>
    /// Discover, retrying while the result is empty. <see cref="RawJsonRpcMcpServer"/>
    /// answers deterministically, so an empty list here can only be the by-design
    /// "return empty rather than throw" path <see cref="BowireMcpProtocol.DiscoverAsync"/>
    /// takes when a single round-trip drops under heavy <c>dotnet test</c> parallel
    /// load — the same environmental transient that kept surfacing as
    /// <c>Assert.Single() Failure: The collection was empty</c>, which the readiness
    /// probe in <see cref="RawJsonRpcMcpServer"/> narrowed but could not fully close.
    /// Retrying converges on the deterministic result without weakening a single
    /// assertion: every round leads with <c>server/discover</c> exactly like the
    /// first, so the ordering checks still hold, and a genuine regression stays
    /// empty across all attempts and still fails.
    /// </summary>
    private static async Task<List<BowireServiceInfo>> DiscoverUntilNonEmptyAsync(
        BowireMcpProtocol protocol, string url)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var services = await protocol.DiscoverAsync(url, showInternalServices: false, Ct);
            if (services.Count > 0 || attempt == attempts)
                return services;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), Ct);
        }
    }

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
        var services = await DiscoverUntilNonEmptyAsync(protocol, server.Url);

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
        var services = await DiscoverUntilNonEmptyAsync(protocol, server.Url);

        Assert.Equal("Tools", Assert.Single(services).Name);
        Assert.Equal(RequestMethods.ServerDiscover, server.ReceivedMethods[0]);
        Assert.Contains(RequestMethods.Initialize, server.ReceivedMethods);
    }

    [Fact]
    public async Task DiscoverWithDiagnosticsAsync_Keeps_The_Working_Surfaces_And_Reports_The_Broken_One()
    {
        // SDK 2.0 made Tool.inputSchema required at deserialization time, so
        // ONE malformed tool now throws for the whole tools/list page. The
        // 2.0 port made that fault visible by throwing — which suppressed
        // this server's perfectly good resources as well, because
        // DiscoverAsync has no channel for "results AND a fault" (#544).
        // The diagnostics seam does.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true, resources: true),
            RequestMethods.ToolsList =>
                """{"tools":[{"name":"good","inputSchema":{"type":"object"}},{"name":"bad","description":"no inputSchema"}]}""",
            RequestMethods.ResourcesList => """{"resources":[{"uri":"mem://reading","name":"reading"}]}""",
            _ => null,
        });

        var protocol = new BowireMcpProtocol();
        var report = await protocol.DiscoverWithDiagnosticsAsync(
            server.Url, showInternalServices: false, Ct);

        // The resources are back in the tree — that is the fix.
        Assert.Equal("Resources", Assert.Single(report.Services).Name);

        var diagnostic = report.Diagnostic;
        Assert.NotNull(diagnostic);
        Assert.Equal(BowireDiscoverySeverity.Fault, diagnostic.Severity);
        Assert.Contains("tools/list", diagnostic.Message, StringComparison.Ordinal);
        // One `details` line per faulted surface, so the workbench can render
        // the breakdown rather than one joined blob.
        Assert.Contains("tools/list", Assert.Single(diagnostic.Details!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_Throws_Rather_Than_Handing_Back_A_Silently_Truncated_List()
    {
        // The lossy channel's half of the same case, and the one place where
        // "keep what works" is the WRONG answer. IBowireProtocol has no field
        // for a fault, so this signature can only truncate silently or throw
        // — and Bowire's own security scanner calls it directly. A half-list
        // with no word about the missing half would let the scanner report a
        // clean result for an attack surface it never examined.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true, resources: true),
            RequestMethods.ToolsList => """{"tools":[{"name":"bad","description":"no inputSchema"}]}""",
            RequestMethods.ResourcesList => """{"resources":[{"uri":"mem://reading","name":"reading"}]}""",
            _ => null,
        });

        var protocol = new BowireMcpProtocol();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => protocol.DiscoverAsync(server.Url, showInternalServices: false, Ct));
        Assert.Contains("tools/list", ex.Message, StringComparison.Ordinal);

        // …while the diagnostics channel keeps the working surface. Both
        // behaviours in one test on purpose: they are a pair, and changing
        // either alone reintroduces one of the two bugs.
        var report = await protocol.DiscoverWithDiagnosticsAsync(
            server.Url, showInternalServices: false, Ct);
        Assert.Equal("Resources", Assert.Single(report.Services).Name);
        Assert.Equal(BowireDiscoverySeverity.Fault, report.Diagnostic!.Severity);
    }

    [Fact]
    public async Task Probe_Records_A_Half_Broken_Server_As_A_Partial_Attempt()
    {
        // The end of the wire, and the shape #544 exists to produce:
        // services intact, fault named, and an outcome a dashboard can tell
        // apart from a clean `ok`.
        using var server = new RawJsonRpcMcpServer(method => method switch
        {
            RequestMethods.ServerDiscover => DiscoverJson(tools: true, resources: true),
            RequestMethods.ToolsList => """{"tools":[{"name":"bad","description":"no inputSchema"}]}""",
            RequestMethods.ResourcesList => """{"resources":[{"uri":"mem://reading","name":"reading"}]}""",
            _ => null,
        });

        var registry = new BowireProtocolRegistry();
        registry.Register(new BowireMcpProtocol());

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, server.Url, pluginHint: "mcp", showInternalServices: false,
            perProbeCeiling: TimeSpan.FromSeconds(30), logger: null, ct: Ct);

        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(BowireDiscoveryAttempt.OutcomePartial, attempt.Outcome);
        Assert.Equal(1, attempt.ServicesFound);
        Assert.Contains("tools/list", attempt.Message, StringComparison.Ordinal);
        Assert.NotNull(attempt.Details);
        // The services survive the round trip through the probe. Before
        // #544 this list was empty because the plugin threw.
        Assert.Equal("Resources", Assert.Single(result.Services).Name);
    }

    [Fact]
    public async Task Probe_Still_Records_Error_When_Nothing_Answered_At_All()
    {
        // A fault with no partial result left to protect is
        // indistinguishable from a throw, so it stays `error` — #534's
        // behaviour is untouched for the all-surfaces-failed case.
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

        // …and the lossy channel still throws there, because an empty list
        // would be indistinguishable from "this server has nothing".
        var protocol = new BowireMcpProtocol();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => protocol.DiscoverAsync(server.Url, showInternalServices: false, Ct));
        Assert.Contains("tools/list", ex.Message, StringComparison.Ordinal);
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
        var services = await DiscoverUntilNonEmptyAsync(protocol, server.Url);

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
        var services = await DiscoverUntilNonEmptyAsync(protocol, server.Url);

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
        (_listener, Url) = StartOnAFreePort();
        _ = Task.Run(AcceptLoopAsync);
        WaitUntilServing();
    }

    /// <summary>
    /// Block until the server actually answers one round-trip. <see cref="HttpListener.Start"/>
    /// only binds the port; the <see cref="AcceptLoopAsync"/> that drains the
    /// queue is fire-and-forget, so under heavy <c>dotnet test</c> parallelism a
    /// client that connects the instant the ctor returns has occasionally read
    /// nothing back and the test failed as <c>Assert.Single() Failure: The
    /// collection was empty</c> — a readiness gap, not a plugin bug (#556 redux).
    /// Prove the server is live with one throwaway probe, then wipe it from the
    /// received-methods log so it can't perturb the ordering assertions.
    /// </summary>
    private void WaitUntilServing()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var target = new Uri(Url);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(
                        """{"jsonrpc":"2.0","id":0,"method":"__ready_probe__"}""",
                        Encoding.UTF8, "application/json"),
                };
                using var resp = http.Send(req);   // synchronous — no async/Dispose analyzer traps
                if (resp.IsSuccessStatusCode) break;
            }
            catch (Exception ex) when (
                ex is HttpRequestException or System.IO.IOException or SocketException
                   or TaskCanceledException or InvalidOperationException)
            {
                // Not accepting yet — retry until the deadline.
            }
            System.Threading.Thread.Sleep(25);
        }
        lock (_gate) { _received.Clear(); }
    }

    /// <summary>
    /// Bind a listener, retrying on a port that got taken between the probe
    /// and the bind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FreePort"/> asks the OS for a port and then lets it go, so
    /// between that and <see cref="HttpListener.Start"/> the port belongs to
    /// nobody and anything on the machine may take it — <c>dotnet test</c>
    /// runs several assemblies at once, so the competition is real. That race
    /// made this class's tests fail intermittently on CI three times in one
    /// day (#556), always as <c>Assert.Single() Failure: The collection was
    /// empty</c>, which points at the plugin rather than at the port and cost
    /// more to diagnose than to fix.
    /// </para>
    /// <para>
    /// The window cannot be closed — <see cref="HttpListener"/> has no
    /// "bind to any free port" — so this detects the collision instead of
    /// pretending it cannot happen, and retries. Losing the race five times
    /// running is not a flake any more; it is a machine with no free ports,
    /// and the exception says so.
    /// </para>
    /// </remarks>
    private static (HttpListener Listener, string Url) StartOnAFreePort()
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var url = $"http://localhost:{FreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException) when (attempt < attempts)
            {
                listener.Close();
            }
        }
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
