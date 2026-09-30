// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// End-to-end proof that <see cref="BowireOptions.LockServerUrl"/> and
/// <see cref="BowireOptions.AllowedServerUrls"/> are enforced on the server,
/// not just in the UI: every endpoint that dials a caller-named target —
/// invoke, invoke/stream, channel/open, services, security/fuzz,
/// parallel/start-local and parallel/start — answers a foreign
/// <c>serverUrl</c> with a 403 problem-details body before the protocol
/// plugin (or the HTTP client) is reached, while the configured target still
/// works and an unlocked host behaves exactly as before.
/// </summary>
[Collection("StaticEndpointState")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireTargetLockEndpointTests
{
    private const string Allowed = "http://allowed.bowire.test:5000";
    private const string Foreign = "http://169.254.169.254/latest/meta-data";

    /// <summary>Records every URL the plugin was asked to dial.</summary>
    private sealed class RecordingProtocol : IBowireProtocol
    {
        public ConcurrentQueue<string> Dialed { get; } = new();
        public string Id => "grpc";
        public string Name => "Recording";
        public string IconSvg => "<svg/>";
        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            Dialed.Enqueue(serverUrl);
            return Task.FromResult(new List<BowireServiceInfo>());
        }
        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages, bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            Dialed.Enqueue(serverUrl);
            return Task.FromResult(new InvokeResult("""{"ok":true}""", 1, "OK", new Dictionary<string, string>()));
        }
#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages, bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Dialed.Enqueue(serverUrl);
            yield return """{"frame":1}""";
        }
