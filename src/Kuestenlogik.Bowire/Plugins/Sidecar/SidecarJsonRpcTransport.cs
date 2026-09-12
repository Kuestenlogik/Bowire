// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.ComponentModel;
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
internal sealed class SidecarJsonRpcTransport : ISidecarTransport
{
    private readonly Process _process;
    private readonly int _shutdownTimeoutMs;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    // Per-id notification fan-out (one channel per live stream / channel)
    // so concurrent streams + duplex channels never steal each other's
    // frames. Shared with the HTTP transport via SidecarSubscriptionHub.
    private readonly SidecarSubscriptionHub _hub = new();
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

    /// <inheritdoc />
    public ChannelReader<JsonObject> Subscribe(string id) => _hub.Subscribe(id);

    /// <inheritdoc />
    public void Unsubscribe(string id) => _hub.Unsubscribe(id);

    /// <summary>True once the underlying process has exited (cleanly or otherwise).</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Where a manifest's <c>executable</c> is looked for.</summary>
    internal enum ExecutableSource
    {
        /// <summary>An absolute path, or a path resolved against the plugin directory.</summary>
        PluginDirectory,

        /// <summary>A bare name handed to the OS to look up on <c>PATH</c>.</summary>
        SearchPath,
    }

    // Both separators, always. A manifest is written once and installed
    // on every platform, so "bin\sidecar" names a path on Linux too and
    // must not be mistaken for a bare name there.
    private static readonly char[] s_pathSeparators = ['/', '\\'];

    private static bool IsBareName(string executable)
        => executable.IndexOfAny(s_pathSeparators) < 0;

    /// <summary>
    /// Resolve a manifest's <c>executable</c> to the file name the
    /// process should start with.
    /// </summary>
    /// <remarks>
    /// <para>Two steps, in this order:</para>
    /// <list type="number">
    /// <item><description>the plugin's own directory — an absolute path,
    /// or a relative path that exists under <paramref name="pluginDir"/>;</description></item>
    /// <item><description><c>PATH</c>, but only for a bare name (no
    /// directory separator). That step is what lets a manifest say
    /// <c>"executable": "python3", "args": ["plugin.py"]</c> and work on
    /// every platform, rather than resolving to a non-existent
    /// <c>&lt;pluginDir&gt;/python3</c>.</description></item>
    /// </list>
    /// <para>
    /// The order is the security-relevant part: the plugin directory is
    /// the trusted location, so a binary the plugin ships always wins
    /// over a same-named program on <c>PATH</c>. <c>PATH</c> is only
    /// reached when the plugin ships no such file.
    /// </para>
    /// <para>
    /// The lookup itself is left to the OS — passing a name without a
    /// separator as <see cref="ProcessStartInfo.FileName"/> is what makes
    /// it search <c>PATH</c> (and, on Windows, apply <c>PATHEXT</c>, so
    /// <c>python3</c> finds <c>python3.exe</c>). Doing it by hand here
    /// would mean reimplementing both.
    /// </para>
    /// </remarks>
    internal static (string FileName, ExecutableSource Source) ResolveExecutable(
        string executable, string pluginDir)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(pluginDir);

        if (Path.IsPathRooted(executable))
            return (executable, ExecutableSource.PluginDirectory);

        var pluginLocal = Path.Combine(pluginDir, executable);
        if (File.Exists(pluginLocal) || !IsBareName(executable))
            return (pluginLocal, ExecutableSource.PluginDirectory);

        return (executable, ExecutableSource.SearchPath);
    }

    /// <summary>
    /// Failure message naming every place the executable was looked for.
    /// The OS error on its own ("The specified executable is not a valid
    /// application for this OS platform") says nothing about which of the
    /// two steps was taken, which is precisely what the reader needs.
    /// </summary>
    private static string DescribeStartFailure(
        string executable, string pluginDir, ExecutableSource source, string reason)
    {
        var pluginLocal = Path.Combine(pluginDir, executable);
        var tried = source switch
        {
            ExecutableSource.SearchPath =>
                "tried '" + pluginLocal + "' (the plugin ships no such file), then '"
                + executable + "' on PATH",
            _ when Path.IsPathRooted(executable) =>
                "tried '" + executable + "'",
            _ =>
                "tried '" + pluginLocal + "'; PATH was not searched because '"
                + executable + "' names a path rather than a bare command",
        };
        return "Couldn't start the sidecar executable '" + executable + "': " + tried + ". " + reason;
    }

    /// <summary>
    /// Spawn the sidecar process for <paramref name="manifest"/> and
    /// start the read loop. The plugin directory is the working
    /// directory and the first place the <c>executable</c> is looked
    /// for; a bare name the plugin doesn't ship falls back to
    /// <c>PATH</c> — see <see cref="ResolveExecutable"/>.
    /// </summary>
    public static SidecarJsonRpcTransport Start(SidecarPluginManifest manifest, string pluginDir)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pluginDir);

        var (exePath, exeSource) = ResolveExecutable(manifest.Executable, pluginDir);

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

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            // The OS couldn't launch it: missing file, missing execute
            // bit, wrong architecture. Re-throw naming the places we
            // looked, keeping the original as the inner exception.
            throw new InvalidOperationException(
                DescribeStartFailure(manifest.Executable, pluginDir, exeSource, ex.Message), ex);
        }

        if (proc is null)
        {
            throw new InvalidOperationException(DescribeStartFailure(
                manifest.Executable, pluginDir, exeSource, "Process.Start returned null."));
        }

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

                // No id → notification. Route to the subscription whose
                // id matches the notification's streamId / channelId.
                _hub.Route(obj);
            }
        }
        finally
        {
            // Complete every live subscription so readers blocked on
            // WaitToReadAsync wake up and exit their loops.
            _hub.CompleteAll();
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
            // Best-effort graceful-shutdown RPC. We're about to kill the
            // process anyway, so any failure is irrelevant.
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_shutdownTimeoutMs));
                await RequestAsync("shutdown", null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { _ = ex; }
#pragma warning restore CA1031

            // Process may already be gone (stream closed, disposed, or
            // kernel reaped it) -- stdin-close is just a polite EOF
            // signal, not a correctness requirement.
#pragma warning disable CA1031 // Do not catch general exception types
            try { _process.StandardInput.Close(); }
            catch (Exception ex) { _ = ex; }
#pragma warning restore CA1031

            // Race with the OS reaping the process: WaitForExitAsync
            // can throw InvalidOperationException, Kill can throw
            // Win32Exception. Either way we're in disposal.
#pragma warning disable CA1031 // Do not catch general exception types
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
            catch (Exception ex) { _ = ex; }
#pragma warning restore CA1031
        }

        if (_readLoop is not null)
        {
            // Read loop normally exits cleanly when the process closes
            // stdout; if it didn't (cancel race, pipe error), we're
            // already in disposal so propagating doesn't help.
#pragma warning disable CA1031 // Do not catch general exception types
            try { await _readLoop.ConfigureAwait(false); }
            catch (Exception ex) { _ = ex; }
#pragma warning restore CA1031
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
