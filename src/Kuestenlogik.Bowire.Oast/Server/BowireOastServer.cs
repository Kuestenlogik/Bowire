// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DNS.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Oast.Server;

/// <summary>Options for <c>bowire oast serve</c>.</summary>
public sealed record OastServeOptions
{
    /// <summary>
    /// The delegated zone this instance is authoritative for, e.g.
    /// <c>oast.example.com</c>. Callback hosts are handed out beneath it.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The instance's public address — answered for A queries, so a target that
    /// resolves a callback host then connects lands on the HTTP catcher.
    /// </summary>
    public required string PublicIp { get; init; }

    /// <summary>HTTP catcher + API port. Default 80.</summary>
    public int HttpPort { get; init; } = 80;

    /// <summary>DNS catcher port. Default 53 — a real delegation cannot use another.</summary>
    public int DnsPort { get; init; } = 53;

    /// <summary>Address to bind. Default all interfaces.</summary>
    public string ListenIp { get; init; } = "0.0.0.0";

    /// <summary>
    /// When set, register calls must present it as <c>Authorization</c>.
    /// Without it the instance is an open callback catcher for anyone who finds
    /// it.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>Idle sessions are evicted after this long. Default 1h.</summary>
    public int SessionIdleMinutes { get; init; } = 60;
}

/// <summary>
/// <c>bowire oast serve</c> — the out-of-band interaction server (#35 Phase 2f).
/// Runs the DNS + HTTP callback catchers and the register/poll API, wire-compatible
/// with the interactsh protocol.
/// </summary>
/// <remarks>
/// Lets the whole OAST chain be self-hosted with no third-party service and no
/// Go binary: point <c>bowire scan --oast-server</c> at this. It is protocol-
/// compatible on purpose, so an existing deployment can be swapped for this one
/// behind the same DNS delegation without touching any client.
/// </remarks>
public static class BowireOastServer
{
    /// <summary>Run until cancelled. Returns a process exit code.</summary>
    public static async Task<int> RunAsync(
        OastServeOptions options,
        CancellationToken ct,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (!IPAddress.TryParse(options.PublicIp, out var publicIp))
        {
            await stderr.WriteLineAsync($"  --public-ip must be an IP address, got '{options.PublicIp}'.").ConfigureAwait(false);
            return 2;
        }
        if (!IPAddress.TryParse(options.ListenIp, out var listenIp))
        {
            await stderr.WriteLineAsync($"  --listen-ip must be an IP address, got '{options.ListenIp}'.").ConfigureAwait(false);
            return 2;
        }

        var store = new OastInteractionStore(
            idleTimeout: TimeSpan.FromMinutes(Math.Max(1, options.SessionIdleMinutes)));
        void Log(string line) => stdout.WriteLine(line);

        // ---- HTTP catcher + API ----
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(listenIp, options.HttpPort));
        var app = builder.Build();
        app.MapBowireOast(store, options.Token, Log);

        // ---- DNS catcher ----
        var catcher = new OastDnsCatcher(options.Domain, publicIp, store, Log);
        using var dns = new DnsServer(catcher);

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await stderr.WriteLineAsync($"  Could not bind HTTP port {options.HttpPort}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var dnsListen = dns.Listen(options.DnsPort, listenIp);

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"  Bowire OAST server — authoritative for *.{options.Domain}")).ConfigureAwait(false);
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"    DNS  {listenIp}:{options.DnsPort}   → answers A with {publicIp}")).ConfigureAwait(false);
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"    HTTP {listenIp}:{options.HttpPort}   → catcher + /register /poll /status")).ConfigureAwait(false);
        await stdout.WriteLineAsync(options.Token is null
            ? "    Auth: OPEN — anyone who finds this can register. Use --token to gate it."
            : "    Auth: token required on /register.").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"  Point a scan at it:  bowire scan --target <url> --nuclei <dir> --oast-server http://{options.Domain}:{options.HttpPort}").ConfigureAwait(false);
        await stdout.WriteLineAsync("  Press Ctrl+C to stop.").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);

        // Evict idle sessions so a long-lived catcher doesn't hold other
        // people's callback traffic forever. The loop owns its timer — a
        // `using` here would dispose it while the task still runs.
        var eviction = EvictLoopAsync(store, TimeSpan.FromMinutes(5), ct);

        try
        {
            await Task.WhenAny(dnsListen, eviction, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — the normal exit path.
        }
        catch (SocketException ex)
        {
            await stderr.WriteLineAsync($"  DNS listener failed on port {options.DnsPort}: {ex.Message}").ConfigureAwait(false);
            await stderr.WriteLineAsync("  Port 53 is usually held by systemd-resolved on Linux; disable its stub listener or pass --dns-port for a local test.").ConfigureAwait(false);
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return 1;
        }

        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await stdout.WriteLineAsync("Stopped.").ConfigureAwait(false);
        return 0;
    }

    private static async Task EvictLoopAsync(OastInteractionStore store, TimeSpan period, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                store.EvictIdle();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