#pragma warning restore CS1998
        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method, bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            Dialed.Enqueue(serverUrl);
            return Task.FromResult<IBowireChannel?>(null);
        }
    }

    private sealed record Host(WebApplication App, HttpClient Http, RecordingProtocol Protocol) : IAsyncDisposable
    {
        public string Origin => App.Urls.First();

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            BowireEndpointHelpers.ResetRegistry();
        }
    }

    private static BowireOptions LockedOptions()
    {
        var options = new BowireOptions { Mode = BowireMode.Standalone, LockServerUrl = true };
        options.ServerUrls.Add(Allowed);
        return options;
    }

    private static async Task<Host> StartAsync(BowireOptions options, CancellationToken ct)
    {
        var protocol = new RecordingProtocol();
        var reg = new BowireProtocolRegistry();
        reg.Register(protocol);
        BowireEndpointHelpers.SetRegistry(reg);

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = b.Build();
        app.MapBowireDiscoveryEndpoints(options, "");
        app.MapBowireInvokeEndpoints(options, "");
        app.MapBowireChannelEndpoints(options, "");
        app.MapBowireSecurityEndpoints("", options);
        app.MapBowireParallelEndpoints("", options);
        await app.StartAsync(ct).ConfigureAwait(false);
        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, protocol);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static Uri Rel(string path, string? serverUrl = null) => new(
        serverUrl is null
            ? path
            : path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "serverUrl=" + Uri.EscapeDataString(serverUrl),
        UriKind.Relative);

    private static async Task AssertRefusedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(BowireTargetPolicy.RefusedProblemType, body.GetProperty("type").GetString());
        Assert.Equal(403, body.GetProperty("status").GetInt32());
    }

    private static Task<HttpResponseMessage> InvokeAsync(Host h, string? serverUrl, CancellationToken ct) =>
        h.Http.PostAsync(Rel("/api/invoke", serverUrl),
            Json("""{ "service": "S", "method": "M", "protocol": "grpc", "messages": ["{}"] }"""), ct);

    private static Task<HttpResponseMessage> StreamAsync(Host h, string? serverUrl, CancellationToken ct) =>
        h.Http.GetAsync(Rel("/api/invoke/stream?service=S&method=M&protocol=grpc", serverUrl), ct);

    private static Task<HttpResponseMessage> ChannelAsync(Host h, string? serverUrl, CancellationToken ct) =>
        h.Http.PostAsync(Rel("/api/channel/open", serverUrl),
            Json("""{ "service": "S", "method": "M", "protocol": "grpc" }"""), ct);

    private static Task<HttpResponseMessage> ServicesAsync(Host h, string? serverUrl, CancellationToken ct) =>
        h.Http.GetAsync(Rel("/api/services", serverUrl), ct);

    private static Task<HttpResponseMessage> FuzzAsync(Host h, string target, string? httpPath, CancellationToken ct) =>
        h.Http.PostAsync(new Uri("/api/security/fuzz", UriKind.Relative),
            Json(JsonSerializer.Serialize(new
            {
                target,
                httpPath,
                httpVerb = "POST",
                body = """{"q":"x"}""",
                field = "$.q",
                category = "sqli",
            })), ct);

    private static Task<HttpResponseMessage> ParallelAsync(Host h, string route, string targetUrl, CancellationToken ct) =>
        h.Http.PostAsync(new Uri(route, UriKind.Relative),
            Json(JsonSerializer.Serialize(new
            {
                targets = new[] { new { url = targetUrl, method = "GET" } },
                sessionCount = 1,
            })), ct);

    // ---------------------------- locked: refused -----------------------------

    public static TheoryData<string> ForeignTargets => new()
    {
        Foreign,
        "http://10.0.0.5:8080",
        "grpcweb@http://169.254.169.254",
        "grpc@" + Foreign,
        "http://allowed.bowire.test:5000@169.254.169.254",
        "http://allowed.bowire.test:5001",
        "https://allowed.bowire.test:5000",
    };

    [Theory]
    [MemberData(nameof(ForeignTargets))]
    public async Task Locked_invoke_refuses_foreign_target(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await InvokeAsync(h, target, ct);

        await AssertRefusedAsync(resp, ct);
        Assert.Empty(h.Protocol.Dialed);
    }

    [Theory]
    [MemberData(nameof(ForeignTargets))]
    public async Task Locked_stream_refuses_foreign_target(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await StreamAsync(h, target, ct);

        await AssertRefusedAsync(resp, ct);
        Assert.Empty(h.Protocol.Dialed);
    }

    [Theory]
    [MemberData(nameof(ForeignTargets))]
    public async Task Locked_channel_open_refuses_foreign_target(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await ChannelAsync(h, target, ct);

        await AssertRefusedAsync(resp, ct);
        Assert.Empty(h.Protocol.Dialed);
    }

    [Theory]
    [MemberData(nameof(ForeignTargets))]
    public async Task Locked_discovery_refuses_foreign_target(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await ServicesAsync(h, target, ct);

        await AssertRefusedAsync(resp, ct);
        Assert.Empty(h.Protocol.Dialed);
    }

    [Fact]
    public async Task Locked_fuzz_refuses_foreign_target()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await FuzzAsync(h, Foreign, "/", ct);

        await AssertRefusedAsync(resp, ct);
    }

    [Fact]
    public async Task Locked_fuzz_refuses_a_path_that_escapes_the_allowed_host()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        // The path is appended verbatim to the target, so "@evil" after a
        // bare authority would move the host; the combined URL is checked.
        using var resp = await FuzzAsync(h, "http://allowed.bowire.test:5000@", "169.254.169.254/", ct);

        await AssertRefusedAsync(resp, ct);
    }

    [Theory]
    [InlineData("/api/parallel/start-local")]
    [InlineData("/api/parallel/start")]
    public async Task Locked_parallel_refuses_foreign_target(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await ParallelAsync(h, route, Foreign, ct);

        await AssertRefusedAsync(resp, ct);
    }

    // ---------------------------- locked: allowed -----------------------------

    public static TheoryData<string> AllowedVariants => new()
    {
        Allowed,
        Allowed + "/",
        "HTTP://ALLOWED.BOWIRE.TEST:5000",
        "grpc@" + Allowed,
        "grpcweb@http://Allowed.Bowire.Test:5000/",
    };

    [Theory]
    [MemberData(nameof(AllowedVariants))]
    public async Task Locked_invoke_accepts_the_configured_target(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await InvokeAsync(h, target, ct);

        resp.EnsureSuccessStatusCode();
        Assert.NotEmpty(h.Protocol.Dialed);
    }

    [Fact]
    public async Task Locked_stream_accepts_the_configured_target()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await StreamAsync(h, Allowed, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame", await resp.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locked_channel_open_reaches_the_plugin_for_the_configured_target()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await ChannelAsync(h, Allowed, ct);

        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.NotEmpty(h.Protocol.Dialed);
    }

    [Fact]
    public async Task Locked_discovery_accepts_the_configured_target()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedOptions(), ct);

        using var resp = await ServicesAsync(h, "grpc@" + Allowed + "/", ct);

        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.NotEmpty(h.Protocol.Dialed);
    }

    [Fact]
    public async Task Locked_parallel_runs_an_allowed_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = LockedOptions();
        await using var h = await StartAsync(options, ct);
        // The host's own (unmapped) root is a cheap, reachable target.
        options.AllowedServerUrls.Add(h.Origin);

        using var resp = await ParallelAsync(h, "/api/parallel/start-local", h.Origin + "/nowhere", ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task AllowedServerUrls_alone_enforces_without_the_ui_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new BowireOptions { Mode = BowireMode.Standalone };
        options.AllowedServerUrls.Add(Allowed);
        await using var h = await StartAsync(options, ct);

        using (var refused = await InvokeAsync(h, Foreign, ct))
            await AssertRefusedAsync(refused, ct);
        using var ok = await InvokeAsync(h, Allowed, ct);
        ok.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Locked_embedded_host_defaults_to_its_own_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(new BowireOptions { Mode = BowireMode.Embedded, LockServerUrl = true }, ct);

        // No serverUrl → the endpoint falls back to the host itself, which
        // the lock allows; anything else is still refused.
        using (var ok = await InvokeAsync(h, null, ct))
            ok.EnsureSuccessStatusCode();
        using var refused = await InvokeAsync(h, Foreign, ct);
        await AssertRefusedAsync(refused, ct);
    }

    // ------------------------------- unlocked ---------------------------------

    [Fact]
    public async Task Unlocked_default_still_dials_any_target()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(new BowireOptions(), ct);

        using (var invoke = await InvokeAsync(h, Foreign, ct))
            invoke.EnsureSuccessStatusCode();
        using (var stream = await StreamAsync(h, Foreign, ct))
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using (var channel = await ChannelAsync(h, Foreign, ct))
            Assert.NotEqual(HttpStatusCode.Forbidden, channel.StatusCode);
        using (var services = await ServicesAsync(h, Foreign, ct))
            Assert.NotEqual(HttpStatusCode.Forbidden, services.StatusCode);

        // Every route reached the plugin with the foreign URL, as before.
        Assert.True(h.Protocol.Dialed.Count >= 4);
        Assert.All(h.Protocol.Dialed, url => Assert.StartsWith("http://169.254.169.254", url, StringComparison.Ordinal));
    }
}
