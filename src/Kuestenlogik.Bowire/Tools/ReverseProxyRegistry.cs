// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Kuestenlogik.Bowire.Interceptor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tools;

/// <summary>
/// Process-wide registry of <see cref="BowireReverseProxyHost"/>
/// instances started from the workbench's Tools → Reverse-proxy
/// surface (#153 UI Phase). Hosts started here die when the
/// surrounding Bowire process exits — the registry hooks
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> to
/// stop and dispose every entry so a Ctrl-C on the parent doesn't
/// leak Kestrel listeners.
/// </summary>
/// <remarks>
/// <para>
/// Keying by edge port (the bound TCP port) is intentional: it's the
/// stable identifier the operator sees in the UI ("Running on
/// :5200 → upstream"). A second Start request that names a
/// port already in flight returns a 409 in
/// <c>BowireToolsEndpoints</c> so the operator gets a clear
/// "pick another port" signal instead of a silent overwrite.
/// </para>
/// <para>
/// Lifetimes here are bounded by the host process — by design.
/// The reverse-proxy is a transient developer tool, not a
/// long-running production gateway; a UI-driven persist-across-
/// restart story would invite operators to use Bowire as a
/// production proxy, which is out of scope for the workbench (the
/// standalone <c>bowire proxy</c> CLI covers that surface).
/// </para>
/// </remarks>
public sealed class ReverseProxyRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, ReverseProxyRegistryEntry> _entries = new();
    private readonly ILogger<ReverseProxyRegistry>? _logger;
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private bool _disposed;

    /// <summary>
    /// Construct the registry + register a graceful-shutdown hook so
    /// every started proxy stops when the host stops. The
    /// <paramref name="lifetime"/> is optional so unit tests can
    /// instantiate the registry without faking a full ASP.NET host.
    /// </summary>
    public ReverseProxyRegistry(
        IHostApplicationLifetime? lifetime = null,
        ILogger<ReverseProxyRegistry>? logger = null)
    {
        _logger = logger;
        if (lifetime is not null)
        {
            _stoppingRegistration = lifetime.ApplicationStopping.Register(() =>
            {
                // Fire-and-forget at shutdown: blocking on a graceful
                // Kestrel stop here would gate the host's own shutdown
                // sequence on a possibly-misbehaving upstream. The
                // hosts' StopAsync calls are themselves cancellable;
                // the in-flight requests get the standard
                // ConnectionReset on host teardown either way.
                _ = StopAllAsync();
            });
        }
    }

    /// <summary>Snapshot of every running proxy keyed by edge port.</summary>
    public IReadOnlyCollection<ReverseProxyRegistryEntry> Snapshot()
        => _entries.Values.OrderBy(e => e.Port).ToArray();

    /// <summary>
    /// Try to register a freshly-started host. Returns <c>true</c> when
    /// the port slot was free, <c>false</c> when an entry already owns
    /// that port (caller surfaces a 409).
    /// </summary>
    public bool TryAdd(ReverseProxyRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.TryAdd(entry.Port, entry);
    }

    /// <summary>
    /// Look up a running entry by edge port.
    /// </summary>
    public ReverseProxyRegistryEntry? Get(int port)
        => _entries.TryGetValue(port, out var entry) ? entry : null;

    /// <summary>
    /// Stop + remove the entry for <paramref name="port"/>. No-op when
    /// no such entry exists (idempotent — repeated Stop clicks from
    /// the UI shouldn't error).
    /// </summary>
    public async Task<bool> StopAsync(int port, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryRemove(port, out var entry)) return false;
        try
        {
            await entry.Host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A Kestrel host that's already torn down (caller raced
            // with the application-stopping hook, say) throws on a
            // second StopAsync. Surface as a log-line; the registry
            // entry is gone either way, which is what the caller
            // wants.
            if (_logger is not null) RegistryLog.StopFailed(_logger, port, ex);
        }
        await entry.Host.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Stop every registered entry. Used by the
    /// <c>ApplicationStopping</c> hook and the "Stop all" button in
    /// the workbench's Settings → Tools list.
    /// </summary>
    public async Task<int> StopAllAsync(CancellationToken cancellationToken = default)
    {
        var stopped = 0;
        // Snapshot ports first so a concurrent Start during shutdown
        // doesn't mutate the dictionary while we iterate.
        var ports = _entries.Keys.ToArray();
        foreach (var port in ports)
        {
            if (await StopAsync(port, cancellationToken).ConfigureAwait(false))
                stopped++;
        }
        return stopped;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stoppingRegistration.Dispose();
        await StopAllAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// One running reverse-proxy entry — the host instance plus the
/// metadata the workbench renders on the Tools list.
/// </summary>
public sealed class ReverseProxyRegistryEntry
{
    public ReverseProxyRegistryEntry(
        int port,
        Uri upstream,
        BowireReverseProxyHost host)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(host);
        Port = port;
        Upstream = upstream;
        Host = host;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The bound TCP port the proxy listens on.</summary>
    public int Port { get; }

    /// <summary>The upstream URL every request is forwarded to.</summary>
    public Uri Upstream { get; }

    /// <summary>The host instance — wraps Kestrel + the forwarder middleware.</summary>
    public BowireReverseProxyHost Host { get; }

    /// <summary>UTC timestamp when the host was started.</summary>
    public DateTimeOffset StartedAt { get; }
}

/// <summary>
/// Source-generated logger for the registry's shutdown paths.
/// </summary>
internal static partial class RegistryLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Reverse-proxy host on port {Port} failed to stop cleanly.")]
    public static partial void StopFailed(ILogger logger, int port, Exception ex);
}
