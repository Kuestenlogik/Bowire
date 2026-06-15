// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// Adapter that implements <see cref="IBowireProtocol"/> by proxying
/// every call into a sidecar process over JSON-RPC. One instance per
/// <see cref="SidecarPluginManifest"/>. See
/// <c>docs/architecture/sidecar-plugins.md</c> for the wire spec.
/// </summary>
/// <remarks>
/// <para>
/// Process spawn is **lazy** — the constructor only stores the manifest.
/// The first <see cref="DiscoverAsync"/> / <see cref="InvokeAsync"/>
/// call spawns the executable and sends <c>initialize</c>. Hosts that
/// scan the registry without calling anything (e.g. <c>plugin list</c>)
/// don't pay the cost of starting every sidecar.
/// </para>
/// <para>
/// Phase 1 surface: discover, invoke, invokeStream. Channels
/// (<see cref="OpenChannelAsync"/>) return null — plugins that need
/// long-lived duplex pipes should expose them via the server-streaming
/// surface of <see cref="InvokeStreamAsync"/>. Full channel support
/// lands in a Phase 2 follow-up.
/// </para>
/// </remarks>
// CA1001: SidecarJsonRpcTransport holds the long-lived process; the
// transport is disposed when the host shuts down (same lifetime as
// the registry). Mirroring IDisposable through IBowireProtocol just
// for one disposable field would ripple through every plugin.
#pragma warning disable CA1001
public sealed class SidecarBowireProtocol : IBowireProtocol
#pragma warning restore CA1001
{
    private readonly SidecarPluginManifest _manifest;
    private readonly string _pluginDir;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private ISidecarTransport? _transport;
    private InitializeResult? _initResult;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SidecarBowireProtocol(SidecarPluginManifest manifest, string pluginDir)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _pluginDir = pluginDir ?? throw new ArgumentNullException(nameof(pluginDir));
    }

    public string Name => _initResult?.Name ?? _manifest.Protocol.Name;
    public string Id => _initResult?.Id ?? _manifest.Protocol.Id;
    public string IconSvg => _initResult?.IconSvg
        ?? _manifest.Protocol.IconSvg
        // Generic plug-shape fallback — picked up by all sidecars
        // whose manifest didn't supply an iconSvg.
        ?? """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" width="16" height="16" aria-hidden="true"><path d="M9 7V4"/><path d="M15 7V4"/><path d="M5 7h14v4a5 5 0 01-5 5h-4a5 5 0 01-5-5z"/><path d="M12 16v5"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider) { /* lazy spawn in EnsureStartedAsync */ }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var transport = await EnsureStartedAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await transport.RequestAsync("discover", new
            {
                serverUrl,
                showInternalServices,
            }, ct).ConfigureAwait(false);

            if (result.ValueKind != JsonValueKind.Array)
                return [];

            return JsonSerializer.Deserialize<List<BowireServiceInfo>>(result.GetRawText(), s_jsonOpts)
                ?? [];
        }
        catch (SidecarJsonRpcException)
        {
            // Sidecar said no — match the .NET-plugin contract by
            // returning an empty list rather than throwing.
            return [];
        }
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ISidecarTransport transport;
        // Sidecar spawn surface: Process.Start (Win32Exception),
        // child-process initialise RPC (JSON, IO, timeout), or any
        // 3rd-party exception bubbling out of EnsureStartedAsync.
        // Wrap them all into a result-object error since the caller
        // expects an InvokeResult either way.
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            transport = await EnsureStartedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            sw.Stop();
            return new InvokeResult(null, sw.ElapsedMilliseconds, "Sidecar spawn failed: " + ex.Message, new());
        }

        try
        {
            var result = await transport.RequestAsync("invoke", new
            {
                serverUrl,
                service,
                method,
                jsonMessages,
                showInternalServices,
                metadata,
            }, ct).ConfigureAwait(false);

            sw.Stop();
            return ParseInvokeResult(result, sw.ElapsedMilliseconds);
        }
        catch (SidecarJsonRpcException ex)
        {
            sw.Stop();
            return new InvokeResult(ex.RawError, sw.ElapsedMilliseconds, "sidecar:" + ex.Code, new());
        }
        // Sidecar transport surface: any plugin-author defined type
        // can come through. Propagate cancellation but report
        // everything else as an error result.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            sw.Stop();
            return new InvokeResult(null, sw.ElapsedMilliseconds, ex.Message, new());
        }
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var transport = await EnsureStartedAsync(ct).ConfigureAwait(false);

        // Host generates the streamId and subscribes *before* sending
        // the request, so a sidecar that starts pushing $/stream/data
        // immediately can't beat us to the subscription.
        var streamId = Guid.NewGuid().ToString("N");
        var reader = transport.Subscribe(streamId);
        try
        {
            await transport.RequestAsync("invokeStream", new
            {
                streamId,
                serverUrl,
                service,
                method,
                jsonMessages,
                showInternalServices,
                metadata,
            }, ct).ConfigureAwait(false);

            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var note))
                {
                    var noteMethod = note["method"]?.GetValue<string>();
                    var nparams = note["params"] as JsonObject;

                    if (noteMethod == "$/stream/data")
                    {
                        yield return ExtractMessage(nparams?["message"]);
                    }
                    else if (noteMethod == "$/stream/end")
                    {
                        yield break;
                    }
                }
            }
        }
        finally
        {
            transport.Unsubscribe(streamId);
        }
    }

    public async Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var transport = await EnsureStartedAsync(ct).ConfigureAwait(false);

        // Host generates the channelId + subscribes before the request
        // (same race-avoidance as invokeStream).
        var channelId = Guid.NewGuid().ToString("N");
        var reader = transport.Subscribe(channelId);
        try
        {
            await transport.RequestAsync("openChannel", new
            {
                channelId,
                serverUrl,
                service,
                method,
                showInternalServices,
                metadata,
            }, ct).ConfigureAwait(false);
        }
        // Sidecar openChannel rejection — any RPC error / transport
        // failure means we can't return a usable channel.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _ = ex;
            transport.Unsubscribe(channelId);
            return null;
        }

        return new SidecarChannel(transport, channelId, reader);
    }

    /// <summary>
    /// Native plugins yield raw strings (the JSON payload). When the
    /// sidecar sends a JSON-string value, unwrap it so consumers don't
    /// see double quotes; for objects / arrays / numbers, emit the raw
    /// JSON so consumers can parse it themselves.
    /// </summary>
    internal static string ExtractMessage(JsonNode? messageNode)
    {
        if (messageNode is null) return "";
        if (messageNode is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return messageNode.ToJsonString();
    }

    /// <summary>
    /// Idempotent process spawn + <c>initialize</c> handshake. Wrapped
    /// in a semaphore so concurrent first-callers don't race two
    /// spawns.
    /// </summary>
    internal async Task<ISidecarTransport> EnsureStartedAsync(CancellationToken ct)
    {
        if (_transport is { } existing && !existing.HasExited) return existing;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_transport is { } existing2 && !existing2.HasExited) return existing2;

            // If a previous run died, dispose it before respawning.
            if (_transport is not null)
            {
                // Old transport already dead -- disposal is best-
                // effort. We're about to spawn a fresh one anyway, so a
                // clean-up failure here doesn't block recovery.
#pragma warning disable CA1031 // Do not catch general exception types
                try { await _transport.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _ = ex; }
#pragma warning restore CA1031
                _transport = null;
                _initResult = null;
            }

            // Pick the transport the manifest declares: a (possibly
            // remote) HTTP/SSE service, or a local stdio subprocess.
            // CA2000: ownership transfers to _transport on success; the
            // catch disposes it on any init failure.
#pragma warning disable CA2000
            ISidecarTransport t = _manifest.IsHttp
                ? await SidecarHttpTransport.StartAsync(_manifest, ct).ConfigureAwait(false)
                : SidecarJsonRpcTransport.Start(_manifest, _pluginDir);
#pragma warning restore CA2000
            try
            {
                var initJson = await t.RequestAsync("initialize", new
                {
                    hostVersion = typeof(SidecarBowireProtocol).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    expectedProtocolId = _manifest.Protocol.Id,
                }, ct).ConfigureAwait(false);

                _initResult = JsonSerializer.Deserialize<InitializeResult>(initJson.GetRawText(), s_jsonOpts);

                // Sanity: declared protocol id must match the live one
                // the sidecar reports. Mismatch usually means the
                // operator pointed a sidecar at the wrong manifest.
                if (_initResult is not null
                    && !string.IsNullOrEmpty(_initResult.Id)
                    && !string.Equals(_initResult.Id, _manifest.Protocol.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Sidecar reported protocol id '" + _initResult.Id +
                        "' but manifest declares '" + _manifest.Protocol.Id + "'.");
                }
            }
            catch
            {
                await t.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _transport = t;
            return _transport;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static InvokeResult ParseInvokeResult(JsonElement element, long fallbackDurationMs)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new InvokeResult(null, fallbackDurationMs, "sidecar returned non-object", new());

        string? response = null;
        if (element.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String)
            response = resp.GetString();

        long durationMs = fallbackDurationMs;
        if (element.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number
            && d.TryGetInt64(out var parsed))
        {
            durationMs = parsed;
        }

        var status = "OK";
        if (element.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
            status = s.GetString() ?? "OK";

        var metadata = new Dictionary<string, string>();
        if (element.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in m.EnumerateObject())
            {
                metadata[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => prop.Value.GetRawText(),
                };
            }
        }

        return new InvokeResult(response, durationMs, status, metadata);
    }

    private sealed record InitializeResult(string? Name, string? Id, string? IconSvg);
}
