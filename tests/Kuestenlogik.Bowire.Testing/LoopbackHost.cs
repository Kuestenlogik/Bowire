// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Testing;

/// <summary>
/// Binding a test host to a loopback port without racing anything else on
/// the machine for it.
/// </summary>
/// <remarks>
/// <para>
/// The idiom this replaces was spread across some two dozen test files: open
/// a <c>TcpListener</c> on port 0, read the port the OS handed out, stop the
/// listener, and hand the number to whatever is about to bind. Between the
/// stop and the bind the port belongs to nobody, so anything on the machine
/// can take it — including another test in the same run. It then fails as
/// <c>IOException: Failed to bind to address http://127.0.0.1:NNNNN: address
/// already in use</c>, on a different test each time and never when run
/// alone, which is the shape that gets written off as a flake.
/// </para>
/// <para>
/// There is no window to close here: the host binds port 0 itself and is
/// then asked what it got. The port is never unowned.
/// </para>
/// <para>
/// What this cannot help: a server in another process (the NATS fixture) and
/// <see cref="System.Net.HttpListener"/>, neither of which will report back a
/// port they chose. Those still have to name one up front.
/// </para>
/// </remarks>
public static class LoopbackHost
{
    /// <summary>
    /// The URL to bind: loopback, and the OS picks the port. Pass to
    /// <c>UseUrls</c>, then read <see cref="BaseAddress"/> once started.
    /// </summary>
    public const string AnyPort = "http://127.0.0.1:0";

    /// <summary>As <see cref="AnyPort"/>, over TLS.</summary>
    public const string AnySecurePort = "https://127.0.0.1:0";

    /// <summary>
    /// The address <paramref name="services"/>' host actually bound, without
    /// a trailing slash.
    /// </summary>
    /// <param name="services">A started host's service provider.</param>
    /// <exception cref="InvalidOperationException">
    /// The host is not started, or bound nothing.
    /// </exception>
    public static string BaseAddress(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var address = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The host bound no address. Read this after StartAsync, not before.");

        return address.TrimEnd('/');
    }

    /// <summary>The port <paramref name="services"/>' host actually bound.</summary>
    public static int Port(IServiceProvider services)
        => new Uri(BaseAddress(services)).Port;

    /// <summary>
    /// Starts a server that insists on being told its port, retrying on a
    /// different one when the chosen port turns out to be taken.
    /// </summary>
    /// <param name="start">Starts the server on the given port.</param>
    /// <param name="attempts">How many ports to try before giving up.</param>
    /// <returns>The port it started on.</returns>
    /// <remarks>
    /// For the servers <see cref="BaseAddress"/> cannot help with: MQTTnet
    /// does not report the port an OS-assigned endpoint ended up on, and
    /// <see cref="System.Net.HttpListener"/> will not take port 0 at all. The
    /// gap between picking a port and binding it stays open here — what
    /// changes is that losing that race costs another attempt rather than
    /// the test.
    /// </remarks>
    public static async Task<int> OnAFreePortAsync(Func<int, Task> start, int attempts = 8)
    {
        ArgumentNullException.ThrowIfNull(start);

        for (var attempt = 1; ; attempt++)
        {
            var port = Reserve();
            try
            {
                await start(port).ConfigureAwait(false);
                return port;
            }
            catch (Exception ex) when (attempt < attempts && IsPortTaken(ex))
            {
                // Someone took it in the gap. The next one is a fresh draw.
            }
        }
    }

    /// <summary>
    /// As <see cref="OnAFreePortAsync"/>, for servers that bind
    /// synchronously — <see cref="System.Net.HttpListener"/> among them.
    /// </summary>
    /// <param name="start">Starts the server on the given port.</param>
    /// <param name="attempts">How many ports to try before giving up.</param>
    /// <returns>The port it started on.</returns>
    public static int OnAFreePort(Action<int> start, int attempts = 8)
    {
        ArgumentNullException.ThrowIfNull(start);

        for (var attempt = 1; ; attempt++)
        {
            var port = Reserve();
            try
            {
                start(port);
                return port;
            }
            catch (Exception ex) when (attempt < attempts && IsPortTaken(ex))
            {
                // Someone took it in the gap. The next one is a fresh draw.
            }
        }
    }

    /// <summary>
    /// A loopback port nothing is listening on — for the tests that need a
    /// connection to be refused.
    /// </summary>
    /// <remarks>
    /// The same open-and-close as <see cref="Reserve"/>, named separately
    /// because the intent is the opposite: here the point is that the port
    /// ends up unowned, and the risk is something else taking it and
    /// answering a connection that was supposed to fail.
    /// </remarks>
    public static int ClosedPort() => Reserve();

    /// <summary>A port that was free a moment ago. See <see cref="OnAFreePortAsync"/>.</summary>
    private static int Reserve()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool IsPortTaken(Exception ex) => ex switch
    {
        SocketException socket => socket.SocketErrorCode is SocketError.AddressAlreadyInUse,
        IOException or HttpListenerException => true,
        // Servers that wrap the bind failure — MQTTnet among them.
        _ => ex.InnerException is not null && IsPortTaken(ex.InnerException),
    };
}
