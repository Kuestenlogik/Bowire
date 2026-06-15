// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the <see cref="PluginUpdateCheckHostedService"/>
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>. Covers
/// the opt-in short-circuit, the first-iteration run, the cancellation
/// path through both the work and the interval delay, and the
/// IntervalHours clamp.
/// </summary>
[Collection("BowireUserContext")]
public sealed class PluginUpdateCheckHostedServiceTests : IDisposable
{
    private readonly string _originalPluginDir;
    private readonly IBowireUserStore _originalUserStore;
    private readonly string _sandbox;

    public PluginUpdateCheckHostedServiceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), $"bowire-pluginupd-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_sandbox, "plugins"));
        Directory.CreateDirectory(Path.Combine(_sandbox, "userstore"));

        _originalPluginDir = PluginUpdateCheckService.PluginDir;
        _originalUserStore = BowireUserContext.Current;
        PluginUpdateCheckService.PluginDir = Path.Combine(_sandbox, "plugins");
        BowireUserContext.Current = new TempStore(Path.Combine(_sandbox, "userstore"));
    }

    public void Dispose()
    {
        PluginUpdateCheckService.PluginDir = _originalPluginDir;
        BowireUserContext.Current = _originalUserStore;
        try { Directory.Delete(_sandbox, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Immediately_When_Disabled()
    {
        // Off-by-default: the hosted service registers in DI but does
        // nothing on startup. Guards the laptop privacy default.
        using var svc = NewService(new BowirePluginUpdateCheckOptions { Enabled = false });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await svc.StartAsync(cts.Token);
        // BackgroundService completes its ExecuteAsync near-immediately
        // when disabled; await StopAsync to flush the host pipeline.
        await svc.StopAsync(CancellationToken.None);

        Assert.Null(PluginUpdateCheckService.ReadCached());
    }

    [Fact]
    public async Task ExecuteAsync_Runs_Check_On_First_Iteration_When_Enabled()
    {
        // Enabled + empty plugin dir: the loop calls CheckAsync once,
        // persists the (empty) snapshot to cache, then waits on the
        // interval. We cancel before the delay completes; the snapshot
        // is the proof the work ran.
        using var svc = NewService(new BowirePluginUpdateCheckOptions { Enabled = true, IntervalHours = 24 });

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        // Poll briefly for the cache file — first iteration runs in
        // microseconds against the empty plugin dir.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && PluginUpdateCheckService.ReadCached() is null)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(PluginUpdateCheckService.ReadCached());
    }

    [Fact]
    public async Task ExecuteAsync_Honors_IntervalHours_Floor_Of_One()
    {
        // IntervalHours <= 0 clamps to 1 hour. We can't observe the
        // wall-clock of the delay without a clock fake, but the host
        // must still start cleanly and complete its first iteration.
        using var svc = NewService(new BowirePluginUpdateCheckOptions { Enabled = true, IntervalHours = 0 });

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && PluginUpdateCheckService.ReadCached() is null)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(PluginUpdateCheckService.ReadCached());
    }

    [Fact]
    public async Task ExecuteAsync_Recovers_When_Check_Throws()
    {
        // CheckAsync throwing (NuGet outage, DNS hiccup, &c.) lands in
        // the LogWarning catch and the loop continues — the host stays
        // up. We can't see the log directly, but the service should
        // shut down cleanly when cancelled despite the iteration error.
        var throwingInner = new ThrowingService();
        using var svc = new PluginUpdateCheckHostedService(
            throwingInner,
            Options.Create(new BowirePluginUpdateCheckOptions { Enabled = true, IntervalHours = 24 }),
            NullLogger<PluginUpdateCheckHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        // Give the iteration time to throw + land in the catch.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && throwingInner.Calls == 0)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        Assert.True(throwingInner.Calls >= 1, "CheckAsync should have been invoked at least once before cancel");
    }

    [Fact]
    public async Task ExecuteAsync_Cancels_Cleanly_During_Interval_Delay()
    {
        // Long IntervalHours + immediate cancel exercises the second
        // OperationCanceledException catch (inside the Task.Delay
        // try-block) — guards the graceful-stop path.
        using var svc = NewService(new BowirePluginUpdateCheckOptions { Enabled = true, IntervalHours = 24 });

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        // Let the first iteration kick off, then cancel — the await
        // Task.Delay inside the loop swallows OCE and exits cleanly.
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);
    }

    private static PluginUpdateCheckHostedService NewService(BowirePluginUpdateCheckOptions opts)
    {
        // CA2000: HttpClient owns the FakeHandler and disposes it.
#pragma warning disable CA2000
        var http = new HttpClient(new BlockNetworkHandler());
#pragma warning restore CA2000
        var inner = new PluginUpdateCheckService(http);
        return new PluginUpdateCheckHostedService(
            inner,
            Options.Create(opts),
            NullLogger<PluginUpdateCheckHostedService>.Instance);
    }

    /// <summary>
    /// Test double that throws on every <see cref="PluginUpdateCheckService.CheckAsync"/>
    /// call so the hosted service's catch+log path gets exercised.
    /// </summary>
    private sealed class ThrowingService : PluginUpdateCheckService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        // CA2000: PluginUpdateCheckService owns the HttpClient. The test
        // double doesn't actually use it, but the base ctor still needs
        // a non-null instance.
#pragma warning disable CA2000
        public ThrowingService() : base(new HttpClient()) { }
#pragma warning restore CA2000

        public override Task<PluginUpdateCheckSnapshot> CheckAsync(bool includePrerelease, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("simulated failure");
        }
    }

    /// <summary>
    /// Defensive fake handler: with the plugin dir empty, no HTTP call
    /// should fire. If one does, the test would silently hit the real
    /// network; this handler trips a 500 so the test fails loudly.
    /// </summary>
    /// <remarks>
    /// Tracks every emitted response so they're disposed when the owning
    /// HttpClient is -- closes the cs/local-not-disposed loop the
    /// analyzer can't see across the handler -> HttpClient boundary.
    /// HttpResponseMessage.Dispose is idempotent, so production code's
    /// own response-disposal doesn't conflict if it ever fires.
    /// </remarks>
    private sealed class BlockNetworkHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _emitted = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "test fake: no HTTP calls expected",
            };
            lock (_emitted) _emitted.Add(resp);
            return Task.FromResult(resp);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_emitted)
                {
                    foreach (var r in _emitted) r.Dispose();
                    _emitted.Clear();
                }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
