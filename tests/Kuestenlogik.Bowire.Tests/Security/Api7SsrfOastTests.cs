// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// The SSRF probe with an out-of-band callback channel (#486).
/// </summary>
/// <remarks>
/// <para>
/// Without one the probe settles for a timing differential, which its own
/// summary admits: a large latency delta when a parameter is swapped for a
/// blackhole address is evidence the server fetched it. That is an inference,
/// and it is the kind a reader has to trust a threshold for.
/// </para>
/// <para>
/// A callback is the request arriving. What is pinned here is that the probe
/// asks for one per parameter, that a callback becomes a finding naming the
/// parameter it came from, and — the part that is easy to get wrong — that no
/// callback is never reported as proof of safety. A target with no outbound
/// network reaches nothing, and that says nothing about whether it tried.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class Api7SsrfOastTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Callback_Host_Is_Planted_In_Every_Url_Parameter()
    {
        // One host per parameter, so an arriving callback names which input
        // reached the network -- the difference between a finding and a
        // report somebody has to re-test by hand.
        await using var target = await StartAsync();
        var channel = new FakeChannel();

        await new Api7SsrfProbe().RunAsync(Context(target, channel), Ct);

        Assert.Equal(2, channel.Allocations.Count);
        // And each host was actually sent somewhere.
        Assert.All(channel.Allocations, host =>
            Assert.Contains(target.Received, url => url.Contains(host, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_Callback_Becomes_A_Finding_That_Names_The_Parameter()
    {
        await using var target = await StartAsync();
        var channel = new FakeChannel();
        channel.CallbackFor(allocationIndex: 0, protocol: "dns", remote: "203.0.113.7");

        var findings = await new Api7SsrfProbe().RunAsync(Context(target, channel), Ct);

        var confirmed = Assert.Single(findings,
            f => f.Template.Recording.Vulnerability?.Id?.Contains("OOB", StringComparison.Ordinal) == true);
        Assert.Equal(ScanFindingStatus.Vulnerable, confirmed.Status);
        // The parameter, so the report says what to fix.
        Assert.Contains("url", confirmed.Detail, StringComparison.OrdinalIgnoreCase);
        // The address, which is the evidence.
        Assert.Contains("203.0.113.7", confirmed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Callback_Travels_On_The_Finding_As_Evidence()
    {
        // Not only in the prose: the interactions are what reaches SARIF and
        // the workbench, and a finding that describes a callback it does not
        // carry cannot be checked by anything downstream.
        await using var target = await StartAsync();
        var channel = new FakeChannel();
        channel.CallbackFor(allocationIndex: 0, protocol: "http", remote: "198.51.100.4");

        var findings = await new Api7SsrfProbe().RunAsync(Context(target, channel), Ct);

        var confirmed = Assert.Single(findings,
            f => f.Template.Recording.Vulnerability?.Id?.Contains("OOB", StringComparison.Ordinal) == true);
        var interaction = Assert.Single(confirmed.Response!.Interactions);
        Assert.Equal("http", interaction.Protocol);
        Assert.Equal("198.51.100.4", interaction.RemoteAddress);
    }

    [Fact]
    public async Task No_Callback_Is_Not_Reported_As_Proof_Of_Safety()
    {
        // The line that matters. A target behind an egress firewall reaches
        // nothing whatever it tried to do, so silence cannot be read as
        // "no SSRF" -- and the clean marker has to say so.
        await using var target = await StartAsync();
        var channel = new FakeChannel();

        var findings = await new Api7SsrfProbe().RunAsync(Context(target, channel), Ct);

        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
        var clean = Assert.Single(findings, f => f.Status == ScanFindingStatus.Safe);
        Assert.Contains("not the same as proof", clean.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_A_Channel_The_Probe_Behaves_As_Before()
    {
        // The normal case: no interaction server configured. Nothing is
        // planted, and the clean marker says nothing about callbacks.
        await using var target = await StartAsync();

        var findings = await new Api7SsrfProbe().RunAsync(
            new OwaspApiProbeContext { Target = target.Url, Http = target.Http }, Ct);

        var clean = Assert.Single(findings, f => f.Status == ScanFindingStatus.Safe);
        Assert.DoesNotContain("callback", clean.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---- harness ----

    private static OwaspApiProbeContext Context(TargetHost target, IOastProbeChannel channel) => new()
    {
        Target = target.Url,
        Http = target.Http,
        Oast = channel,
        // The production default waits seconds for a target that may be
        // slow. The fake channel answers immediately, so waiting for it is
        // waiting for nothing -- and five tests doing so took half a minute.
        OastGrace = TimeSpan.FromMilliseconds(400),
        OastPollInterval = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>
    /// A channel that hands out hosts and can be told a callback arrived for
    /// one of them.
    /// </summary>
    private sealed class FakeChannel : IOastProbeChannel
    {
        private readonly List<OastCallback> _feed = [];

        public List<string> Allocations { get; } = [];

        /// <summary>Say that host number <paramref name="allocationIndex"/> was contacted.</summary>
        public void CallbackFor(int allocationIndex, string protocol, string remote)
            => _pending.Add((allocationIndex, protocol, remote));

        private readonly List<(int Index, string Protocol, string Remote)> _pending = [];

        public Task<string> AllocateAsync(CancellationToken ct = default)
        {
            var host = $"corr{Allocations.Count}.oast.test";
            Allocations.Add(host);
            return Task.FromResult(host);
        }

        public Task<IReadOnlyList<OastCallback>> PollAsync(CancellationToken ct = default)
        {
            // Resolved late: the callbacks are declared before the probe runs,
            // and only then is it known which host each index got.
            foreach (var (index, protocol, remote) in _pending)
            {
                if (index >= Allocations.Count) continue;
                var host = Allocations[index];
                if (_feed.Exists(c => c.Id == host)) continue;
                _feed.Add(new OastCallback
                {
                    Protocol = protocol,
                    Id = host,
                    RemoteAddress = remote,
                    TimestampUnixMs = 0,
                    RawRequest = "GET / HTTP/1.1",
                });
            }
            // The whole feed, as the session returns it.
            return Task.FromResult<IReadOnlyList<OastCallback>>([.. _feed]);
        }
    }

    /// <summary>A target with two URL-looking query parameters.</summary>
    private sealed record TargetHost(WebApplication App, HttpClient Http, string Url, List<string> Received)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<TargetHost> StartAsync()
    {
        var received = new List<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(
            o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(ctx =>
        {
            lock (received) received.Add(ctx.Request.QueryString.Value ?? "");
            return Task.CompletedTask;
        });
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        // Two parameters the probe recognises as URL inputs.
        var url = app.Urls.First() + "/fetch?url=https://example.com/a&webhook=https://example.com/b";
        return new TargetHost(app, http, url, received);
    }
}
