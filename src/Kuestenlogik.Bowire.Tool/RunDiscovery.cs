// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Discovery as one <c>bowire test</c> run needs it (#1053): only for a plugin
/// whose <see cref="IBowireProtocol.InvokeNeedsDiscovery"/> says invoking
/// depends on it, and then once per plugin and server URL for the whole run,
/// not once per test, step or data-set row.
/// </summary>
/// <remarks>
/// Every call used to discover first. For REST that meant a GET to the call's
/// own URL before every call — discovery tries a URL as an OpenAPI document —
/// so a <c>POST /orders</c> step sent <c>GET /orders</c> ahead of itself, and a
/// data-driven run did it for every row. REST discovers on its own when an
/// invoke needs it, so the runner leaves it alone.
/// </remarks>
internal sealed class RunDiscovery
{
    private readonly Dictionary<(string Protocol, string ServerUrl), string?> _done = [];

    /// <summary>
    /// Make sure <paramref name="protocol"/> has discovered
    /// <paramref name="serverUrl"/> if it needs to before an invoke. Returns
    /// the discovery error to report for the call, or <c>null</c>. An error is
    /// remembered too, so every call to that URL reports it without trying
    /// again.
    /// </summary>
    public async Task<string?> EnsureAsync(IBowireProtocol protocol, string serverUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (!protocol.InvokeNeedsDiscovery) return null;

        var key = (protocol.Id, serverUrl ?? string.Empty);
        if (_done.TryGetValue(key, out var known)) return known;

        string? error = null;
        try
        {
            await protocol.DiscoverAsync(serverUrl ?? string.Empty, showInternalServices: false, ct).ConfigureAwait(false);
        }
        // Plugin DiscoverAsync: a third-party transport. Reported on the
        // call, never aborting the run.
        catch (Exception ex) when (PluginBoundary.NonFatal(ex))
        {
            error = ex.Message;
        }
        _done[key] = error;
        return error;
    }
}
