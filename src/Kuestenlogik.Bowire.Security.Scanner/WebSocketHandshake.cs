// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// One WebSocket upgrade as the WebSocket probes make it: connect with a given
/// set of headers, close at once, and report whether the upgrade completed.
/// </summary>
/// <remarks>
/// Connect and close only. A probe never sends a frame, so it cannot drive any
/// application behaviour on a channel — which matters most when it connects
/// with a forged credential or as an identity that may not be entitled to the
/// channel.
/// </remarks>
internal static class WebSocketHandshake
{
    /// <summary>The outcome of one upgrade attempt.</summary>
    /// <param name="Connected">The upgrade completed.</param>
    /// <param name="Reason">Why it did not, or <c>connected</c>.</param>
    /// <param name="Exception">The failure, when the attempt threw.</param>
    public readonly record struct Attempt(bool Connected, string Reason, Exception? Exception)
    {
        /// <summary>The server refused the upgrade on authentication grounds (401 / 403).</summary>
        public bool AuthRejected
        {
            get
            {
                if (Connected) return false;
                var msg = Reason;
                return msg.Contains("401", StringComparison.Ordinal) || msg.Contains("403", StringComparison.Ordinal)
                    || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Open an upgrade to <paramref name="target"/> carrying
    /// <paramref name="authHeaders"/> as request headers (none when empty),
    /// then close it.
    /// </summary>
    public static async Task<Attempt> TryAsync(
        string target, IBowireProtocol protocol, IList<string>? authHeaders, CancellationToken ct)
    {
        IBowireChannel? channel;
        try
        {
            channel = await protocol.OpenChannelAsync(target, service: "", method: "",
                showInternalServices: false, metadata: ToMetadata(authHeaders), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Attempt(false, (ex.Message ?? ex.GetType().Name).Trim(), ex);
        }

        if (channel is null) return new Attempt(false, "no channel was returned", null);

        try { await channel.CloseAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort close */ }
        finally { await channel.DisposeAsync().ConfigureAwait(false); }

        return new Attempt(true, "connected", null);
    }

    /// <summary>
    /// <c>Name: value</c> header lines as the metadata a protocol plugin puts
    /// on the upgrade request; <c>null</c> for none.
    /// </summary>
    public static Dictionary<string, string>? ToMetadata(IList<string>? authHeaders)
    {
        if (authHeaders is null || authHeaders.Count == 0) return null;
        var md = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in authHeaders.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            var value = raw[(colon + 1)..].TrimStart();
            if (name.Length > 0) md[name] = value;
        }
        return md.Count > 0 ? md : null;
    }
}
