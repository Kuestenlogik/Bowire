// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// <c>GET /api/invoke/stream</c> — the branches the happy path does not reach
/// (#216).
/// </summary>
/// <remarks>
/// <para>
/// The existing suite covers a missing parameter, a missing plugin, and a
/// clean run of frames. What it leaves is where the endpoint actually does
/// work: the wire-bytes path a gRPC plugin takes, the per-frame envelope the
/// recorder persists, what a plugin failing mid-stream looks like to the
/// browser, and how a malformed query is treated.
/// </para>
/// <para>
/// Each frame's envelope is a contract with two readers — the workbench
/// renders it live, and the recorder writes it to disk for replay. A field
/// dropped here is a recording that cannot be replayed, discovered much
/// later.
/// </para>
/// </remarks>
[Collection("StaticEndpointState")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireInvokeStreamTests
{
    // ---- frames ----

    [Fact]
    public async Task Each_Frame_Carries_What_A_Recording_Needs_To_Replay_It()
    {
        // index to order them, data to render, timestampMs to pace a replay
        // at the cadence they arrived at.
        await using var host = await StartAsync(new StubProtocol(streamFrames: ["""{"n":1}""", """{"n":2}"""]));

        var frames = await ReadFramesAsync(host, "service=S&method=M");

        Assert.Equal(2, frames.Count);
        Assert.Equal(0, frames[0].GetProperty("index").GetInt32());
        Assert.Equal(1, frames[1].GetProperty("index").GetInt32());
        Assert.Equal("""{"n":1}""", frames[0].GetProperty("data").GetString());
        Assert.True(frames[0].TryGetProperty("timestampMs", out _));
    }

    [Fact]
    public async Task A_Plugin_With_Wire_Bytes_Has_Them_Carried_Too()
    {
        // The gRPC path. responseBinary is what Phase-2d streaming replay
        // re-emits; without it a recorded gRPC stream can be shown but not
        // played back.
        await using var host = await StartAsync(new WireByteProtocol(
            (Json: """{"n":1}""", Binary: new byte[] { 1, 2, 3 })));

        var frames = await ReadFramesAsync(host, "service=S&method=M&protocol=stub");

        var frame = Assert.Single(frames);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            frame.GetProperty("responseBinary").GetString());
    }

    [Fact]
    public async Task A_Plugin_Without_Wire_Bytes_Leaves_The_Field_Out()
    {
        // Absent rather than an empty string: a replayer that finds a value
        // there will try to send it.
        await using var host = await StartAsync(new StubProtocol(streamFrames: ["""{"n":1}"""]));

        var frame = Assert.Single(await ReadFramesAsync(host, "service=S&method=M"));

        Assert.False(frame.TryGetProperty("responseBinary", out var bin) && bin.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task A_Stream_That_Yields_Nothing_Still_Ends()
    {
        // The workbench waits for `done`; without it the run never settles
        // and the stop button is the only way out.
        await using var host = await StartAsync(new StubProtocol(streamFrames: []));

        var raw = await ReadRawAsync(host, "service=S&method=M");

        Assert.Empty(await ReadFramesAsync(host, "service=S&method=M"));
        Assert.Contains("event: done", raw, StringComparison.Ordinal);
    }

    // ---- failure ----

    [Fact]
    public async Task A_Plugin_Failing_Mid_Stream_Is_An_Error_Event_Not_A_Dropped_Connection()
    {
        // Frames already delivered stay delivered, and the reason arrives on
        // the same channel. A dropped connection would leave the workbench
        // unable to tell a finished stream from a broken one.
        await using var host = await StartAsync(new StubProtocol(
            streamFrames: ["""{"n":1}"""], throwAfterFrames: 1));

        var raw = await ReadRawAsync(host, "service=S&method=M");

        Assert.Contains("event: error", raw, StringComparison.Ordinal);
        Assert.Contains("stream boom", raw, StringComparison.Ordinal);
        // The frame that made it is still there -- asserted on the index
        // rather than on the escaped payload, so the test is about delivery
        // and not about how the serialiser quotes a JSON string.
        Assert.Contains("\"index\":0", raw, StringComparison.Ordinal);
        // And no done event, because it did not finish.
        Assert.DoesNotContain("event: done", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Plugin_Failing_Before_The_First_Frame_Still_Reports_Why()
    {
        await using var host = await StartAsync(new StubProtocol(
            streamFrames: [], throwAfterFrames: 0));

        var raw = await ReadRawAsync(host, "service=S&method=M");

        Assert.Contains("event: error", raw, StringComparison.Ordinal);
        Assert.Contains("stream boom", raw, StringComparison.Ordinal);
    }

    // ---- what the query says ----

    [Theory]
    // Not a JSON array.
    [InlineData("messages=notjson")]
    // Valid JSON of the wrong shape.
    [InlineData("messages=%7B%22a%22%3A1%7D")]
    public async Task A_Malformed_Messages_Parameter_Runs_With_An_Empty_Message(string query)
    {
        // Refusing would be defensible; what is not defensible is a 500. The
        // parameter is built by the browser, and an empty message is what a
        // streaming call with no request payload sends anyway.
        var plugin = new StubProtocol(streamFrames: ["""{"n":1}"""]);
        await using var host = await StartAsync(plugin);

        var frames = await ReadFramesAsync(host, "service=S&method=M&" + query);

        Assert.Single(frames);
        Assert.Equal(["{}"], plugin.SeenMessages);
    }

    [Fact]
    public async Task A_Malformed_Metadata_Parameter_Is_Dropped_Rather_Than_Fatal()
    {
        var plugin = new StubProtocol(streamFrames: ["""{"n":1}"""]);
        await using var host = await StartAsync(plugin);

        await ReadFramesAsync(host, "service=S&method=M&metadata=notjson");

        Assert.Null(plugin.SeenMetadata);
    }

    [Fact]
    public async Task Metadata_Reaches_The_Plugin()
    {
        var plugin = new StubProtocol(streamFrames: ["""{"n":1}"""]);
        await using var host = await StartAsync(plugin);

        await ReadFramesAsync(
            host, "service=S&method=M&metadata=%7B%22x-key%22%3A%22v%22%7D");

        Assert.NotNull(plugin.SeenMetadata);
        Assert.Equal("v", plugin.SeenMetadata!["x-key"]);
    }

    [Fact]
    public async Task The_Response_Is_An_Event_Stream()
    {
        // The browser's EventSource refuses anything else, so this is the
        // difference between a working stream and a silent one.
        await using var host = await StartAsync(new StubProtocol(streamFrames: []));

        using var response = await host.Http.GetAsync(
            new Uri("/api/invoke/stream?service=S&method=M", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- harness ----

    private sealed record Host(WebApplication App, HttpClient Http) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            BowireEndpointHelpers.ResetRegistry();
        }
    }

    private static async Task<Host> StartAsync(IBowireProtocol protocol)
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(protocol);
        BowireEndpointHelpers.SetRegistry(registry);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(
            o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.MapBowireInvokeEndpoints(new BowireOptions(), "");
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static async Task<string> ReadRawAsync(Host host, string query)
        => await host.Http.GetStringAsync(
            new Uri("/api/invoke/stream?" + query, UriKind.Relative),
            TestContext.Current.CancellationToken);

    /// <summary>The <c>data:</c> lines of a finished stream, parsed.</summary>
    private static async Task<List<JsonElement>> ReadFramesAsync(Host host, string query)
    {
        var raw = await ReadRawAsync(host, query);
        var frames = new List<JsonElement>();
        foreach (var line in raw.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line["data: ".Length..].Trim();
            // The done and error events carry data lines too; only the
            // frames have an index.
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("index", out _)) frames.Add(doc.RootElement.Clone());
        }
        return frames;
    }

    /// <summary>A plugin that streams what it was given, and can stop badly.</summary>
    // ---- #712: why a stream stopped ----

    [Fact]
    public async Task An_Error_Frame_Surfaces_Beside_The_Data_Not_Inside_It()
    {
        // The envelope is the level the browser already parses. Putting the
        // reason inside `data` is what made an aborted stream look like a
        // message: api.js parsed every frame as payload and then reported
        // the run as OK.
        await using var host = await StartAsync(new StubProtocol(streamFrames: [
            """{"n":1}""",
            BowireStreamErrorEnvelope.Frame(BowireStreamErrorKinds.Refused, "quota exceeded", "429"),
        ]));

        var frames = await ReadFramesAsync(host, "service=S&method=M");

        Assert.Equal(2, frames.Count);
        Assert.False(frames[0].TryGetProperty("error", out _), "a data frame carries no error");

        var error = frames[1].GetProperty("error");
        Assert.Equal("refused", error.GetProperty("kind").GetString());
        Assert.Equal("quota exceeded", error.GetProperty("message").GetString());
        Assert.Equal("429", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_Ordinary_Frame_Envelope_Is_Unchanged()
    {
        // The field is omitted rather than sent as null, so every existing
        // consumer and every recording on disk keeps the exact shape it had.
        await using var host = await StartAsync(new StubProtocol(streamFrames: ["""{"error":"this is payload"}"""]));

        var frames = await ReadFramesAsync(host, "service=S&method=M");

        var frame = Assert.Single(frames);
        // A bare `error` key in the payload is data -- several plugins in
        // this repo emit exactly that -- and must not be mistaken for the
        // stream ending.
        Assert.False(frame.TryGetProperty("error", out _));
        Assert.Equal("""{"error":"this is payload"}""", frame.GetProperty("data").GetString());
    }

    [Fact]
    public async Task A_Wire_Bytes_Plugin_Reports_Its_Error_Through_The_Typed_Slot()
    {
        // gRPC's path. No envelope needed: StreamFrame has somewhere to put
        // it, which is what its private ConnectStreamFrame.ErrorCode was
        // standing in for.
        await using var host = await StartAsync(new WireByteErrorProtocol());

        var frames = await ReadFramesAsync(host, "service=S&method=M&protocol=stub");

        var frame = Assert.Single(frames);
        var error = frame.GetProperty("error");
        Assert.Equal("server", error.GetProperty("kind").GetString());
        Assert.Equal("8", error.GetProperty("code").GetString());
    }

    /// <summary>
    /// A wire-bytes plugin whose stream ends with a typed error (#712).
    /// </summary>
    private sealed class WireByteErrorProtocol : IBowireProtocol, IBowireStreamingWithWireBytes
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult(new InvokeResult("{}", 0, "OK", new Dictionary<string, string>()));

        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
#pragma warning disable CS0162 // Unreachable — the endpoint prefers the wire-bytes surface.
            await Task.Yield();
#pragma warning restore CS0162
        }

        public async IAsyncEnumerable<StreamFrame> InvokeStreamWithFramesAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new StreamFrame("""{"status":"RESOURCE_EXHAUSTED"}""", null)
            {
                Error = new StreamError(BowireStreamErrorKinds.Server, "RESOURCE_EXHAUSTED", "8"),
            };
            await Task.Yield();
        }

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class StubProtocol(
        IEnumerable<string>? streamFrames = null,
        int? throwAfterFrames = null) : IBowireProtocol
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string IconSvg => "<svg/>";

        public List<string>? SeenMessages { get; private set; }
        public Dictionary<string, string>? SeenMetadata { get; private set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult(new InvokeResult("{}", 0, "OK", new Dictionary<string, string>()));

        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            SeenMessages = jsonMessages;
            SeenMetadata = metadata;

            var sent = 0;
            foreach (var frame in streamFrames ?? [])
            {
                if (throwAfterFrames == sent) throw new InvalidOperationException("stream boom");
                yield return frame;
                sent++;
                await Task.Yield();
            }
            if (throwAfterFrames == sent) throw new InvalidOperationException("stream boom");
        }

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    /// <summary>A plugin that also exposes the bytes that came off the wire.</summary>
    private sealed class WireByteProtocol(params (string Json, byte[] Binary)[] frames)
        : IBowireProtocol, IBowireStreamingWithWireBytes
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult(new InvokeResult("{}", 0, "OK", new Dictionary<string, string>()));

        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Never taken for this plugin — the endpoint prefers the
            // wire-bytes surface. Present because the interface requires it.
            foreach (var frame in frames) { yield return frame.Json; await Task.Yield(); }
        }

        public async IAsyncEnumerable<StreamFrame> InvokeStreamWithFramesAsync(
            string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var frame in frames)
            {
                yield return new StreamFrame(frame.Json, frame.Binary);
                await Task.Yield();
            }
        }

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
