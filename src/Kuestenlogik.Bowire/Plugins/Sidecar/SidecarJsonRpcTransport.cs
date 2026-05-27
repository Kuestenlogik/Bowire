// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// JSON-RPC 2.0 client over the sidecar process's stdin / stdout. The
/// framing is NDJSON — one envelope per UTF-8 line, terminated by
/// <c>\n</c>. Matches MCP's stdio transport (and avoids LSP's
/// <c>Content-Length</c> header parsing).
/// </summary>
/// <remarks>
/// <para>
/// Concurrency model: requests are correlated by their numeric
/// <c>id</c>; the transport holds an in-flight dictionary keyed on
/// the id, and the reader loop completes the matching TCS when a
/// response arrives. Notifications (no id) are routed to the
/// notification handler so the stream / channel surfaces can fan
/// them out.
/// </para>
/// <para>
/// Lifetime: created and owned by <see cref="SidecarBowireProtocol"/>.
/// Disposing it sends a best-effort <c>shutdown</c>, waits up to the
/// manifest's <c>shutdownTimeoutMs</c>, then force-kills the process.
/// </para>
/// </remarks>
internal sealed class SidecarJsonRpcTransport : IAsyncDisposable
{
    private readonly Process _process;
    private readonly int _shutdownTimeoutMs;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonObject> _notifications =
        Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private long _nextId;
    private Task? _readLoop;
    private bool _shutdownRequested;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private SidecarJsonRpcTransport(Process process, int shutdownTimeoutMs)
    {
        _process = process;
        _shutdownTimeoutMs = shutdownTimeoutMs;
    }

    /// <summary>Notifications fan-out for stream / channel data.</summary>
    public ChannelReader<JsonObject> Notifications => _notifications.Reader;

    /// <summary>True once the underlying process has exited (cleanly or otherwise).</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Spawn the sidecar process for <paramref name="manifest"/> and
    /// start the read loop. The plugin directory is the working
    /// directory and the base for the relative <c>executable</c> path.
    /// </summary>
    public static SidecarJsonRpcTransport Start(SidecarPluginManifest manifest, string pluginDir)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pluginDir);

        var exePath = Path.IsPathRooted(manifest.Executable)
            ? manifest.Executable
            : Path.Combine(pluginDir, manifest.Executable);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = pluginDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // Stderr inherited so sidecar diagnostics land on the host
            // console where operators expect them; we don't try to
            // multiplex stderr through JSON-RPC.
            RedirectStandardError = false,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        if (manifest.Args is { Count: > 0 })
        {
            foreach (var arg in manifest.Args) psi.ArgumentList.Add(arg);
        }
        // Forward env vars matching the prefix so the sidecar can read
        // its own config from the host's environment without us having
        // to mirror every variable.
        if (!string.IsNullOrEmpty(manifest.EnvPrefix))
        {
            foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            {
                if (kv.Key is string k && k.StartsWith(manifest.EnvPrefix, StringComparison.Ordinal))
                {
                    psi.Environment[k] = kv.Value as string ?? "";
                }
            }
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Process.Start returned null for sidecar executable '" + exePath + "'");

        var transport = new SidecarJsonRpcTransport(proc, manifest.ShutdownTimeoutMs);
        transport._readLoop = Task.Run(transport.ReadLoopAsync);
        return transport;
    }

    /// <summary>
    /// Send a JSON-RPC request, wait for the matching response, return
    /// the <c>result</c> element. Throws <see cref="SidecarJsonRpcException"/>
    /// when the sidecar returns an <c>error</c> object.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await SendAsync(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params,
            }, ct).ConfigureAwait(false);

            using var reg = ct.Register(static state =>
            {
                var (tcs, ct) = ((TaskCompletionSource<JsonElement>, CancellationToken))state!;
                tcs.TrySetCanceled(ct);
            }, (tcs, ct));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Fire-and-forget notification (no id, no reply expected).</summary>
    public Task NotifyAsync(string method, object? @params, CancellationToken ct)
        => SendAsync(new { jsonrpc = "2.0", method, @params }, ct);

    private async Task SendAsync(object envelope, CancellationToken ct)
    {
        if (_process.HasExited)
            throw new InvalidOperationException("Sidecar process has exited; can't send.");

        var json = JsonSerializer.Serialize(envelope, s_jsonOpts);
        // NDJSON framing: write the envelope then a single newline.
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (node is not JsonObject obj) continue;

                // id present + (result | error) → response correlation
                if (obj.TryGetPropertyValue("id", out var idNode)
                    && idNode is JsonValue idVal
                    && idVal.TryGetValue<long>(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (obj["error"] is JsonObject err)
                        {
                            var code = err["code"]?.GetValue<int>() ?? -32000;
                            var msg = err["message"]?.GetValue<string>() ?? "sidecar error";
                            tcs.TrySetException(new SidecarJsonRpcException(code, msg, err.ToJsonString()));
                        }
                        else if (obj["result"] is { } resultNode)
                        {
                            tcs.TrySetResult(JsonSerializer.SerializeToElement(resultNode));
                        }
                        else
                        {
                            // Spec-wise an envelope with id but no
                            // result/error is malformed — return empty
                            // object so callers don't NRE downstream.
                            tcs.TrySetResult(JsonSerializer.SerializeToElement(new { }));
                        }
                    }
                    continue;
                }

                // No id → notification; fan out to the channel.
                _notifications.Writer.TryWrite(obj);
            }
        }
        finally
        {
            _notifications.Writer.TryComplete();
            // Any still-pending requests get failed so callers don't
            // hang forever waiting for a reply that won't come.
            foreach (var kv in _pending)
            {
                kv.Value.TrySetException(new SidecarJsonRpcException(
                    -32000, "Sidecar exited before reply", null));
            }
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdownRequested) return;
        _shutdownRequested = true;

        if (!_process.HasExited)
        {
            // Best-effort graceful shutdown — send the JSON-RPC
            // `shutdown` request, wait briefly for the process to exit,
            // then kill.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_shutdownTimeoutMs));
                await RequestAsync("shutdown", null, cts.Token).ConfigureAwait(false);
            }
            catch { /* swallow — we're killing the process anyway */ }

            try { _process.StandardInput.Close(); }
            catch { }

            try
            {
                using var waitCts = new CancellationTokenSource(_shutdownTimeoutMs);
                try
                {
                    await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { /* race with the OS reaping the process */ }
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
        _process.Dispose();
    }
}

/// <summary>Thrown when the sidecar returns a JSON-RPC <c>error</c> object.</summary>
[Serializable]
public sealed class SidecarJsonRpcException : Exception
{
    public int Code { get; }
    public string? RawError { get; }

    public SidecarJsonRpcException() : base() { }
    public SidecarJsonRpcException(string message) : base(message) { }
    public SidecarJsonRpcException(string message, Exception innerException) : base(message, innerException) { }

    public SidecarJsonRpcException(int code, string message, string? rawError) : base(message)
    {
        Code = code;
        RawError = rawError;
    }
}
