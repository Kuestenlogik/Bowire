// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Kuestenlogik.Bowire.Oast.Server;

namespace Kuestenlogik.Bowire.Oast.Tests;

/// <summary>
/// <c>bowire oast serve</c> — what it refuses before binding anything, and
/// what it tells the operator once it is up.
/// </summary>
/// <remarks>
/// <para>
/// An OAST server catches other people's callback traffic, so the two things
/// that matter on start-up are that a mistyped address fails immediately with
/// an exit code a script can read, and that the banner says out loud when the
/// instance is running without a token — an open catcher is a real exposure,
/// not a configuration nicety.
/// </para>
/// <para>
/// The run test binds on ephemeral ports on the loopback interface: the real
/// defaults (80 and 53) need privileges and are usually already taken.
/// </para>
/// </remarks>
public sealed class OastServeCommandTests : IDisposable
{
    // Fields rather than locals in the run helper: the writers are handed to a
    // task that is still running when the helper's scope would end, and CA2025
    // is right to refuse that shape. xUnit builds one instance per test, so
    // these stay per-test anyway.
    private readonly StringWriter _stdout = new(CultureInfo.InvariantCulture);
    private readonly StringWriter _stderr = new(CultureInfo.InvariantCulture);

    /// <summary>What the server is given to print to; see <see cref="BannerWatch"/>.</summary>
    private readonly BannerWatch _banner;

    public OastServeCommandTests() => _banner = new BannerWatch(_stdout, LastBannerLine);

    public void Dispose()
    {
        _banner.Dispose();
        _stdout.Dispose();
        _stderr.Dispose();
    }

    private static OastServeOptions Options(
        string publicIp = "203.0.113.10",
        string listenIp = "127.0.0.1",
        int httpPort = 0,
        int dnsPort = 0,
        string? token = null)
        => new()
        {
            Domain = "oast.example.com",
            PublicIp = publicIp,
            ListenIp = listenIp,
            HttpPort = httpPort,
            DnsPort = dnsPort,
            Token = token,
        };

    [Fact]
    public async Task A_Public_Address_That_Is_Not_An_Ip_Is_Refused_Before_Anything_Binds()
    {
        // The public IP is what every A answer hands back. A hostname here
        // would produce a server that starts fine and answers nonsense.
        var exit = await BowireOastServer.RunAsync(
            Options(publicIp: "oast.example.com"), CancellationToken.None, TextWriter.Null, _stderr);

        Assert.Equal(2, exit);
        Assert.Contains("--public-ip", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("oast.example.com", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Listen_Address_That_Is_Not_An_Ip_Is_Refused_Too()
    {
        var exit = await BowireOastServer.RunAsync(
            Options(listenIp: "localhost"), CancellationToken.None, TextWriter.Null, _stderr);

        Assert.Equal(2, exit);
        Assert.Contains("--listen-ip", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Ipv6_Address_Is_A_Valid_Address()
    {
        // Rejecting it would be an accident of the validation, not a decision:
        // IPAddress.TryParse takes v6, and a v6-only host is a legitimate place
        // to run this.
        var exit = await BowireOastServer.RunAsync(
            Options(publicIp: "2001:db8::1", listenIp: "not-an-ip"), CancellationToken.None, TextWriter.Null, _stderr);

        // It got past the public-ip check and failed on the listen address.
        Assert.Equal(2, exit);
        Assert.Contains("--listen-ip", _stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--public-ip", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serving_Without_A_Token_Says_So_In_The_Banner()
    {
        var (exit, banner) = await RunBrieflyAsync(Options());

        Assert.Equal(0, exit);
        // The word an operator can grep for when they wonder whether the box
        // they left running is open to the internet.
        Assert.Contains("OPEN", banner, StringComparison.Ordinal);
        Assert.Contains("--token", banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serving_With_A_Token_Says_That_Instead()
    {
        var (_, banner) = await RunBrieflyAsync(Options(token: "s3cret"));

        Assert.Contains("token required", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("OPEN", banner, StringComparison.Ordinal);
        // And never the token itself — this banner goes into terminal
        // scrollback and CI logs.
        Assert.DoesNotContain("s3cret", banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Banner_Names_The_Zone_And_The_Scan_Flag_That_Uses_It()
    {
        // Someone who just started this needs the one command that points a
        // scan at it; making them look it up is where the chain breaks.
        var (_, banner) = await RunBrieflyAsync(Options());

        Assert.Contains("*.oast.example.com", banner, StringComparison.Ordinal);
        Assert.Contains("--oast-server", banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_Stops_Cleanly_Rather_Than_Throwing()
    {
        // Ctrl+C is the documented way to stop it, so the cancellation has to
        // come back as exit 0 and a final line, not an unhandled exception.
        var (exit, banner) = await RunBrieflyAsync(Options());

        Assert.Equal(0, exit);
        Assert.Contains("Stopped.", banner, StringComparison.Ordinal);
    }

    /// <summary>
    /// Start the server on ephemeral ports, let it run briefly, and hand back
    /// the exit code plus everything it printed.
    /// </summary>
    /// <remarks>
    /// Stopped on the server's own word that it is up, not after a wall-clock
    /// guess. The two seconds this replaces were ample on an idle machine and
    /// not on a loaded one: cancelled mid-<c>StartAsync</c> the server returns
    /// 0 without printing anything — a documented path, and indistinguishable
    /// here from a clean run with an empty banner. That is what made these
    /// tests pass alone and fail in a full solution run.
    /// </remarks>
    private async Task<(int Exit, string Output)> RunBrieflyAsync(OastServeOptions options)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var run = BowireOastServer.RunAsync(options, cts.Token, _banner, _stderr);

        // Whichever comes first: the banner is out, or the run ended on its
        // own — a refused bind never prints it. The timeout is a backstop
        // against a hang, not the thing being waited on.
        await Task.WhenAny(run, _banner.Printed)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var exit = await run;

        // A CI runner may refuse a bind (locked-down container, no raw UDP).
        // That is the environment saying no, not the server misbehaving.
        Assert.SkipWhen(exit == 1, $"could not bind on this machine: {_stderr}");

        return (exit, _stdout.ToString());
    }

    /// <summary>The server's last line before it settles into serving.</summary>
    private const string LastBannerLine = "Press Ctrl+C to stop.";

    /// <summary>
    /// Forwards everything written to an inner writer and completes
    /// <see cref="Printed"/> once a marker has gone through.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="TextWriter"/> rather than wrapping
    /// <see cref="StringWriter"/>: every base overload bottoms out in
    /// <see cref="Write(char)"/>, so one override sees the whole stream
    /// whichever method — sync, async, line or char — the server calls.
    /// </remarks>
    private sealed class BannerWatch(StringWriter inner, string marker) : TextWriter
    {
        private readonly TaskCompletionSource _printed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Text.StringBuilder _line = new();
        private readonly Lock _gate = new();

        /// <summary>Completes once <c>marker</c> has been written.</summary>
        public Task Printed => _printed.Task;

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            bool hit;
            lock (_gate)
            {
                inner.Write(value);
                if (value is '\n' or '\r') _line.Clear();
                else _line.Append(value);
                hit = _line.Length >= marker.Length
                    && _line.ToString().EndsWith(marker, StringComparison.Ordinal);
            }
            // Outside the lock: a continuation that writes here in response
            // would otherwise re-enter it.
            if (hit) _printed.TrySetResult();
        }
    }
}
